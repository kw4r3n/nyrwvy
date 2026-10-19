namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Map<K, V> (post-M2 orders 44 and 45): keys of any equatable type compared with ValueEqual, entries in
/// insertion order, immutable values, over the maintained JSONL protocol and runPure.
module MapTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    /// The error code of a rejected request, or of the first diagnostic that rejected a simulation.
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
    let private int64Type = primitive "int64"
    let private mapOf key value = $"""{{"kind":"builtinGeneric","generic":"map","arguments":[{key},{value}]}}"""
    let private listOf item = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{item}]}}"""
    let private optionOf item = $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{item}]}}"""
    let private wordCounts = mapOf str int64Type

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-map-" + Guid.NewGuid().ToString("N"))
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

    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""

    let private define session name (parameters: (string * string) list) resultType body =
        let parameterList =
            parameters |> List.map (fun (parameterName, parameterType) -> $"""{{"displayName":"{parameterName}","type":{parameterType}}}""") |> String.concat ","
        publish
            session
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameterList}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private simulateCode session (parameters: (string * string) list) resultType body =
        let parameterList =
            parameters |> List.map (fun (parameterName, parameterType) -> $"""{{"displayName":"{parameterName}","type":{parameterType}}}""") |> String.concat ","
        rejectionCode
            session
            $"""{{"id":"bad","op":"simulateCreateFunctionByName","exported":true,"displayName":"bad","parameters":[{parameterList}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private text (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""
    let private int64 (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private strings (values: string list) =
        $"""{{"kind":"list","itemKind":"string","items":[{values |> List.map JsonSerializer.Serialize |> String.concat ","}]}}"""
    let private counts (entries: (string * int64) list) =
        let items = entries |> List.map (fun (key, value) -> $"""{{"key":{text key},"value":{int64 value}}}""") |> String.concat ","
        $"""{{"kind":"map","keyType":{str},"valueType":{int64Type},"entries":[{items}]}}"""

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    let private readSnapshot path =
        match SnapshotCodec.deserialize (File.ReadAllText path) with
        | Ok snapshot -> snapshot
        | Error error -> failwithf "Workspace did not decode: %A" error

    [<Fact; Trait("Suite", "Extended")>]
    let ``Map operations keep insertion order, replace in place and move a re-inserted key to the end`` () =
        withSession (fun session path ->
            let m = parameter "m"
            let k = parameter "k"
            define session "size" [ "m", wordCounts ] int64Type $"""{{"kind":"mapSize","value":{m}}}"""
            define session "keys" [ "m", wordCounts ] (listOf str) $"""{{"kind":"mapKeys","value":{m}}}"""
            define session "values" [ "m", wordCounts ] (listOf int64Type) $"""{{"kind":"mapValues","value":{m}}}"""
            define session "get" [ "m", wordCounts; "k", str ] (optionOf int64Type) $"""{{"kind":"mapGet","map":{m},"key":{k}}}"""
            define session "has" [ "m", wordCounts; "k", str ] (primitive "bool") $"""{{"kind":"mapContainsKey","map":{m},"key":{k}}}"""
            define session "remove" [ "m", wordCounts; "k", str ] wordCounts $"""{{"kind":"mapRemove","map":{m},"key":{k}}}"""
            define session "put" [ "m", wordCounts; "k", str; "v", int64Type ] wordCounts $"""{{"kind":"mapInsert","map":{m},"key":{k},"value":{parameter "v"}}}"""
            // Remove then insert again: the key moves to the end.
            define
                session
                "reinsert"
                [ "m", wordCounts; "k", str; "v", int64Type ]
                wordCounts
                $"""{{"kind":"mapInsert","map":{{"kind":"mapRemove","map":{m},"key":{k}}},"key":{k},"value":{parameter "v"}}}"""
            define
                session
                "empty"
                []
                wordCounts
                $"""{{"kind":"mapEmpty","keyType":{str},"valueType":{int64Type}}}"""

            let abc = counts [ "a", 1L; "b", 2L; "c", 3L ]
            expectRun session "size" [ abc ] (int64 3L)
            expectRun session "keys" [ abc ] (strings [ "a"; "b"; "c" ])
            expectRun session "values" [ abc ] """{"kind":"list","itemKind":"int64","items":[1,2,3]}"""
            expectRun session "get" [ abc; text "b" ] """{"kind":"option","itemKind":"int64","hasValue":true,"value":2}"""
            expectRun session "get" [ abc; text "z" ] """{"kind":"option","itemKind":"int64","hasValue":false}"""
            expectRun session "has" [ abc; text "c" ] """{"kind":"bool","value":true}"""
            expectRun session "has" [ abc; text "" ] """{"kind":"bool","value":false}"""
            expectRun session "put" [ abc; text "b"; int64 20L ] (counts [ "a", 1L; "b", 20L; "c", 3L ])
            expectRun session "put" [ abc; text "d"; int64 4L ] (counts [ "a", 1L; "b", 2L; "c", 3L; "d", 4L ])
            expectRun session "remove" [ abc; text "a" ] (counts [ "b", 2L; "c", 3L ])
            expectRun session "remove" [ abc; text "z" ] abc
            expectRun session "reinsert" [ abc; text "a"; int64 9L ] (counts [ "b", 2L; "c", 3L; "a", 9L ])
            expectRun session "empty" [] (counts [])

            let snapshot = readSnapshot path
            Assert.Empty(SnapshotValidator.validate snapshot)
            Assert.Contains("\"mapInsert\"", File.ReadAllText path)
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("map-insert", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored))

    [<Fact>]
    let ``A word count built with ListFold keeps first-occurrence order and accepts Product keys`` () =
        withSession (fun session _ ->
            let acc, word = parameter "acc", parameter "word"
            let previous =
                $"""{{"kind":"matchOption","input":{{"kind":"mapGet","map":{acc},"key":{word}}},"someBody":{{"kind":"patternReference","scopeDepth":0,"slot":"value"}},"noneBody":{{"kind":"int64Literal","value":0}}}}"""
            define
                session
                "countStep"
                [ "acc", wordCounts; "word", str ]
                wordCounts
                $"""{{"kind":"mapInsert","map":{acc},"key":{word},"value":{{"kind":"int64WrappingAdd","left":{previous},"right":{{"kind":"int64Literal","value":1}}}}}}"""
            define
                session
                "countWords"
                [ "words", listOf str ]
                wordCounts
                $"""{{"kind":"listFold","source":{parameter "words"},"seed":{{"kind":"mapEmpty","keyType":{str},"valueType":{int64Type}}},"function":"countStep"}}"""
            expectRun session "countWords" [ strings [ "b"; "a"; "b"; "c"; "a"; "b" ] ] (counts [ "b", 3L; "a", 2L; "c", 1L ])
            // 3,000 distinct words: a fold that would be quadratic on lists stays fast.
            let many = [ for index in 1 .. 3000 -> $"w{index % 1500}" ]
            expectRun session "countWords" [ strings many ] (counts [ for index in 1 .. 1500 -> $"w{index % 1500}", 2L ])

            publish
                session
                $"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"Part","typeKind":{{"kind":"product","fields":[{{"displayName":"message","type":{str}}},{{"displayName":"index","type":{int64Type}}}]}}}}"""
            let part = """{"kind":"named","name":"Part"}"""
            let partValue (message: string) (index: int64) =
                $"""{{"kind":"product","type":"Part","fields":[{{"field":"message","value":{text message}}},{{"field":"index","value":{int64 index}}}]}}"""
            define
                session
                "byPart"
                [ "m", mapOf part str; "p", part ]
                (optionOf str)
                $"""{{"kind":"mapGet","map":{parameter "m"},"key":{parameter "p"}}}"""
            let partMap =
                $"""{{"kind":"map","keyType":{part},"valueType":{str},"entries":[{{"key":{partValue "m1" 0L},"value":{text "first"}}},{{"key":{partValue "m1" 1L},"value":{text "second"}}}]}}"""
            expectRun session "byPart" [ partMap; partValue "m1" 1L ] """{"kind":"option","itemKind":"string","hasValue":true,"value":"second"}"""
            // The same Product key with its fields listed in the other order is a duplicate key.
            let swapped = $"""{{"kind":"product","type":"Part","fields":[{{"field":"index","value":{int64 0L}}},{{"field":"message","value":{text "m1"}}}]}}"""
            let duplicate =
                $"""{{"kind":"map","keyType":{part},"valueType":{str},"entries":[{{"key":{partValue "m1" 0L},"value":{text "a"}}},{{"key":{swapped},"value":{text "b"}}}]}}"""
            Assert.Equal(
                "RunContainerTypeMismatch",
                rejectionCode session $"""{{"id":"run","op":"runPureByName","function":"byPart","arguments":[{duplicate},{partValue "m1" 0L}]}}"""
            ))

    [<Fact>]
    let ``Map key types must be equatable and operands must match the map`` () =
        withSession (fun session _ ->
            let float64Map = $"""{{"kind":"mapEmpty","keyType":{primitive "float64"},"valueType":{str}}}"""
            Assert.Equal("MapKeyTypeNotEquatable", simulateCode session [] (mapOf (primitive "float64") str) float64Map)
            // A map is equatable when its values are, so it can be a key only with equatable values.
            let floatValued = mapOf str (primitive "float64")
            let nestedMapKey = $"""{{"kind":"mapEmpty","keyType":{floatValued},"valueType":{str}}}"""
            Assert.Equal("MapKeyTypeNotEquatable", simulateCode session [] (mapOf floatValued str) nestedMapKey)
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "m", wordCounts ] (optionOf int64Type) $"""{{"kind":"mapGet","map":{parameter "m"},"key":{{"kind":"int64Literal","value":1}}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "m", wordCounts ] wordCounts $"""{{"kind":"mapInsert","map":{parameter "m"},"key":{{"kind":"stringLiteral","value":"x"}},"value":{{"kind":"stringLiteral","value":"y"}}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", listOf str ] int64Type $"""{{"kind":"mapSize","value":{parameter "l"}}}"""
            )
            Assert.Equal(
                "ValueEqualTypeNotEquatable",
                simulateCode session [ "m", floatValued ] (primitive "bool") $"""{{"kind":"valueEqual","left":{parameter "m"},"right":{parameter "m"}}}"""
            )
            // A key of the wrong type and a duplicate key in a run value are rejected before running.
            let mistyped = $"""{{"kind":"map","keyType":{str},"valueType":{int64Type},"entries":[{{"key":{int64 1L},"value":{int64 2L}}}]}}"""
            define session "size" [ "m", wordCounts ] int64Type $"""{{"kind":"mapSize","value":{parameter "m"}}}"""
            Assert.Equal(
                "ProtocolError",
                rejectionCode session $"""{{"id":"run","op":"runPureByName","function":"size","arguments":[{counts [ "a", 1L; "a", 2L ]}]}}"""
            )
            Assert.Equal(
                "RunContainerTypeMismatch",
                rejectionCode session $"""{{"id":"run","op":"runPureByName","function":"size","arguments":[{mistyped}]}}"""
            ))

    [<Fact; Trait("Suite", "Extended")>]
    let ``MapFold walks entries in insertion order and MapFromList keeps the first position and the last value`` () =
        withSession (fun session path ->
            let s, k, v = parameter "s", parameter "k", parameter "v"
            // s + k + "=" + toString(v) + ";"
            let piece =
                $"""{{"kind":"stringConcat","left":{{"kind":"stringConcat","left":{{"kind":"stringConcat","left":{s},"right":{k}}},"right":{{"kind":"stringLiteral","value":"="}}}},"right":{{"kind":"stringConcat","left":{{"kind":"int64ToString","value":{v}}},"right":{{"kind":"stringLiteral","value":";"}}}}}}"""
            define session "render" [ "s", str; "k", str; "v", int64Type ] str piece
            define
                session
                "dump"
                [ "m", wordCounts ]
                str
                $"""{{"kind":"mapFold","map":{parameter "m"},"seed":{{"kind":"stringLiteral","value":""}},"function":"render"}}"""
            expectRun session "dump" [ counts [ "b", 2L; "a", 1L; "c", 3L ] ] (text "b=2;a=1;c=3;")
            expectRun session "dump" [ counts [] ] (text "")

            publish
                session
                $"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"Row","typeKind":{{"kind":"product","fields":[{{"displayName":"id","type":{str}}},{{"displayName":"n","type":{int64Type}}}]}}}}"""
            let row = """{"kind":"named","name":"Row"}"""
            let rowValue (id: string) (n: int64) =
                $"""{{"kind":"product","type":"Row","fields":[{{"field":"id","value":{text id}}},{{"field":"n","value":{int64 n}}}]}}"""
            define session "rowId" [ "r", row ] str $"""{{"kind":"fieldAccess","source":{parameter "r"},"type":"Row","field":"id"}}"""
            define
                session
                "index"
                [ "rows", listOf row ]
                (mapOf str row)
                $"""{{"kind":"mapFromList","source":{parameter "rows"},"function":"rowId"}}"""
            let rows = $"""{{"kind":"list","itemType":{row},"items":[{rowValue "x" 1L},{rowValue "y" 2L},{rowValue "x" 3L}]}}"""
            let expected =
                $"""{{"kind":"map","keyType":{str},"valueType":{row},"entries":[{{"key":{text "x"},"value":{rowValue "x" 3L}}},{{"key":{text "y"},"value":{rowValue "y" 2L}}}]}}"""
            expectRun session "index" [ rows ] expected
            let snapshot = readSnapshot path
            Assert.Empty(SnapshotValidator.validate snapshot)
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("map-fold", nyason)
            Assert.Contains("map-from-list", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)

            // A folder with two parameters, and a key callback returning Float64, are rejected.
            define session "twoArgs" [ "s", str; "k", str ] str (parameter "s")
            Assert.Equal(
                "PureExpressionCallbackArityMismatch",
                simulateCode session [ "m", wordCounts ] str $"""{{"kind":"mapFold","map":{parameter "m"},"seed":{{"kind":"stringLiteral","value":""}},"function":"twoArgs"}}"""
            )
            define session "rowWeight" [ "r", row ] (primitive "float64") """{"kind":"int64ToFloat64","value":{"kind":"fieldAccess","source":{"kind":"parameter","name":"r"},"type":"Row","field":"n"}}"""
            Assert.Equal(
                "MapKeyTypeNotEquatable",
                simulateCode session [ "rows", listOf row ] (mapOf (primitive "float64") row) $"""{{"kind":"mapFromList","source":{parameter "rows"},"function":"rowWeight"}}"""
            ))

    [<Fact; Trait("Suite", "Extended")>]
    let ``MapFilter keeps the entries its callback accepts in their insertion positions`` () =
        withSession (fun session path ->
            // limit < v, with limit bound by context.
            define session "above" [ "limit", int64Type; "k", str; "v", int64Type ] (primitive "bool") $"""{{"kind":"int64LessThan","left":{parameter "limit"},"right":{parameter "v"}}}"""
            define
                session
                "frequent"
                [ "m", wordCounts; "n", int64Type ]
                wordCounts
                $"""{{"kind":"mapFilter","map":{parameter "m"},"function":"above","context":[{{"parameter":"limit","value":{parameter "n"}}}]}}"""
            expectRun session "frequent" [ counts [ "b", 2L; "a", 1L; "c", 3L; "d", 5L ]; int64 1L ] (counts [ "b", 2L; "c", 3L; "d", 5L ])
            expectRun session "frequent" [ counts [ "b", 2L; "a", 1L ]; int64 9L ] (counts [])
            expectRun session "frequent" [ counts [] ; int64 0L ] (counts [])
            // Keys take part too: keep keys other than "a".
            define session "notA" [ "k", str; "v", int64Type ] (primitive "bool") $"""{{"kind":"boolNot","value":{{"kind":"stringEqual","left":{parameter "k"},"right":{{"kind":"stringLiteral","value":"a"}}}}}}"""
            define session "dropA" [ "m", wordCounts ] wordCounts $"""{{"kind":"mapFilter","map":{parameter "m"},"function":"notA"}}"""
            expectRun session "dropA" [ counts [ "b", 2L; "a", 1L; "c", 3L ] ] (counts [ "b", 2L; "c", 3L ])

            let snapshot = readSnapshot path
            Assert.Empty(SnapshotValidator.validate snapshot)
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("map-filter", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)

            // A callback with one parameter, one not returning Bool, and a non-Map source are rejected.
            define session "oneArg" [ "k", str ] (primitive "bool") """{"kind":"boolLiteral","value":true}"""
            Assert.Equal(
                "PureExpressionCallbackArityMismatch",
                simulateCode session [ "m", wordCounts ] wordCounts $"""{{"kind":"mapFilter","map":{parameter "m"},"function":"oneArg"}}"""
            )
            define session "valueOf" [ "k", str; "v", int64Type ] int64Type (parameter "v")
            Assert.Equal(
                "PureExpressionCallbackInputTypeMismatch",
                simulateCode session [ "m", wordCounts ] wordCounts $"""{{"kind":"mapFilter","map":{parameter "m"},"function":"valueOf"}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", listOf str ] wordCounts $"""{{"kind":"mapFilter","map":{parameter "l"},"function":"notA"}}"""
            ))

    [<Fact; Trait("Suite", "Extended")>]
    let ``MapGetOrDefault returns the value for a present key and the default for an absent one`` () =
        withSession (fun session path ->
            let m = parameter "m"
            let k = parameter "k"
            define
                session
                "countOf"
                [ "m", wordCounts; "k", str; "d", int64Type ]
                int64Type
                $"""{{"kind":"mapGetOrDefault","map":{m},"key":{k},"default":{parameter "d"}}}"""
            // Python: counts[word] = counts.get(word, 0) + 1.
            define
                session
                "bump"
                [ "m", wordCounts; "k", str ]
                wordCounts
                $"""{{"kind":"mapInsert","map":{m},"key":{k},"value":{{"kind":"int64WrappingAdd","left":{{"kind":"mapGetOrDefault","map":{m},"key":{k},"default":{{"kind":"int64Literal","value":0}}}},"right":{{"kind":"int64Literal","value":1}}}}}}"""
            let ab = counts [ "a", 1L; "b", 2L ]
            expectRun session "countOf" [ ab; text "b"; int64 0L ] (int64 2L)
            expectRun session "countOf" [ ab; text "z"; int64 0L ] (int64 0L)
            expectRun session "countOf" [ counts []; text "a"; int64 (-1L) ] (int64 (-1L))
            expectRun session "bump" [ ab; text "a" ] (counts [ "a", 2L; "b", 2L ])
            expectRun session "bump" [ ab; text "c" ] (counts [ "a", 1L; "b", 2L; "c", 1L ])

            let snapshot = readSnapshot path
            Assert.Empty(SnapshotValidator.validate snapshot)
            Assert.Contains("\"mapGetOrDefault\"", File.ReadAllText path)
            let node =
                snapshot.Functions
                |> List.collect (fun fn -> fn.Body.Nodes)
                |> List.find (fun node -> match node.Kind with SemanticNodeKind.MapGetOrDefault _ -> true | _ -> false)
            Assert.Equal<ValueDependencyRole list>(
                [ ValueDependencyRole.Source; ValueDependencyRole.Key; ValueDependencyRole.Value ],
                ValueDependency.ofNode node |> List.map (fun dependency -> dependency.Role)
            )
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("map-get-or-default", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)

            // The default must have the map's value type, the key its key type, and the result is V.
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "m", wordCounts; "k", str ] int64Type $"""{{"kind":"mapGetOrDefault","map":{m},"key":{k},"default":{{"kind":"stringLiteral","value":"none"}}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "m", wordCounts ] int64Type $"""{{"kind":"mapGetOrDefault","map":{m},"key":{{"kind":"int64Literal","value":1}},"default":{{"kind":"int64Literal","value":0}}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", listOf str; "k", str ] int64Type $"""{{"kind":"mapGetOrDefault","map":{parameter "l"},"key":{k},"default":{{"kind":"int64Literal","value":0}}}}"""
            )
            Assert.Equal(
                "FunctionCreationResultTypeMismatch",
                simulateCode session [ "m", wordCounts; "k", str ] (optionOf int64Type) $"""{{"kind":"mapGetOrDefault","map":{m},"key":{k},"default":{{"kind":"int64Literal","value":0}}}}"""
            ))

    [<Fact; Trait("Suite", "Extended")>]
    let ``MapMapValues replaces each value through its callback and keeps the keys in order`` () =
        withSession (fun session path ->
            let flags (entries: (string * bool) list) =
                let items =
                    entries
                    |> List.map (fun (key, value) -> $"""{{"key":{text key},"value":{{"kind":"bool","value":{(if value then "true" else "false")}}}}}""")
                    |> String.concat ","
                $"""{{"kind":"map","keyType":{str},"valueType":{primitive "bool"},"entries":[{items}]}}"""
            let labels (entries: (string * string) list) =
                let items = entries |> List.map (fun (key, value) -> $"""{{"key":{text key},"value":{text value}}}""") |> String.concat ","
                $"""{{"kind":"map","keyType":{str},"valueType":{str},"entries":[{items}]}}"""
            // limit < v, with limit bound by context: Map<String, Int64> to Map<String, Bool>.
            define session "above" [ "limit", int64Type; "k", str; "v", int64Type ] (primitive "bool") $"""{{"kind":"int64LessThan","left":{parameter "limit"},"right":{parameter "v"}}}"""
            define
                session
                "aboveFlags"
                [ "m", wordCounts; "n", int64Type ]
                (mapOf str (primitive "bool"))
                $"""{{"kind":"mapMapValues","map":{parameter "m"},"function":"above","context":[{{"parameter":"limit","value":{parameter "n"}}}]}}"""
            // The key takes part: k + "=", a String value from an Int64 map.
            define session "keyLabel" [ "k", str; "v", int64Type ] str $"""{{"kind":"stringConcat","left":{parameter "k"},"right":{{"kind":"stringLiteral","value":"="}}}}"""
            define session "labelled" [ "m", wordCounts ] (mapOf str str) $"""{{"kind":"mapMapValues","map":{parameter "m"},"function":"keyLabel"}}"""
            // Same value type: v + v.
            define session "twice" [ "k", str; "v", int64Type ] int64Type $"""{{"kind":"int64WrappingAdd","left":{parameter "v"},"right":{parameter "v"}}}"""
            define session "doubled" [ "m", wordCounts ] wordCounts $"""{{"kind":"mapMapValues","map":{parameter "m"},"function":"twice"}}"""

            let input = counts [ "b", 2L; "a", 1L; "c", 3L ]
            expectRun session "aboveFlags" [ input; int64 1L ] (flags [ "b", true; "a", false; "c", true ])
            expectRun session "aboveFlags" [ counts []; int64 0L ] (flags [])
            expectRun session "labelled" [ input ] (labels [ "b", "b="; "a", "a="; "c", "c=" ])
            expectRun session "doubled" [ input ] (counts [ "b", 4L; "a", 2L; "c", 6L ])
            expectRun session "doubled" [ counts [] ] (counts [])

            let snapshot = readSnapshot path
            Assert.Empty(SnapshotValidator.validate snapshot)
            Assert.Contains("\"mapMapValues\"", File.ReadAllText path)
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("map-map-values", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)

            // A callback with one parameter, one whose inputs do not match (K, V), a non-Map source
            // and a declared result other than Map<K, U> are rejected.
            define session "oneArg" [ "k", str ] (primitive "bool") """{"kind":"boolLiteral","value":true}"""
            Assert.Equal(
                "PureExpressionCallbackArityMismatch",
                simulateCode session [ "m", wordCounts ] (mapOf str (primitive "bool")) $"""{{"kind":"mapMapValues","map":{parameter "m"},"function":"oneArg"}}"""
            )
            define session "intKeys" [ "k", int64Type; "v", int64Type ] int64Type (parameter "v")
            Assert.Equal(
                "PureExpressionCallbackInputTypeMismatch",
                simulateCode session [ "m", wordCounts ] wordCounts $"""{{"kind":"mapMapValues","map":{parameter "m"},"function":"intKeys"}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", listOf str ] wordCounts $"""{{"kind":"mapMapValues","map":{parameter "l"},"function":"twice"}}"""
            )
            Assert.Equal(
                "FunctionCreationResultTypeMismatch",
                simulateCode session [ "m", wordCounts ] wordCounts $"""{{"kind":"mapMapValues","map":{parameter "m"},"function":"keyLabel"}}"""
            ))
