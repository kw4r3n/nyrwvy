namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Task scopes: a leader loop starts members one at a time, waits for any or all of them, stops one,
/// and every member still running is cancelled and cleaned up when the scope's body returns.
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
                                  ExpressionRecipe.TaskWait(ExpressionRecipe.ListConstruct(taskType, [ pattern 0 RecipePatternSlot.Error ]), TaskWaitMode.All) ]))))
            let onStop =
                create "onStop" helperParameters outcomeType team
                    (ExpressionRecipe.MatchResult(
                        nth,
                        again (slot 4) (text "missing"),
                        ExpressionRecipe.Bind(ExpressionRecipe.TaskCancel(pattern 0 RecipePatternSlot.Error), again (slot 4) (text "stopped"))))
            let own index = ExpressionRecipe.Parameter placeholder.Parameters[index].Id
            let helper (target: SemanticFunction) indexField =
                ExpressionRecipe.DirectCall(target.Id, loopArguments own target (own 4) [ parameter "index" target, pattern 0 (RecipePatternSlot.Field indexField) ])
            let again = againWith own
            let body =
                ExpressionRecipe.MatchUnion(
                    invoke (own 1) nextId reportId (own 5),
                    [ spawnId,
                      again
                          (ExpressionRecipe.ListAppendItem(
                              own 4,
                              ExpressionRecipe.TaskStart(
                                  own 0,
                                  memberWorker.Id,
                                  [ parameter "agent" memberWorker, own 2; parameter "workspaces" memberWorker, own 3 ],
                                  pattern 0 (RecipePatternSlot.Field nameFieldId))))
                          (text "spawned")
                      awaitId, helper onAwait awaitIndexId
                      stopId, helper onStop stopIndexId
                      waitAnyId,
                      again (own 4) (ExpressionRecipe.DirectCall(describe.Id, [ parameter "outcomes" describe, ExpressionRecipe.TaskWait(own 4, TaskWaitMode.Any) ]))
                      finishId, ExpressionRecipe.ResultOk(stringType, text "done") ])
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
                    ExpressionRecipe.DirectCall(describe.Id, [ parameter "outcomes" describe, ExpressionRecipe.TaskWait(pattern 0 RecipePatternSlot.Value, TaskWaitMode.Any) ]),
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
        PureRunValue.FakeCapability(capabilityId, operations |> List.map (fun (operationId, responses) -> { OperationId = operationId; Responses = responses }))
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
            Assert.Contains("\"taskStart\"", TypedSemanticIrCodec.serialize typed)
            Assert.Contains("\"taskWait\"", ExecutionIrCodec.serialize execution)
            let artifact = session.EmitCSharp(CSharpBackendProfile.externalImplementations [ nextId; workId; createId; removeId ]) |> unwrap "emit"
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
            Assert.Equal("PureExpressionListCallbackInputTypeMismatch",
                reject (capabilities @ [ "scope", scopeType ]) taskType (ExpressionRecipe.TaskStart(slot 2, memberWorker.Id, context, ExpressionRecipe.Int64Literal 1L)))
            Assert.Equal("TaskWaitInputTypeMismatch",
                reject [ "tasks", listOf stringType ] (listOf (optionOf stringType)) (ExpressionRecipe.TaskWait(slot 0, TaskWaitMode.All)))
            Assert.Equal("TaskCancelInputTypeMismatch", reject [ "task", stringType ] unitType (ExpressionRecipe.TaskCancel(slot 0)))
            // The body's free parameter must be the scope.
            Assert.Equal("TaskScopeBodyMismatch", reject capabilities outcomeType (ExpressionRecipe.TaskScope(memberWorker.Id, context)))
            // Task values never cross a capability boundary or live in declared types.
            let withTaskOperation =
                { snapshot with
                    Capabilities =
                        snapshot.Capabilities |> List.map (fun capability ->
                            if capability.Id <> agentId then capability
                            else { capability with Operations = capability.Operations |> List.map (fun operation -> { operation with ResultType = taskType }) }) }
            Assert.Contains(SnapshotValidator.validate withTaskOperation, fun issue -> issue.Code = "TaskTypeAtCapabilityBoundary")
            let withTaskField =
                { snapshot with
                    Types = snapshot.Types |> List.map (fun typeEntity ->
                        match typeEntity.Kind with
                        | SemanticTypeKind.Union(first :: rest) ->
                            { typeEntity with Kind = SemanticTypeKind.Union({ first with Fields = [ { Id = nameFieldId; DisplayName = "name"; Type = taskType } ] } :: rest) }
                        | _ -> typeEntity) }
            Assert.Contains(SnapshotValidator.validate withTaskField, fun issue -> issue.Code = "TaskTypeInDeclaredType")
            // JSONL recipes name the wait mode.
            let describe = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "describe")
            let line =
                """{"id":"w","op":"simulateCreateFunctionByName","displayName":"waitAll","parameters":[{"displayName":"tasks","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"builtinGeneric","generic":"task","arguments":[{"kind":"builtinGeneric","generic":"result","arguments":[{"kind":"primitive","primitive":"string"},{"kind":"primitive","primitive":"string"}]}]}]}}],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"kind":"directCall","function":"describe","arguments":[{"parameter":"outcomes","value":{"kind":"taskWait","tasks":{"kind":"parameter","name":"tasks"},"mode":"all"}}]}}"""
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            Assert.NotNull(describe))

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
