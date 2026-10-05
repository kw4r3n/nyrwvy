namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// A pure fold that stops at the first Err: ListEffectFold with an effect-free folder returning
/// Result<S, E>. It needs no Result accumulator and no pass-through arm in the folder.
module ResultFoldTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringList = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ])
    let private countType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ intType; stringType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private slot = ExpressionRecipe.ParameterSlot
    let private str = ExpressionRecipe.StringLiteral
    let private bound patternSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = patternSlot }
    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private parameter (fn: SemanticFunction) name = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session
          DisplayName = name
          Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = []
          Body = body }
    let private create session name parameters resultType body =
        request session name parameters resultType body |> session.SimulateCreateFunction |> unwrap name |> publish session
        find session name
    let private rejection (session: AuthoringSession) name parameters resultType body =
        match session.SimulateCreateFunction(request session name parameters resultType body) with
        | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) -> diagnostic
        | other -> failwithf "Invalid fold accepted: %A" other

    /// The step shared by both forms: an item starting with "bad" is an Err carrying the item;
    /// any other item adds the code-point count of `text`, which is linear in its length.
    let private step total item =
        ExpressionRecipe.Conditional(
            ExpressionRecipe.StringStartsWith(item, str "bad"),
            ExpressionRecipe.ResultErr(intType, item),
            ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.Int64WrappingAdd(total, ExpressionRecipe.ListLength(ExpressionRecipe.StringCodePoints(slot 0)))))

    /// `text` repeated 16 times, computed once as the folder's context value (run arguments are
    /// limited to 65,536 code units).
    let private amplified text =
        let twice value = ExpressionRecipe.Bind(value, ExpressionRecipe.StringConcat(bound RecipePatternSlot.Value, bound RecipePatternSlot.Value))
        text |> twice |> twice |> twice |> twice

    /// Short-circuiting form: count(text, total: Int64, item) -> Result<Int64, String> and
    /// ListEffectFold(items, 0, count). The pass-through form threads a Result accumulator through
    /// ListFold and repeats the Err in every folder.
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-result-fold-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap "session"
            let count = create session "count" [ "text", stringType; "total", intType; "item", stringType ] countType (step (slot 1) (slot 2))
            let countAll =
                create session "countAll" [ "items", stringList; "text", stringType ] countType
                    (ExpressionRecipe.ListEffectFold(slot 0, ExpressionRecipe.Int64Literal 0L, count.Id, [ parameter count "text", amplified (slot 1) ]))
            let passCount =
                create session "passCount" [ "text", stringType; "total", countType; "item", stringType ] countType
                    (ExpressionRecipe.MatchResult(slot 1, step (bound RecipePatternSlot.Value) (slot 2), ExpressionRecipe.ResultErr(intType, bound RecipePatternSlot.Error)))
            let passAll =
                create session "passAll" [ "items", stringList; "text", stringType ] countType
                    (ExpressionRecipe.ListFold(slot 0, ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.Int64Literal 0L), passCount.Id, [ parameter passCount "text", amplified (slot 1) ]))
            run session path (count, countAll) (passCount, passAll)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``a pure folder returning Result makes ListEffectFold the short-circuiting fold with fewer nodes`` () =
        withSession (fun session path (count, countAll) (passCount, passAll) ->
            // Accepted with no effects: the enclosing function stays pure.
            Assert.Empty(countAll.RequiredEffects)
            Assert.Equal(countType, countAll.ResultType)
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "decode"
            Assert.Empty(SnapshotValidator.validate snapshot)
            Assert.True(PureFunctions.isPure snapshot countAll.Id)
            // The pass-through form costs a Result seed, a MatchResult and a re-raised Err per fold.
            let nodes (fns: SemanticFunction list) = fns |> List.sumBy (fun fn -> (ValueGraph.collectNodes fn.Body).Length)
            let shortCircuit, passThrough = nodes [ count; countAll ], nodes [ passCount; passAll ]
            Assert.Equal(passThrough - 5, shortCircuit)
            let context = [ parameter count "text", slot 1 ]
            // ListFold keeps requiring the seed type, and names ListEffectFold for a Result folder.
            let hint = rejection session "bad" [ "items", stringList; "text", stringType ] countType (ExpressionRecipe.ListFold(slot 0, ExpressionRecipe.Int64Literal 0L, count.Id, context))
            Assert.Equal("PureExpressionCallbackInputTypeMismatch", hint.Code)
            Assert.Contains("ListEffectFold", hint.Message)
            // Mismatched shapes: a seed that is neither S nor matches Result<S, E>'s S, and a Result seed.
            let fold seed = ExpressionRecipe.ListEffectFold(slot 0, seed, count.Id, context)
            Assert.Equal("PureExpressionCallbackInputTypeMismatch", (rejection session "bad" [ "items", stringList; "text", stringType ] countType (fold (str "0"))).Code)
            Assert.Equal("PureExpressionCallbackInputTypeMismatch",
                (rejection session "bad" [ "items", stringList; "text", stringType ] countType (fold (ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.Int64Literal 0L)))).Code)
            // The declared result must be the Result, not the seed type.
            Assert.NotNull(rejection session "bad" [ "items", stringList; "text", stringType ] intType (fold (ExpressionRecipe.Int64Literal 0L)))
            // Over the wire a pure listEffectFold needs no requiredEffects either.
            let line =
                """{"id":"create","op":"simulateCreateFunctionByName","displayName":"countWire","parameters":[{"displayName":"items","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"string"}]}},{"displayName":"text","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"builtinGeneric","generic":"result","arguments":[{"kind":"primitive","primitive":"int64"},{"kind":"primitive","primitive":"string"}]},"requiredEffects":[],"body":{"kind":"listEffectFold","source":{"kind":"parameter","name":"items"},"seed":{"kind":"int64Literal","value":0},"function":"count","context":[{"parameter":"text","value":{"kind":"parameter","name":"text"}}]}}"""
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``a pure Result fold returns the first Err and never visits later items`` () =
        withSession (fun session _ (_, countAll) (_, passAll) ->
            let run (fn: SemanticFunction) items text =
                (PureRun.run session fn.Id [ PureRunValue.StringList items; PureRunValue.String text ] None |> unwrap fn.DisplayName).Actual
            let ok (value: int64) = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 value)
            let err (value: string) = PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String value)
            for fn in [ countAll; passAll ] do
                Assert.Equal(ok 96L, run fn [ "a"; "b"; "c" ] "xy")
                Assert.Equal(ok 0L, run fn [] "xy")
                Assert.Equal(err "bad1", run fn [ "a"; "bad1"; "b"; "bad2" ] "xy")
            // Each item after the Err would scan 960,000 code points; 4,000 of them would take far
            // longer than the run limit, so this returns only because the folder is not called again.
            let text = String('x', 60_000)
            let items = "bad0" :: List.replicate 4_000 "a"
            Assert.Equal(err "bad0", run countAll items text))
