namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Map and Set equality: a Map is equatable when its values are and a Set always is, and two of
/// them are equal when they hold the same entries whatever their insertion order, as Python's
/// dict and set equality.
module MapSetEqualityTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private rejectionText session line =
        use reply = execute session line
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.GetRawText())
        reply.RootElement.GetRawText()

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private str = primitive "string"
    let private int64Type = primitive "int64"
    let private boolType = primitive "bool"
    let private setOf item = $"""{{"kind":"builtinGeneric","generic":"set","arguments":[{item}]}}"""
    let private mapOf key value = $"""{{"kind":"builtinGeneric","generic":"map","arguments":[{key},{value}]}}"""
    let private words = setOf str
    let private counts = mapOf str int64Type
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private l, r = parameter "l", parameter "r"
    let private equal left right = $"""{{"kind":"valueEqual","left":{left},"right":{right}}}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-map-set-equality-" + Guid.NewGuid().ToString("N"))
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

    let private createLine name parameters resultType body =
        $"""{{"id":"{name}","op":"simulateCreateFunctionByName","displayName":"{name}","parameters":[{signature parameters}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private define session name parameters resultType body = publish session (createLine name parameters resultType body)

    let private text (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""
    let private boolean (value: bool) = $"""{{"kind":"bool","value":{(if value then "true" else "false")}}}"""
    let private integer (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private set (values: string list) =
        $"""{{"kind":"set","itemType":{str},"items":[{values |> List.map text |> String.concat ","}]}}"""
    let private map (entries: (string * int64) list) =
        let items = entries |> List.map (fun (key, value) -> $"""{{"key":{text key},"value":{integer value}}}""") |> String.concat ","
        $"""{{"kind":"map","keyType":{str},"valueType":{int64Type},"entries":[{items}]}}"""

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    /// Sets of sets and maps keyed by sets, built from two String sets: a set of two equal sets has
    /// one item, and a key equal to an existing one replaces its value.
    let private defineAll session =
        define session "setsEqual" [ "l", words; "r", words ] boolType (equal l r)
        define session "mapsEqual" [ "l", counts; "r", counts ] boolType (equal l r)
        let pair = $"""{{"kind":"setInsert","set":{{"kind":"setInsert","set":{{"kind":"setEmpty","itemType":{words}}},"item":{l}}},"item":{r}}}"""
        define session "distinctSets" [ "l", words; "r", words ] int64Type $"""{{"kind":"setSize","value":{pair}}}"""
        let keyed =
            $"""{{"kind":"mapInsert","map":{{"kind":"mapInsert","map":{{"kind":"mapEmpty","keyType":{words},"valueType":{int64Type}}},"key":{l},"value":{{"kind":"int64Literal","value":1}}}},"key":{r},"value":{{"kind":"int64Literal","value":2}}}}"""
        define session "keyedBySets" [ "l", words; "r", words ] int64Type $"""{{"kind":"mapSize","value":{keyed}}}"""

    [<Fact>]
    let ``Maps with equatable values and sets are equatable through every layer`` () =
        withSession (fun session path ->
            // Float64 values make a map not equatable, at the path to them.
            let floatValued = mapOf str (primitive "float64")
            let floats = rejectionText session (createLine "bad" [ "l", floatValued; "r", floatValued ] boolType (equal l r))
            Assert.Contains("ValueEqualTypeNotEquatable", floats)
            Assert.Contains("Map value", floats)
            let floatKey = rejectionText session (createLine "bad" [] (mapOf floatValued str) $"""{{"kind":"mapEmpty","keyType":{floatValued},"valueType":{str}}}""")
            Assert.Contains("MapKeyTypeNotEquatable", floatKey)
            Assert.Contains("PureExpressionOperandTypeMismatch", rejectionText session (createLine "bad" [ "l", words; "r", counts ] boolType (equal l r)))
            defineAll session

            let snapshot =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok snapshot -> snapshot
                | Error error -> failwithf "Workspace did not decode: %A" error
            Assert.Empty(SnapshotValidator.validate snapshot)
            let nyason = NyasonCodec.serialize snapshot |> Result.defaultWith (failwithf "nyason: %A")
            let restored = NyasonCodec.deserialize nyason |> Result.defaultWith (failwithf "nyason decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let execution =
                TypedSemanticIr.project restored
                |> Result.defaultWith (failwithf "typed: %A")
                |> ExecutionIr.lower
                |> Result.defaultWith (failwithf "lower: %A")
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> Result.defaultWith (failwithf "emit: %A")).Source
            Assert.Contains("IEquatable<NyMap<K, V>>", source)
            Assert.Contains("IEquatable<NySet<T>>", source))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Map and Set equality ignore insertion order under runPure`` () =
        withSession (fun session _ ->
            defineAll session
            for left, right, expected in
                [ [ "a"; "b" ], [ "b"; "a" ], true
                  [ "a"; "b" ], [ "a" ], false
                  [ "a" ], [ "b" ], false
                  [], [], true ] do
                expectRun session "setsEqual" [ set left; set right ] (boolean expected)
                expectRun session "distinctSets" [ set left; set right ] (integer (if expected then 1L else 2L))
                expectRun session "keyedBySets" [ set left; set right ] (integer (if expected then 1L else 2L))
            for left, right, expected in
                [ [ "a", 1L; "b", 2L ], [ "b", 2L; "a", 1L ], true
                  [ "a", 1L; "b", 2L ], [ "a", 1L; "b", 3L ], false
                  [ "a", 1L ], [ "a", 1L; "b", 2L ], false
                  [ "a", 1L ], [ "b", 1L ], false
                  [], [], true ] do
                expectRun session "mapsEqual" [ map left; map right ] (boolean expected)
            let forward = [ for index in 0 .. 1999 -> $"k{index}", int64 index ]
            expectRun session "mapsEqual" [ map forward; map (List.rev forward) ] (boolean true)
            expectRun session "setsEqual" [ set (List.map fst forward); set (List.rev (List.map fst forward)) ] (boolean true))
