namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Entry selection must precede semantic readiness and execution ordering checks,
/// while keeping every structural function target and the complete declaration boundary.
module PureRunEntryProjectionTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "Unexpected fixture error: %A" error
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ])
    let private number = ExpressionRecipe.Int64Literal

    type private Fixture =
        { /// Replaced when `legacyUnordered` reopens the workspace file.
          mutable Session: AuthoringSession
          Path: string
          Effect: EffectId
          Capability: CapabilityId
          Operation: CapabilityOperationId
          Create: string -> (string * TypeRef) list -> TypeRef -> EffectId list -> ExpressionRecipe -> SemanticFunction }

    let private withFixture test =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-entry-projection-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let mutable current: AuthoringSession option = None
        try
            let path = Path.Combine(root, "workspace.json")
            let session = AuthoringSession.createNew path |> unwrap
            current <- Some session
            let publish (proposal: AuthoringProposal) = session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Io" } |> unwrap |> publish
            let effect = session.Declarations().Effects |> List.exactlyOne
            session.SimulateCreateCapability
                { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Values"
                  Operations = [ { DisplayName = "read"; Parameters = []; ResultType = intType; FailureType = None; EffectId = effect.Id } ] }
            |> unwrap |> publish
            let capability = session.Declarations().Capabilities |> List.exactlyOne
            let fixtureRef = ref Unchecked.defaultof<Fixture>
            let create name parameters result effects body =
                let session = fixtureRef.Value.Session
                session.SimulateCreateFunction
                    { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = name
                      Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
                      ResultType = result; RequiredEffects = effects; Body = body }
                |> unwrap |> fun proposal -> session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
                session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            fixtureRef.Value <-
                { Session = session; Path = path; Effect = effect.Id; Capability = capability.Id
                  Operation = capability.Operations.Head.Id; Create = create }
            try test fixtureRef.Value
            finally current <- Some fixtureRef.Value.Session
        finally
            current |> Option.iter (fun session -> session.Close())
            Directory.Delete(root, true)

    /// Proposals refuse unordered effects, so a graph holding them can only be one published
    /// before that check. Each function was authored with one capability invocation beside an
    /// `Int64Literal 0` stand-in; the stand-in becomes a second, unordered invocation of the same
    /// receiver in the persisted snapshot, and the workspace is reopened from it.
    let private legacyUnordered fixture (functions: SemanticFunction list) =
        let ids = functions |> List.map (fun fn -> fn.Id) |> Set.ofList
        let published = SnapshotCodec.deserialize(File.ReadAllText fixture.Path) |> unwrap
        let tamper (fn: SemanticFunction) =
            let invoke =
                ValueGraph.collectNodes fn.Body
                |> List.pick (fun node -> match node.Kind with SemanticNodeKind.CapabilityInvoke _ -> Some node.Kind | _ -> None)
            { fn with
                Body = fn.Body |> ValueGraph.mapNodes (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.Int64Literal 0L -> { node with Kind = invoke }
                    | _ -> node) }
        let legacy = { published with Functions = published.Functions |> List.map (fun fn -> if Set.contains fn.Id ids then tamper fn else fn) }
        Assert.Empty(SnapshotValidator.validate legacy)
        fixture.Session.Close()
        File.WriteAllText(fixture.Path, SnapshotCodec.serialize legacy |> unwrap)
        fixture.Session <- AuthoringSession.openExisting fixture.Path |> unwrap
        functions |> List.map (fun fn -> fixture.Session.Declarations().Functions |> List.find (fun item -> item.Id = fn.Id))

    let private createBad fixture name =
        let invoke = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, fixture.Operation, [])
        fixture.Create name [ "values", TypeRef.Capability fixture.Capability ] intType [ fixture.Effect ]
            (ExpressionRecipe.Int64WrappingAdd(invoke, number 0L))
        |> List.singleton |> legacyUnordered fixture |> List.exactlyOne

    let private fake fixture =
        PureRunValue.FakeCapability(fixture.Capability,
            [ { OperationId = fixture.Operation
                Responses = [ PureRunFakeResponse.Return(PureRunValue.Int64 1L); PureRunFakeResponse.Return(PureRunValue.Int64 2L) ]
                Cases = None } ])

    let private assertLoweringFailure = function
        | Error(PureRunError.SessionFailure(AuthoringSessionError.ExecutionLoweringFailure(ExecutionIrError.AmbiguousEffectOrder _))) -> ()
        | other -> failwithf "Reachable unordered effects were not rejected: %A" other

    [<Fact; Trait("Suite", "Extended")>]
    let ``unrelated unordered effects do not poison complete entries or omit any target role`` () =
        withFixture (fun fixture ->
            let bad = createBad fixture "unfinished"
            let session = fixture.Session
            let good = fixture.Create "good" [] intType [] (number 7L)
            let direct = fixture.Create "direct" [] intType [] (ExpressionRecipe.DirectCall(good.Id, []))
            let double = fixture.Create "double" [ "item", intType ] intType []
                            (ExpressionRecipe.Int64WrappingMultiply(ExpressionRecipe.ParameterSlot 0, number 2L))
            let value = fixture.Create "value" [] intType []
                            (ExpressionRecipe.FunctionInvoke(ExpressionRecipe.FunctionReference double.Id, [ number 21L ]))
            let mapped = fixture.Create "mapped" [] listType []
                            (ExpressionRecipe.ListMap(ExpressionRecipe.ListConstruct(intType, [ number 1L; number 2L ]), double.Id, []))
            let implemented = fixture.Create "implemented" [] intType [ fixture.Effect ]
                                (ExpressionRecipe.CapabilityInvoke(
                                    ExpressionRecipe.CapabilityConstruct(fixture.Capability, [ fixture.Operation, good.Id ]), fixture.Operation, []))
            let snapshot = SnapshotCodec.deserialize(File.ReadAllText fixture.Path) |> unwrap
            let selected = ExecutionCallGraph.forSnapshotEntry snapshot implemented.Id
            Assert.Equal<Set<FunctionId>>(Set.ofList [ implemented.Id; good.Id ], selected.Functions |> List.map (fun fn -> fn.Id) |> Set.ofList)
            Assert.Equal<SemanticType list>(snapshot.Types, selected.Types)
            Assert.Equal<SemanticCapability list>(snapshot.Capabilities, selected.Capabilities)
            Assert.Equal(snapshot.Id, selected.Id)
            let bytes = File.ReadAllBytes fixture.Path
            for fn, expected in [ good, PureRunValue.Int64 7L; direct, PureRunValue.Int64 7L; value, PureRunValue.Int64 42L
                                  mapped, PureRunValue.Int64List [ 2L; 4L ]; implemented, PureRunValue.Int64 7L ] do
                let actual = PureRun.run session fn.Id [] (Some expected) |> unwrap
                Assert.Equal(Some true, actual.Matches)
                Assert.Equal(snapshot.Id, actual.SnapshotId)
            PureRun.run session bad.Id [ fake fixture ] None |> assertLoweringFailure
            Assert.Equal<byte[]>(bytes, File.ReadAllBytes fixture.Path)
            let reachable = fixture.Create "reachable" [ "values", TypeRef.Capability fixture.Capability ] intType [ fixture.Effect ]
                                (ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral true, number 0L,
                                    ExpressionRecipe.DirectCall(bad.Id, [ bad.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ])))
            // Both branches remain structural targets, even when a literal decides the branch.
            PureRun.run session reachable.Id [ fake fixture ] None |> assertLoweringFailure)

    [<Fact; Trait("Suite", "Extended")>]
    let ``verification projects both snapshots by entry and preserves unknown for reachable bad graphs`` () =
        withFixture (fun fixture ->
            let bad = createBad fixture "unfinished"
            let session = fixture.Session
            let good = fixture.Create "good" [] intType [] (number 7L)
            let proposal = session.SimulateReplaceBody
                               { TargetFunctionId = good.Id; ExpectedBaseSnapshotId = session.SnapshotId
                                 ExpectedCurrentResultNodeId = good.Body.ResultNodeId; Replacement = number 8L } |> unwrap
            let bytes = File.ReadAllBytes fixture.Path
            let evidence = ChangeVerification.verify session proposal.ProposalId
                               [ { Label = "complete entry in working graph"; FunctionId = good.Id; Arguments = []; ExpectedCandidate = Some(PureRunValue.Int64 8L) }
                                 { Label = "unfinished entry stays rejected"; FunctionId = bad.Id; Arguments = [ fake fixture ]; ExpectedCandidate = None } ] |> unwrap
            let complete = evidence.Observations.Head
            Assert.Equal("intended-change-tested", complete.Status)
            Assert.Equal(PureRunValue.Int64 7L, (complete.Baseline |> unwrap).Actual)
            Assert.Equal(PureRunValue.Int64 8L, (complete.Candidate |> unwrap).Actual)
            let unfinished = evidence.Observations[1]
            Assert.Equal("unknown", unfinished.Status)
            unfinished.Baseline |> assertLoweringFailure
            unfinished.Candidate |> assertLoweringFailure
            Assert.Equal<byte[]>(bytes, File.ReadAllBytes fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``candidate only unordered function is refused before verification can see it`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            fixture.Create "good" [] intType [] (number 7L) |> ignore
            let invoke = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, fixture.Operation, [])
            let bytes = File.ReadAllBytes fixture.Path
            let refused = session.SimulateCreateFunction
                              { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "unfinishedCandidate"
                                Parameters = [ { DisplayName = "values"; Type = TypeRef.Capability fixture.Capability } ]
                                ResultType = intType; RequiredEffects = [ fixture.Effect ]
                                Body = ExpressionRecipe.Int64WrappingAdd(invoke, invoke) }
            Assert.True(Result.isError refused)
            Assert.Contains("AmbiguousEffectOrder", sprintf "%A" refused)
            Assert.Equal<byte[]>(bytes, File.ReadAllBytes fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``effectful callback and task worker targets remain reachable before lowering`` () =
        withFixture (fun fixture ->
            let resultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ intType; TypeRef.Primitive PrimitiveType.String ])
            let capabilityType = TypeRef.Capability fixture.Capability
            let invoke = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, fixture.Operation, [])
            let standIn = ExpressionRecipe.ResultOk(TypeRef.Primitive PrimitiveType.String, ExpressionRecipe.Int64WrappingAdd(invoke, number 0L))
            let folder, worker =
                [ fixture.Create "unfinishedFolder" [ "values", capabilityType; "acc", intType; "item", intType ] resultType [ fixture.Effect ] standIn
                  fixture.Create "unfinishedWorker" [ "values", capabilityType; "item", intType ] resultType [ fixture.Effect ] standIn ]
                |> legacyUnordered fixture
                |> function [ folder; worker ] -> folder, worker | other -> failwithf "%A" other
            let session = fixture.Session
            let callback = fixture.Create "callback" [ "values", capabilityType ] resultType [ fixture.Effect ]
                               (ExpressionRecipe.ListEffectFold(ExpressionRecipe.ListConstruct(intType, [ number 1L ]), number 0L, folder.Id,
                                    [ folder.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ]))
            PureRun.run session callback.Id [ fake fixture ] None |> assertLoweringFailure
            let scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
            let body = fixture.Create "taskBody" [ "values", capabilityType; "scope", scopeType ] resultType [ fixture.Effect ]
                           (ExpressionRecipe.Bind(ExpressionRecipe.TaskStart(ExpressionRecipe.ParameterSlot 1, worker.Id,
                                [ worker.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ], number 1L),
                                ExpressionRecipe.ResultOk(TypeRef.Primitive PrimitiveType.String, number 0L)))
            let taskEntry = fixture.Create "taskEntry" [ "values", capabilityType ] resultType [ fixture.Effect ]
                                (ExpressionRecipe.TaskScope(body.Id, [ body.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ]))
            PureRun.run session taskEntry.Id [ fake fixture ] None |> assertLoweringFailure)

    [<Fact; Trait("Suite", "Extended")>]
    let ``unrelated missing arguments remain stored but do not block complete entry or candidate`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let good = fixture.Create "good" [] intType [] (number 7L)
            let callee = fixture.Create "callee" [] intType [] (number 3L)
            let caller = fixture.Create "caller" [] intType [] (ExpressionRecipe.DirectCall(callee.Id, []))
            let addition = session.SimulateOperations
                               [ AgentOperationIntent.AddFunctionParameter
                                     { TargetFunctionId = callee.Id; NewParameterDisplayName = "required"; NewParameterType = boolType
                                       CallSitePlans = [ AgentCallPlan.LeaveMissing caller.Body.ResultNodeId ] } ] |> unwrap
            session.Commit(addition.ProposalId, CommitPolicy.AllowBlockingObligations) |> unwrap |> ignore
            let incomplete = SnapshotCodec.deserialize(File.ReadAllText fixture.Path) |> unwrap
            Assert.Single(incomplete.Obligations) |> ignore
            let selected = ExecutionCallGraph.forSnapshotEntry incomplete good.Id
            Assert.Empty(selected.Obligations)
            Assert.Single((ExecutionCallGraph.forSnapshotEntry incomplete caller.Id).Obligations) |> ignore
            let actual = PureRun.run session good.Id [] (Some(PureRunValue.Int64 7L)) |> unwrap
            Assert.Equal(Some true, actual.Matches)
            match PureRun.run session caller.Id [] None with
            | Error(PureRunError.SessionFailure(AuthoringSessionError.SemanticProjectionFailure(TypedSemanticIrError.IncompleteSnapshot _))) -> ()
            | other -> failwithf "Reachable obligation was hidden: %A" other
            let bytes = File.ReadAllBytes fixture.Path
            let proposal = session.SimulateReplaceBody
                               { TargetFunctionId = good.Id; ExpectedBaseSnapshotId = session.SnapshotId
                                 ExpectedCurrentResultNodeId = good.Body.ResultNodeId; Replacement = number 8L } |> unwrap
            let evidence = ChangeVerification.verify session proposal.ProposalId
                               [ { Label = "completed report beside unfinished caller"; FunctionId = good.Id
                                   Arguments = []; ExpectedCandidate = Some(PureRunValue.Int64 8L) } ] |> unwrap
            Assert.Equal("intended-change-tested", evidence.Observations.Head.Status)
            Assert.Equal<byte[]>(bytes, File.ReadAllBytes fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``unrelated task fields do not make sequential verification unknown while task operations still do`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let errorType = TypeRef.Primitive PrimitiveType.String
            let outcome = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ intType; errorType ])
            let task = TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ outcome ])
            let declaration = session.SimulateCreateType
                                  { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "UnrelatedTaskRecord"
                                    Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "task"; Type = task } ] } |> unwrap
            session.Commit(declaration.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let good = fixture.Create "good" [] intType [] (number 7L)
            let scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
            let body = fixture.Create "scopeBody" [ "scope", scopeType ] outcome [] (ExpressionRecipe.ResultOk(errorType, number 0L))
            let concurrent = fixture.Create "taskEntry" [] outcome [] (ExpressionRecipe.TaskScope(body.Id, []))
            let proposal = session.SimulateReplaceBody
                               { TargetFunctionId = good.Id; ExpectedBaseSnapshotId = session.SnapshotId
                                 ExpectedCurrentResultNodeId = good.Body.ResultNodeId; Replacement = number 8L } |> unwrap
            let evidence = ChangeVerification.verify session proposal.ProposalId
                               [ { Label = "sequential with unrelated task record"; FunctionId = good.Id; Arguments = []; ExpectedCandidate = Some(PureRunValue.Int64 8L) }
                                 { Label = "task scope stays unknown"; FunctionId = concurrent.Id; Arguments = []; ExpectedCandidate = None } ] |> unwrap
            Assert.Equal("intended-change-tested", evidence.Observations.Head.Status)
            Assert.Equal("unknown", evidence.Observations[1].Status)
            evidence.Observations[1].Baseline |> unwrap |> ignore
            evidence.Observations[1].Candidate |> unwrap |> ignore)
