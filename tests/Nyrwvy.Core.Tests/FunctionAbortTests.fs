namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module FunctionAbortTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private result t = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ t; textType ])
    let private listOf t = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ t ])
    let private str = ExpressionRecipe.StringLiteral
    let private slot = ExpressionRecipe.ParameterSlot
    let private bound slot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = slot }
    let private abort message = ExpressionRecipe.Abort(str message)
    let private ok value = ExpressionRecipe.ResultOk(textType, value)
    let private okText value = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.String value)
    let private errText value = PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.String value)
    let private unitResult success value = PureRunValue.ResultValue(unitType, textType, success, value)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = effects; Body = body }
    let private create session name parameters resultType effects body =
        request session name parameters resultType effects body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-abort-" + Guid.NewGuid().ToString("N"))
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
    let private io (session: AuthoringSession) =
        session.SimulateCreateEffect { ExpectedBaseSnapshotId = current session; DisplayName = "IO" } |> unwrap |> publish session
        let effect = session.Declarations().Effects |> List.exactlyOne
        let operation name t: AgentCapabilityOperationIntent =
            { DisplayName = name; Parameters = [ { DisplayName = "value"; Type = textType } ]
              ResultType = t; FailureType = None; EffectId = effect.Id }
        session.SimulateCreateCapability
            { ExpectedBaseSnapshotId = current session; DisplayName = "IO"
              Operations = [ operation "mark" textType; operation "open" (result textType); operation "release" (result unitType) ] }
        |> unwrap |> publish session
        let cap = session.Declarations().Capabilities |> List.exactlyOne
        let op name = cap.Operations |> List.find (fun op -> op.DisplayName = name)
        effect, cap, op
    let private invoke (op: SemanticCapabilityOperation) receiver value =
        ExpressionRecipe.CapabilityInvoke(receiver, op.Id, [ op.Parameters.Head.Id, value ])
    let private fake (cap: SemanticCapability) responses =
        PureRunValue.FakeCapability(cap.Id, cap.Operations |> List.map (fun op ->
            { OperationId = op.Id; Cases = None; Responses = responses |> List.tryFind (fun (name, _) -> name = op.DisplayName) |> Option.map snd |> Option.defaultValue [] }))
    let private returns value = PureRunFakeResponse.Return value
    let private calls (cap: SemanticCapability) (values: PureRunCall list) =
        values |> List.map (fun call -> (cap.Operations |> List.find (fun op -> op.Id = call.OperationId)).DisplayName)

    [<Fact>]
    let ``abort is typed abrupt completion through every scoped branch and preserves old encodings`` () =
        withSession (fun session path ->
            let plain =
                create session "plain" [] (result textType) [] (ok (str "value"))
            let old = File.ReadAllText path
            Assert.Contains("\"schemaVersion\":9", old)
            let oldTyped = TypedSemanticIr.project (snapshot path) |> unwrap
            Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", TypedSemanticIrSerializer.serialize oldTyped)
            Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", ExecutionIrSerializer.serialize (ExecutionIr.lower oldTyped |> unwrap))
            Assert.Equal(old, SnapshotCodec.serialize (snapshot path) |> unwrap)
            let choose = ExpressionRecipe.Conditional(slot 0, abort "bad", str "normal")
            let fn =
                create session "choose" [ "fail", boolType ] (result textType) [] (ExpressionRecipe.Bind(choose, ok (bound RecipePatternSlot.Value)))
            create session "allAbort" [ "fail", boolType ] (result textType) []
                (ExpressionRecipe.Conditional(slot 0, abort "a", ExpressionRecipe.Bind(str "before", abort "b"))) |> ignore
            create session "option" [] (result textType) []
                (ExpressionRecipe.MatchOption(ExpressionRecipe.OptionSome(str "input"), abort "some", ok (str "none"))) |> ignore
            create session "match" [] (result textType) []
                (ExpressionRecipe.MatchResult(ExpressionRecipe.ResultErr(textType, str "input"), ok (bound RecipePatternSlot.Value), ExpressionRecipe.Abort(bound RecipePatternSlot.Error))) |> ignore
            let graph = snapshot path
            let encoded = File.ReadAllText path
            Assert.Contains("\"schemaVersion\":11", encoded)
            let nyason = NyasonCodec.serialize graph |> unwrap
            Assert.Contains("nayamnyam.snapshot.v7", nyason)
            Assert.Equal(encoded, SnapshotCodec.serialize(NyasonCodec.deserialize nyason |> unwrap) |> unwrap)
            for version in [ 9; 10 ] do
                Assert.True(Result.isError(SnapshotCodec.deserialize(encoded.Replace("\"schemaVersion\":11", $"\"schemaVersion\":{version}"))))
            for version in [ 5; 6 ] do
                Assert.True(Result.isError(NyasonCodec.deserialize(nyason.Replace("nayamnyam.snapshot.v7", $"nayamnyam.snapshot.v{version}"))))
            let typed = TypedSemanticIr.project graph |> unwrap
            let program = ExecutionIr.lower typed |> unwrap
            Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"completion\":\"abort\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", ExecutionIrSerializer.serialize program)
            let ordered = { graph with Functions = List.rev graph.Functions |> List.map (fun fn -> { fn with Body = { fn.Body with Nodes = List.rev fn.Body.Nodes } }) }
            Assert.Equal(ExecutionIrSerializer.serialize program, ExecutionIrSerializer.serialize(ExecutionIr.lower(TypedSemanticIr.project ordered |> unwrap) |> unwrap))
            let resolve = SemanticFacts.createNodeCompletionResolver graph fn
            let abortNode = ValueGraph.collectNodes fn.Body |> List.find (fun node -> match node.Kind with SemanticNodeKind.Abort _ -> true | _ -> false)
            Assert.Equal(Some GraphCompletion.Aborts, resolve abortNode.Id)
            Assert.Equal(Some(GraphCompletion.Normal(result textType)), resolve fn.Body.ResultNodeId)
            Assert.Empty((session.FocusFunction fn.Id |> unwrap).BodyEffectRequirements.EffectIds)
            // A saved graph can store a non-result control that always aborts. Its enclosing
            // branch still has no normal completion, even when its nominal result is a value.
            let always = find session "allAbort"
            let literalId = StableId.createForTesting<GraphNodeTag> "abort-orphan-literal"
            let normalId = StableId.createForTesting<GraphNodeTag> "abort-orphan-normal"
            let branch = { always.Body with Nodes = always.Body.Nodes @ [ { Id = literalId; Kind = SemanticNodeKind.StringLiteral "unused" }; { Id = normalId; Kind = SemanticNodeKind.ResultOk(textType, literalId) } ]; ResultNodeId = normalId }
            let conditionId = StableId.createForTesting<GraphNodeTag> "abort-orphan-condition"
            let parentId = StableId.createForTesting<GraphNodeTag> "abort-orphan-parent"
            let errorId = StableId.createForTesting<GraphNodeTag> "abort-orphan-error"
            let abortId = StableId.createForTesting<GraphNodeTag> "abort-orphan-exit"
            let other: ValueGraph = { Nodes = [ { Id = errorId; Kind = SemanticNodeKind.StringLiteral "other" }; { Id = abortId; Kind = SemanticNodeKind.Abort errorId } ]; ResultNodeId = abortId }
            let stored = { always with Body = { Nodes = [ { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral true }; { Id = parentId; Kind = SemanticNodeKind.Conditional(conditionId, branch, other) } ]; ResultNodeId = parentId } }
            let unusual = { graph with Functions = [ stored ] }
            Assert.Empty(SnapshotValidator.validate unusual)
            Assert.Equal(Some GraphCompletion.Aborts, SemanticFacts.createNodeCompletionResolver unusual stored parentId)
            let lowered = ExecutionIr.lower(TypedSemanticIr.project unusual |> unwrap) |> unwrap
            Assert.DoesNotContain(lowered.Functions.Head.Blocks, fun block -> match block.Terminator with ExecutionTerminator.Return _ -> true | _ -> false)
            Assert.Equal(plain.Id, (find session "plain").Id))

    [<Fact>]
    let ``authoring and saved graphs reject wrong owners payloads and abrupt value uses`` () =
        withSession (fun session path ->
            let reject code parameters resultType body =
                let outcome = session.SimulateCreateFunction(request session "bad" parameters resultType [] body)
                Assert.True(Result.isError outcome)
                Assert.Contains(code, sprintf "%A" outcome)
            reject "AbortOwnerResultTypeMismatch" [] textType (abort "bad")
            reject "AbortPayloadTypeMismatch" [] (result textType) (ExpressionRecipe.Abort(ExpressionRecipe.BoolLiteral true))
            reject "AbortValueUse" [] (result textType) (ok (abort "bad"))
            reject "AbortValueUse" [] (result textType) (ExpressionRecipe.Bind(abort "bad", ok (str "later")))
            reject "AbortValueUse" [] (result textType) (ok (ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral true, abort "a", abort "b")))
            reject "PureExpressionBranchTypeMismatch" [] (result textType)
                (ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral true, ok (str "a"), ExpressionRecipe.ResultOk(textType, ExpressionRecipe.BoolLiteral true)))
            let fn =
                create session "valid" [] (result textType) [] (abort "error")
            let graph = snapshot path
            let check code fn = Assert.Contains(SnapshotValidator.validate { graph with Functions = [ fn ] }, fun issue -> issue.Code = code)
            check "AbortOwnerResultTypeMismatch" { fn with ResultType = textType }
            let payload = fn.Body.Nodes |> List.find (fun node -> match node.Kind with SemanticNodeKind.StringLiteral _ -> true | _ -> false)
            check "AbortPayloadTypeMismatch" { fn with Body = { fn.Body with Nodes = fn.Body.Nodes |> List.map (fun node -> if node.Id = payload.Id then { node with Kind = SemanticNodeKind.BoolLiteral true } else node) } }
            let badUse: SemanticNode = { Id = StableId.createForTesting<GraphNodeTag> "use-abort"; Kind = SemanticNodeKind.ResultOk(textType, fn.Body.ResultNodeId) }
            let invalid = { graph with Functions = [ { fn with Body = { Nodes = fn.Body.Nodes @ [ badUse ]; ResultNodeId = badUse.Id } } ] }
            Assert.Contains(SnapshotValidator.validate invalid, fun issue -> issue.Code = "AbortValueUse")
            Assert.Contains(SnapshotValidator.validate invalid, fun issue -> issue.Code = "AbortNotGraphResult")
            // Decode a malformed saved payload through the full validator, independently of authoring.
            let encoded = File.ReadAllText path
            let malformed = encoded.Replace("\"kind\":\"stringLiteral\",\"value\":\"error\"", "\"kind\":\"boolLiteral\",\"value\":true")
            Assert.True(Result.isError(SnapshotCodec.deserialize malformed)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``nested abort skips effects but Err data and caller recovery continue normally`` () =
        withSession (fun session path ->
            let effect, cap, op = io session
            let capType = TypeRef.Capability cap.Id
            let branch = ExpressionRecipe.MatchOption(ExpressionRecipe.OptionSome(str "check"), abort "bad", str "continue")
            let callee =
                create session "callee" [ "io", capType ] (result textType) [ effect.Id ]
                            (ExpressionRecipe.Bind(invoke (op "mark") (slot 0) (str "before"),
                                ExpressionRecipe.Bind(branch, ok (invoke (op "mark") (slot 0) (str "after")))))
            let recover input = ExpressionRecipe.MatchResult(input, ok (bound RecipePatternSlot.Value), ok (invoke (op "mark") (slot 0) (str "recovered")))
            let caller =
                create session "caller" [ "io", capType ] (result textType) [ effect.Id ]
                            (recover (ExpressionRecipe.DirectCall(callee.Id, [ callee.Parameters.Head.Id, slot 0 ])))
            let data =
                create session "data" [ "io", capType ] (result textType) [ effect.Id ] (recover (ExpressionRecipe.ResultErr(textType, str "ordinary")))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run fn values = PureRun.run reopened fn values None |> unwrap
            let fixture values = fake cap [ "mark", values |> List.map (PureRunValue.String >> returns) ]
            let stopped = run callee.Id [ fixture [ "once" ] ]
            Assert.Equal(errText "bad", stopped.Actual)
            Assert.Equal<string list>([ "mark" ], calls cap stopped.Calls)
            let recovered = run caller.Id [ fixture [ "once"; "recovered" ] ]
            Assert.Equal(okText "recovered", recovered.Actual)
            Assert.Equal(2, recovered.Calls.Length)
            let ordinary = run data.Id [ fixture [ "continued" ] ]
            Assert.Equal(okText "continued", ordinary.Actual)
            Assert.Single ordinary.Calls |> ignore)

    [<Fact>]
    let ``a resource is acquired in the graph that releases it and by that node alone`` () =
        withSession (fun session path ->
            let effect, cap, op = io session
            let capType = TypeRef.Capability cap.Id
            let body = create session "body" [ "handle", textType ] (result textType) [] (ok (slot 0))
            let scoped =
                create session "scoped" [ "io", capType ] (result textType) [ effect.Id ]
                    (ExpressionRecipe.UseResource(invoke (op "open") (slot 0) (str "resource"), slot 0, (op "release").Id, body.Id, []))
            // Authoring places a branch's acquisition in that branch, so a scoped resource inside
            // control flow already satisfies the rule.
            create session "branched" [ "io", capType; "flag", boolType ] (result textType) [ effect.Id ]
                (ExpressionRecipe.Conditional(slot 1,
                    ExpressionRecipe.UseResource(invoke (op "open") (slot 0) (str "resource"), slot 0, (op "release").Id, body.Id, []),
                    ExpressionRecipe.ResultErr(textType, str "skipped"))) |> ignore
            let graph = snapshot path
            Assert.Empty(SnapshotValidator.validate graph)
            let codes fn = SnapshotValidator.validate { graph with Functions = [ body; fn ] } |> List.map (fun issue -> issue.Code)
            let useNode = scoped.Body.Nodes |> List.find (fun node -> match node.Kind with SemanticNodeKind.UseResource _ -> true | _ -> false)
            let acquireId = match useNode.Kind with SemanticNodeKind.UseResource(acquireId, _, _, _, _) -> acquireId | _ -> failwith "unreachable"
            let acquire = scoped.Body.Nodes |> List.find (fun node -> node.Id = acquireId)
            let rest = scoped.Body.Nodes |> List.filter (fun node -> node.Id <> acquireId && node.Id <> useNode.Id)
            let withNodes nodes = { scoped with Body = { scoped.Body with Nodes = nodes } }
            // One acquisition handed to two scopes would be released twice.
            let twice = { useNode with Id = StableId.createForTesting<GraphNodeTag> "use-resource-twice" }
            Assert.Equal<string list>([ "UseResourceAcquireShared"; "UseResourceAcquireShared" ], codes (withNodes (scoped.Body.Nodes @ [ twice ])))
            // Any other consumer breaks the guarantee, in the same graph or in a branch.
            let peek: SemanticNode = { Id = StableId.createForTesting<GraphNodeTag> "use-resource-peek"; Kind = SemanticNodeKind.ValueEqual(acquireId, acquireId) }
            Assert.Equal<string list>([ "UseResourceAcquireShared" ], codes (withNodes (scoped.Body.Nodes @ [ peek ])))
            let conditionId = StableId.createForTesting<GraphNodeTag> "use-resource-condition"
            let branchId = StableId.createForTesting<GraphNodeTag> "use-resource-branch"
            let errBranch suffix extra: ValueGraph =
                let payloadId = StableId.createForTesting<GraphNodeTag> ("use-resource-payload-" + suffix)
                let errId = StableId.createForTesting<GraphNodeTag> ("use-resource-err-" + suffix)
                { Nodes = extra @ [ { Id = payloadId; Kind = SemanticNodeKind.StringLiteral "skipped" }; { Id = errId; Kind = SemanticNodeKind.ResultErr(textType, payloadId) } ]
                  ResultNodeId = errId }
            let branched outer (whenTrue: ValueGraph) =
                { scoped with
                    Body =
                        { Nodes = outer @ [ { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral false }; { Id = branchId; Kind = SemanticNodeKind.Conditional(conditionId, whenTrue, errBranch "false" []) } ]
                          ResultNodeId = branchId } }
            Assert.Equal<string list>([ "UseResourceAcquireShared" ], codes (branched (rest @ [ acquire; useNode ]) (errBranch "peek" [ peek ])))
            // An acquisition outside the branch that releases it happens even when the branch is not taken.
            Assert.Equal<string list>([ "UseResourceAcquireOutOfGraph" ], codes (branched (rest @ [ acquire ]) { Nodes = [ useNode ]; ResultNodeId = useNode.Id }))
            // Acquired inside the branch, it is released whenever it is acquired.
            Assert.Equal<string list>([], codes (branched rest { Nodes = [ acquire; useNode ]; ResultNodeId = useNode.Id })))

    [<Fact; Trait("Suite", "Extended")>]
    let ``abort releases acquired resources exactly once with typed and host failure precedence`` () =
        withSession (fun session _ ->
            let effect, cap, op = io session
            let capType = TypeRef.Capability cap.Id
            let body =
                create session "body" [ "io", capType; "fail", boolType; "handle", textType ] (result textType) [ effect.Id ]
                            (ExpressionRecipe.Bind(invoke (op "mark") (slot 0) (str "body"),
                                ExpressionRecipe.Bind(
                                    ExpressionRecipe.Conditional(slot 1, ExpressionRecipe.Abort(invoke (op "mark") (slot 0) (str "payload")), str "continue"),
                                    ok (invoke (op "mark") (slot 0) (str "after")))))
            let scoped =
                create session "scoped" [ "io", capType; "fail", boolType ] (result textType) [ effect.Id ]
                            (ExpressionRecipe.UseResource(invoke (op "open") (slot 0) (str "resource"), slot 0, (op "release").Id, body.Id,
                                [ body.Parameters[0].Id, slot 0; body.Parameters[1].Id, slot 1 ]))
            let run fail opened marked released =
                PureRun.run session scoped.Id
                    [ fake cap [ "open", opened; "mark", marked; "release", released ]; PureRunValue.Bool fail ] None
            let opened = [ returns (okText "handle") ]
            let marked = [ returns (PureRunValue.String "seen"); returns (PureRunValue.String "body error") ]
            let released = [ returns (unitResult true PureRunValue.Unit) ]
            let stopped = run true opened marked released |> unwrap
            Assert.Equal(errText "body error", stopped.Actual)
            Assert.Equal<string list>([ "open"; "mark"; "mark"; "release" ], calls cap stopped.Calls)
            let both = run true opened marked [ returns (unitResult false (PureRunValue.String "release error")) ] |> unwrap
            Assert.Equal(errText "body error", both.Actual)
            Assert.Equal<string list>([ "open"; "mark"; "mark"; "release" ], calls cap both.Calls)
            let acquireFailure = run true [ returns (errText "missing") ] [] [] |> unwrap
            Assert.Equal(errText "missing", acquireFailure.Actual)
            Assert.Equal<string list>([ "open" ], calls cap acquireFailure.Calls)
            let successReleaseError = run false opened marked [ returns (unitResult false (PureRunValue.String "release error")) ] |> unwrap
            Assert.Equal(errText "release error", successReleaseError.Actual)
            let assertFault expected outcome =
                match outcome with
                | Error(PureRunError.RuntimeFailure failure) ->
                    Assert.Equal(expected, failure.Message)
                    Assert.Equal("release", calls cap failure.Calls |> List.last)
                | other -> failwithf "Expected host failure after cleanup: %A" other
            run true opened marked [ PureRunFakeResponse.Throw "release fault" ] |> assertFault "release fault"
            run true opened [ PureRunFakeResponse.Throw "body fault" ] [ PureRunFakeResponse.Throw "release fault" ] |> assertFault "body fault"
            // The inner helper aborts locally; explicit propagation in the outer helper keeps both releases.
            let inner =
                create session "inner" [ "handle", textType ] (result textType) [] (abort "inner error")
            let innerScope = ExpressionRecipe.UseResource(ok (str "inner"), slot 0, (op "release").Id, inner.Id, [])
            let outerBody =
                create session "outerBody" [ "io", capType; "handle", textType ] (result textType) [ effect.Id ]
                                (ExpressionRecipe.MatchResult(innerScope, ok (bound RecipePatternSlot.Value), ExpressionRecipe.Abort(bound RecipePatternSlot.Error)))
            let nested =
                create session "nested" [ "io", capType ] (result textType) [ effect.Id ]
                            (ExpressionRecipe.UseResource(ok (str "outer"), slot 0, (op "release").Id, outerBody.Id, [ outerBody.Parameters.Head.Id, slot 0 ]))
            let cleaned = PureRun.run session nested.Id [ fake cap [ "release", List.replicate 2 (returns (unitResult true PureRunValue.Unit)) ] ] None |> unwrap
            Assert.Equal(errText "inner error", cleaned.Actual)
            Assert.Equal<PureRunValue list>([ PureRunValue.String "inner"; PureRunValue.String "outer" ], cleaned.Calls |> List.map (fun call -> call.Arguments.Head |> snd))
            // An abort in a program-defined release helper becomes its own typed Err. It
            // cannot consume the body's Err or bypass the enclosing scope's release.
            session.SimulateCreateCapability
                { ExpectedBaseSnapshotId = current session; DisplayName = "PureRelease"
                  Operations = [ { DisplayName = "release"; Parameters = [ { DisplayName = "handle"; Type = textType } ]; ResultType = result unitType; FailureType = None; EffectId = effect.Id } ] }
            |> unwrap |> publish session
            let pureCap = session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = "PureRelease")
            let helper =
                create session "releaseHelper" [ "handle", textType ] (result unitType) [] (abort "helper error")
            let programBody =
                create session "programBody" [ "handle", textType ] (result textType) [ effect.Id ]
                                (ExpressionRecipe.UseResource(ok (str "inner"), ExpressionRecipe.CapabilityConstruct(pureCap.Id, [ pureCap.Operations.Head.Id, helper.Id ]), pureCap.Operations.Head.Id, inner.Id, []))
            let programEntry =
                create session "programEntry" [ "io", capType ] (result textType) [ effect.Id ]
                                (ExpressionRecipe.UseResource(ok (str "outer"), slot 0, (op "release").Id, programBody.Id, []))
            let programCleaned = PureRun.run session programEntry.Id [ fake cap [ "release", [ returns (unitResult true PureRunValue.Unit) ] ] ] None |> unwrap
            Assert.Equal(errText "inner error", programCleaned.Actual)
            Assert.Equal(PureRunValue.String "outer", snd (Assert.Single programCleaned.Calls).Arguments.Head))

    [<Fact; Trait("Suite", "Extended")>]
    let ``parallel abort cancels cooperatively joins every worker and releases the surrounding resource last`` () =
        withSession (fun session path ->
            let effect, cap, op = io session
            let capType = TypeRef.Capability cap.Id
            // Keep source order and payloads fixed while reversing completion order. The delayed
            // worker can only finish after the other worker's Err requests cancellation.
            let workerBody =
                create session "workerBody" [ "io", capType; "delayed", textType; "name", textType ] (result textType) [ effect.Id ]
                    (ExpressionRecipe.Conditional(ExpressionRecipe.StringEqual(slot 2, slot 1),
                        ExpressionRecipe.Bind(invoke (op "mark") (slot 0) (str "wait"), ExpressionRecipe.Abort(ExpressionRecipe.StringConcat(slot 2, str " error"))),
                        ExpressionRecipe.ResultErr(textType, ExpressionRecipe.StringConcat(slot 2, str " error"))))
            let worker =
                create session "worker" [ "io", capType; "delayed", textType; "name", textType ] (result textType) [ effect.Id ]
                    (ExpressionRecipe.UseResource(ok (slot 2), slot 0, (op "release").Id, workerBody.Id,
                        [ workerBody.Parameters[0].Id, slot 0; workerBody.Parameters[1].Id, slot 1 ]))
            let join =
                create session "join" [ "io", capType; "delayed", textType; "resource", textType ] (result (listOf textType)) [ effect.Id ]
                    (ExpressionRecipe.ParallelAll(ExpressionRecipe.ListConstruct(textType, [ str "first"; str "second" ]), worker.Id,
                        [ worker.Parameters[0].Id, slot 0; worker.Parameters[1].Id, slot 1 ]))
            let entry =
                create session "entry" [ "io", capType; "delayed", textType ] (result (listOf textType)) [ effect.Id ]
                    (ExpressionRecipe.UseResource(ok (str "outer"), slot 0, (op "release").Id, join.Id,
                        [ join.Parameters[0].Id, slot 0; join.Parameters[1].Id, slot 1 ]))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            for delayed in [ "first"; "second" ] do
                let completed = PureRun.run reopened entry.Id
                                    [ fake cap [ "mark", [ PureRunFakeResponse.AwaitCancellation(PureRunValue.String "cancelled") ]
                                                 "release", List.replicate 3 (returns (unitResult true PureRunValue.Unit)) ]
                                      PureRunValue.String delayed ] None |> unwrap
                Assert.Equal(PureRunValue.ResultValue(listOf textType, textType, false, PureRunValue.String "first error"), completed.Actual)
                Assert.Equal(4, completed.Calls.Length)
                let released = completed.Calls |> List.filter (fun call -> call.OperationId = (op "release").Id)
                let handles = released |> List.map (fun call -> snd call.Arguments.Head)
                Assert.Equal<PureRunValue list>([ PureRunValue.String "first"; PureRunValue.String "outer"; PureRunValue.String "second" ], List.sort handles)
                Assert.Equal(PureRunValue.String delayed, handles[1])
                Assert.Equal(PureRunValue.String "outer", snd (List.last completed.Calls).Arguments.Head)
            // A host fault from a worker still outranks an already available typed error.
            match PureRun.run reopened entry.Id
                    [ fake cap [ "mark", [ PureRunFakeResponse.Throw "worker fault" ]; "release", List.replicate 3 (returns (unitResult true PureRunValue.Unit)) ]
                      PureRunValue.String "second" ] None with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("worker fault", failure.Message)
                Assert.Equal(4, failure.Calls.Length)
                Assert.Equal(PureRunValue.String "outer", snd (List.last failure.Calls).Arguments.Head)
            | other -> failwithf "Host fault precedence was lost: %A" other)

    [<Fact; Trait("Suite", "Extended")>]
    let ``ordinary symbolic and exact recipes support reviewed target replacement inspect and reopen`` () =
        withSession (fun session path ->
            let text = """{"kind":"primitive","primitive":"string"}"""
            let resultType = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{text},{text}]}}"""
            use created = send session ($"""{{"id":"create","op":"simulateCreateFunctionByName","structuredReview":true,"displayName":"wire","parameters":[{{"displayName":"input","type":{text}}}],"resultType":{resultType},"requiredEffects":[],"body":{{"kind":"abort","error":{{"kind":"stringLiteral","value":"initial"}}}}}}""")
            Assert.Contains("ownerFunctionId", created.RootElement.GetRawText())
            publishWire session created
            let fn = find session "wire"
            let id, target = StableId.value fn.Id, StableId.value fn.Body.ResultNodeId
            use inspected = send session ($"""{{"id":"inspect","op":"readFunction","functionId":"{id}"}}""")
            Assert.Contains("\"kind\":\"abort\"", inspected.RootElement.GetRawText())
            let focus = session.FocusFunction fn.Id |> unwrap
            let boundary = Assert.Single focus.Aborts
            Assert.Equal(fn.Id, boundary.OwnerFunctionId)
            Assert.Equal(Some textType, boundary.ErrorType)
            let html = ViewerProjection.focus focus |> ViewerHtml.renderFocusDocument
            Assert.Contains("Function abort boundaries", html)
            Assert.Contains(target, html)
            let before = File.ReadAllText path
            let replace body =
                send session ($"""{{"id":"edit","op":"simulateReplaceTarget","structuredReview":true,"functionId":"{id}","targetNodeId":"{target}","replacement":{body}}}""")
            use toNormal = replace ($"""{{"kind":"resultOk","errorType":{text},"value":{{"kind":"stringLiteral","value":"normal"}}}}""")
            Assert.Equal(before, File.ReadAllText path)
            publishWire session toNormal
            Assert.Equal(fn.Body.ResultNodeId, (find session "wire").Body.ResultNodeId)
            Assert.Equal(okText "normal", (PureRun.run session fn.Id [ PureRunValue.String "input" ] None |> unwrap).Actual)
            let normalFunction = find session "wire"
            let scalar = normalFunction.Body.Nodes |> List.find (fun node -> match node.Kind with SemanticNodeKind.StringLiteral _ -> true | _ -> false)
            let saved = File.ReadAllText path
            use invalid = AuthoringProtocol.executeLine session ($"""{{"id":"invalid","op":"simulateReplaceTarget","functionId":"{id}","targetNodeId":"{StableId.value scalar.Id}","replacement":{{"kind":"abort","error":{{"kind":"stringLiteral","value":"invalid"}}}}}}""") |> JsonDocument.Parse
            Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean())
            Assert.Contains("TargetedExpressionAbruptValueUse", invalid.RootElement.GetRawText())
            Assert.Equal(saved, File.ReadAllText path)
            use toAbort = replace """{"kind":"abort","error":{"kind":"stringLiteral","value":"changed"}}"""
            publishWire session toAbort
            Assert.Equal(fn.Id, (find session "wire").Id)
            Assert.Equal(fn.Body.ResultNodeId, (find session "wire").Body.ResultNodeId)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(fn.Body.ResultNodeId, (find reopened "wire").Body.ResultNodeId)
            Assert.Equal<SemanticParameter list>(fn.Parameters, (find reopened "wire").Parameters)
            use executed = send reopened ($"""{{"id":"run","op":"runPure","functionId":"{id}","arguments":[{{"kind":"string","value":"input"}}]}}""")
            Assert.Contains("changed", executed.RootElement.GetRawText())
            Assert.Equal(errText "changed", (PureRun.run reopened fn.Id [ PureRunValue.String "input" ] None |> unwrap).Actual))

    [<Fact>]
    let ``abort never erases effect requirements or permits ambiguous sibling effects`` () =
        withSession (fun session _ ->
            let effect, cap, op = io session
            let capType = TypeRef.Capability cap.Id
            let body = ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral true, abort "stop", ok (invoke (op "mark") (slot 0) (str "unreachable")))
            let missing = session.SimulateCreateFunction(request session "missing" [ "io", capType ] (result textType) [] body)
            Assert.True(Result.isError missing)
            Assert.Contains("MissingEffectRequirement", sprintf "%A" missing)
            let fn =
                create session "declared" [ "io", capType ] (result textType) [ effect.Id ] body
            Assert.Equal<EffectId list>([ effect.Id ], (session.FocusFunction fn.Id |> unwrap).BodyEffectRequirements.EffectIds)
            // A local abort and an independent effect cannot be scheduled by node ID.
            let ambiguous = ExpressionRecipe.Bind(
                                ExpressionRecipe.StringConcat(
                                    ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral true, abort "stop", str "a"),
                                    invoke (op "mark") (slot 0) (str "sibling")), ok (str "done"))
            let outcome = session.SimulateCreateFunction(request session "ambiguous" [ "io", capType ] (result textType) [ effect.Id ] ambiguous)
            Assert.True(Result.isError outcome)
            Assert.Contains("AmbiguousEffectOrder", sprintf "%A" outcome))

    [<Fact; Trait("Suite", "Extended")>]
    let ``generic abort pure function values and union matches retain their own invocation boundaries`` () =
        withSession (fun session _ ->
            let errorType = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "error-type", "E")
            let generic =
                create session "generic" [ "error", errorType ] (TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ textType; errorType ])) [] (ExpressionRecipe.Abort(slot 0))
            let concrete =
                create session "concrete" [] (result textType) []
                                (ExpressionRecipe.DirectCall(generic.Id, [ generic.Parameters.Head.Id, str "generic error" ]))
            let invocation = ExpressionRecipe.FunctionInvoke(ExpressionRecipe.FunctionReference concrete.Id, [])
            let consumer =
                create session "consumer" [] (result textType) []
                                (ExpressionRecipe.MatchResult(invocation, ok (bound RecipePatternSlot.Value), ok (bound RecipePatternSlot.Error)))
            Assert.Equal(okText "generic error", (PureRun.run session consumer.Id [] None |> unwrap).Actual)
            session.SimulateCreateType
                { TypeParameters = []; ExpectedBaseSnapshotId = current session; DisplayName = "Choice"
                  Kind = AgentTypeDeclarationKindIntent.Union [ { DisplayName = "Left"; Fields = [] }; { DisplayName = "Right"; Fields = [] } ] }
            |> unwrap |> publish session
            let choice = session.Declarations().Types |> List.exactlyOne
            let variants = match choice.Kind with SemanticTypeKind.Union variants -> variants | _ -> failwith "union"
            let left, right = variants[0].Id, variants[1].Id
            let union =
                create session "union" [ "left", boolType ] (result textType) []
                            (ExpressionRecipe.MatchUnion(
                                ExpressionRecipe.Conditional(slot 0, ExpressionRecipe.UnionConstruct(left, [], []), ExpressionRecipe.UnionConstruct(right, [], [])),
                                [ [ left ], abort "left"; [ right ], ok (str "right") ]))
            for condition, expected in [ true, errText "left"; false, okText "right" ] do
                Assert.Equal(expected, (PureRun.run session union.Id [ PureRunValue.Bool condition ] None |> unwrap).Actual)
            // All-aborting control flow generates no merge value or fabricated default result.
            let all =
                create session "all" [ "condition", boolType ] (result textType) []
                        (ExpressionRecipe.Conditional(slot 0, abort "left", ExpressionRecipe.MatchResult(ExpressionRecipe.ResultErr(textType, str "right"), abort "ok", ExpressionRecipe.Abort(bound RecipePatternSlot.Error))))
            for condition, expected in [ true, "left"; false, "right" ] do
                Assert.Equal(errText expected, (PureRun.run session all.Id [ PureRunValue.Bool condition ] None |> unwrap).Actual))

    [<Fact>]
    let ``proposals reject a hidden intrinsic helper beside local abort while legacy snapshots stay reopenable`` () =
        withSession (fun session path ->
            let ambient = create session "ambient" [] boolType [] ExpressionRecipe.CancellationRequested
            let hop = create session "hop" [] boolType [] (ExpressionRecipe.DirectCall(ambient.Id, []))
            let unsequenced callee =
                ExpressionRecipe.Bind(
                    ExpressionRecipe.StringConcat(
                        ExpressionRecipe.Conditional(ExpressionRecipe.BoolLiteral true, abort "stop", str "a"),
                        ExpressionRecipe.Conditional(ExpressionRecipe.DirectCall(callee, []), str "yes", str "no")),
                    ok (str "done"))
            let before = File.ReadAllBytes path
            // Proposal time applies the lowering rule: the helper's CancellationRequested is observable.
            let rejected = session.SimulateCreateFunction(request session "legacy" [] (result textType) [] (unsequenced hop.Id))
            Assert.True(Result.isError rejected)
            Assert.Contains("AmbiguousEffectOrder", sprintf "%A" rejected)
            Assert.Equal<byte>(before, File.ReadAllBytes path)
            // A snapshot published under the historical abort-only rule: the caller was admitted
            // while its helper was pure, and the helper became intrinsically effectful outside the
            // proposal path. Full validation, and so reopening, keeps accepting it.
            let pureHop = create session "pureHop" [] boolType [] (ExpressionRecipe.BoolLiteral true)
            let legacy = create session "legacy" [] (result textType) [] (unsequenced pureHop.Id)
            let published = snapshot path
            let tamperedHop =
                published.Functions |> List.find (fun fn -> fn.Id = pureHop.Id) |> fun fn ->
                    { fn with Body = { fn.Body with Nodes = [ { Id = fn.Body.ResultNodeId; Kind = SemanticNodeKind.DirectCall(ambient.Id, []) } ] } }
            let historical = { published with Functions = published.Functions |> List.map (fun fn -> if fn.Id = pureHop.Id then tamperedHop else fn) }
            Assert.Empty(SnapshotValidator.validate historical)
            session.Close()
            File.WriteAllText(path, SnapshotCodec.serialize historical |> unwrap)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(legacy.Id, (find reopened "legacy").Id)
            match reopened.ProjectExecution() with
            | Error(AuthoringSessionError.ExecutionLoweringFailure(ExecutionIrError.AmbiguousEffectOrder(owner, _, _))) -> Assert.Equal(legacy.Id, owner)
            | other -> failwithf "A hidden intrinsic helper was lowered beside local Abort: %A" other
            // Proposals that leave the legacy function untouched still publish.
            create reopened "unrelated" [] boolType [] (ExpressionRecipe.BoolLiteral false) |> ignore
            // Edits to the legacy function are held to the runtime rule: an ambiguous edit is
            // rejected and an ordered repair publishes and lowers.
            let status = reopened.Status() |> unwrap
            let still: AgentReplaceFunctionBodyWithExpressionRecipe =
                { TargetFunctionId = legacy.Id
                  ExpectedBaseSnapshotId = status.SnapshotId
                  ExpectedCurrentResultNodeId = legacy.Body.ResultNodeId
                  Replacement = unsequenced hop.Id }
            let stillRejected = reopened.SimulateReplaceBody still
            Assert.True(Result.isError stillRejected)
            Assert.Contains("AmbiguousEffectOrder", sprintf "%A" stillRejected)
            let edit = { still with Replacement = ExpressionRecipe.Bind(ExpressionRecipe.DirectCall(hop.Id, []), abort "stop") }
            let proposal = reopened.SimulateReplaceBody edit |> unwrap
            publish reopened proposal
            let execution = reopened.ProjectExecution() |> unwrap
            Assert.Contains(execution.Functions, fun fn -> fn.SemanticFunctionId = legacy.Id))

    [<Fact>]
    let ``abort free callers of intrinsic helpers are ordered at proposal time including callee changes`` () =
        withSession (fun session path ->
            let ambient = create session "ambient" [] boolType [] ExpressionRecipe.CancellationRequested
            let twice callee =
                ExpressionRecipe.StringConcat(
                    ExpressionRecipe.Conditional(ExpressionRecipe.DirectCall(callee, []), str "a", str "b"),
                    ExpressionRecipe.Conditional(ExpressionRecipe.DirectCall(callee, []), str "c", str "d"))
            let before = File.ReadAllBytes path
            // No Abort and no declared effects anywhere: the helper's CancellationRequested alone
            // makes the two calls observable, so their order must be explicit.
            let rejected = session.SimulateCreateFunction(request session "unordered" [] textType [] (twice ambient.Id))
            Assert.True(Result.isError rejected)
            Assert.Contains("AmbiguousEffectOrder", sprintf "%A" rejected)
            Assert.Equal<byte>(before, File.ReadAllBytes path)
            // Unordered calls to a pure helper are fine...
            let probe = create session "probe" [] boolType [] (ExpressionRecipe.BoolLiteral true)
            let caller = create session "caller" [] textType [] (twice probe.Id)
            session.ProjectExecution() |> unwrap |> ignore
            // ...until the helper becomes observable: the unchanged caller would be newly ambiguous.
            let edit: AgentReplaceFunctionBodyWithExpressionRecipe =
                { TargetFunctionId = probe.Id
                  ExpectedBaseSnapshotId = current session
                  ExpectedCurrentResultNodeId = probe.Body.ResultNodeId
                  Replacement = ExpressionRecipe.CancellationRequested }
            let beforeEdit = File.ReadAllBytes path
            let refused = session.SimulateReplaceBody edit
            Assert.True(Result.isError refused)
            Assert.Contains("AmbiguousEffectOrder", sprintf "%A" refused)
            Assert.Contains(StableId.value caller.Id, sprintf "%A" refused)
            Assert.Equal<byte>(beforeEdit, File.ReadAllBytes path)
            session.ProjectExecution() |> unwrap |> ignore)
