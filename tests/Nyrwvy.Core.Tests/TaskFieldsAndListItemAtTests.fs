namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Task handles stored in declared types (a `Member { name, task }` Product) with a transitive
/// boundary rule, and `ListItemAt(list, index): Option<T>`.
module TaskFieldsAndListItemAtTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private outcomeType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ textType; textType ])
    let private taskType = TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ outcomeType ])
    let private scopeType = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
    let private str = ExpressionRecipe.StringLiteral
    let private slot = ExpressionRecipe.ParameterSlot
    let private bound depth = ExpressionRecipe.PatternReference { ScopeDepth = depth; Slot = RecipePatternSlot.Value }
    let private err message = ExpressionRecipe.ResultErr(textType, str message)
    let private concat left right = ExpressionRecipe.StringConcat(left, right)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private parameter name (fn: SemanticFunction) = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
    let private request session name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = effects; Body = body }
    let private create session name parameters resultType effects body =
        request session name parameters resultType effects body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private rejects (session: AuthoringSession) name parameters resultType effects body =
        match session.SimulateCreateFunction(request session name parameters resultType effects body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwithf "Function '%s' was accepted." name
    let private withSession prefix run =
        let root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private snapshot path = SnapshotCodec.deserialize(File.ReadAllText path) |> unwrap
    let private send session line =
        let response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private publishWire (session: AuthoringSession) (response: JsonDocument) =
        session.Commit(response.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore

    let private createType (session: AuthoringSession) name kind =
        session.SimulateCreateType { ExpectedBaseSnapshotId = current session; DisplayName = name; Kind = kind }
    let private product fields =
        AgentTypeDeclarationKindIntent.Product(fields |> List.map (fun (name, t) -> ({ DisplayName = name; Type = t }: AgentTypeFieldIntent)))
    let private declaredType (session: AuthoringSession) name = session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)
    let private fieldOf (typeEntity: SemanticType) name =
        match typeEntity.Kind with
        | SemanticTypeKind.Product fields -> (fields |> List.find (fun field -> field.DisplayName = name)).Id
        | SemanticTypeKind.Union _ -> failwith "Expected a Product."
    let private createCapability (session: AuthoringSession) name (effect: EffectId) operations =
        session.SimulateCreateCapability
            { ExpectedBaseSnapshotId = current session; DisplayName = name
              Operations =
                operations
                |> List.map (fun (operationName, parameters, resultType, failureType) ->
                    ({ DisplayName = operationName
                       Parameters = parameters |> List.map (fun (parameterName, t) -> { DisplayName = parameterName; Type = t })
                       ResultType = resultType
                       FailureType = failureType
                       EffectId = effect }: AgentCapabilityOperationIntent)) }

    /// A team with a `Member { name, task }` Product: its leader starts members "a" and "b" in a scope,
    /// keeps them in a `List<Member>`, and selects one by name with a first-match fold.
    let private withTeam run =
        withSession "nyrwvy-task-fields-" (fun session path ->
            session.SimulateCreateEffect { ExpectedBaseSnapshotId = current session; DisplayName = "Team" } |> unwrap |> publish session
            let team = (session.Declarations().Effects |> List.exactlyOne).Id
            createCapability session "Agent" team [ "work", [ "name", textType ], outcomeType, None ] |> unwrap |> publish session
            let agent = session.Declarations().Capabilities |> List.exactlyOne
            createType session "Member" (product [ "name", textType; "task", taskType ]) |> unwrap |> publish session
            let memberEntity = declaredType session "Member"
            let memberType = TypeRef.Named memberEntity.Id
            let nameField, taskField = fieldOf memberEntity "name", fieldOf memberEntity "task"
            let agentType = TypeRef.Capability agent.Id
            let work =
                create session "work" [ "agent", agentType; "name", textType ] outcomeType [ team ]
                    (ExpressionRecipe.CapabilityInvoke(slot 0, agent.Operations.Head.Id, [ agent.Operations.Head.Parameters.Head.Id, slot 1 ]))
            let start =
                create session "start" [ "scope", scopeType; "agent", agentType; "name", textType ] memberType [ team ]
                    (ExpressionRecipe.ProductConstruct(
                        memberEntity.Id,
                        [ nameField, slot 2
                          taskField, ExpressionRecipe.TaskStart(slot 0, work.Id, [ parameter "agent" work, slot 1 ], slot 2) ]))
            // First match wins: a found member is kept; otherwise the item is taken when its name matches.
            let findStep =
                create session "findStep" [ "found", optionOf memberType; "member", memberType; "wanted", textType ] (optionOf memberType) []
                    (ExpressionRecipe.MatchOption(
                        slot 0,
                        slot 0,
                        ExpressionRecipe.Conditional(
                            ExpressionRecipe.StringEqual(ExpressionRecipe.FieldAccess(slot 1, nameField), slot 2),
                            ExpressionRecipe.OptionSome(slot 1),
                            ExpressionRecipe.OptionNone memberType)))
            let findMember =
                create session "findMember" [ "members", listOf memberType; "wanted", textType ] (optionOf memberType) []
                    (ExpressionRecipe.ListFold(slot 0, ExpressionRecipe.OptionNone memberType, findStep.Id, [ parameter "wanted" findStep, slot 1 ]))
            let taskOf = create session "taskOf" [ "member", memberType ] taskType [] (ExpressionRecipe.FieldAccess(slot 0, taskField))
            let describeOne =
                create session "describeOne" [ "text", textType; "outcome", optionOf outcomeType ] textType []
                    (concat (slot 0) (concat (str "|")
                        (ExpressionRecipe.MatchOption(
                            slot 1,
                            ExpressionRecipe.MatchResult(
                                bound 0,
                                concat (str "ok:") (bound 0),
                                concat (str "err:") (ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Error })),
                            str "running"))))
            let describeAll =
                create session "describeAll" [ "members", listOf memberType ] textType []
                    (ExpressionRecipe.ListFold(
                        ExpressionRecipe.TaskWait(ExpressionRecipe.ListMap(slot 0, taskOf.Id, []), TaskWaitMode.All, None),
                        str "",
                        describeOne.Id,
                        []))
            // The one-element wait result is read with ListItemAt instead of a fold.
            let waitOne =
                create session "waitOne" [ "member", memberType ] outcomeType []
                    (ExpressionRecipe.MatchOption(
                        ExpressionRecipe.ListItemAt(
                            ExpressionRecipe.TaskWait(ExpressionRecipe.ListConstruct(taskType, [ ExpressionRecipe.FieldAccess(slot 0, taskField) ]), TaskWaitMode.All, None),
                            ExpressionRecipe.Int64Literal 0L),
                        ExpressionRecipe.MatchOption(bound 0, bound 0, err "running"),
                        err "missing"))
            let lookup = ExpressionRecipe.DirectCall(findMember.Id, [ parameter "members" findMember, slot 0; parameter "wanted" findMember, slot 1 ])
            let settleStop =
                create session "settleStop" [ "members", listOf memberType; "name", textType ] outcomeType []
                    (ExpressionRecipe.MatchOption(
                        lookup,
                        ExpressionRecipe.Bind(
                            ExpressionRecipe.TaskCancel(ExpressionRecipe.FieldAccess(bound 0, taskField)),
                            ExpressionRecipe.ResultOk(textType, ExpressionRecipe.DirectCall(describeAll.Id, [ parameter "members" describeAll, slot 0 ]))),
                        err "missing"))
            let settleAwait =
                create session "settleAwait" [ "members", listOf memberType; "name", textType ] outcomeType []
                    (ExpressionRecipe.MatchOption(lookup, ExpressionRecipe.DirectCall(waitOne.Id, [ parameter "member" waitOne, bound 0 ]), err "missing"))
            let leader name (settle: SemanticFunction) =
                let startMember memberName =
                    ExpressionRecipe.DirectCall(start.Id, [ parameter "scope" start, slot 2; parameter "agent" start, slot 0; parameter "name" start, str memberName ])
                let body =
                    create session (name + "Body") [ "agent", agentType; "name", textType; "scope", scopeType ] outcomeType [ team ]
                        (ExpressionRecipe.Bind(
                            startMember "a",
                            ExpressionRecipe.Bind(
                                startMember "b",
                                ExpressionRecipe.DirectCall(
                                    settle.Id,
                                    [ parameter "members" settle, ExpressionRecipe.ListConstruct(memberType, [ bound 1; bound 0 ])
                                      parameter "name" settle, slot 1 ]))))
                create session name [ "agent", agentType; "name", textType ] outcomeType [ team ]
                    (ExpressionRecipe.TaskScope(body.Id, [ parameter "agent" body, slot 0; parameter "name" body, slot 1 ]))
            let stopOne = leader "stopOne" settleStop
            let awaitOne = leader "awaitOne" settleAwait
            run session path team agent memberEntity stopOne awaitOne)

    let private okText value = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.String value)
    let private errText value = PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.String value)

    [<Fact>]
    let ``declared types may hold Task handles but not scopes`` () =
        withTeam (fun session path _ _ memberEntity _ _ ->
            Assert.Empty(SnapshotValidator.validate (snapshot path))
            // A Union variant field may hold a Task as well.
            createType session "Slot"
                (AgentTypeDeclarationKindIntent.Union
                    [ { DisplayName = "Idle"; Fields = [] }
                      { DisplayName = "Busy"; Fields = [ { DisplayName = "task"; Type = taskType } ] } ])
            |> unwrap |> publish session
            // A scope is lexical: it stays out of declared types, directly or inside another type argument.
            for fieldType in [ scopeType; listOf scopeType; TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ scopeType; textType ]) ]) ] do
                match createType session "Scoped" (product [ "scope", fieldType ]) with
                | Ok _ -> failwith "A TaskScope field was accepted."
                | Error error -> Assert.Contains("TaskTypeInDeclaredType", sprintf "%A" error)
            let committed = snapshot path
            let withScopeVariant =
                { committed with
                    Types = committed.Types |> List.map (fun typeEntity ->
                        match typeEntity.Kind with
                        | SemanticTypeKind.Union(first :: rest) ->
                            { typeEntity with Kind = SemanticTypeKind.Union({ first with Fields = [ { Id = StableId.createForTesting<FieldTag> "field-scope"; DisplayName = "scope"; Type = scopeType } ] } :: rest) }
                        | _ -> typeEntity) }
            Assert.Contains(SnapshotValidator.validate withScopeVariant, fun issue -> issue.Code = "TaskTypeInDeclaredType")
            // Persisted forms keep the Task field.
            let nyason = NyasonCodec.serialize committed |> unwrap
            Assert.Equal(SnapshotCodec.serialize committed |> unwrap, NyasonCodec.deserialize nyason |> unwrap |> SnapshotCodec.serialize |> unwrap)
            Assert.Contains(committed.Types, fun item -> item.Id = memberEntity.Id))

    [<Fact>]
    let ``task-bearing types never cross a capability or runPure boundary`` () =
        withTeam (fun session path team _ memberEntity stopOne _ ->
            let memberType = TypeRef.Named memberEntity.Id
            createType session "Crew" (product [ "members", listOf memberType ]) |> unwrap |> publish session
            let crewType = TypeRef.Named (declaredType session "Crew").Id
            // Direct, through a List, through a named type holding a List, and as a failure type.
            let boundaryCases =
                [ [ "take", [ "member", memberType ], textType, None ]
                  [ "give", [], listOf memberType, None ]
                  [ "crew", [ "crew", crewType ], textType, None ]
                  [ "fail", [], textType, Some(optionOf crewType) ] ]
            for operations in boundaryCases do
                match createCapability session "Host" team operations with
                | Ok _ -> failwithf "Operation %A was accepted." operations
                | Error error -> Assert.Contains("TaskTypeAtCapabilityBoundary", sprintf "%A" error)
            // A recursive type that reaches a Member is task-bearing; one that does not is fine.
            let committed = snapshot path
            let recursiveId = StableId.createForTesting<TypeTag> "type-chain"
            let chain fieldType: SemanticType =
                { Id = recursiveId; DisplayName = "Chain"
                  Kind =
                    SemanticTypeKind.Product
                        [ { Id = StableId.createForTesting<FieldTag> "field-next"; DisplayName = "next"; Type = optionOf (TypeRef.Named recursiveId) }
                          { Id = StableId.createForTesting<FieldTag> "field-payload"; DisplayName = "payload"; Type = fieldType } ] }
            let withChain fieldType =
                { committed with
                    Types = chain fieldType :: committed.Types
                    Capabilities =
                        committed.Capabilities |> List.map (fun capability ->
                            { capability with Operations = capability.Operations |> List.map (fun operation -> { operation with ResultType = TypeRef.Named recursiveId }) }) }
            Assert.Contains(SnapshotValidator.validate (withChain crewType), fun issue -> issue.Code = "TaskTypeAtCapabilityBoundary")
            Assert.DoesNotContain(SnapshotValidator.validate (withChain textType), fun issue -> issue.Code = "TaskTypeAtCapabilityBoundary")
            Assert.True(TaskTypes.bearing (chain crewType :: committed.Types) (TypeRef.Named recursiveId))
            Assert.False(TaskTypes.bearing (chain textType :: committed.Types) (TypeRef.Named recursiveId))
            // runPure entries take and return plain values only.
            let echo = create session "echo" [ "member", memberType ] memberType [] (slot 0)
            let noMembers = create session "noMembers" [] (listOf memberType) [] (ExpressionRecipe.ListEmpty memberType)
            let crewSize = create session "crewSize" [ "crew", crewType ] intType [] (ExpressionRecipe.ListLength(ExpressionRecipe.FieldAccess(slot 0, fieldOf (declaredType session "Crew") "members")))
            for fn in [ echo; noMembers; crewSize ] do
                match PureRun.run session fn.Id [] None with
                | Ok _ -> failwithf "Entry '%s' was accepted." fn.DisplayName
                | Error error -> Assert.Contains("RunTaskTypeUnsupported", sprintf "%A" error)
            // A function holding members inside is still a valid entry: stopOne takes and returns plain values.
            let types = session.Declarations().Types
            Assert.False(stopOne.ResultType :: (stopOne.Parameters |> List.map (fun item -> item.Type)) |> List.exists (TaskTypes.bearing types)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``a leader keeps Member products and waits for or stops one by name through runPure`` () =
        withTeam (fun session _ _ agent _ stopOne awaitOne ->
            let operation = agent.Operations.Head
            let agentFake () =
                PureRunValue.FakeCapability(
                    agent.Id,
                    [ { OperationId = operation.Id
                        Responses = []
                        Cases =
                          Some
                              [ { Arguments = [ operation.Parameters.Head.Id, PureRunValue.String "a" ]; Responses = [ PureRunFakeResponse.Return(okText "A") ] }
                                { Arguments = [ operation.Parameters.Head.Id, PureRunValue.String "b" ]
                                  Responses = [ PureRunFakeResponse.AwaitCancellation(errText "stopped") ] } ] } ])
            let run (fn: SemanticFunction) name expected =
                let outcome = PureRun.run session fn.Id [ agentFake (); PureRunValue.String name ] (Some expected) |> unwrap
                Assert.Equal(expected, outcome.Actual)
            // Stopping "b" lets it return its own Err; waiting for all then reports both in member order.
            run stopOne "b" (okText "|ok:A|err:stopped")
            run stopOne "nobody" (errText "missing")
            // Waiting for "a" alone; the scope stops "b" when its body returns.
            run awaitOne "a" (okText "A"))

    [<Fact>]
    let ``ListItemAt checks its operands in recipes and persisted graphs`` () =
        withSession "nyrwvy-list-item-at-" (fun session _ ->
            let mismatch = "PureExpressionOperandTypeMismatch"
            let itemAt list index = ExpressionRecipe.ListItemAt(list, index)
            Assert.Contains(mismatch, rejects session "notList" [ "value", textType; "index", intType ] (optionOf textType) [] (itemAt (slot 0) (slot 1)))
            Assert.Contains(mismatch, rejects session "textIndex" [ "items", listOf textType; "index", textType ] (optionOf textType) [] (itemAt (slot 0) (slot 1)))
            // The result is Option<T> of the list's item type.
            Assert.NotEqual<string>("", rejects session "bare" [ "items", listOf textType; "index", intType ] textType [] (itemAt (slot 0) (slot 1)))
            Assert.NotEqual<string>("", rejects session "otherItem" [ "items", listOf intType; "index", intType ] (optionOf textType) [] (itemAt (slot 0) (slot 1)))
            let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-list-item-at-" + name)
            let graphWith listKind indexKind =
                let body: ValueGraph =
                    { Nodes =
                        [ { Id = nodeId "list"; Kind = listKind }
                          { Id = nodeId "index"; Kind = indexKind }
                          { Id = nodeId "at"; Kind = SemanticNodeKind.ListItemAt(nodeId "list", nodeId "index") } ]
                      ResultNodeId = nodeId "at" }
                let fn: SemanticFunction =
                    { Id = StableId.createForTesting<FunctionTag> "function-list-item-at"
                      DisplayName = "itemAt"; Parameters = []; ResultType = optionOf textType; RequiredEffects = []; Body = body }
                SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-list-item-at") with Functions = [ fn ] }
                |> List.map (fun issue -> issue.Code)
            Assert.Empty(graphWith (SemanticNodeKind.ListEmpty textType) (SemanticNodeKind.Int64Literal 0L))
            Assert.Contains("ListItemAtListTypeMismatch", graphWith (SemanticNodeKind.StringLiteral "x") (SemanticNodeKind.Int64Literal 0L))
            Assert.Contains("ListItemAtIndexTypeMismatch", graphWith (SemanticNodeKind.ListEmpty textType) (SemanticNodeKind.StringLiteral "0")))

    [<Fact>]
    let ``exact-ID and name-resolved ListItemAt recipes persist reopen and project`` () =
        withSession "nyrwvy-list-item-at-" (fun session path ->
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let generic name argument = sprintf """{"kind":"builtinGeneric","generic":"%s","arguments":[%s]}""" name argument
            let signature = sprintf """"parameters":[{"displayName":"items","type":%s},{"displayName":"index","type":%s}],"resultType":%s,"requiredEffects":[]""" (generic "list" (primitive "string")) (primitive "int64") (generic "option" (primitive "string"))
            use exactProposal =
                send session (sprintf """{"id":"exact","op":"simulateCreateFunction","displayName":"itemAt",%s,"body":{"kind":"listItemAt","list":{"kind":"parameterSlot","index":0},"index":{"kind":"parameterSlot","index":1}}}""" signature)
            publishWire session exactProposal
            use namedProposal =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"itemAtByName",%s,"body":{"kind":"listItemAt","list":{"kind":"parameter","name":"items"},"index":{"kind":"parameter","name":"index"}}}""" signature)
            Assert.Contains("\"kind\":\"listItemAt\"", namedProposal.RootElement.GetRawText())
            publishWire session namedProposal
            use missingIndex =
                AuthoringProtocol.executeLine session (sprintf """{"id":"bad","op":"simulateCreateFunction","displayName":"bad",%s,"body":{"kind":"listItemAt","list":{"kind":"parameterSlot","index":0}}}""" signature) |> JsonDocument.Parse
            Assert.False(missingIndex.RootElement.GetProperty("ok").GetBoolean())
            let named = find session "itemAtByName"
            let viewer = session.FocusFunction named.Id |> unwrap |> ViewerProjection.focus
            let node = viewer.Graph.Nodes |> List.find (fun item -> item.Id = named.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.ListItemAt, node.Role)
            Assert.Equal<ValueDependencyRole list>([ ValueDependencyRole.Source; ValueDependencyRole.Index ], node.ValueDependencies |> List.map (fun item -> item.Role))
            Assert.Contains("List item at", ViewerHtml.renderFocusDocument viewer)
            session.Close()
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"listItemAt\"", encoded)
            let graph = snapshot path
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            Assert.Contains("list-item-at", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"listItemAt\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"kind\":\"listItemAt\"", ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(named.Body.ResultNodeId, (find reopened "itemAtByName").Body.ResultNodeId))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListItemAt returns Some only for indexes inside the list through runPure`` () =
        withSession "nyrwvy-list-item-at-" (fun session path ->
            let itemAt =
                create session "itemAt" [ "items", listOf textType; "index", intType ] (optionOf textType) [] (ExpressionRecipe.ListItemAt(slot 0, slot 1))
            // Nested item types keep their shape: the item of a List<List<Int64>> is a List<Int64>.
            let rowAt =
                create session "rowAt" [ "rows", listOf (listOf intType); "index", intType ] (optionOf (listOf intType)) [] (ExpressionRecipe.ListItemAt(slot 0, slot 1))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let items = [ "x"; "y"; "z" ]
            for index, expected in [ -1L, None; 0L, Some "x"; 2L, Some "z"; 3L, None; Int64.MaxValue, None; Int64.MinValue, None ] do
                Assert.Equal(PureRunValue.StringOption expected, run itemAt [ PureRunValue.StringList items; PureRunValue.Int64 index ])
            Assert.Equal(PureRunValue.StringOption None, run itemAt [ PureRunValue.StringList []; PureRunValue.Int64 0L ])
            let rows = PureRunValue.ListValue(listOf intType, [ PureRunValue.Int64List [ 1L ]; PureRunValue.Int64List [ 2L; 3L ] ])
            Assert.Equal(PureRunValue.OptionValue(listOf intType, Some(PureRunValue.Int64List [ 2L; 3L ])), run rowAt [ rows; PureRunValue.Int64 1L ])
            Assert.Equal(PureRunValue.OptionValue(listOf intType, None), run rowAt [ rows; PureRunValue.Int64 2L ]))
