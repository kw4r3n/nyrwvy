namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Set<T> (post-M2 order 46; union, intersection and difference order 47): equatable items compared with ValueEqual, insertion order, immutable
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
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{signature parameters}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private simulateCode session parameters resultType body =
        rejectionCode
            session
            $"""{{"id":"bad","op":"simulateCreateFunctionByName","exported":true,"displayName":"bad","parameters":[{signature parameters}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private text (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""
    let private strings (values: string list) =
        $"""{{"kind":"list","itemKind":"string","items":[{values |> List.map JsonSerializer.Serialize |> String.concat ","}]}}"""
    let private set (values: string list) =
        $"""{{"kind":"set","itemType":{str},"items":[{values |> List.map text |> String.concat ","}]}}"""

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    [<Fact; Trait("Suite", "Extended")>]
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
            let json = SnapshotCodec.serialize snapshot |> Result.defaultWith (failwithf "reserialize: %A")
            let restored = SnapshotCodec.deserialize json |> Result.defaultWith (failwithf "decode: %A")
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
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "s", words; "l", listOf str ] (primitive "bool") $"""{{"kind":"valueEqual","left":{parameter "s"},"right":{parameter "l"}}}"""
            )
            define session "size" [ "s", words ] (primitive "int64") $"""{{"kind":"setSize","value":{parameter "s"}}}"""
            Assert.Equal("RunContainerTypeMismatch", rejectionCode session $"""{{"id":"run","op":"runPureByName","function":"size","arguments":[{set [ "a"; "a" ]}]}}"""))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Set union, intersection and difference keep the left order`` () =
        withSession (fun session path ->
            let l, r = parameter "l", parameter "r"
            define session "union" [ "l", words; "r", words ] words $"""{{"kind":"setUnion","left":{l},"right":{r}}}"""
            define session "intersection" [ "l", words; "r", words ] words $"""{{"kind":"setIntersection","left":{l},"right":{r}}}"""
            define session "difference" [ "l", words; "r", words ] words $"""{{"kind":"setDifference","left":{l},"right":{r}}}"""
            define session "differenceThenAdd" [ "l", words; "r", words ] words $"""{{"kind":"setInsert","set":{{"kind":"setDifference","left":{l},"right":{r}}},"item":{{"kind":"stringLiteral","value":"z"}}}}"""

            let left, right = set [ "c"; "a"; "b" ], set [ "d"; "b"; "e"; "c" ]
            expectRun session "union" [ left; right ] (set [ "c"; "a"; "b"; "d"; "e" ])
            expectRun session "union" [ right; left ] (set [ "d"; "b"; "e"; "c"; "a" ])
            expectRun session "intersection" [ left; right ] (set [ "c"; "b" ])
            expectRun session "intersection" [ right; left ] (set [ "b"; "c" ])
            expectRun session "difference" [ left; right ] (set [ "a" ])
            expectRun session "difference" [ right; left ] (set [ "d"; "e" ])
            expectRun session "union" [ set []; set [] ] (set [])
            expectRun session "intersection" [ left; set [] ] (set [])
            expectRun session "difference" [ left; set [] ] left
            // Items removed by the difference leave no gap: an insert still goes last.
            expectRun session "differenceThenAdd" [ left; right ] (set [ "a"; "z" ])
            // A smaller right set is removed item by item (order 53); the result and its order are the same.
            expectRun session "differenceThenAdd" [ right; left ] (set [ "d"; "e"; "z" ])
            let big = set [ for index in 1 .. 3000 -> $"w{index}" ]
            let odd = set [ for index in 1 .. 3000 do if index % 2 = 1 then $"w{index}" ]
            let even = set [ for index in 1 .. 3000 do if index % 2 = 0 then $"w{index}" ]
            expectRun session "difference" [ big; odd ] even
            expectRun session "intersection" [ big; even ] even
            expectRun session "union" [ odd; even ] (set ([ for index in 1 .. 3000 do if index % 2 = 1 then $"w{index}" ] @ [ for index in 1 .. 3000 do if index % 2 = 0 then $"w{index}" ]))

            let snapshot =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok snapshot -> snapshot
                | Error error -> failwithf "Workspace did not decode: %A" error
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> Result.defaultWith (failwithf "reserialize: %A")
            let restored = SnapshotCodec.deserialize json |> Result.defaultWith (failwithf "decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)

            // Both operands must be sets of the same item type.
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", words; "r", setOf (primitive "int64") ] words $"""{{"kind":"setUnion","left":{l},"right":{r}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", listOf str; "r", words ] words $"""{{"kind":"setDifference","left":{l},"right":{r}}}"""
            ))
