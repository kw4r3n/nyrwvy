namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// BoolNot, and BoolAnd/BoolOr whose right operand is a nested value graph evaluated only when needed.
module BoolOperatorTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private result t = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ t; textType ])
    let private str = ExpressionRecipe.StringLiteral
    let private bool = ExpressionRecipe.BoolLiteral
    let private slot = ExpressionRecipe.ParameterSlot
    let private bound slot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = slot }
    let private abort message = ExpressionRecipe.Abort(str message)
    let private ok value = ExpressionRecipe.ResultOk(textType, value)
    let private boolNot = ExpressionRecipe.BoolNot
    let private boolAnd left right = ExpressionRecipe.BoolAnd(left, right)
    let private boolOr left right = ExpressionRecipe.BoolOr(left, right)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
          TypeParameters = None
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = effects; Body = body }
    let private create session name parameters resultType effects body =
        request session name parameters resultType effects body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private rejects (session: AuthoringSession) name parameters resultType effects body =
        match session.SimulateCreateFunction(request session name parameters resultType effects body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwithf "Function '%s' was accepted." name
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-bool-" + Guid.NewGuid().ToString("N"))
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
        session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "IO" } |> unwrap |> publish session
        let effect = session.Declarations().Effects |> List.exactlyOne
        session.SimulateCreateCapability
            { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "IO"
              Operations =
                [ { DisplayName = "mark"; Parameters = [ { DisplayName = "value"; Type = textType } ]
                    ResultType = textType; FailureType = None; EffectId = effect.Id } ] }
        |> unwrap |> publish session
        let cap = session.Declarations().Capabilities |> List.exactlyOne
        effect, cap, cap.Operations.Head
    /// A Bool right operand whose evaluation is observable: it invokes `mark` on the first parameter.
    let private probe (op: SemanticCapabilityOperation) =
        ExpressionRecipe.StringEqual(ExpressionRecipe.CapabilityInvoke(slot 0, op.Id, [ op.Parameters.Head.Id, str "probe" ]), str "yes")
    let private fake (cap: SemanticCapability) responses =
        PureRunValue.FakeCapability(cap.Id, [ { OperationId = cap.Operations.Head.Id; Cases = None; Responses = responses |> List.map (PureRunValue.String >> PureRunFakeResponse.Return) } ])
    let private okBool value = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Bool value)
    let private errBool value = PureRunValue.ResultErr(PureRunScalarKind.Bool, PureRunScalar.String value)
    let private truthTable =
        [ for left in [ false; true ] do
              for right in [ false; true ] do
                  left, right ]

    [<Fact>]
    let ``bool operators reject non-Bool operands in recipes and persisted graphs`` () =
        withSession (fun session path ->
            let mismatch = "PureExpressionOperandTypeMismatch"
            Assert.Contains(mismatch, rejects session "notInt" [ "value", intType ] boolType [] (boolNot (slot 0)))
            Assert.Contains(mismatch, rejects session "andLeft" [ "value", intType ] boolType [] (boolAnd (slot 0) (bool true)))
            Assert.Contains(mismatch, rejects session "andRight" [ "value", intType ] boolType [] (boolAnd (bool true) (slot 0)))
            Assert.Contains(mismatch, rejects session "orRight" [] boolType [] (boolOr (bool false) (str "text")))
            // The result is Bool, whatever the operands.
            Assert.NotEqual<string>("", rejects session "notText" [ "value", boolType ] textType [] (boolNot (slot 0)))
            // A right operand may not abort outside a Result-returning function, as in a Conditional branch.
            Assert.Contains("AbortOwnerResultTypeMismatch", rejects session "abortRight" [ "value", boolType ] boolType [] (boolAnd (slot 0) (abort "stop")))
            let both = create session "both" [ "left", boolType; "right", boolType ] boolType [] (boolAnd (boolNot (slot 0)) (slot 1))
            let committed = snapshot path
            Assert.Empty(SnapshotValidator.validate committed)
            let breakParameter index =
                let parameterId = both.Parameters[index].Id
                { committed with
                    Functions =
                        committed.Functions |> List.map (fun fn ->
                            { fn with
                                Body =
                                    fn.Body |> ValueGraph.mapNodes (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference id when id = parameterId ->
                                            { node with Kind = SemanticNodeKind.StringLiteral "text" }
                                        | _ -> node) }) }
            // Index 0 feeds BoolNot in the parent graph; index 1 is the result of the nested right graph.
            for index in [ 0; 1 ] do
                Assert.Contains(SnapshotValidator.validate (breakParameter index), fun issue -> issue.Code = "BoolOperandTypeMismatch"))

    [<Theory>]
    [<InlineData("shared", "")>]
    [<InlineData("escape", "ValueNodeOutOfScope")>]
    [<InlineData("cycle", "GraphNodeDependencyCycle")>]
    let ``right operand graphs keep lexical scope and cycle diagnostics`` referenceKind expectedCode =
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-bool-scope-" + name)
        let leftId, andId, innerId, notId = nodeId "left", nodeId "and", nodeId "inner", nodeId "not"
        // The right graph may read the parent's nodes; the parent may not read a node inside the right graph.
        let dependencyId, negatedId =
            match referenceKind with
            | "shared" -> leftId, andId
            | "escape" -> leftId, innerId
            | "cycle" -> andId, andId
            | other -> failwithf "Unknown fixture case: %s" other
        let right: ValueGraph =
            { Nodes = [ { Id = innerId; Kind = SemanticNodeKind.BoolNot dependencyId } ]; ResultNodeId = innerId }
        let body: ValueGraph =
            { Nodes =
                [ { Id = leftId; Kind = SemanticNodeKind.BoolLiteral true }
                  { Id = andId; Kind = SemanticNodeKind.BoolAnd(leftId, right) }
                  { Id = notId; Kind = SemanticNodeKind.BoolNot negatedId } ]
              ResultNodeId = notId }
        let fn: SemanticFunction =
            { Exported = true; Id = StableId.createForTesting<FunctionTag> "function-bool-scope"
              TypeParameters = []
              DisplayName = "scope"; Parameters = []; ResultType = boolType; RequiredEffects = []; Body = body }
        let issues =
            SnapshotValidator.validate
                { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-bool-scope") with Functions = [ fn ] }
        let expectedCodes = if expectedCode = "" then [] else [ expectedCode ]
        Assert.Equal<string list>(expectedCodes, issues |> List.map (fun issue -> issue.Code))

    [<Fact>]
    let ``effect checks cover the right operand without hoisting it`` () =
        withSession (fun session path ->
            let effect, cap, op = io session
            let capability = [ "io", TypeRef.Capability cap.Id ]
            for name, body in [ "andMissing", boolAnd (bool false) (probe op); "orMissing", boolOr (bool true) (probe op) ] do
                Assert.Contains("MissingEffectRequirement", rejects session name capability boolType [] body)
            let fn = create session "declared" capability boolType [ effect.Id ] (boolOr (bool true) (probe op))
            Assert.Equal<EffectId list>([ effect.Id ], (session.FocusFunction fn.Id |> unwrap).BodyEffectRequirements.EffectIds)
            let committed = snapshot path
            let undeclared =
                { committed with Functions = committed.Functions |> List.map (fun item -> { item with RequiredEffects = [] }) }
            Assert.Contains(SnapshotValidator.validate undeclared, fun issue -> issue.Code = "MissingEffectRequirement")
            // The invocation stays inside the nested graph: the parent graph holds only the left operand and the BoolOr.
            let orNode = fn.Body.Nodes |> List.find (fun node -> node.Id = fn.Body.ResultNodeId)
            match orNode.Kind with
            | SemanticNodeKind.BoolOr(_, right) ->
                Assert.DoesNotContain(fn.Body.Nodes, fun node -> match node.Kind with SemanticNodeKind.CapabilityInvoke _ -> true | _ -> false)
                Assert.Contains(ValueGraph.collectNodes right, fun node -> match node.Kind with SemanticNodeKind.CapabilityInvoke _ -> true | _ -> false)
            | other -> failwithf "Expected a BoolOr result, found %A" other
            // A right operand's effect is observable to ordering: unordered with a local abort, it is ambiguous.
            let ambiguous =
                ExpressionRecipe.Bind(
                    ExpressionRecipe.StringConcat(
                        ExpressionRecipe.Conditional(bool true, abort "stop", str "a"),
                        ExpressionRecipe.Conditional(boolAnd (bool true) (probe op), str "b", str "c")),
                    ok (bound RecipePatternSlot.Value))
            Assert.Contains("AmbiguousEffectOrder", rejects session "ambiguous" capability (result textType) [ effect.Id ] ambiguous))

    [<Fact>]
    let ``exact-ID and name-resolved recipes persist reopen and project the dedicated constructs`` () =
        withSession (fun session path ->
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let exact =
                """{"kind":"boolOr","left":{"kind":"boolNot","value":{"kind":"parameterSlot","index":0}},"right":{"kind":"boolAnd","left":{"kind":"parameterSlot","index":0},"right":{"kind":"parameterSlot","index":1}}}"""
            use exactProposal =
                send session (sprintf """{"id":"exact","op":"simulateCreateFunction","exported":true,"displayName":"implies","parameters":[{"displayName":"a","type":%s},{"displayName":"b","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}""" (primitive "bool") (primitive "bool") (primitive "bool") exact)
            publishWire session exactProposal
            let named =
                """{"kind":"boolAnd","left":{"kind":"boolOr","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}},"right":{"kind":"boolNot","value":{"kind":"boolAnd","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}}}"""
            use namedProposal =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"xor","parameters":[{"displayName":"a","type":%s},{"displayName":"b","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}""" (primitive "bool") (primitive "bool") (primitive "bool") named)
            Assert.Contains("\"kind\":\"boolAnd\"", namedProposal.RootElement.GetRawText())
            publishWire session namedProposal
            use unknownName =
                AuthoringProtocol.executeLine session (sprintf """{"id":"bad","op":"simulateCreateFunctionByName","exported":true,"displayName":"bad","parameters":[],"resultType":%s,"requiredEffects":[],"body":{"kind":"boolOr","left":{"kind":"boolLiteral","value":true},"right":{"kind":"parameter","name":"missing"}}}""" (primitive "bool")) |> JsonDocument.Parse
            Assert.False(unknownName.RootElement.GetProperty("ok").GetBoolean())
            let xor = find session "xor"
            let viewer = session.FocusFunction xor.Id |> unwrap |> ViewerProjection.focus
            let andNode = viewer.Graph.Nodes |> List.find (fun node -> node.Id = xor.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.Operation "BoolAnd", andNode.Role)
            Assert.Equal(ViewerBranchRole.RightOperand, (List.exactlyOne andNode.Branches).Role)
            Assert.Equal(ValueDependencyRole.LeftOperand, (List.exactlyOne andNode.ValueDependencies).Role)
            Assert.Contains((List.exactlyOne andNode.Branches).Graph.Nodes, fun node -> node.Role = ViewerNodeRole.Operation "BoolNot")
            Assert.Contains(viewer.Graph.Nodes, fun node -> node.Role = ViewerNodeRole.Operation "BoolOr")
            let html = ViewerHtml.renderFocusDocument viewer
            Assert.Contains("bool and", html)
            Assert.Contains("right operand", html)
            session.Close()
            let encoded = File.ReadAllText path
            for kind in [ "boolNot"; "boolAnd"; "boolOr" ] do
                Assert.Contains($"\"kind\":\"{kind}\"", encoded)
            let graph = snapshot path
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            for kind in [ "bool-not"; "bool-and"; "bool-or" ] do
                Assert.Contains(kind, nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            let typedText = TypedSemanticIrSerializer.serialize typed
            for kind in [ "boolNot"; "boolAnd"; "boolOr" ] do
                Assert.Contains($"\"kind\":\"{kind}\"", typedText)
            // Execution IR keeps BoolNot as an instruction and lowers BoolAnd/BoolOr to branch blocks.
            let executionText = ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize
            Assert.Contains("\"kind\":\"boolNot\"", executionText)
            Assert.Contains("/right", executionText)
            Assert.Contains("/decided", executionText)
            Assert.DoesNotContain("\"kind\":\"boolAnd\"", executionText)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(xor.Body.ResultNodeId, (find reopened "xor").Body.ResultNodeId))

    [<Fact; Trait("Suite", "Extended")>]
    let ``not and or follow their truth tables through runPure`` () =
        withSession (fun session path ->
            let pair = [ "left", boolType; "right", boolType ]
            let notFn = create session "not" [ "value", boolType ] boolType [] (boolNot (slot 0))
            let andFn = create session "and" pair boolType [] (boolAnd (slot 0) (slot 1))
            let orFn = create session "or" pair boolType [] (boolOr (slot 0) (slot 1))
            // Operators compose inside each other's right operand: exclusive or.
            let xorFn = create session "xor" pair boolType [] (boolOr (boolAnd (slot 0) (boolNot (slot 1))) (boolAnd (boolNot (slot 0)) (slot 1)))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) values = (PureRun.run reopened fn.Id (values |> List.map PureRunValue.Bool) None |> unwrap).Actual
            for value in [ false; true ] do
                Assert.Equal(PureRunValue.Bool(not value), run notFn [ value ])
            for left, right in truthTable do
                Assert.Equal(PureRunValue.Bool(left && right), run andFn [ left; right ])
                Assert.Equal(PureRunValue.Bool(left || right), run orFn [ left; right ])
                Assert.Equal(PureRunValue.Bool(left <> right), run xorFn [ left; right ])
            use executed =
                send reopened ($"""{{"id":"run","op":"runPure","functionId":"{StableId.value orFn.Id}","arguments":[{{"kind":"bool","value":false}},{{"kind":"bool","value":true}}],"expected":{{"kind":"bool","value":true}}}}""")
            Assert.True(executed.RootElement.GetProperty("result").GetProperty("matches").GetBoolean(), executed.RootElement.ToString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``the right operand runs only when the left operand does not decide the result`` () =
        withSession (fun session _ ->
            let effect, cap, op = io session
            let parameters = [ "io", TypeRef.Capability cap.Id; "flag", boolType ]
            let andFn = create session "andProbe" parameters boolType [ effect.Id ] (boolAnd (slot 1) (probe op))
            let orFn = create session "orProbe" parameters boolType [ effect.Id ] (boolOr (slot 1) (probe op))
            let run (fn: SemanticFunction) flag responses =
                let outcome = PureRun.run session fn.Id [ fake cap responses; PureRunValue.Bool flag ] None |> unwrap
                outcome.Actual, outcome.Calls.Length
            // No responses are scripted where the right operand must not run: a call would fail the run.
            Assert.Equal((PureRunValue.Bool false, 0), run andFn false [])
            Assert.Equal((PureRunValue.Bool true, 1), run andFn true [ "yes" ])
            Assert.Equal((PureRunValue.Bool false, 1), run andFn true [ "no" ])
            Assert.Equal((PureRunValue.Bool true, 0), run orFn true [])
            Assert.Equal((PureRunValue.Bool true, 1), run orFn false [ "yes" ])
            Assert.Equal((PureRunValue.Bool false, 1), run orFn false [ "no" ])
            // An aborting right operand aborts only when it is evaluated.
            let returning name body = create session name [ "flag", boolType ] (result boolType) [] (ExpressionRecipe.Bind(body, ok (bound RecipePatternSlot.Value)))
            let andAbort = returning "andAbort" (boolAnd (slot 0) (abort "and right"))
            let orAbort = returning "orAbort" (boolOr (slot 0) (abort "or right"))
            let runAbort (fn: SemanticFunction) flag = (PureRun.run session fn.Id [ PureRunValue.Bool flag ] None |> unwrap).Actual
            Assert.Equal(okBool false, runAbort andAbort false)
            Assert.Equal(errBool "and right", runAbort andAbort true)
            Assert.Equal(okBool true, runAbort orAbort true)
            Assert.Equal(errBool "or right", runAbort orAbort false))
