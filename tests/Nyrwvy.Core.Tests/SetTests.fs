namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Set<T> (post-M2 order 46): equatable items compared with ValueEqual, insertion order, immutable
/// values, over the maintained JSONL protocol and runPure.
module SetTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private rejectionCode session line =
        use reply = execute session line
        let root = reply.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.GetRawText())
        let error = root.GetProperty("error")
        let mutable details = Unchecked.defaultof<JsonElement>
        let mutable diagnostics = Unchecked.defaultof<JsonElement>
        if error.TryGetProperty("details", &details) && details.ValueKind = JsonValueKind.Object
           && details.TryGetProperty("diagnostics", &diagnostics) && diagnostics.GetArrayLength() > 0 then
            diagnostics[0].GetProperty("code").GetString()
        else error.GetProperty("code").GetString()

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private str = primitive "string"
    let private setOf item = $"""{{"kind":"builtinGeneric","generic":"set","arguments":[{item}]}}"""
    let private listOf item = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{item}]}}"""
    let private words = setOf str
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-set-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            run session path
        finally
            Directory.Delete(root, true)

    let private signature (parameters: (string * string) list) =
        parameters |> List.map (fun (name, typeRef) -> $"""{{"displayName":"{name}","type":{typeRef}}}""") |> String.concat ","

    let private define session name parameters resultType body =
        publish
            session
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","displayName":"{name}","parameters":[{signature parameters}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private simulateCode session parameters resultType body =
        rejectionCode
            session
            $"""{{"id":"bad","op":"simulateCreateFunctionByName","displayName":"bad","parameters":[{signature parameters}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private text (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""
    let private strings (values: string list) =
        $"""{{"kind":"list","itemKind":"string","items":[{values |> List.map JsonSerializer.Serialize |> String.concat ","}]}}"""
    let private set (values: string list) =
        $"""{{"kind":"set","itemType":{str},"items":[{values |> List.map text |> String.concat ","}]}}"""

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    [<Fact>]
    let ``Set operations keep first-insertion order and ignore repeated items`` () =
        withSession (fun session path ->
            let s, x = parameter "s", parameter "x"
            define session "fromList" [ "items", listOf str ] words $"""{{"kind":"setFromList","value":{parameter "items"}}}"""
            define session "toList" [ "s", words ] (listOf str) $"""{{"kind":"setToList","value":{s}}}"""
            define session "size" [ "s", words ] (primitive "int64") $"""{{"kind":"setSize","value":{s}}}"""
            define session "has" [ "s", words; "x", str ] (primitive "bool") $"""{{"kind":"setContains","set":{s},"item":{x}}}"""
            define session "add" [ "s", words; "x", str ] words $"""{{"kind":"setInsert","set":{s},"item":{x}}}"""
            define session "drop" [ "s", words; "x", str ] words $"""{{"kind":"setRemove","set":{s},"item":{x}}}"""
            define session "readd" [ "s", words; "x", str ] words $"""{{"kind":"setInsert","set":{{"kind":"setRemove","set":{s},"item":{x}}},"item":{x}}}"""
            define session "empty" [] words $"""{{"kind":"setEmpty","itemType":{str}}}"""

            expectRun session "fromList" [ strings [ "b"; "a"; "b"; "c"; "a" ] ] (set [ "b"; "a"; "c" ])
            expectRun session "toList" [ set [ "b"; "a"; "c" ] ] (strings [ "b"; "a"; "c" ])
            expectRun session "size" [ set [ "b"; "a"; "c" ] ] """{"kind":"int64","value":"3"}"""
            expectRun session "has" [ set [ "b"; "a" ]; text "a" ] """{"kind":"bool","value":true}"""
            expectRun session "has" [ set [ "b"; "a" ]; text "z" ] """{"kind":"bool","value":false}"""
            expectRun session "add" [ set [ "b"; "a" ]; text "a" ] (set [ "b"; "a" ])
            expectRun session "add" [ set [ "b"; "a" ]; text "c" ] (set [ "b"; "a"; "c" ])
            expectRun session "drop" [ set [ "b"; "a"; "c" ]; text "b" ] (set [ "a"; "c" ])
            expectRun session "drop" [ set [ "b"; "a" ]; text "z" ] (set [ "b"; "a" ])
            expectRun session "readd" [ set [ "b"; "a"; "c" ]; text "b" ] (set [ "a"; "c"; "b" ])
            expectRun session "empty" [] (set [])
            let many = [ for index in 1 .. 4000 -> $"w{index % 2000}" ]
            expectRun session "size" [ set [ for index in 1 .. 2000 -> $"w{index}" ] ] """{"kind":"int64","value":"2000"}"""
            expectRun session "toList" [ set [ for index in 1 .. 3 -> $"w{index}" ] ] (strings [ "w1"; "w2"; "w3" ])
            expectRun session "fromList" [ strings many ] (set [ for index in 1 .. 2000 -> $"w{index % 2000}" ])

            let snapshot =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok snapshot -> snapshot
                | Error error -> failwithf "Workspace did not decode: %A" error
            Assert.Empty(SnapshotValidator.validate snapshot)
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("set-from-list", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored))

    [<Fact>]
    let ``Set items must be equatable and operands must match the set`` () =
        withSession (fun session _ ->
            Assert.Equal("SetItemTypeNotEquatable", simulateCode session [] (setOf (primitive "float64")) $"""{{"kind":"setEmpty","itemType":{primitive "float64"}}}""")
            Assert.Equal(
                "SetItemTypeNotEquatable",
                simulateCode session [ "items", listOf (primitive "float64") ] (setOf (primitive "float64")) $"""{{"kind":"setFromList","value":{parameter "items"}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "s", words ] (primitive "bool") $"""{{"kind":"setContains","set":{parameter "s"},"item":{{"kind":"int64Literal","value":1}}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", listOf str ] (primitive "int64") $"""{{"kind":"setSize","value":{parameter "l"}}}"""
            )
            Assert.Equal(
                "ValueEqualTypeNotEquatable",
                simulateCode session [ "s", words ] (primitive "bool") $"""{{"kind":"valueEqual","left":{parameter "s"},"right":{parameter "s"}}}"""
            )
            define session "size" [ "s", words ] (primitive "int64") $"""{{"kind":"setSize","value":{parameter "s"}}}"""
            Assert.Equal("RunContainerTypeMismatch", rejectionCode session $"""{{"id":"run","op":"runPureByName","function":"size","arguments":[{set [ "a"; "a" ]}]}}"""))
