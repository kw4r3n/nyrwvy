namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// SetIsSubset(left, right) (Python left <= right) and MapUnion(left, right) (Python left | right,
/// the order of inserting right into left with MapInsert), post-M2 order 59.
module SetIsSubsetAndMapUnionTests =
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

    let private rejectionText session line =
        use reply = execute session line
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.GetRawText())
        reply.RootElement.GetRawText()

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private str = primitive "string"
    let private int64Type = primitive "int64"
    let private setOf item = $"""{{"kind":"builtinGeneric","generic":"set","arguments":[{item}]}}"""
    let private mapOf key value = $"""{{"kind":"builtinGeneric","generic":"map","arguments":[{key},{value}]}}"""
    let private listOf item = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{item}]}}"""
    let private words = setOf str
    let private counts = mapOf str int64Type
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private l, r = parameter "l", parameter "r"

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-subset-union-" + Guid.NewGuid().ToString("N"))
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
    let private boolean (value: bool) = $"""{{"kind":"bool","value":{(if value then "true" else "false")}}}"""
    let private strings (values: string list) =
        $"""{{"kind":"list","itemKind":"string","items":[{values |> List.map JsonSerializer.Serialize |> String.concat ","}]}}"""
    let private set (values: string list) =
        $"""{{"kind":"set","itemType":{str},"items":[{values |> List.map text |> String.concat ","}]}}"""
    let private map (entries: (string * int64) list) =
        let items = entries |> List.map (fun (key, value) -> $"""{{"key":{text key},"value":{{"kind":"int64","value":"{value}"}}}}""") |> String.concat ","
        $"""{{"kind":"map","keyType":{str},"valueType":{int64Type},"entries":[{items}]}}"""

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    let private defineAll session =
        define session "isSubset" [ "l", words; "r", words ] (primitive "bool") $"""{{"kind":"setIsSubset","left":{l},"right":{r}}}"""
        define session "union" [ "l", counts; "r", counts ] counts $"""{{"kind":"mapUnion","left":{l},"right":{r}}}"""
        define session "unionKeys" [ "l", counts; "r", counts ] (listOf str) $"""{{"kind":"mapKeys","value":{{"kind":"mapUnion","left":{l},"right":{r}}}}}"""

    [<Fact>]
    let ``SetIsSubset and MapUnion type check, persist and lower through every layer`` () =
        withSession (fun session path ->
            let floats = setOf (primitive "float64")
            let badFloats = $"""{{"id":"bad","op":"simulateCreateFunctionByName","displayName":"bad","parameters":[{signature [ "l", floats; "r", floats ]}],"resultType":{primitive "bool"},"requiredEffects":[],"body":{{"kind":"setIsSubset","left":{l},"right":{r}}}}}"""
            Assert.Contains("SetItemTypeNotEquatable", rejectionText session badFloats)
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", words; "r", setOf int64Type ] (primitive "bool") $"""{{"kind":"setIsSubset","left":{l},"right":{r}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", words; "r", counts ] (primitive "bool") $"""{{"kind":"setIsSubset","left":{l},"right":{r}}}"""
            )
            let floatKeys = mapOf (primitive "float64") int64Type
            let badKeys = $"""{{"id":"bad","op":"simulateCreateFunctionByName","displayName":"bad","parameters":[{signature [ "l", floatKeys; "r", floatKeys ]}],"resultType":{floatKeys},"requiredEffects":[],"body":{{"kind":"mapUnion","left":{l},"right":{r}}}}}"""
            Assert.Contains("MapKeyTypeNotEquatable", rejectionText session badKeys)
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", counts; "r", mapOf str str ] counts $"""{{"kind":"mapUnion","left":{l},"right":{r}}}"""
            )
            Assert.Equal(
                "PureExpressionOperandTypeMismatch",
                simulateCode session [ "l", counts; "r", mapOf int64Type int64Type ] counts $"""{{"kind":"mapUnion","left":{l},"right":{r}}}"""
            )
            defineAll session

            let snapshot =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok snapshot -> snapshot
                | Error error -> failwithf "Workspace did not decode: %A" error
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> Result.defaultWith (failwithf "serialize: %A")
            Assert.Contains("\"setIsSubset\"", json)
            Assert.Contains("\"mapUnion\"", json)
            Assert.Contains("\"schemaVersion\":17", json.Replace(" ", ""))
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            Assert.Contains("set-is-subset", nyason)
            Assert.Contains("map-union", nyason)
            Assert.Contains("nayamnyam.snapshot.v13", nyason)
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let execution =
                TypedSemanticIr.project restored
                |> Result.defaultWith (failwithf "typed: %A")
                |> ExecutionIr.lower
                |> Result.defaultWith (failwithf "lower: %A")
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> Result.defaultWith (failwithf "emit: %A")).Source
            Assert.Contains(".IsSubsetOf(", source)
            Assert.Contains(".Union(", source)

            let older = json.Replace("\"schemaVersion\": 17", "\"schemaVersion\": 16").Replace("\"schemaVersion\":17", "\"schemaVersion\":16")
            Assert.NotEqual<string>(json, older)
            match SnapshotCodec.deserialize older with
            | Ok _ -> failwith "A schema 16 snapshot with SetIsSubset and MapUnion decoded."
            | Error _ -> ())

    [<Fact; Trait("Suite", "Extended")>]
    let ``SetIsSubset is Python left <= right under runPure`` () =
        withSession (fun session _ ->
            defineAll session
            for left, right, expected in
                [ [ "a"; "b" ], [ "a"; "b" ], true
                  [ "a"; "b" ], [ "b"; "a" ], true
                  [ "b" ], [ "a"; "b"; "c" ], true
                  [ "a"; "b"; "c" ], [ "b" ], false
                  [ "a"; "z" ], [ "a"; "b"; "c" ], false
                  [ "x"; "y" ], [ "a"; "b" ], false
                  [], [ "a" ], true
                  [], [], true
                  [ "a" ], [], false ] do
                expectRun session "isSubset" [ set left; set right ] (boolean expected)
            let evens = [ for index in 0 .. 1999 -> $"w{index * 2}" ]
            let all = [ for index in 0 .. 3999 -> $"w{index}" ]
            expectRun session "isSubset" [ set evens; set all ] (boolean true)
            expectRun session "isSubset" [ set all; set evens ] (boolean false)
            expectRun session "isSubset" [ set (evens @ [ "w1" ]); set (List.rev evens @ [ "w3" ]) ] (boolean false))

    [<Fact; Trait("Suite", "Extended")>]
    let ``MapUnion is Python left | right in the order of inserting right into left under runPure`` () =
        withSession (fun session _ ->
            defineAll session
            let left = [ "c", 1L; "a", 2L; "b", 3L ]
            let right = [ "d", 10L; "a", 20L; "e", 30L ]
            expectRun session "union" [ map left; map right ] (map [ "c", 1L; "a", 20L; "b", 3L; "d", 10L; "e", 30L ])
            expectRun session "unionKeys" [ map left; map right ] (strings [ "c"; "a"; "b"; "d"; "e" ])
            expectRun session "union" [ map right; map left ] (map [ "d", 10L; "a", 2L; "e", 30L; "c", 1L; "b", 3L ])
            expectRun session "union" [ map left; map left ] (map left)
            expectRun session "union" [ map []; map right ] (map right)
            expectRun session "unionKeys" [ map []; map right ] (strings [ "d"; "a"; "e" ])
            expectRun session "union" [ map left; map [] ] (map left)
            expectRun session "union" [ map []; map [] ] (map [])
            // A small left and a large right: the left keys still come first.
            let large = [ for index in 0 .. 1999 -> $"k{index}", int64 index ]
            expectRun session "unionKeys" [ map [ "k1500", -1L; "z", -2L ]; map large ] (strings ([ "k1500"; "z" ] @ [ for index in 0 .. 1999 do if index <> 1500 then $"k{index}" ]))
            // Two halves of a few thousand entries overlapping by 1,000 keys.
            let lower = [ for index in 0 .. 1999 -> $"k{index}", int64 index ]
            let upper = [ for index in 1000 .. 2999 -> $"k{index}", int64 (index * 10) ]
            let expected = [ for index in 0 .. 2999 -> $"k{index}", (if index < 1000 then int64 index else int64 (index * 10)) ]
            expectRun session "union" [ map lower; map upper ] (map expected)
            expectRun session "unionKeys" [ map upper; map lower ] (strings ([ for index in 1000 .. 2999 -> $"k{index}" ] @ [ for index in 0 .. 999 -> $"k{index}" ])))
