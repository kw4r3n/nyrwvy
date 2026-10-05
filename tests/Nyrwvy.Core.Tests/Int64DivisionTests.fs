namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Int64FloorDivide and Int64FloorModulo: Python-style floor semantics that never fail at runtime.
module Int64DivisionTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private slot = ExpressionRecipe.ParameterSlot
    let private divide left right = ExpressionRecipe.Int64FloorDivide(left, right)
    let private modulo left right = ExpressionRecipe.Int64FloorModulo(left, right)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = []; Body = body }
    let private create session name parameters resultType body =
        request session name parameters resultType body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private rejects (session: AuthoringSession) name parameters resultType body =
        match session.SimulateCreateFunction(request session name parameters resultType body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwithf "Function '%s' was accepted." name
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-int64-division-" + Guid.NewGuid().ToString("N"))
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
    let private pair = [ "left", intType; "right", intType ]

    [<Fact>]
    let ``floor division and modulo reject non-Int64 operands in recipes and persisted graphs`` () =
        withSession (fun session path ->
            let mismatch = "PureExpressionOperandTypeMismatch"
            for name, construct in [ "divide", divide; "modulo", modulo ] do
                Assert.Contains(mismatch, rejects session (name + "Left") [ "value", textType ] intType (construct (slot 0) (ExpressionRecipe.Int64Literal 2L)))
                Assert.Contains(mismatch, rejects session (name + "Right") [ "value", boolType ] intType (construct (ExpressionRecipe.Int64Literal 7L) (slot 0)))
                // The result is Int64, whatever the operands.
                Assert.NotEqual<string>("", rejects session (name + "Result") pair boolType (construct (slot 0) (slot 1)))
            let both = create session "both" pair intType (modulo (divide (slot 0) (slot 1)) (slot 1))
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
            for index in [ 0; 1 ] do
                Assert.Contains(SnapshotValidator.validate (breakParameter index), fun issue -> issue.Code = "Int64OperandTypeMismatch"))

    [<Fact>]
    let ``exact-ID and name-resolved recipes persist reopen and project the dedicated constructs`` () =
        withSession (fun session path ->
            let int64 = """{"kind":"primitive","primitive":"int64"}"""
            let parameters = sprintf """[{"displayName":"left","type":%s},{"displayName":"right","type":%s}]""" int64 int64
            let exact =
                """{"kind":"int64FloorDivide","left":{"kind":"parameterSlot","index":0},"right":{"kind":"parameterSlot","index":1}}"""
            use exactProposal =
                send session (sprintf """{"id":"exact","op":"simulateCreateFunction","displayName":"quotient","parameters":%s,"resultType":%s,"requiredEffects":[],"body":%s}""" parameters int64 exact)
            publishWire session exactProposal
            let named =
                """{"kind":"int64FloorModulo","left":{"kind":"parameter","name":"left"},"right":{"kind":"parameter","name":"right"}}"""
            use namedProposal =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"remainder","parameters":%s,"resultType":%s,"requiredEffects":[],"body":%s}""" parameters int64 named)
            Assert.Contains("\"kind\":\"int64FloorModulo\"", namedProposal.RootElement.GetRawText())
            publishWire session namedProposal
            let quotient = find session "quotient"
            let remainder = find session "remainder"
            let roleOf (fn: SemanticFunction) =
                let viewer = session.FocusFunction fn.Id |> unwrap |> ViewerProjection.focus
                (viewer.Graph.Nodes |> List.find (fun node -> node.Id = fn.Body.ResultNodeId)).Role, ViewerHtml.renderFocusDocument viewer
            let divideRole, divideHtml = roleOf quotient
            let moduloRole, moduloHtml = roleOf remainder
            Assert.Equal(ViewerNodeRole.Int64FloorDivide, divideRole)
            Assert.Equal(ViewerNodeRole.Int64FloorModulo, moduloRole)
            Assert.Contains("Int64 floor divide", divideHtml)
            Assert.Contains("Int64 floor modulo", moduloHtml)
            session.Close()
            let encoded = File.ReadAllText path
            let kinds = [ "int64FloorDivide"; "int64FloorModulo" ]
            for kind in kinds do
                Assert.Contains($"\"kind\":\"{kind}\"", encoded)
            let graph = snapshot path
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            for kind in [ "int64-floor-divide"; "int64-floor-modulo" ] do
                Assert.Contains(kind, nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            let typedText = TypedSemanticIrSerializer.serialize typed
            let executionText = ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize
            for kind in kinds do
                Assert.Contains($"\"kind\":\"{kind}\"", typedText)
                Assert.Contains($"\"kind\":\"{kind}\"", executionText)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(remainder.Body.ResultNodeId, (find reopened "remainder").Body.ResultNodeId))

    [<Fact; Trait("Suite", "Extended")>]
    let ``floor division and modulo follow Python semantics and never fail through runPure`` () =
        withSession (fun session path ->
            let divideFn = create session "divide" pair intType (divide (slot 0) (slot 1))
            let moduloFn = create session "modulo" pair intType (modulo (slot 0) (slot 1))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) left right =
                (PureRun.run reopened fn.Id [ PureRunValue.Int64 left; PureRunValue.Int64 right ] None |> unwrap).Actual
            // Expected values follow Python's // and %, with divisor 0 giving (0, left) and MinValue // -1 wrapping.
            let table =
                [ 7L, 2L, 3L, 1L
                  -7L, 2L, -4L, 1L
                  7L, -2L, -4L, -1L
                  -7L, -2L, 3L, -1L
                  0L, 5L, 0L, 0L
                  5L, 0L, 0L, 5L
                  -5L, 0L, 0L, -5L
                  Int64.MinValue, -1L, Int64.MinValue, 0L
                  Int64.MinValue, 1L, Int64.MinValue, 0L
                  Int64.MaxValue, -1L, -Int64.MaxValue, 0L
                  1L, Int64.MaxValue, 0L, 1L
                  -1L, Int64.MaxValue, -1L, Int64.MaxValue - 1L ]
            for left, right, quotient, remainder in table do
                Assert.Equal(PureRunValue.Int64 quotient, run divideFn left right)
                Assert.Equal(PureRunValue.Int64 remainder, run moduloFn left right)
                // left = right * q + r holds for every row, under wrapping arithmetic.
                Assert.Equal(left, right * quotient + remainder)
            use executed =
                send reopened ($"""{{"id":"run","op":"runPure","functionId":"{StableId.value moduloFn.Id}","arguments":[{{"kind":"int64","value":"-7"}},{{"kind":"int64","value":"2"}}],"expected":{{"kind":"int64","value":"1"}}}}""")
            Assert.True(executed.RootElement.GetProperty("result").GetProperty("matches").GetBoolean(), executed.RootElement.ToString()))
