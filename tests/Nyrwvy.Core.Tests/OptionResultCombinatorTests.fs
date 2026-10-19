namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// OptionGetOrDefault, OptionMap, OptionAndThen, ResultMap, ResultMapErr and ResultAndThen
/// (post-M2 order 64), with the semantics of Rust's methods of the same names: a callback runs
/// only for the matching case, and OptionGetOrDefault evaluates its default either way.
module OptionResultCombinatorTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("option-result-" + name)
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
    let private amountContext = """[{"parameter":"amount","value":{"kind":"parameter","name":"amount"}}]"""
    let private limitContext = """[{"parameter":"limit","value":{"kind":"parameter","name":"limit"}}]"""

    let private create name parameters resultType body =
        """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
        + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
        + """],"resultType":""" + resultType + ""","requiredEffects":[],"body":""" + body + "}"

    let private combinator kind sourceField source functionName context =
        "{\"kind\":\"" + kind + "\",\"" + sourceField + "\":" + source + ",\"function\":\"" + functionName + "\""
        + (match context with Some c -> ",\"context\":" + c | None -> "") + "}"

    /// Authors the callbacks and one function per combinator in a fresh session and passes `call`
    /// and the snapshot path on.
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
            let intResult = resultOf int64Type int64Type
            // Callbacks.
            commit (create "addTo" [ "value", int64Type; "amount", int64Type ] int64Type
                        """{"kind":"int64WrappingAdd","left":{"kind":"parameter","name":"value"},"right":{"kind":"parameter","name":"amount"}}""")
            commit (create "parse" [ "value", text ] (optionOf int64Type) """{"kind":"stringParseInt64","value":{"kind":"parameter","name":"value"}}""")
            commit (create "textLength" [ "value", text ] int64Type """{"kind":"stringLength","value":{"kind":"parameter","name":"value"}}""")
            commit (create "belowLimit" [ "value", int64Type; "limit", int64Type ] intResult
                        ("""{"kind":"conditional","condition":{"kind":"int64LessThan","left":{"kind":"parameter","name":"value"},"right":{"kind":"parameter","name":"limit"}},"whenTrue":{"kind":"resultOk","errorType":""" + int64Type
                         + ""","value":{"kind":"parameter","name":"value"}},"whenFalse":{"kind":"resultErr","okType":""" + int64Type + ""","error":{"kind":"parameter","name":"limit"}}}"""))
            // A function without declared effects that checks cancellation is not pure.
            commit (create "ambient" [ "value", int64Type ] boolType """{"kind":"cancellationRequested"}""")
            // One function per combinator.
            commit (create "optMap" [ "option", optionOf int64Type; "amount", int64Type ] (optionOf int64Type)
                        (combinator "optionMap" "option" (parameter "option") "addTo" (Some amountContext)))
            commit (create "optAndThen" [ "option", optionOf text ] (optionOf int64Type)
                        (combinator "optionAndThen" "option" (parameter "option") "parse" None))
            commit (create "resMap" [ "result", intResult; "amount", int64Type ] intResult
                        (combinator "resultMap" "result" (parameter "result") "addTo" (Some amountContext)))
            commit (create "resMapErr" [ "result", intResult; "amount", int64Type ] intResult
                        (combinator "resultMapErr" "result" (parameter "result") "addTo" (Some amountContext)))
            commit (create "resMapErrLength" [ "result", resultOf int64Type text ] intResult
                        (combinator "resultMapErr" "result" (parameter "result") "textLength" None))
            commit (create "resAndThen" [ "result", intResult; "limit", int64Type ] intResult
                        (combinator "resultAndThen" "result" (parameter "result") "belowLimit" (Some limitContext)))
            commit (create "getOr" [ "option", optionOf int64Type; "fallback", int64Type ] int64Type
                        """{"kind":"optionGetOrDefault","option":{"kind":"parameter","name":"option"},"default":{"kind":"parameter","name":"fallback"}}""")
            commit (create "getOrNested" [ "option", optionOf (optionOf int64Type); "fallback", optionOf int64Type ] (optionOf int64Type)
                        """{"kind":"optionGetOrDefault","option":{"kind":"parameter","name":"option"},"default":{"kind":"parameter","name":"fallback"}}""")
            body call path
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private rejection (call: string -> JsonDocument) line =
        use response = call line
        let root = response.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.ToString())
        root.ToString()

    [<Fact>]
    let ``Option and Result combinators type check, persist and lower through every layer`` () =
        withProgram "layers" (fun call path ->
            let intOption = optionOf int64Type
            let reject name parameters resultType body expectedCode =
                Assert.Contains(expectedCode, rejection call (create name parameters resultType body))
            reject "badSource" [ "value", int64Type; "amount", int64Type ] intOption
                (combinator "optionMap" "option" (parameter "value") "addTo" (Some amountContext)) "PureExpressionOperandTypeMismatch"
            reject "badResultSource" [ "value", intOption ] intOption
                (combinator "resultMap" "result" (parameter "value") "parse" None) "PureExpressionOperandTypeMismatch"
            reject "badInput" [ "option", optionOf text; "amount", int64Type ] intOption
                (combinator "optionMap" "option" (parameter "option") "addTo" (Some amountContext)) "PureExpressionCallbackInputTypeMismatch"
            reject "badErrInput" [ "result", resultOf int64Type int64Type ] (resultOf int64Type int64Type)
                (combinator "resultMapErr" "result" (parameter "result") "textLength" None) "PureExpressionCallbackInputTypeMismatch"
            reject "badArity" [ "option", intOption ] intOption
                (combinator "optionMap" "option" (parameter "option") "addTo" None) "PureExpressionCallbackArityMismatch"
            reject "impureMap" [ "option", intOption ] (optionOf boolType)
                (combinator "optionMap" "option" (parameter "option") "ambient" None) "PureExpressionCallbackEffectsNotAllowed"
            reject "impureResultMap" [ "result", resultOf int64Type text ] (resultOf boolType text)
                (combinator "resultMap" "result" (parameter "result") "ambient" None) "PureExpressionCallbackEffectsNotAllowed"
            reject "notOption" [ "option", intOption; "amount", int64Type ] int64Type
                (combinator "optionAndThen" "option" (parameter "option") "addTo" (Some amountContext)) "PureExpressionCallbackResultTypeMismatch"
            reject "otherError" [ "result", resultOf int64Type text; "limit", int64Type ] (resultOf int64Type int64Type)
                (combinator "resultAndThen" "result" (parameter "result") "belowLimit" (Some limitContext)) "PureExpressionCallbackResultTypeMismatch"
            reject "badDefault" [ "option", intOption ] int64Type
                """{"kind":"optionGetOrDefault","option":{"kind":"parameter","name":"option"},"default":{"kind":"stringLiteral","value":"x"}}""" "PureExpressionOperandTypeMismatch"
            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap "serialize"
            Assert.Contains("\"schemaVersion\":17", json)
            for kind in [ "optionGetOrDefault"; "optionMap"; "optionAndThen"; "resultMap"; "resultMapErr"; "resultAndThen" ] do
                Assert.Contains("\"" + kind + "\"", json)
            let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
            for tag in [ "option-get-or-default"; "option-map"; "option-and-then"; "result-map"; "result-map-err"; "result-and-then" ] do
                Assert.Contains(tag, nyason)
            Assert.Contains("nayamnyam.snapshot.v13", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let execution = TypedSemanticIr.project restored |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".HasValue ? ", source)
            Assert.Contains(".IsOk ? ", source)

            // A persisted graph reports the callback codes the List callbacks use (context bindings that
            // no longer fit the new callback add their own issues).
            let functionId name = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = name) |> fun fn -> StableId.value fn.Id
            let withCallback kind field fromName toName =
                let pattern = "(\"kind\":\"" + kind + "\",\"[a-zA-Z]+\":\"[^\"]+\",\"" + field + "\":\")" + Regex.Escape(functionId fromName) + "\""
                let changed = Regex.Replace(json, pattern, (fun (m: Match) -> m.Groups[1].Value + functionId toName + "\""))
                Assert.NotEqual<string>(json, changed)
                // Reading validates, so the issues arrive as the read error.
                match SnapshotCodec.deserialize changed with
                | Ok tampered -> sprintf "%A" (SnapshotValidator.validate tampered)
                | Error error -> sprintf "%A" error
            Assert.Contains("CallbackParameterTypeMismatch", withCallback "optionMap" "mapperFunctionId" "addTo" "textLength")
            Assert.Contains("CallbackEffectsNotAllowed", withCallback "resultMap" "mapperFunctionId" "addTo" "ambient")
            Assert.Contains("CallbackResultTypeMismatch", withCallback "optionAndThen" "binderFunctionId" "parse" "textLength")
            Assert.Contains("CallbackArityMismatch", withCallback "resultAndThen" "binderFunctionId" "belowLimit" "addTo"))

    [<Fact; Trait("Suite", "Extended")>]
    let ``each combinator handles both cases and runs its callback only for the matching one under runPure`` () =
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
            let ok (value: int) = """{"kind":"result","okKind":"int64","errorKind":"int64","isOk":true,"value":""" + string value + "}"
            let err (value: int) = """{"kind":"result","okKind":"int64","errorKind":"int64","isOk":false,"value":""" + string value + "}"
            let errText (value: string) = """{"kind":"result","okKind":"int64","errorKind":"string","isOk":false,"value":""" + "\"" + value + "\"}"
            let okOfText (value: int) = """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":""" + string value + "}"
            let int64 (value: int) = """{"kind":"int64","value":""" + "\"" + string value + "\"}"
            let run fn arguments expected =
                use result = call ("""{"id":"run","op":"runPureByName","function":""" + "\"" + fn + "\"" + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
                let root = result.RootElement
                Assert.True(root.GetProperty("ok").GetBoolean() && root.GetProperty("result").GetProperty("matches").GetBoolean(), fn + ": " + root.ToString())
            // OptionMap: the context value reaches the callback; None stays None.
            run "optMap" [ intOption (Some 5); int64 100 ] (intOption (Some 105))
            run "optMap" [ intOption None; int64 100 ] (intOption None)
            // OptionAndThen: the binder's own None is the result.
            run "optAndThen" [ textOption (Some "42") ] (intOption (Some 42))
            run "optAndThen" [ textOption (Some "x") ] (intOption None)
            run "optAndThen" [ textOption None ] (intOption None)
            // ResultMap and ResultMapErr over Result<Int64, Int64>: mapping the other case would add 100.
            run "resMap" [ ok 5; int64 100 ] (ok 105)
            run "resMap" [ err 5; int64 100 ] (err 5)
            run "resMapErr" [ err 5; int64 100 ] (err 105)
            run "resMapErr" [ ok 5; int64 100 ] (ok 5)
            // ResultMapErr changes the error type.
            run "resMapErrLength" [ errText "abc" ] (err 3)
            run "resMapErrLength" [ okOfText 7 ] (ok 7)
            // ResultAndThen: the binder may fail; an Err input is kept even when the binder would give Err 0.
            run "resAndThen" [ ok 3; int64 10 ] (ok 3)
            run "resAndThen" [ ok 30; int64 10 ] (err 10)
            run "resAndThen" [ err 5; int64 0 ] (err 5)
            // OptionGetOrDefault, also with an Option item: Some None is None, not the default.
            run "getOr" [ intOption (Some 4); int64 9 ] (int64 4)
            run "getOr" [ intOption None; int64 9 ] (int64 9)
            run "getOrNested" [ nested (Some None); intOption (Some 7) ] (intOption None)
            run "getOrNested" [ nested (Some(Some 1)); intOption (Some 7) ] (intOption (Some 1))
            run "getOrNested" [ nested None; intOption (Some 7) ] (intOption (Some 7)))
