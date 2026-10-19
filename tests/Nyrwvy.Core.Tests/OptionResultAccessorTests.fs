namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// ResultGetOrDefault, OptionOkOr, ResultOkValue, ResultErrValue, OptionIsSome and ResultIsOk
/// (post-M2 order 65), with the semantics of Rust's unwrap_or, ok_or, ok, err, is_some and is_ok:
/// every operand is evaluated and none of them fails.
module OptionResultAccessorTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("option-result-accessor-" + name)
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private text = """{"kind":"primitive","primitive":"string"}"""
    let private boolType = """{"kind":"primitive","primitive":"bool"}"""
    let private optionOf item = """{"kind":"builtinGeneric","generic":"option","arguments":[""" + item + "]}"
    let private resultOf ok error = """{"kind":"builtinGeneric","generic":"result","arguments":[""" + ok + "," + error + "]}"
    let private parameter name = """{"kind":"parameter","name":""" + "\"" + name + "\"}"
    let private unary kind operand = "{\"kind\":\"" + kind + "\",\"value\":" + operand + "}"

    let private create name parameters resultType body =
        """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
        + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
        + """],"resultType":""" + resultType + ""","requiredEffects":[],"body":""" + body + "}"

    /// Authors one function per accessor in a fresh session and passes `call` and the snapshot path on.
    let private withProgram name (body: (string -> JsonDocument) -> string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let call (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            let commit (line: string) =
                use proposal = call line
                Assert.True(proposal.RootElement.GetProperty("ok").GetBoolean(), proposal.RootElement.ToString())
                let proposalId = proposal.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use committed = call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposalId + "\"}")
                Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            let parsed = resultOf int64Type text
            commit (create "getOr" [ "result", parsed; "fallback", int64Type ] int64Type
                        ("""{"kind":"resultGetOrDefault","result":""" + parameter "result" + ""","default":""" + parameter "fallback" + "}"))
            commit (create "okOr" [ "option", optionOf int64Type; "error", text ] parsed
                        ("""{"kind":"optionOkOr","option":""" + parameter "option" + ""","error":""" + parameter "error" + "}"))
            commit (create "okValue" [ "result", parsed ] (optionOf int64Type) (unary "resultOkValue" (parameter "result")))
            commit (create "errValue" [ "result", parsed ] (optionOf text) (unary "resultErrValue" (parameter "result")))
            commit (create "isSome" [ "option", optionOf int64Type ] boolType (unary "optionIsSome" (parameter "option")))
            commit (create "isOk" [ "result", parsed ] boolType (unary "resultIsOk" (parameter "result")))
            // Nested: Some None is Some, and ok_or keeps an Option item.
            commit (create "isSomeNested" [ "option", optionOf (optionOf int64Type) ] boolType (unary "optionIsSome" (parameter "option")))
            body call path
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private rejection (call: string -> JsonDocument) line =
        use response = call line
        let root = response.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.ToString())
        root.ToString()

    /// The snapshot with one node of the named function replaced, so a persisted graph can be mistyped.
    let private tamper (snapshot: Snapshot) functionName (replace: SemanticFunction -> SemanticNodeKind -> SemanticNodeKind option) =
        { snapshot with
            Functions =
                snapshot.Functions
                |> List.map (fun fn ->
                    if fn.DisplayName <> functionName then fn
                    else
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes =
                                        fn.Body.Nodes
                                        |> List.map (fun node ->
                                            match replace fn node.Kind with
                                            | Some kind -> { node with Kind = kind }
                                            | None -> node) } }) }

    [<Fact>]
    let ``Option and Result accessors type check, persist and lower through every layer`` () =
        withProgram "layers" (fun call path ->
            let parsed = resultOf int64Type text
            let reject name parameters resultType body expectedCode =
                Assert.Contains(expectedCode, rejection call (create name parameters resultType body))
            reject "badDefault" [ "result", parsed ] int64Type
                ("""{"kind":"resultGetOrDefault","result":""" + parameter "result" + ""","default":{"kind":"stringLiteral","value":"x"}}""") "PureExpressionOperandTypeMismatch"
            reject "notResult" [ "option", optionOf int64Type; "fallback", int64Type ] int64Type
                ("""{"kind":"resultGetOrDefault","result":""" + parameter "option" + ""","default":""" + parameter "fallback" + "}") "PureExpressionOperandTypeMismatch"
            reject "notOption" [ "result", parsed; "error", text ] parsed
                ("""{"kind":"optionOkOr","option":""" + parameter "result" + ""","error":""" + parameter "error" + "}") "PureExpressionOperandTypeMismatch"
            reject "okValueOfOption" [ "option", optionOf int64Type ] (optionOf int64Type) (unary "resultOkValue" (parameter "option")) "PureExpressionOperandTypeMismatch"
            reject "errValueOfInt" [ "value", int64Type ] (optionOf text) (unary "resultErrValue" (parameter "value")) "PureExpressionOperandTypeMismatch"
            reject "isSomeOfResult" [ "result", parsed ] boolType (unary "optionIsSome" (parameter "result")) "PureExpressionOperandTypeMismatch"
            reject "isOkOfOption" [ "option", optionOf int64Type ] boolType (unary "resultIsOk" (parameter "option")) "PureExpressionOperandTypeMismatch"
            // The declared result type must match what the accessor infers.
            reject "wrongResult" [ "option", optionOf int64Type; "error", text ] (resultOf int64Type int64Type)
                ("""{"kind":"optionOkOr","option":""" + parameter "option" + ""","error":""" + parameter "error" + "}") "FunctionCreationResultTypeMismatch"

            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap "serialize"
            Assert.Contains("\"schemaVersion\":19", json)
            for kind in [ "resultGetOrDefault"; "optionOkOr"; "resultOkValue"; "resultErrValue"; "optionIsSome"; "resultIsOk" ] do
                Assert.Contains("\"" + kind + "\"", json)
            let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
            for tag in [ "result-get-or-default"; "option-ok-or"; "result-ok-value"; "result-err-value"; "option-is-some"; "result-is-ok" ] do
                Assert.Contains(tag, nyason)
            Assert.Contains("nayamnyam.snapshot.v15", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap "serialize again")
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            let typedJson = TypedSemanticIrSerializer.serialize typed
            Assert.Contains("\"optionOkOr\"", typedJson)
            Assert.Contains("\"resultIsOk\"", typedJson)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains("\"resultGetOrDefault\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".IsOk ? ", source)
            Assert.Contains(".HasValue ? ", source)
            Assert.Contains(".HasValue;", source)
            Assert.Contains(".IsOk;", source)
            Assert.Contains(".Err(", source)

            // An older reader rejects the snapshot by its declared version.
            match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":19", "\"schemaVersion\":18")) with
            | Ok _ -> failwith "A schema 18 snapshot with Option and Result accessors decoded."
            | Error error -> Assert.Contains("Option and Result accessors", sprintf "%A" error)

            // A persisted graph reports its own input diagnostics.
            let codes tampered = SnapshotValidator.validate tampered |> List.map (fun issue -> issue.Code)
            let literal fn kind =
                match kind with
                | SemanticNodeKind.ParameterReference parameterId when parameterId = (fn: SemanticFunction).Parameters.Head.Id -> Some(SemanticNodeKind.Int64Literal 1L)
                | _ -> None
            Assert.Contains("ResultInputTypeMismatch", codes (tamper snapshot "isOk" literal))
            Assert.Contains("ResultInputTypeMismatch", codes (tamper snapshot "okValue" literal))
            Assert.Contains("ResultInputTypeMismatch", codes (tamper snapshot "getOr" literal))
            Assert.Contains("OptionInputTypeMismatch", codes (tamper snapshot "isSome" literal))
            Assert.Contains("OptionInputTypeMismatch", codes (tamper snapshot "okOr" literal))
            let textDefault (fn: SemanticFunction) kind =
                match kind with
                | SemanticNodeKind.ParameterReference parameterId when parameterId = fn.Parameters[1].Id -> Some(SemanticNodeKind.StringLiteral "x")
                | _ -> None
            Assert.Contains("ResultDefaultTypeMismatch", codes (tamper snapshot "getOr" textDefault)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``each accessor follows Rust's method of the same name under runPure`` () =
        withProgram "run" (fun call _ ->
            let intOption (value: int option) =
                match value with
                | Some i -> """{"kind":"option","itemKind":"int64","hasValue":true,"value":""" + "\"" + string i + "\"}"
                | None -> """{"kind":"option","itemKind":"int64","hasValue":false}"""
            let textOption (value: string option) =
                match value with
                | Some s -> """{"kind":"option","itemKind":"string","hasValue":true,"value":""" + "\"" + s + "\"}"
                | None -> """{"kind":"option","itemKind":"string","hasValue":false}"""
            let nested (value: int option option) =
                match value with
                | Some inner -> """{"kind":"option","itemType":""" + optionOf int64Type + ""","hasValue":true,"value":""" + intOption inner + "}"
                | None -> """{"kind":"option","itemType":""" + optionOf int64Type + ""","hasValue":false}"""
            let ok (value: int) = """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":""" + string value + "}"
            let err (value: string) = """{"kind":"result","okKind":"int64","errorKind":"string","isOk":false,"value":""" + "\"" + value + "\"}"
            let int64 (value: int) = """{"kind":"int64","value":""" + "\"" + string value + "\"}"
            let str (value: string) = """{"kind":"string","value":""" + "\"" + value + "\"}"
            let bool (value: bool) = """{"kind":"bool","value":""" + (if value then "true" else "false") + "}"
            let run fn arguments expected =
                use result = call ("""{"id":"run","op":"runPureByName","function":""" + "\"" + fn + "\"" + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
                let root = result.RootElement
                Assert.True(root.GetProperty("ok").GetBoolean() && root.GetProperty("result").GetProperty("matches").GetBoolean(), fn + ": " + root.ToString())
            // unwrap_or: the Ok value, or the default for Err (the error is dropped).
            run "getOr" [ ok 4; int64 9 ] (int64 4)
            run "getOr" [ err "bad"; int64 9 ] (int64 9)
            // ok_or: Some becomes Ok, None becomes Err of the error operand.
            run "okOr" [ intOption (Some 4); str "missing" ] (ok 4)
            run "okOr" [ intOption None; str "missing" ] (err "missing")
            // ok and err: each case as an Option, the other case None.
            run "okValue" [ ok 4 ] (intOption (Some 4))
            run "okValue" [ err "bad" ] (intOption None)
            run "errValue" [ err "bad" ] (textOption (Some "bad"))
            run "errValue" [ ok 4 ] (textOption None)
            // is_some and is_ok.
            run "isSome" [ intOption (Some 0) ] (bool true)
            run "isSome" [ intOption None ] (bool false)
            run "isOk" [ ok 0 ] (bool true)
            run "isOk" [ err "" ] (bool false)
            // Some None is Some.
            run "isSomeNested" [ nested (Some None) ] (bool true)
            run "isSomeNested" [ nested None ] (bool false))
