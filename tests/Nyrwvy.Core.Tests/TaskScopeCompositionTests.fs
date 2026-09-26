namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Task scopes combined with purity, call reachability, concurrent bindings, abort ordering and
/// derived effect requirements: a task body or worker must never be invisible to these checks.
module TaskScopeCompositionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("task-composition-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private outcomeType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ stringType; stringType ])
    let private taskType = TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ outcomeType ])
    let private scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private effectId = id<EffectTag> "effect"
    let private agentId = id<CapabilityTag> "agent"
    let private workId = id<CapabilityOperationTag> "work"
    let private workNameId = id<CapabilityParameterTag> "work-name"

    let private initial =
        { Snapshot.empty (id<SnapshotTag> "initial") with
            Effects = [ { Id = effectId; DisplayName = "Work" } ]
            Capabilities =
                [ { Id = agentId
                    DisplayName = "Agent"
                    Operations =
                      [ { Id = workId
                          DisplayName = "work"
                          Parameters = [ { Id = workNameId; DisplayName = "name"; Type = stringType } ]
                          ResultType = outcomeType
                          FailureType = None
                          EffectId = effectId } ] } ] }

    type private Fixture =
        { Session: AuthoringSession
          Snapshot: unit -> Snapshot
          Find: string -> SemanticFunction
          Create: string -> (string * TypeRef) list -> TypeRef -> EffectId list -> ExpressionRecipe -> Result<SemanticFunction, AuthoringSessionError> }

    let private withFixture test =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-task-composition-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize initial |> unwrap "serialize")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let create name parameters resultType effects body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      DisplayName = name
                      Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
                      ResultType = resultType
                      RequiredEffects = effects
                      Body = body }
                session.SimulateCreateFunction request
                |> Result.map (fun proposal ->
                    session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
                    find name)
            let snapshot () = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "decode"
            test { Session = session; Snapshot = snapshot; Find = find; Create = create }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private slot = ExpressionRecipe.ParameterSlot
    let private text = ExpressionRecipe.StringLiteral

    /// entry(agent) runs a scope whose body starts a worker invoking Agent.work.
    let private createEffectfulScope (fixture: Fixture) =
        let parameter name (fn: SemanticFunction) = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
        let agent = TypeRef.Capability agentId
        let worker =
            fixture.Create "worker" [ "agent", agent; "name", stringType ] outcomeType [ effectId ]
                (ExpressionRecipe.CapabilityInvoke(slot 0, workId, [ workNameId, slot 1 ]))
            |> unwrap "worker"
        let body =
            fixture.Create "body" [ "agent", agent; "scope", scopeType ] outcomeType [ effectId ]
                (ExpressionRecipe.Bind(
                    ExpressionRecipe.TaskStart(slot 1, worker.Id, [ parameter "agent" worker, slot 0 ], text "job"),
                    ExpressionRecipe.ResultOk(stringType, text "started")))
            |> unwrap "body"
        let entry =
            fixture.Create "entry" [ "agent", agent ] outcomeType [ effectId ]
                (ExpressionRecipe.TaskScope(body.Id, [ parameter "agent" body, slot 0 ]))
            |> unwrap "entry"
        worker, body, entry

    [<Fact>]
    let ``task bodies and workers are reachable and task workers need concurrent capability bindings`` () =
        withFixture (fun fixture ->
            let worker, body, entry = createEffectfulScope fixture
            let program = fixture.Session.ProjectExecution() |> unwrap "lower"
            let selected = ExecutionCallGraph.forEntry program entry.Id
            let selectedIds = selected.Functions |> List.map (fun fn -> fn.SemanticFunctionId) |> Set.ofList
            Assert.Equal<Set<FunctionId>>(set [ worker.Id; body.Id; entry.Id ], selectedIds)
            let profile = CSharpBackendProfile.externalImplementations [ workId ]
            match CSharpBackend.emit selected profile with
            | Error(CSharpBackendError.ConcurrentCapabilityBindingRequired(functionId, _, capabilityId, operationId)) ->
                Assert.Equal(worker.Id, functionId)
                Assert.Equal(agentId, capabilityId)
                Assert.Equal(workId, operationId)
            | other -> failwithf "Task worker bypassed the concurrent binding check: %A" other
            CSharpBackend.emit selected (profile |> CSharpBackendProfile.withConcurrentCapabilities [ agentId ])
            |> unwrap "asserted"
            |> ignore)

    [<Fact>]
    let ``derived body effect requirements include task scope bodies and task workers`` () =
        withFixture (fun fixture ->
            let worker, body, entry = createEffectfulScope fixture
            let snapshot = fixture.Snapshot()
            let sources fn = (EffectRequirements.derive snapshot fn).Evidence |> List.map (fun item -> item.EffectId, item.Source)
            Assert.Equal<(EffectId * EffectRequirementSource) list>([ effectId, EffectRequirementSource.FunctionContract body.Id ], sources entry)
            Assert.Equal<(EffectId * EffectRequirementSource) list>([ effectId, EffectRequirementSource.FunctionContract worker.Id ], sources (fixture.Find "body")))

    [<Fact>]
    let ``functions that start or wait for tasks are never pure function values or capability implementations`` () =
        withFixture (fun fixture ->
            let echo = fixture.Create "echo" [ "item", stringType ] outcomeType [] (ExpressionRecipe.ResultOk(stringType, slot 0)) |> unwrap "echo"
            let body =
                fixture.Create "pureBody" [ "scope", scopeType ] outcomeType []
                    (ExpressionRecipe.Bind(ExpressionRecipe.TaskStart(slot 0, echo.Id, [], text "job"), ExpressionRecipe.ResultOk(stringType, text "ok")))
                |> unwrap "pureBody"
            let scoped = fixture.Create "scoped" [] outcomeType [] (ExpressionRecipe.TaskScope(body.Id, [])) |> unwrap "scoped"
            let snapshot = fixture.Snapshot()
            Assert.True(PureFunctions.isPure snapshot echo.Id)
            Assert.False(PureFunctions.isPure snapshot body.Id)
            Assert.False(PureFunctions.isPure snapshot scoped.Id)
            match fixture.Create "reference" [] (TypeRef.Function([], outcomeType)) [] (ExpressionRecipe.FunctionReference scoped.Id) with
            | Ok _ -> failwith "A task scope was admitted as a pure function value."
            | Error _ -> ())

    [<Fact>]
    let ``task types inside function types still cannot enter declared types`` () =
        Assert.True(TaskTypes.mentions (TypeRef.Function([ scopeType ], stringType)))
        Assert.True(TaskTypes.mentions (TypeRef.Function([ stringType ], taskType)))
        Assert.False(TypeRef.Function([ stringType ], outcomeType) |> TaskTypes.mentions)

    [<Fact>]
    let ``abort ordering treats an unsequenced task cancel as observable`` () =
        let parameterId = id<ParameterTag> "task"
        let node name kind: SemanticNode = { Id = id<GraphNodeTag> name; Kind = kind }
        let fn: SemanticFunction =
            { Id = id<FunctionTag> "abort-with-cancel"
              DisplayName = "abortWithCancel"
              Parameters = [ { Id = parameterId; DisplayName = "task"; Type = taskType } ]
              ResultType = outcomeType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ node "task" (SemanticNodeKind.ParameterReference parameterId)
                      node "cancel" (SemanticNodeKind.TaskCancel(id<GraphNodeTag> "task"))
                      node "reason" (SemanticNodeKind.StringLiteral "stop")
                      node "abort" (SemanticNodeKind.Abort(id<GraphNodeTag> "reason")) ]
                  ResultNodeId = id<GraphNodeTag> "abort" } }
        match GraphOrder.tryAbortOrderConflict (Map.ofList [ fn.Id, fn ]) fn with
        | Some(first, second) ->
            Assert.Equal<Set<GraphNodeId>>(set [ id<GraphNodeTag> "cancel"; id<GraphNodeTag> "abort" ], set [ first; second ])
        | None -> failwith "An unsequenced TaskCancel was allowed beside an abort."
