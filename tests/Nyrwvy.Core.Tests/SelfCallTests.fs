namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `SelfCall`: a direct call to the owning function keyed by parameter slot, so a recursive
/// function is created or rewritten in one request. It persists as an ordinary DirectCall.
module SelfCallTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private int (value: int64) = ExpressionRecipe.Int64Literal value
    let private slot = ExpressionRecipe.ParameterSlot
    let private selfCall arguments = ExpressionRecipe.SelfCall arguments
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
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-self-call-" + Guid.NewGuid().ToString("N"))
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
    let private failure session line =
        let response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response.RootElement.ToString()
    let private publishWire (session: AuthoringSession) (response: JsonDocument) =
        session.Commit(response.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private int64Json = """{"kind":"primitive","primitive":"int64"}"""
    /// factorial(n) = if n <= 1 then 1 else n * factorial(n - 1), by exact-ID selfCall.
    let private factorialExact =
        """{"kind":"conditional","condition":{"kind":"int64LessThanOrEqual","left":{"kind":"parameterSlot","index":0},"right":{"kind":"int64Literal","value":1}},"whenTrue":{"kind":"int64Literal","value":1},"whenFalse":{"kind":"int64WrappingMultiply","left":{"kind":"parameterSlot","index":0},"right":{"kind":"selfCall","arguments":[{"slot":0,"value":{"kind":"int64WrappingSubtract","left":{"kind":"parameterSlot","index":0},"right":{"kind":"int64Literal","value":1}}}]}}}"""
    /// sumTo(n) = if n <= 0 then 0 else n + sumTo(n - 1), with the call resolved by the new function's name.
    let private sumToNamed =
        """{"kind":"conditional","condition":{"kind":"int64LessThanOrEqual","left":{"kind":"parameter","name":"n"},"right":{"kind":"int64Literal","value":0}},"whenTrue":{"kind":"int64Literal","value":0},"whenFalse":{"kind":"int64WrappingAdd","left":{"kind":"parameter","name":"n"},"right":{"kind":"directCall","function":"sumTo","arguments":[{"parameter":"n","value":{"kind":"int64WrappingSubtract","left":{"kind":"parameter","name":"n"},"right":{"kind":"int64Literal","value":1}}}]}}}"""
    let private createWire session op name body =
        use proposal =
            send session (sprintf """{"id":"%s","op":"%s","displayName":"%s","parameters":[{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}""" name op name int64Json int64Json body)
        publishWire session proposal
        find session name
    let private selfCalls (fn: SemanticFunction) =
        ValueGraph.collectNodes fn.Body
        |> List.choose (fun node ->
            match node.Kind with
            | SemanticNodeKind.DirectCall(target, arguments) -> Some(target, arguments |> List.map (fun argument -> argument.TargetParameterId))
            | _ -> None)
    let private assertCallsItself (fn: SemanticFunction) =
        Assert.Equal<(FunctionId * ParameterId list) list>([ fn.Id, [ fn.Parameters.Head.Id ] ], selfCalls fn)

    [<Fact>]
    let ``recursive functions are created in one request by exact-ID selfCall and by name`` () =
        withSession (fun session path ->
            let factorial = createWire session "simulateCreateFunction" "factorial" factorialExact
            let sumTo = createWire session "simulateCreateFunctionByName" "sumTo" sumToNamed
            assertCallsItself factorial
            assertCallsItself sumTo
            session.Close()
            let graph = snapshot path
            Assert.Empty(SnapshotValidator.validate graph)
            // No new node kind: the persisted self call is an ordinary DirectCall.
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"directCall\"", encoded)
            Assert.DoesNotContain("selfCall", encoded)
            TypedSemanticIr.project graph |> unwrap |> ExecutionIr.lower |> unwrap |> ignore
            use reopened = AuthoringSession.openExisting path |> unwrap
            assertCallsItself (find reopened "factorial")
            assertCallsItself (find reopened "sumTo"))

    [<Fact>]
    let ``self calls reject wrong missing and duplicate slots and mismatched argument types`` () =
        withSession (fun session _ ->
            let reject body = rejects session "count" [ "n", intType ] intType body
            Assert.Contains("PureExpressionCallArgumentTargetNotFound", reject (selfCall [ 0, slot 0; 1, int 1L ]))
            Assert.Contains("PureExpressionCallArgumentTargetNotFound", reject (selfCall [ -1, slot 0 ]))
            Assert.Contains("PureExpressionCallArgumentMissing", reject (selfCall []))
            Assert.Contains("PureExpressionDuplicateCallArgument", reject (selfCall [ 0, slot 0; 0, int 1L ]))
            Assert.Contains("PureExpressionCallArgumentTypeMismatch", reject (selfCall [ 0, ExpressionRecipe.StringLiteral "x" ]))
            // The result is the owner's declared result type: Int64 here, not the String StringLength needs.
            Assert.Contains("StringLength", reject (ExpressionRecipe.StringLength(selfCall [ 0, slot 0 ])))
            // By name, arguments resolve against the owner's parameter names.
            let unknownParameter = sumToNamed.Replace("\"parameter\":\"n\"", "\"parameter\":\"m\"")
            Assert.Contains("parameter on sumTo", failure session (sprintf """{"id":"bad","op":"simulateCreateFunctionByName","displayName":"sumTo","parameters":[{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}""" int64Json int64Json unknownParameter)))

    [<Fact>]
    let ``replacing an existing function body can call the function itself`` () =
        withSession (fun session path ->
            let placeholder = create session "countDown" [ "n", intType ] intType (int 0L)
            let recursive =
                ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThanOrEqual(ExpressionRecipe.Parameter placeholder.Parameters.Head.Id, int 0L),
                    int 0L,
                    selfCall [ 0, ExpressionRecipe.Int64WrappingSubtract(ExpressionRecipe.Parameter placeholder.Parameters.Head.Id, int 1L) ])
            session.SimulateReplaceBody
                { TargetFunctionId = placeholder.Id
                  ExpectedBaseSnapshotId = current session
                  ExpectedCurrentResultNodeId = placeholder.Body.ResultNodeId
                  Replacement = recursive }
            |> unwrap |> publish session
            assertCallsItself (find session "countDown")
            // ParameterSlot stays creation-only; SelfCall slots address the existing owner's parameters.
            let other = create session "sumTo" [ "n", intType ] intType (int 0L)
            use replaced = send session (sprintf """{"id":"named","op":"simulateReplaceBodyByName","function":"sumTo","replacement":%s}""" sumToNamed)
            publishWire session replaced
            assertCallsItself (find session "sumTo")
            Assert.Equal(other.Id, (find session "sumTo").Id)
            Assert.Empty(SnapshotValidator.validate (snapshot path)))

    [<Fact>]
    let ``parameter type migration keeps self calls on the migrated target and on callers`` () =
        withSession (fun session path ->
            let measure = create session "measure" [ "n", intType ] intType (slot 0)
            let caller =
                create session "callsMeasure" [ "x", intType ] intType
                    (ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, slot 0 ]))
            let n = ExpressionRecipe.Parameter measure.Parameters.Head.Id
            let x = ExpressionRecipe.Parameter caller.Parameters.Head.Id
            // measure(n: String) = if n is empty then 0 else 1 + measure(n[1..]).
            let targetReplacement =
                ExpressionRecipe.Conditional(
                    ExpressionRecipe.StringIsEmpty n,
                    int 0L,
                    ExpressionRecipe.Int64WrappingAdd(
                        int 1L,
                        selfCall [ 0, ExpressionRecipe.StringSlice(n, int 1L, ExpressionRecipe.StringLength n) ]))
            let callerReplacement =
                ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThanOrEqual(x, int 0L),
                    ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, ExpressionRecipe.Int64ToString x ]),
                    selfCall [ 0, ExpressionRecipe.Int64WrappingSubtract(x, int 1L) ])
            session.SimulateParameterTypeMigration
                { ExpectedBaseSnapshotId = current session
                  TargetFunctionId = measure.Id
                  TargetParameterId = measure.Parameters.Head.Id
                  NewParameterType = textType
                  TargetReplacement = targetReplacement
                  CallerRewrites = [ { CallerFunctionId = caller.Id; Replacement = callerReplacement } ] }
            |> unwrap |> publish session
            let migrated = find session "measure"
            Assert.Equal(measure.Id, migrated.Id)
            Assert.Equal(textType, migrated.Parameters.Head.Type)
            assertCallsItself migrated
            let migratedCaller = find session "callsMeasure"
            Assert.Contains((caller.Id, [ caller.Parameters.Head.Id ]), selfCalls migratedCaller)
            Assert.Contains((measure.Id, [ measure.Parameters.Head.Id ]), selfCalls migratedCaller)
            Assert.Empty(SnapshotValidator.validate (snapshot path)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``recursive functions created with self calls run through runPure`` () =
        withSession (fun session path ->
            let factorial = createWire session "simulateCreateFunction" "factorial" factorialExact
            let sumTo = createWire session "simulateCreateFunctionByName" "sumTo" sumToNamed
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) (value: int64) = (PureRun.run reopened fn.Id [ PureRunValue.Int64 value ] None |> unwrap).Actual
            for value, expected in [ 0L, 1L; 1L, 1L; 5L, 120L; 10L, 3628800L ] do
                Assert.Equal(PureRunValue.Int64 expected, run factorial value)
            for value, expected in [ 0L, 0L; 1L, 1L; 10L, 55L ] do
                Assert.Equal(PureRunValue.Int64 expected, run sumTo value))
