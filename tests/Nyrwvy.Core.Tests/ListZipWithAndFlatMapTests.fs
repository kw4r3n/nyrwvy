namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// ListZipWith(left, right, combiner, context) and ListFlatMap(source, mapper, context) (post-M2
/// order 63): item i of the zip is combiner(left[i], right[i]) below the shorter length, as Python
/// zip; the flat map concatenates the mapper's lists in source order, in time linear in the output.
module ListZipWithAndFlatMapTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("list-zip-flat-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private slot = ExpressionRecipe.ParameterSlot
    let private number value = ExpressionRecipe.Int64Literal value

    type private Program =
        { Session: AuthoringSession
          Path: string
          Find: string -> SemanticFunction
          Create: string -> (string * TypeRef) list -> TypeRef -> ExpressionRecipe -> SemanticFunction
          Rejects: (string * TypeRef) list -> TypeRef -> ExpressionRecipe -> string }

    /// Authors the callbacks every test uses in a fresh session.
    let private withProgram name (body: Program -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-zip-flat-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let request name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                  TypeParameters = None
                  DisplayName = name
                  Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
                  ResultType = resultType
                  RequiredEffects = []
                  Body = body }
            let create name parameters resultType body =
                let proposal = session.SimulateCreateFunction(request name parameters resultType body) |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
                find name
            let rejects parameters resultType body =
                match session.SimulateCreateFunction(request "rejected" parameters resultType body) with
                | Error error -> sprintf "%A" error
                | Ok _ -> failwith "The function was accepted."
            // Callbacks: subtract is not commutative, so a swapped pair shows.
            create "subtract" [ "left", intType; "right", intType ] intType (ExpressionRecipe.Int64WrappingSubtract(slot 0, slot 1)) |> ignore
            create "label" [ "name", stringType; "count", intType ] stringType (ExpressionRecipe.StringConcat(slot 0, ExpressionRecipe.Int64ToString(slot 1))) |> ignore
            // weighted(weight, left, right) = left * weight + right; context binds weight.
            create "weighted" [ "weight", intType; "left", intType; "right", intType ] intType
                (ExpressionRecipe.Int64WrappingAdd(ExpressionRecipe.Int64WrappingMultiply(slot 1, slot 0), slot 2)) |> ignore
            create "upTo" [ "count", intType ] (listOf intType) (ExpressionRecipe.ListRange(number 0L, slot 0)) |> ignore
            // around(item, distance) = [item - distance, item + distance]; context binds distance.
            create "around" [ "item", intType; "distance", intType ] (listOf intType)
                (ExpressionRecipe.ListConstruct(intType, [ ExpressionRecipe.Int64WrappingSubtract(slot 0, slot 1); ExpressionRecipe.Int64WrappingAdd(slot 0, slot 1) ])) |> ignore
            create "hundred" [ "block", intType ] (listOf intType)
                (ExpressionRecipe.ListRange(ExpressionRecipe.Int64WrappingMultiply(slot 0, number 100L), ExpressionRecipe.Int64WrappingMultiply(ExpressionRecipe.Int64WrappingAdd(slot 0, number 1L), number 100L))) |> ignore
            // Pure-looking but checks cancellation, so it is not pure.
            create "ambientPair" [ "left", intType; "right", intType ] boolType ExpressionRecipe.CancellationRequested |> ignore
            create "ambientList" [ "item", intType ] (listOf boolType)
                (ExpressionRecipe.ListConstruct(boolType, [ ExpressionRecipe.CancellationRequested ])) |> ignore
            create "negate" [ "item", intType ] intType (ExpressionRecipe.Int64WrappingSubtract(number 0L, slot 0)) |> ignore
            body { Session = session; Path = path; Find = find; Create = create; Rejects = rejects }
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private ints = [ "xs", listOf intType; "ys", listOf intType ]

    [<Fact>]
    let ``authored ListZipWith and ListFlatMap reject mismatched and impure callbacks`` () =
        withProgram "reject" (fun program ->
            let fn name = (program.Find name).Id
            let zip combiner = ExpressionRecipe.ListZipWith(slot 0, slot 1, fn combiner, [])
            Assert.Contains("PureExpressionCallbackArityMismatch", program.Rejects ints (listOf intType) (zip "negate"))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", program.Rejects ints (listOf stringType) (zip "label"))
            Assert.Contains("PureExpressionCallbackEffectsNotAllowed", program.Rejects ints (listOf boolType) (zip "ambientPair"))
            Assert.Contains("PureExpressionOperandTypeMismatch", program.Rejects [ "xs", listOf intType; "y", intType ] (listOf intType) (zip "subtract"))
            Assert.Contains("PureExpressionOperandTypeMismatch", program.Rejects [ "x", intType; "ys", listOf intType ] (listOf intType) (zip "subtract"))
            let flat mapper = ExpressionRecipe.ListFlatMap(slot 0, fn mapper, [])
            Assert.Contains("PureExpressionCallbackResultTypeMismatch", program.Rejects ints intType (flat "negate"))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", program.Rejects [ "names", listOf stringType ] (listOf intType) (flat "upTo"))
            Assert.Contains("PureExpressionCallbackArityMismatch", program.Rejects ints (listOf intType) (flat "subtract"))
            Assert.Contains("PureExpressionCallbackEffectsNotAllowed", program.Rejects ints (listOf boolType) (flat "ambientList"))
            Assert.Contains("PureExpressionOperandTypeMismatch", program.Rejects [ "x", intType ] (listOf intType) (flat "upTo"))
            // The declared result must agree with the callback's.
            program.Rejects ints (listOf stringType) (zip "subtract") |> ignore
            program.Rejects ints (listOf stringType) (flat "upTo") |> ignore)

    [<Fact>]
    let ``persisted ListZipWith and ListFlatMap callbacks are validated`` () =
        withProgram "persisted" (fun program ->
            program.Create "zipped" ints (listOf intType) (ExpressionRecipe.ListZipWith(slot 0, slot 1, (program.Find "subtract").Id, [])) |> ignore
            program.Create "flattened" [ "xs", listOf intType ] (listOf intType) (ExpressionRecipe.ListFlatMap(slot 0, (program.Find "upTo").Id, [])) |> ignore
            let snapshot = File.ReadAllText program.Path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let retarget owner callback =
                let target = (program.Find callback).Id
                let rewrite (node: SemanticNode) =
                    match node.Kind with
                    | SemanticNodeKind.ListZipWith(left, right, _, context) -> { node with Kind = SemanticNodeKind.ListZipWith(left, right, target, context) }
                    | SemanticNodeKind.ListFlatMap(source, _, context) -> { node with Kind = SemanticNodeKind.ListFlatMap(source, target, context) }
                    | _ -> node
                let functions =
                    snapshot.Functions
                    |> List.map (fun fn -> if fn.DisplayName = owner then { fn with Body = { fn.Body with Nodes = List.map rewrite fn.Body.Nodes } } else fn)
                SnapshotValidator.validate { snapshot with Functions = functions } |> List.map (fun issue -> issue.Code)
            Assert.Contains("CallbackArityMismatch", retarget "zipped" "negate")
            Assert.Contains("CallbackParameterTypeMismatch", retarget "zipped" "label")
            Assert.Contains("CallbackEffectsNotAllowed", retarget "zipped" "ambientPair")
            Assert.Contains("CallbackResultTypeMismatch", retarget "flattened" "negate")
            Assert.Contains("CallbackArityMismatch", retarget "flattened" "subtract")
            Assert.Contains("CallbackEffectsNotAllowed", retarget "flattened" "ambientList"))

    [<Fact>]
    let ``ListZipWith and ListFlatMap author by name, persist at schema 17 and lower through every layer`` () =
        withProgram "layers" (fun program ->
            let call (line: string) = AuthoringProtocol.executeLine program.Session line |> JsonDocument.Parse
            let ints = """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
            let int = """{"kind":"primitive","primitive":"int64"}"""
            let commit name parameters body =
                let line =
                    """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
                    + ""","parameters":[""" + (parameters |> List.map (fun (n, t) -> """{"displayName":""" + "\"" + n + "\"" + ""","type":""" + t + "}") |> String.concat ",")
                    + """],"resultType":""" + ints + ""","requiredEffects":[],"body":""" + body + "}"
                use proposal = call line
                Assert.True(proposal.RootElement.GetProperty("ok").GetBoolean(), proposal.RootElement.ToString())
                let proposalId = proposal.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use committed = call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposalId + "\"}")
                Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            commit "weightedZip" [ "xs", ints; "ys", ints; "w", int ]
                """{"kind":"listZipWith","left":{"kind":"parameter","name":"xs"},"right":{"kind":"parameter","name":"ys"},"function":"weighted","context":[{"parameter":"weight","value":{"kind":"parameter","name":"w"}}]}"""
            commit "spread" [ "xs", ints; "d", int ]
                """{"kind":"listFlatMap","source":{"kind":"parameter","name":"xs"},"function":"around","context":[{"parameter":"distance","value":{"kind":"parameter","name":"d"}}]}"""
            let snapshot = File.ReadAllText program.Path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serializeCanonical snapshot |> unwrap "serialize"
            use document = JsonDocument.Parse json
            Assert.Equal(17, document.RootElement.GetProperty("schemaVersion").GetInt32())
            Assert.Contains("\"listZipWith\"", json)
            Assert.Contains("\"listFlatMap\"", json)
            Assert.Contains("\"combinerFunctionId\"", json)
            let restored = SnapshotCodec.deserialize json |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            let typedJson = TypedSemanticIrSerializer.serialize typed
            Assert.Contains("\"listZipWith\"", typedJson)
            Assert.Contains("\"listFlatMap\"", typedJson)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            let executionJson = ExecutionIrSerializer.serialize execution
            Assert.Contains("\"listZipWith\"", executionJson)
            Assert.Contains("\"listFlatMap\"", executionJson)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".ZipWith(", source)
            Assert.Contains(".FlatMap(", source))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListZipWith stops at the shorter list and ListFlatMap concatenates in order under runPure`` () =
        withProgram "run" (fun program ->
            let fn name = (program.Find name).Id
            program.Create "zipped" ints (listOf intType) (ExpressionRecipe.ListZipWith(slot 0, slot 1, fn "subtract", [])) |> ignore
            program.Create "labels" [ "names", listOf stringType; "counts", listOf intType ] (listOf stringType)
                (ExpressionRecipe.ListZipWith(slot 0, slot 1, fn "label", [])) |> ignore
            let weighted = program.Find "weighted"
            program.Create "weightedZip" [ "xs", listOf intType; "ys", listOf intType; "w", intType ] (listOf intType)
                (ExpressionRecipe.ListZipWith(slot 0, slot 1, weighted.Id, [ weighted.Parameters.Head.Id, slot 2 ])) |> ignore
            program.Create "flattened" [ "xs", listOf intType ] (listOf intType) (ExpressionRecipe.ListFlatMap(slot 0, fn "upTo", [])) |> ignore
            let around = program.Find "around"
            program.Create "spread" [ "xs", listOf intType; "d", intType ] (listOf intType)
                (ExpressionRecipe.ListFlatMap(slot 0, around.Id, [ around.Parameters.Tail.Head.Id, slot 1 ])) |> ignore
            // 1,000 blocks of 100 items: 100,000 items, equal to the range they cover.
            let big = ExpressionRecipe.ListFlatMap(ExpressionRecipe.ListRange(number 0L, number 1000L), fn "hundred", [])
            program.Create "bigLength" [] intType (ExpressionRecipe.ListLength big) |> ignore
            program.Create "bigIsRange" [] boolType (ExpressionRecipe.ValueEqual(big, ExpressionRecipe.ListRange(number 0L, number 100000L))) |> ignore
            let run name arguments expected =
                let result = PureRun.run program.Session (fn name) arguments None |> unwrap name
                Assert.Equal(expected, result.Actual)
            let list values = PureRunValue.Int64List values
            // Zip: unequal lengths both ways, empty on either side, and argument order.
            run "zipped" [ list [ 10L; 20L; 30L ]; list [ 1L; 2L ] ] (list [ 9L; 18L ])
            run "zipped" [ list [ 10L ]; list [ 1L; 2L; 3L ] ] (list [ 9L ])
            run "zipped" [ list [ 5L; 6L ]; list [ 7L; 9L ] ] (list [ -2L; -3L ])
            run "zipped" [ list []; list [ 1L; 2L ] ] (list [])
            run "zipped" [ list [ 1L; 2L ]; list [] ] (list [])
            run "zipped" [ list []; list [] ] (list [])
            run "labels" [ PureRunValue.StringList [ "a"; "b"; "c" ]; list [ 1L; 2L ] ] (PureRunValue.StringList [ "a1"; "b2" ])
            // Context: weight 10 gives left * 10 + right.
            run "weightedZip" [ list [ 1L; 2L; 3L ]; list [ 4L; 5L; 6L; 7L ]; PureRunValue.Int64 10L ] (list [ 14L; 25L; 36L ])
            // Flat map: mappers returning [] drop their item; order follows the source.
            run "flattened" [ list [ 3L; 0L; 2L ] ] (list [ 0L; 1L; 2L; 0L; 1L ])
            run "flattened" [ list [ 0L; 0L ] ] (list [])
            run "flattened" [ list [] ] (list [])
            run "flattened" [ list [ 1L; 1L; 1L ] ] (list [ 0L; 0L; 0L ])
            run "spread" [ list [ 10L; 20L ]; PureRunValue.Int64 1L ] (list [ 9L; 11L; 19L; 21L ])
            run "bigLength" [] (PureRunValue.Int64 100000L)
            run "bigIsRange" [] (PureRunValue.Bool true))
