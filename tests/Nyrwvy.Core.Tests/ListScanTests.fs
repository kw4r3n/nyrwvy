namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// ListScan(source, seed, folder, context) (post-M2 order 72): the seed followed by every
/// intermediate ListFold state, as F# List.scan; item 0 is the seed and item i + 1 is
/// folder(item i, source[i]), so the length is the source length plus one and the last item equals
/// ListFold.
module ListScanTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("list-scan-" + name)
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

    /// Authors the folders every test uses in a fresh session.
    let private withProgram name (body: Program -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-scan-" + name + "-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> name)) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let request name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
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
            // Folders: add gives prefix sums; subtract is not commutative, so a swapped pair shows.
            create "add" [ "total", intType; "item", intType ] intType (ExpressionRecipe.Int64WrappingAdd(slot 0, slot 1)) |> ignore
            create "subtract" [ "total", intType; "item", intType ] intType (ExpressionRecipe.Int64WrappingSubtract(slot 0, slot 1)) |> ignore
            // second(state, item) = item, so folding a scan with it reads the scan's last item.
            create "second" [ "state", intType; "item", intType ] intType (slot 1) |> ignore
            // scaledAdd(scale, total, item) = total + item * scale; context binds scale.
            create "scaledAdd" [ "scale", intType; "total", intType; "item", intType ] intType
                (ExpressionRecipe.Int64WrappingAdd(slot 1, ExpressionRecipe.Int64WrappingMultiply(slot 2, slot 0))) |> ignore
            create "label" [ "name", stringType; "count", intType ] stringType (ExpressionRecipe.StringConcat(slot 0, ExpressionRecipe.Int64ToString(slot 1))) |> ignore
            create "describe" [ "total", intType; "item", intType ] stringType (ExpressionRecipe.Int64ToString(slot 1)) |> ignore
            create "negate" [ "item", intType ] intType (ExpressionRecipe.Int64WrappingSubtract(number 0L, slot 0)) |> ignore
            // Pure-looking but checks cancellation, so it is not pure.
            create "ambientStep" [ "state", boolType; "item", intType ] boolType ExpressionRecipe.CancellationRequested |> ignore
            body { Session = session; Path = path; Find = find; Create = create; Rejects = rejects }
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private ints = [ "xs", listOf intType ]

    [<Fact>]
    let ``authored ListScan rejects mismatched and impure folders with the shared callback codes`` () =
        withProgram "reject" (fun program ->
            let fn name = (program.Find name).Id
            let scan folder = ExpressionRecipe.ListScan(slot 0, number 0L, fn folder, [])
            Assert.Contains("PureExpressionCallbackArityMismatch", program.Rejects ints (listOf intType) (scan "negate"))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", program.Rejects ints (listOf stringType) (scan "label"))
            // A folder that does not return the seed type.
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", program.Rejects ints (listOf stringType) (scan "describe"))
            Assert.Contains(
                "PureExpressionCallbackEffectsNotAllowed",
                program.Rejects ints (listOf boolType) (ExpressionRecipe.ListScan(slot 0, ExpressionRecipe.BoolLiteral false, fn "ambientStep", [])))
            Assert.Contains("PureExpressionOperandTypeMismatch", program.Rejects [ "x", intType ] (listOf intType) (scan "add"))
            // The declared result must be a list of the seed type.
            program.Rejects ints intType (scan "add") |> ignore
            program.Rejects ints (listOf stringType) (scan "add") |> ignore)

    [<Fact>]
    let ``persisted ListScan folders are validated`` () =
        withProgram "persisted" (fun program ->
            program.Create "running" ints (listOf intType) (ExpressionRecipe.ListScan(slot 0, number 0L, (program.Find "add").Id, [])) |> ignore
            let snapshot = File.ReadAllText program.Path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let retargetTo target =
                let rewrite (node: SemanticNode) =
                    match node.Kind with
                    | SemanticNodeKind.ListScan(source, seed, _, context) -> { node with Kind = SemanticNodeKind.ListScan(source, seed, target, context) }
                    | _ -> node
                let functions =
                    snapshot.Functions
                    |> List.map (fun fn -> if fn.DisplayName = "running" then { fn with Body = { fn.Body with Nodes = List.map rewrite fn.Body.Nodes } } else fn)
                SnapshotValidator.validate { snapshot with Functions = functions } |> List.map (fun issue -> issue.Code)
            let retarget callback = retargetTo (program.Find callback).Id
            Assert.Contains("CallbackArityMismatch", retarget "negate")
            Assert.Contains("CallbackParameterTypeMismatch", retarget "label")
            Assert.Contains("CallbackResultTypeMismatch", retarget "describe")
            Assert.Contains("CallbackEffectsNotAllowed", retarget "ambientStep")
            Assert.Contains("UnknownCallbackFunction", retargetTo (id<FunctionTag> "missing")))

    [<Fact>]
    let ``ListScan authors by name, persists at schema 22 and lowers through every layer`` () =
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
            commit "scaledScan" [ "xs", ints; "start", int; "k", int ]
                """{"kind":"listScan","source":{"kind":"parameter","name":"xs"},"seed":{"kind":"parameter","name":"start"},"function":"scaledAdd","context":[{"parameter":"scale","value":{"kind":"parameter","name":"k"}}]}"""
            let snapshot = File.ReadAllText program.Path |> SnapshotCodec.deserialize |> unwrap "deserialize"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serializeCanonical snapshot |> unwrap "serialize"
            use document = JsonDocument.Parse json
            Assert.Equal(22, document.RootElement.GetProperty("schemaVersion").GetInt32())
            Assert.Contains("\"listScan\"", json)
            Assert.Contains("\"sourceListNodeId\"", json)
            Assert.Contains("\"seedNodeId\"", json)
            Assert.Contains("\"folderFunctionId\"", json)
            let restored = SnapshotCodec.deserialize json |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            // The record codec spells the kind list-scan.
            let record = RecordCodec.serialize restored |> unwrap "record"
            Assert.Contains("list-scan", record)
            Assert.Equal(SemanticFingerprint.compute snapshot, RecordCodec.deserialize record |> unwrap "record decode" |> SemanticFingerprint.compute)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains("\"listScan\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains("\"listScan\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit").Source
            Assert.Contains(".Scan(", source)
            Assert.Contains("NyList<S> Scan<S>(S seed, Func<S, T, S> folder)", source)
            Assert.Contains("new S[Count + 1]", source))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListScan returns the seed and every running state under runPure`` () =
        withProgram "run" (fun program ->
            let fn name = (program.Find name).Id
            program.Create "running" [ "xs", listOf intType; "start", intType ] (listOf intType)
                (ExpressionRecipe.ListScan(slot 0, slot 1, fn "add", [])) |> ignore
            program.Create "folded" [ "xs", listOf intType; "start", intType ] intType
                (ExpressionRecipe.ListFold(slot 0, slot 1, fn "add", [])) |> ignore
            program.Create "differences" [ "xs", listOf intType; "start", intType ] (listOf intType)
                (ExpressionRecipe.ListScan(slot 0, slot 1, fn "subtract", [])) |> ignore
            let scaled = program.Find "scaledAdd"
            program.Create "scaledRunning" [ "xs", listOf intType; "k", intType ] (listOf intType)
                (ExpressionRecipe.ListScan(slot 0, number 0L, scaled.Id, [ scaled.Parameters.Head.Id, slot 1 ])) |> ignore
            // 100,000 items: the scan has 100,001, its last item is the total, and the scan's
            // length and last item agree with ListFold over the same range.
            let range = ExpressionRecipe.ListRange(number 0L, number 100000L)
            let big = ExpressionRecipe.ListScan(range, number 0L, fn "add", [])
            program.Create "bigLength" [] intType (ExpressionRecipe.ListLength big) |> ignore
            program.Create "bigLast" [] intType (ExpressionRecipe.ListFold(big, number 0L, fn "second", [])) |> ignore
            program.Create "bigFold" [] intType (ExpressionRecipe.ListFold(range, number 0L, fn "add", [])) |> ignore
            let run name arguments =
                (PureRun.run program.Session (fn name) arguments None |> unwrap name).Actual
            let list values = PureRunValue.Int64List values
            let scan (items: int64 list) start = run "running" [ list items; PureRunValue.Int64 start ]
            // An empty source gives just the seed.
            Assert.Equal(list [ 7L ], scan [] 7L)
            Assert.Equal(list [ 0L ], scan [] 0L)
            // Prefix sums start at the seed and have one more item than the source.
            Assert.Equal(list [ 0L; 1L; 3L; 6L; 10L ], scan [ 1L; 2L; 3L; 4L ] 0L)
            Assert.Equal(list [ 100L; 101L; 103L ], scan [ 1L; 2L ] 100L)
            Assert.Equal(list [ 5L; 5L; 5L ], scan [ 0L; 0L ] 5L)
            // The folder receives (state, item) in that order: subtract is not commutative.
            Assert.Equal(list [ 10L; 7L; 2L ], run "differences" [ list [ 3L; 5L ]; PureRunValue.Int64 10L ])
            // Context binds scale 10 before iteration: total + item * 10.
            Assert.Equal(list [ 0L; 10L; 30L ], run "scaledRunning" [ list [ 1L; 2L ]; PureRunValue.Int64 10L ])
            // The last item equals ListFold with the same arguments.
            for items, start in [ [], 4L; [ 9L ], 1L; [ 1L; 2L; 3L; 4L; 5L ], -3L ] do
                match scan items start, run "folded" [ list items; PureRunValue.Int64 start ] with
                | PureRunValue.Int64List scanned, PureRunValue.Int64 folded ->
                    Assert.Equal(items.Length + 1, scanned.Length)
                    Assert.Equal(folded, List.last scanned)
                | other -> failwithf "Unexpected results: %A" other
            Assert.Equal(PureRunValue.Int64 100001L, run "bigLength" [])
            Assert.Equal(PureRunValue.Int64 4999950000L, run "bigLast" [])
            Assert.Equal(run "bigFold" [], run "bigLast" []))
