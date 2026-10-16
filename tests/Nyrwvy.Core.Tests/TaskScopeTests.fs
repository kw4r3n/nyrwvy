namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Task scopes: a leader loop starts members one at a time, waits for any or all of them (optionally
/// within a time limit), stops one, and every member still running is cancelled and cleaned up when
/// the scope's body returns. runPure runs them on a virtual clock driven only by scripted delays.
module TaskScopeTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("task-scope-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private resultOf okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])
    let private outcomeType = resultOf stringType stringType
    let private taskType = TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ outcomeType ])
    let private scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private effectId = id<EffectTag> "team"
    let private actionId = id<TypeTag> "action"
    let private spawnId = id<VariantTag> "spawn"
    let private awaitId = id<VariantTag> "await"
    let private stopId = id<VariantTag> "stop"
    let private waitAnyId = id<VariantTag> "wait-any"
    let private finishId = id<VariantTag> "finish"
    let private awaitWithinId = id<VariantTag> "await-within"
    let private waitAnyWithinId = id<VariantTag> "wait-any-within"
    let private awaitWithinIndexId = id<FieldTag> "await-within-index"
    let private awaitWithinLimitId = id<FieldTag> "await-within-limit"
    let private waitAnyLimitId = id<FieldTag> "wait-any-limit"
    let private nameFieldId = id<FieldTag> "name"
    let private awaitIndexId = id<FieldTag> "await-index"
    let private stopIndexId = id<FieldTag> "stop-index"
    let private leaderId = id<CapabilityTag> "leader"
    let private nextId = id<CapabilityOperationTag> "next"
    let private reportId = id<CapabilityParameterTag> "report"
    let private agentId = id<CapabilityTag> "agent"
    let private workId = id<CapabilityOperationTag> "work"
    let private workNameId = id<CapabilityParameterTag> "work-name"
    let private workspacesId = id<CapabilityTag> "workspaces"
    let private createId = id<CapabilityOperationTag> "create"
    let private createNameId = id<CapabilityParameterTag> "create-name"
    let private removeId = id<CapabilityOperationTag> "remove"
    let private removeWorkspaceId = id<CapabilityParameterTag> "remove-workspace"

    let private initial =
        let operation operationId name parameterId parameterName resultType: SemanticCapabilityOperation =
            { Id = operationId
              DisplayName = name
              Parameters = [ { Id = parameterId; DisplayName = parameterName; Type = stringType } ]
              ResultType = resultType
              FailureType = None
              EffectId = effectId }
        let variant variantId name fields: SemanticVariant = { Id = variantId; DisplayName = name; Fields = fields }
        { Snapshot.empty (id<SnapshotTag> "initial") with
            Types =
                [ { Id = actionId
                    DisplayName = "Action"
                    Kind =
                      SemanticTypeKind.Union
                          [ variant spawnId "Spawn" [ { Id = nameFieldId; DisplayName = "name"; Type = stringType } ]
                            variant awaitId "Await" [ { Id = awaitIndexId; DisplayName = "index"; Type = intType } ]
                            variant stopId "Stop" [ { Id = stopIndexId; DisplayName = "index"; Type = intType } ]
                            variant waitAnyId "WaitAny" []
                            variant awaitWithinId "AwaitWithin"
                                [ { Id = awaitWithinIndexId; DisplayName = "index"; Type = intType }
                                  { Id = awaitWithinLimitId; DisplayName = "limit"; Type = intType } ]
                            variant waitAnyWithinId "WaitAnyWithin" [ { Id = waitAnyLimitId; DisplayName = "limit"; Type = intType } ]
                            variant finishId "Finish" [] ] } ]
            Effects = [ { Id = effectId; DisplayName = "Team" } ]
            Capabilities =
                [ { Id = leaderId; DisplayName = "Leader"; Operations = [ operation nextId "next" reportId "report" (TypeRef.Named actionId) ] }
                  { Id = agentId; DisplayName = "Agent"; Operations = [ operation workId "work" workNameId "name" outcomeType ] }
                  { Id = workspacesId
                    DisplayName = "Workspaces"
                    Operations =
                      [ operation createId "create" createNameId "name" outcomeType
                        operation removeId "remove" removeWorkspaceId "workspace" unitType ] } ] }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private request (session: AuthoringSession) name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session
          DisplayName = name
          Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
          ResultType = resultType
          RequiredEffects = effects
          Body = body }

    let private commit (session: AuthoringSession) proposal =
        session.Commit((proposal: AuthoringProposal).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private slot = ExpressionRecipe.ParameterSlot
    let private text = ExpressionRecipe.StringLiteral
    let private concat left right = ExpressionRecipe.StringConcat(left, right)
    let private pattern depth patternSlot = ExpressionRecipe.PatternReference { ScopeDepth = depth; Slot = patternSlot }
    let private invoke receiver operationId parameterId argument = ExpressionRecipe.CapabilityInvoke(receiver, operationId, [ parameterId, argument ])

    /// Creates the team program in a fresh session file and runs `test` against the reopened session.
    let private withTeam test =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-task-scope-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize initial |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let parameter name (fn: SemanticFunction) = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
            let create name parameters resultType effects body =
                session.SimulateCreateFunction(request session name parameters resultType effects body) |> unwrap ("create " + name) |> commit session
                find name
            let team = [ effectId ]
            let leader, agent, workspaces = TypeRef.Capability leaderId, TypeRef.Capability agentId, TypeRef.Capability workspacesId
            // A member holds a workspace for as long as it works; UseResource removes it on every exit.
            let memberBody =
                create "memberBody" [ "agent", agent; "name", stringType; "workspace", stringType ] outcomeType team
                    (invoke (slot 0) workId workNameId (slot 1))
            let memberWorker =
                create "member" [ "agent", agent; "workspaces", workspaces; "name", stringType ] outcomeType team
                    (ExpressionRecipe.UseResource(
                        invoke (slot 1) createId createNameId (slot 2),
                        slot 1,
                        removeId,
                        memberBody.Id,
                        [ parameter "agent" memberBody, slot 0; parameter "name" memberBody, slot 2 ]))
            // "|ok:v", "|err:e" or "|running" per member, in member order.
            let describeOne =
                create "describeOne" [ "text", stringType; "outcome", optionOf outcomeType ] stringType []
                    (concat (slot 0) (concat (text "|")
                        (ExpressionRecipe.MatchOption(
                            slot 1,
                            ExpressionRecipe.MatchResult(
                                pattern 0 RecipePatternSlot.Value,
                                concat (text "ok:") (pattern 0 RecipePatternSlot.Value),
                                concat (text "err:") (pattern 0 RecipePatternSlot.Error)),
                            text "running"))))
            let describe =
                create "describe" [ "outcomes", listOf (optionOf outcomeType) ] stringType []
                    (ExpressionRecipe.ListFold(slot 0, text "", describeOne.Id, []))
            // Err(task) once `seen` holds `index` members, so ListEffectFold stops at the index-th member.
            let pick =
                create "pick" [ "seen", listOf taskType; "task", taskType; "index", intType ] (resultOf (listOf taskType) taskType) []
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64Equal(ExpressionRecipe.ListLength(slot 0), slot 2),
                        ExpressionRecipe.ResultErr(listOf taskType, slot 1),
                        ExpressionRecipe.ResultOk(taskType, ExpressionRecipe.ListAppendItem(slot 0, slot 1))))
            let loopParameters =
                [ "scope", scopeType; "leader", leader; "agent", agent; "workspaces", workspaces; "members", listOf taskType; "report", stringType ]
            let placeholder = create "teamLoop" loopParameters outcomeType team (ExpressionRecipe.ResultOk(stringType, text "placeholder"))
            // New functions read their parameters by slot; the replaced loop body reads them by identity.
            let loopArguments (read: int -> ExpressionRecipe) (target: SemanticFunction) members last =
                [ parameter "scope" target, read 0
                  parameter "leader" target, read 1
                  parameter "agent" target, read 2
                  parameter "workspaces" target, read 3
                  parameter "members" target, members ]
                @ last
            let againWith read members report =
                ExpressionRecipe.DirectCall(placeholder.Id, loopArguments read placeholder members [ parameter "report" placeholder, report ])
            let again = againWith slot
            // Helpers take the loop's parameters in the same order, with the member index last.
            let nth =
                ExpressionRecipe.ListEffectFold(slot 4, ExpressionRecipe.ListEmpty taskType, pick.Id, [ parameter "index" pick, slot 5 ])
            let helperParameters = (loopParameters |> List.take 5) @ [ "index", intType ]
            let onAwait =
                create "onAwait" helperParameters outcomeType team
                    (ExpressionRecipe.MatchResult(
                        nth,
                        again (slot 4) (text "missing"),
                        again (slot 4)
                            (ExpressionRecipe.DirectCall(
                                describe.Id,
                                [ parameter "outcomes" describe,
                                  ExpressionRecipe.TaskWait(ExpressionRecipe.ListConstruct(taskType, [ pattern 0 RecipePatternSlot.Error ]), TaskWaitMode.All, None) ]))))
            let onStop =
                create "onStop" helperParameters outcomeType team
                    (ExpressionRecipe.MatchResult(
                        nth,
                        again (slot 4) (text "missing"),
                        ExpressionRecipe.Bind(ExpressionRecipe.TaskCancel(pattern 0 RecipePatternSlot.Error), again (slot 4) (text "stopped"))))
            // N4: wait for one member for at most `limit` milliseconds.
            let onAwaitWithin =
                create "onAwaitWithin" (helperParameters @ [ "limit", intType ]) outcomeType team
                    (ExpressionRecipe.MatchResult(
                        nth,
                        again (slot 4) (text "missing"),
                        again (slot 4)
                            (ExpressionRecipe.DirectCall(
                                describe.Id,
                                [ parameter "outcomes" describe,
                                  ExpressionRecipe.TaskWait(
                                      ExpressionRecipe.ListConstruct(taskType, [ pattern 0 RecipePatternSlot.Error ]),
                                      TaskWaitMode.All,
                                      Some(ExpressionRecipe.OptionSome(slot 6))) ]))))
            let own index = ExpressionRecipe.Parameter placeholder.Parameters[index].Id
            let helper (target: SemanticFunction) indexField =
                ExpressionRecipe.DirectCall(target.Id, loopArguments own target (own 4) [ parameter "index" target, pattern 0 (RecipePatternSlot.Field indexField) ])
            let again = againWith own
            let body =
                ExpressionRecipe.MatchUnion(
                    invoke (own 1) nextId reportId (own 5),
                    [ [ spawnId ],
                      again
                          (ExpressionRecipe.ListAppendItem(
                              own 4,
                              ExpressionRecipe.TaskStart(
                                  own 0,
                                  memberWorker.Id,
                                  [ parameter "agent" memberWorker, own 2; parameter "workspaces" memberWorker, own 3 ],
                                  pattern 0 (RecipePatternSlot.Field nameFieldId))))
                          (text "spawned")
                      [ awaitId ], helper onAwait awaitIndexId
                      [ stopId ], helper onStop stopIndexId
                      [ waitAnyId ],
                      again (own 4) (ExpressionRecipe.DirectCall(describe.Id, [ parameter "outcomes" describe, ExpressionRecipe.TaskWait(own 4, TaskWaitMode.Any, None) ]))
                      [ awaitWithinId ],
                      ExpressionRecipe.DirectCall(
                          onAwaitWithin.Id,
                          loopArguments own onAwaitWithin (own 4)
                              [ parameter "index" onAwaitWithin, pattern 0 (RecipePatternSlot.Field awaitWithinIndexId)
                                parameter "limit" onAwaitWithin, pattern 0 (RecipePatternSlot.Field awaitWithinLimitId) ])
                      [ waitAnyWithinId ],
                      again (own 4)
                          (ExpressionRecipe.DirectCall(
                              describe.Id,
                              [ parameter "outcomes" describe,
                                ExpressionRecipe.TaskWait(
                                    own 4,
                                    TaskWaitMode.Any,
                                    Some(ExpressionRecipe.OptionSome(pattern 0 (RecipePatternSlot.Field waitAnyLimitId)))) ]))
                      [ finishId ], ExpressionRecipe.ResultOk(stringType, text "done") ])
            let replacement: AgentReplaceFunctionBodyWithExpressionRecipe =
                { TargetFunctionId = placeholder.Id
                  ExpectedBaseSnapshotId = current session
                  ExpectedCurrentResultNodeId = placeholder.Body.ResultNodeId
                  Replacement = body }
            session.SimulateReplaceBody replacement |> unwrap "replace teamLoop" |> commit session
            let teamLoop = find "teamLoop"
            create "runTeam" [ "leader", leader; "agent", agent; "workspaces", workspaces ] outcomeType team
                (ExpressionRecipe.TaskScope(
                    teamLoop.Id,
                    [ parameter "leader" teamLoop, slot 0
                      parameter "agent" teamLoop, slot 1
                      parameter "workspaces" teamLoop, slot 2
                      parameter "members" teamLoop, ExpressionRecipe.ListEmpty taskType
                      parameter "report" teamLoop, text "start" ]))
            |> ignore
            // The body hands a started member's handle out of its scope; the scope has stopped it by then.
            let escapeBody =
                create "escapeBody" [ "agent", agent; "workspaces", workspaces; "scope", scopeType ] (resultOf (listOf taskType) stringType) team
                    (ExpressionRecipe.ResultOk(
                        stringType,
                        ExpressionRecipe.ListConstruct(
                            taskType,
                            [ ExpressionRecipe.TaskStart(slot 2, memberWorker.Id, [ parameter "agent" memberWorker, slot 0; parameter "workspaces" memberWorker, slot 1 ], text "late") ])))
            create "escape" [ "agent", agent; "workspaces", workspaces ] stringType team
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.TaskScope(escapeBody.Id, [ parameter "agent" escapeBody, slot 0; parameter "workspaces" escapeBody, slot 1 ]),
                    ExpressionRecipe.DirectCall(describe.Id, [ parameter "outcomes" describe, ExpressionRecipe.TaskWait(pattern 0 RecipePatternSlot.Value, TaskWaitMode.Any, None) ]),
                    pattern 0 RecipePatternSlot.Error))
            |> ignore
            // Review 2026-10-05: a scope handed out of its body is closed. A late start on it whose worker
            // fans out through ParallelAll into more starts on the same scope must not wait on itself.
            let lateLeaf = create "lateLeaf" [ "item", stringType ] outcomeType [] (ExpressionRecipe.ResultOk(stringType, slot 0))
            let lateFan =
                create "lateFan" [ "scope", scopeType; "item", stringType ] (resultOf taskType stringType) []
                    (ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.TaskStart(slot 0, lateLeaf.Id, [], slot 1)))
            let lateWorker =
                create "lateWorker" [ "scope", scopeType; "item", stringType ] outcomeType []
                    (ExpressionRecipe.MatchResult(
                        ExpressionRecipe.ParallelAll(ExpressionRecipe.ListConstruct(stringType, [ text "a"; text "b" ]), lateFan.Id, [ parameter "scope" lateFan, slot 0 ]),
                        ExpressionRecipe.ResultOk(stringType, slot 1),
                        ExpressionRecipe.ResultErr(stringType, pattern 0 RecipePatternSlot.Error)))
            let handOut = create "handOutScope" [ "scope", scopeType ] (resultOf scopeType stringType) [] (ExpressionRecipe.ResultOk(stringType, slot 0))
            let closedScope = pattern 0 RecipePatternSlot.Value
            create "lateFanOut" [ "unused", stringType ] stringType []
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.TaskScope(handOut.Id, []),
                    ExpressionRecipe.DirectCall(
                        describe.Id,
                        [ parameter "outcomes" describe,
                          ExpressionRecipe.TaskWait(
                              ExpressionRecipe.ListConstruct(taskType, [ ExpressionRecipe.TaskStart(closedScope, lateWorker.Id, [ parameter "scope" lateWorker, closedScope ], text "late") ]),
                              TaskWaitMode.All,
                              None) ]),
                    pattern 0 RecipePatternSlot.Error))
            |> ignore
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            test reopened path
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private okText value = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.String value)
    let private errText value = PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.String value)
    let private action variantId fields = PureRunValue.Union(actionId, variantId, fields)
    let private spawn name = action spawnId [ nameFieldId, PureRunValue.String name ]
    let private awaitMember index = action awaitId [ awaitIndexId, PureRunValue.Int64 index ]
    let private stopMember index = action stopId [ stopIndexId, PureRunValue.Int64 index ]
    let private fake capabilityId operations =
        PureRunValue.FakeCapability(capabilityId, operations |> List.map (fun (operationId, responses) -> { OperationId = operationId; Cases = None; Responses = responses }))
    let private returns values = values |> List.map PureRunFakeResponse.Return
    let private workspaces count =
        fake workspacesId
            [ createId, returns (List.replicate count (okText "ws"))
              removeId, returns (List.replicate count PureRunValue.Unit) ]
    let private argumentText (call: PureRunCall) =
        match call.Arguments with
        | [ _, PureRunValue.String value ] -> value
        | other -> failwithf "Unexpected arguments %A" other

    [<Fact>]
    let ``task constructs check their operands and persist`` () =
        withTeam (fun session path ->
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "decode"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
            for tag in [ "task-scope"; "task-start"; "task-wait"; "task-cancel"; "task-type"; "task-scope-type" ] do
                Assert.Contains(tag, nyason)
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (NyasonCodec.deserialize nyason |> unwrap "nyason decode"))
            let typed = TypedSemanticIr.project snapshot |> unwrap "typed"
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains("\"taskStart\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"taskWait\"", ExecutionIrSerializer.serialize execution)
            // Members run concurrently, so the capabilities they use need a concurrent binding assertion.
            let profile = CSharpBackendProfile.externalImplementations [ nextId; workId; createId; removeId ]
            match session.EmitCSharp profile with
            | Error(AuthoringSessionError.BackendFailure(CSharpBackendError.ConcurrentCapabilityBindingRequired _)) -> ()
            | other -> failwithf "Task members bypassed the concurrent binding check: %A" other
            let artifact =
                session.EmitCSharp(profile |> CSharpBackendProfile.withConcurrentCapabilities [ agentId; workspacesId ]) |> unwrap "emit"
            Assert.Contains("public sealed class NyTaskScope", artifact.Source)
            let reject parameters resultType body =
                match session.SimulateCreateFunction(request session "bad" parameters resultType [ effectId ] body) with
                | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) -> diagnostic.Code
                | other -> failwithf "Invalid task construct accepted: %A" other
            let memberWorker = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "member")
            let context = memberWorker.Parameters |> List.take 2 |> List.mapi (fun index item -> item.Id, slot index)
            let capabilities = [ "agent", TypeRef.Capability agentId; "workspaces", TypeRef.Capability workspacesId ]
            Assert.Equal("TaskStartScopeTypeMismatch",
                reject (capabilities @ [ "scope", stringType ]) taskType (ExpressionRecipe.TaskStart(slot 2, memberWorker.Id, context, text "x")))
            Assert.Equal("PureExpressionCallbackInputTypeMismatch",
                reject (capabilities @ [ "scope", scopeType ]) taskType (ExpressionRecipe.TaskStart(slot 2, memberWorker.Id, context, ExpressionRecipe.Int64Literal 1L)))
            Assert.Equal("TaskWaitInputTypeMismatch",
                reject [ "tasks", listOf stringType ] (listOf (optionOf stringType)) (ExpressionRecipe.TaskWait(slot 0, TaskWaitMode.All, None)))
            Assert.Equal("TaskWaitLimitTypeMismatch",
                reject [ "tasks", listOf taskType; "limit", intType ] (listOf (optionOf outcomeType)) (ExpressionRecipe.TaskWait(slot 0, TaskWaitMode.All, Some(slot 1))))
            // The limit operand is optional in every encoding, so phase 1 graphs stay readable.
            let limits =
                snapshot.Functions |> List.collect (fun fn -> ValueGraph.collectNodes fn.Body) |> List.choose (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.TaskWait(_, _, limit) -> Some limit
                    | _ -> None)
            Assert.Contains(None, limits)
            Assert.Contains(limits, Option.isSome)
            Assert.Contains("\"limitNodeId\"", File.ReadAllText path)
            Assert.Contains("limitValueId", ExecutionIrSerializer.serialize execution)
            // A limit operand selects newer schemas, so readers that predate limits reject it by version.
            let stored = File.ReadAllText path
            Assert.Contains("\"schemaVersion\":12", stored)
            Assert.True(SnapshotCodec.deserialize (stored.Replace("\"schemaVersion\":12", "\"schemaVersion\":11")) |> Result.isError)
            Assert.Contains("nayamnyam.snapshot.v8", nyason)
            Assert.True(NyasonCodec.deserialize (nyason.Replace("nayamnyam.snapshot.v8", "nayamnyam.snapshot.v7")) |> Result.isError)
            Assert.Contains("\"schemaVersion\":9", TypedSemanticIrSerializer.serialize typed)
            // The task loops end in tail self calls, so Execution IR carries TailCall (version 9).
            Assert.Contains("\"schemaVersion\":9", ExecutionIrSerializer.serialize execution)
            Assert.Equal("TaskCancelInputTypeMismatch", reject [ "task", stringType ] unitType (ExpressionRecipe.TaskCancel(slot 0)))
            // The body's free parameter must be the scope.
            Assert.Equal("TaskScopeBodyMismatch", reject capabilities outcomeType (ExpressionRecipe.TaskScope(memberWorker.Id, context)))
            // Task values never cross a capability boundary; a declared field may hold a Task but not a TaskScope.
            let withTaskOperation =
                { snapshot with
                    Capabilities =
                        snapshot.Capabilities |> List.map (fun capability ->
                            if capability.Id <> agentId then capability
                            else { capability with Operations = capability.Operations |> List.map (fun operation -> { operation with ResultType = taskType }) }) }
            Assert.Contains(SnapshotValidator.validate withTaskOperation, fun issue -> issue.Code = "TaskTypeAtCapabilityBoundary")
            let withField fieldType =
                { snapshot with
                    Types = snapshot.Types |> List.map (fun typeEntity ->
                        match typeEntity.Kind with
                        | SemanticTypeKind.Union(first :: rest) ->
                            { typeEntity with Kind = SemanticTypeKind.Union({ first with Fields = [ { Id = nameFieldId; DisplayName = "name"; Type = fieldType } ] } :: rest) }
                        | _ -> typeEntity) }
            Assert.DoesNotContain(SnapshotValidator.validate (withField taskType), fun issue -> issue.Code = "TaskTypeInDeclaredType")
            Assert.Contains(SnapshotValidator.validate (withField scopeType), fun issue -> issue.Code = "TaskTypeInDeclaredType")
            // The message names the field by display name and ID, for name-resolved clients.
            let scopeIssue = SnapshotValidator.validate (withField scopeType) |> List.find (fun issue -> issue.Code = "TaskTypeInDeclaredType")
            Assert.Contains($"Field 'name' ({StableId.value nameFieldId}) uses TaskScope", scopeIssue.Message)
            // The Action type now holds a Task, so the leader's `next` operation that returns it is rejected too.
            Assert.Contains(SnapshotValidator.validate (withField taskType), fun issue -> issue.Code = "TaskTypeAtCapabilityBoundary")
            // JSONL recipes name the wait mode.
            let describe = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "describe")
            let line =
                """{"id":"w","op":"simulateCreateFunctionByName","displayName":"waitAll","parameters":[{"displayName":"tasks","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"builtinGeneric","generic":"task","arguments":[{"kind":"builtinGeneric","generic":"result","arguments":[{"kind":"primitive","primitive":"string"},{"kind":"primitive","primitive":"string"}]}]}]}}],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"kind":"directCall","function":"describe","arguments":[{"parameter":"outcomes","value":{"kind":"taskWait","tasks":{"kind":"parameter","name":"tasks"},"mode":"all"}}]}}"""
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let limited = line.Replace("\"mode\":\"all\"}", "\"mode\":\"all\",\"limit\":{\"kind\":\"optionNone\",\"itemType\":{\"kind\":\"primitive\",\"primitive\":\"int64\"}}}")
            Assert.NotEqual<string>(line, limited)
            use limitedProposal = AuthoringProtocol.executeLine session limited |> JsonDocument.Parse
            Assert.True(limitedProposal.RootElement.GetProperty("ok").GetBoolean(), limitedProposal.RootElement.ToString())
            Assert.NotNull(describe))

    [<Fact>]
    let ``task start consumes captured slots through ref worker entries`` () =
        withTeam (fun session _ ->
            let profile =
                CSharpBackendProfile.externalImplementations [ nextId; workId; createId; removeId ]
                |> CSharpBackendProfile.withConcurrentCapabilities [ agentId; workspacesId ]
            let source = (session.EmitCSharp profile |> unwrap "emit").Source
            let starts = System.Text.RegularExpressions.Regex.Matches(source, @"\.Start\(\(\) => (?<call>[^;]+)\);")
            Assert.True(starts.Count > 0, "expected emitted TaskStart callbacks")
            for start in starts do
                let call = start.Groups.["call"].Value
                let arguments = System.Text.RegularExpressions.Regex.Matches(call, @": (?<value>[^,)]+)")
                Assert.True(arguments.Count > 0, "expected at least the captured item")
                for argument in arguments do
                    Assert.StartsWith("ref ", argument.Groups.["value"].Value)
            let entries =
                System.Text.RegularExpressions.Regex.Matches(
                    source, @"private static [^\r\n]+\((?<parameters>ref [^\r\n]+)\)\s*\{(?<prologue>.*?)goto ",
                    System.Text.RegularExpressions.RegexOptions.Singleline)
            Assert.True(entries.Count > 0, "expected consuming worker entry overloads")
            for entry in entries do
                let parameters = entry.Groups.["parameters"].Value.Split(", ref ")
                let prologue = entry.Groups.["prologue"].Value
                for parameter in parameters do
                    let name = parameter.Substring(parameter.LastIndexOf(' ') + 1)
                    let copy = prologue.IndexOf(" = " + name + ";", StringComparison.Ordinal)
                    let clear = prologue.IndexOf(name + " = default!;", StringComparison.Ordinal)
                    Assert.True(copy >= 0 && clear > copy, "snapshot must enter loop locals before its caller slot is cleared"))

    [<Fact; Trait("Suite", "Extended")>]
    let ``a leader loop starts waits stops and cleans up its members`` () =
        withTeam (fun session _ ->
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let leader =
                fake leaderId
                    [ nextId,
                      returns
                          [ spawn "a"; awaitMember 0L; spawn "b"; spawn "c"; action waitAnyId []
                            stopMember 1L; awaitMember 1L; action waitAnyId []; action finishId [] ] ]
            // a finishes at once; b and c work until their cancellation is requested.
            let agent =
                fake agentId
                    [ workId,
                      [ PureRunFakeResponse.Return(okText "a-done")
                        PureRunFakeResponse.AwaitCancellation(errText "stopped")
                        PureRunFakeResponse.AwaitCancellation(errText "stopped") ] ]
            let result = PureRun.run session (functionId "runTeam") [ leader; agent; workspaces 3 ] (Some(okText "done")) |> unwrap "runTeam"
            Assert.True((result.Matches = Some true), sprintf "%A" result.Mismatch)
            let reports = result.Calls |> List.filter (fun call -> call.OperationId = nextId) |> List.map argumentText
            Assert.Equal<string list>(
                [ "start"; "spawned"; "|ok:a-done"; "spawned"; "spawned"; "|ok:a-done|running|running"
                  "stopped"; "|err:stopped"; "|ok:a-done|err:stopped|running" ],
                reports)
            // Every member released its workspace; c only because the scope stopped it after Finish.
            let removes = result.Calls |> List.filter (fun call -> call.OperationId = removeId)
            Assert.Equal(3, removes.Length)
            let lastReport = result.Calls |> List.findIndexBack (fun call -> call.OperationId = nextId)
            Assert.Contains(result.Calls |> List.skip lastReport, fun call -> call.OperationId = removeId)
            // A handle returned out of its scope reads as finished: the scope stopped it on exit.
            let escaped =
                PureRun.run session (functionId "escape")
                    [ fake agentId [ workId, [ PureRunFakeResponse.AwaitCancellation(errText "stopped") ] ]; workspaces 1 ]
                    (Some(PureRunValue.String "|err:stopped")) |> unwrap "escape"
            Assert.True((escaped.Matches = Some true), sprintf "%A" escaped.Mismatch)
            // A member's host exception is rethrown once the scope has stopped and awaited everything.
            match
                PureRun.run session (functionId "runTeam")
                    [ fake leaderId [ nextId, returns [ spawn "a"; action finishId [] ] ]
                      fake agentId [ workId, [ PureRunFakeResponse.Throw "crash" ] ]
                      workspaces 1 ]
                    None
            with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("crash", failure.Message)
                Assert.Contains(failure.Calls, fun call -> call.OperationId = removeId)
            | other -> failwithf "The member's host exception did not propagate: %A" other)

    [<Fact; Trait("Suite", "Extended")>]
    let ``a late start on a closed scope may fan out into more starts on it`` () =
        withTeam (fun session _ ->
            let lateFanOut = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "lateFanOut")
            // A deadlock surfaces as runPure's RunTimeout.
            let result = PureRun.run session lateFanOut.Id [ PureRunValue.String "" ] (Some(PureRunValue.String "|ok:late")) |> unwrap "lateFanOut"
            Assert.True((result.Matches = Some true), sprintf "%A" result.Mismatch))

    /// Real-runtime probes of the generated scope runtime. Cancel() flags a token and wakes its waiters
    /// before its callbacks cancel linked descendants, and runs callbacks newest first. A callback that
    /// waits until the starter has probed holds that window open, so the starter, woken by an ancestor's
    /// cancellation, always starts its task while propagation has not reached the target scope.
    let private causalityProbe =
        """using System;
using System.Threading;
using Nyrwvy.Generated;

static class Program
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Await(WaitHandle handle, string what) => Check(handle.WaitOne(TimeSpan.FromSeconds(10)), "timed out: " + what);
    static bool Join(NyTask<bool> task) { Check(task.Running.Wait(TimeSpan.FromSeconds(10)), "join timed out"); return task.Running.Result; }
    static NyTask<bool> Probe(NyTaskScope scope) => scope.Start(() => NyRuntime.Cancellation.Value.IsCancellationRequested);
    // Holds Cancel() of `token` in its callbacks until the starter has probed.
    static void HoldUntil(CancellationToken token, ManualResetEventSlim probed) => token.Register(() => Await(probed.WaitHandle, "probe inside the window"));

    // The scope's parent is the cancelled token itself (a host token here).
    static void HostParent()
    {
        using var root = new CancellationTokenSource();
        using var probed = new ManualResetEventSlim();
        var scope = new NyTaskScope(root.Token);
        HoldUntil(root.Token, probed);
        var starter = System.Threading.Tasks.Task.Run(() => { Await(root.Token.WaitHandle, "root"); try { return Join(Probe(scope)); } finally { probed.Set(); } });
        root.Cancel();
        Check(starter.Result, "host-parent: a task started after the root was seen cancelled ran clear");
        scope.Close(false);
        Console.WriteLine("host-parent");
    }

    // inner is created by owner, a task of outer; member, a later task of outer, captures inner. Callbacks
    // run newest first, so outer's cancellation reaches member, and member's held callback, before owner.
    static void SiblingOwnedScope(bool ancestor)
    {
        using var root = new CancellationTokenSource();
        var outer = new NyTaskScope(root.Token);
        NyTaskScope? inner = null;
        using var innerReady = new ManualResetEventSlim();
        using var memberReady = new ManualResetEventSlim();
        using var probed = new ManualResetEventSlim();
        var owner = outer.Start(() =>
        {
            inner = new NyTaskScope(NyRuntime.Cancellation.Value);
            innerReady.Set();
            NyRuntime.WaitForCancellation();
            inner.Close(false);
            return true;
        });
        Await(innerReady.WaitHandle, "inner");
        var member = outer.Start(() =>
        {
            HoldUntil(NyRuntime.Cancellation.Value, probed);
            memberReady.Set();
            NyRuntime.WaitForCancellation();
            bool intoInner, intoOuter, intoShielded;
            try
            {
                intoInner = Join(Probe(inner!));
                intoOuter = Join(Probe(outer));
                // A shield cuts the path: a scope created under it stays clear.
                var token = NyRuntime.Cancellation.Value;
                NyRuntime.Cancellation.Value = CancellationToken.None;
                var shielded = new NyTaskScope(NyRuntime.Cancellation.Value);
                intoShielded = Join(Probe(shielded));
                shielded.Close(false);
                NyRuntime.Cancellation.Value = token;
            }
            finally { probed.Set(); }
            return ancestor ? intoInner && intoOuter && !intoShielded : !intoInner && !intoOuter && !intoShielded;
        });
        Await(memberReady.WaitHandle, "member");
        if (ancestor) root.Cancel();
        else member.Cancel();
        Check(Join(member), ancestor ? "sibling-owned: a start after root cancellation ran clear, or a shield leaked it" : "member-only: cancelling one task reached scopes it does not descend from");
        if (!ancestor) owner.Cancel();
        Check(Join(owner), "owner");
        outer.Close(false);
        Console.WriteLine(ancestor ? "sibling-owned" : "member-only");
    }

    static void Main()
    {
        HostParent();
        SiblingOwnedScope(ancestor: true);
        SiblingOwnedScope(ancestor: false);
    }
}
"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``a task started after an ancestor's cancellation is seen starts cancelled on the real runtime`` () =
        withTeam (fun session _ ->
            let profile =
                CSharpBackendProfile.externalImplementations [ nextId; workId; createId; removeId ]
                |> CSharpBackendProfile.withConcurrentCapabilities [ agentId; workspacesId ]
            let source = (session.EmitCSharp profile |> unwrap "emit").Source
            let root = Path.Combine(Path.GetTempPath(), "nyrwvy-task-causality-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(root) |> ignore
            try
                File.WriteAllText(
                    Path.Combine(root, "Probe.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
                File.WriteAllText(Path.Combine(root, "Generated.cs"), source)
                File.WriteAllText(Path.Combine(root, "Program.cs"), causalityProbe)
                let buildExit, buildOut, buildErr = CSharpBackendTests.runDotnet root [ "build"; "Probe.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
                Assert.True((buildExit = 0), $"Probe build failed:\n{buildOut}\n{buildErr}")
                let runExit, runOut, runErr = CSharpBackendTests.runDotnet root [ Path.Combine(root, "bin", "Release", "net10.0", "Probe.dll") ]
                Assert.True((runExit = 0), $"Probe failed:\n{runOut}\n{runErr}")
                Assert.Equal("host-parent\nsibling-owned\nmember-only", runOut.Replace("\r\n", "\n").Trim())
            finally
                Directory.Delete(root, true))

    let private delayed millis response = PureRunFakeResponse.Delayed(millis, response)

    [<Fact; Trait("Suite", "Extended")>]
    let ``time limits run on a virtual clock driven only by scripted delays`` () =
        withTeam (fun session _ ->
            let runTeam = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "runTeam")).Id
            let awaitWithin index limit = action awaitWithinId [ awaitWithinIndexId, PureRunValue.Int64 index; awaitWithinLimitId, PureRunValue.Int64 limit ]
            let waitAnyWithin limit = action waitAnyWithinId [ waitAnyLimitId, PureRunValue.Int64 limit ]
            let reports leaderActions agentResponses members =
                let result =
                    PureRun.run session runTeam [ fake leaderId [ nextId, returns leaderActions ]; fake agentId [ workId, agentResponses ]; workspaces members ]
                        (Some(okText "done"))
                    |> unwrap "runTeam"
                Assert.True((result.Matches = Some true), sprintf "%A" result.Mismatch)
                Assert.Equal(members, result.Calls |> List.filter (fun call -> call.OperationId = removeId) |> List.length)
                result.Calls |> List.filter (fun call -> call.OperationId = nextId) |> List.map argumentText
            // Any-wait: the member with the shortest delay finishes first, whatever the order they started in.
            let race () =
                reports
                    [ spawn "a"; spawn "b"; spawn "c"; spawn "d"; waitAnyWithin 1000L
                      awaitWithin 3L 50L; awaitWithin 2L 1000L; waitAnyWithin 1000L; action finishId [] ]
                    [ delayed 300L (PureRunFakeResponse.Return(okText "a"))
                      delayed 100L (PureRunFakeResponse.Return(okText "b"))
                      delayed 400L (PureRunFakeResponse.Return(okText "c"))
                      delayed 400L (PureRunFakeResponse.Return(okText "d")) ]
                    4
            let expected =
                [ "start"; "spawned"; "spawned"; "spawned"; "spawned"
                  "|running|ok:b|running|running"
                  // The 50 ms limit from t=100 elapses long before d's 400 ms.
                  "|running"
                  // c and d are both due at t=400; everything due has happened before the wait observes it.
                  "|ok:c"
                  "|ok:a|ok:b|ok:c|ok:d" ]
            // The outcome depends only on the delays, not on thread timing: repeated runs agree.
            for _ in 1..3 do
                Assert.Equal<string list>(expected, race ())
            // N4: a member that exceeds its time limit is reported running, stopped, then observed as stopped.
            Assert.Equal<string list>(
                [ "start"; "spawned"; "spawned"; "|ok:quick"; "|running"; "stopped"; "|err:stopped" ],
                reports
                    [ spawn "quick"; spawn "slow"; awaitWithin 0L 250L; awaitWithin 1L 250L; stopMember 1L; awaitMember 1L; action finishId [] ]
                    [ delayed 200L (PureRunFakeResponse.Return(okText "quick"))
                      PureRunFakeResponse.AwaitCancellation(errText "stopped") ]
                    2)
            // A program whose tasks all block with no delay pending fails instead of hanging.
            match
                PureRun.run session runTeam
                    [ fake leaderId [ nextId, returns [ spawn "a"; awaitMember 0L ] ]
                      fake agentId [ workId, [ PureRunFakeResponse.AwaitCancellation(errText "stopped") ] ]
                      workspaces 1 ]
                    None
            with
            | Error(PureRunError.RuntimeFailure failure) -> Assert.Contains("no delay is pending", failure.Message)
            | other -> failwithf "A blocked program did not fail: %A" other)

    [<Fact; Trait("Suite", "Extended")>]
    let ``a time limit near Int64.MaxValue saturates virtual time instead of wrapping`` () =
        withTeam (fun session _ ->
            let runTeam = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "runTeam")).Id
            let awaitWithin index limit = action awaitWithinId [ awaitWithinIndexId, PureRunValue.Int64 index; awaitWithinLimitId, PureRunValue.Int64 limit ]
            // Once virtual time has advanced to t=100, a limit of Int64.MaxValue must still be a
            // deadline in the future: a wrapped deadline would report b running at once.
            let result =
                PureRun.run session runTeam
                    [ fake leaderId [ nextId, returns [ spawn "a"; spawn "b"; awaitWithin 0L 250L; awaitWithin 1L Int64.MaxValue; action finishId [] ] ]
                      fake agentId [ workId, [ delayed 100L (PureRunFakeResponse.Return(okText "a")); delayed 200L (PureRunFakeResponse.Return(okText "b")) ] ]
                      workspaces 2 ]
                    (Some(okText "done"))
                |> unwrap "runTeam"
            Assert.True((result.Matches = Some true), sprintf "%A" result.Mismatch)
            Assert.Equal<string list>(
                [ "start"; "spawned"; "spawned"; "|ok:a"; "|ok:b" ],
                result.Calls |> List.filter (fun call -> call.OperationId = nextId) |> List.map argumentText))

    [<Fact; Trait("Suite", "Extended")>]
    let ``argument keyed fakes fail only member b whatever order the members start in`` () =
        withTeam (fun session _ ->
            let runTeam = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "runTeam")).Id
            // One script keyed by member name, so reordering the spawns cannot move b's failure to a.
            let agent =
                PureRunValue.FakeCapability(agentId,
                    [ { OperationId = workId
                        Responses = []
                        Cases =
                          Some
                              [ { Arguments = [ workNameId, PureRunValue.String "b" ]; Responses = [ delayed 5L (PureRunFakeResponse.Return(errText "b failed")) ] }
                                { Arguments = [ workNameId, PureRunValue.String "a" ]; Responses = [ delayed 10L (PureRunFakeResponse.Return(okText "a")) ] } ] } ])
            for first, second, expected in [ "a", "b", [ "|ok:a"; "|err:b failed" ]; "b", "a", [ "|err:b failed"; "|ok:a" ] ] do
                let result =
                    PureRun.run session runTeam
                        [ fake leaderId [ nextId, returns [ spawn first; spawn second; awaitMember 0L; awaitMember 1L; action finishId [] ] ]; agent; workspaces 2 ]
                        (Some(okText "done"))
                    |> unwrap "runTeam"
                Assert.True((result.Matches = Some true), sprintf "%A" result.Mismatch)
                Assert.Equal<string list>(
                    [ "start"; "spawned"; "spawned" ] @ expected,
                    result.Calls |> List.filter (fun call -> call.OperationId = nextId) |> List.map argumentText)
                Assert.Equal<string list>([ first; second ], result.Calls |> List.filter (fun call -> call.OperationId = workId) |> List.map argumentText)
                Assert.Equal(2, result.Calls |> List.filter (fun call -> call.OperationId = removeId) |> List.length))
