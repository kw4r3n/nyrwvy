namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Reviewed result-type migration: the target's result type, body and every direct caller change
/// together in one publication while identities are kept.
module ResultTypeMigrationTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionString = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private create ws name (parameters: (string * TypeRef) list) resultType body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              TypeParameters = None
              DisplayName = name
              Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap ("create " + name)
        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
        | other -> failwithf "Commit failed: %A" other

    let private find ws name =
        (AgentWorkspace.snapshotForStructuralEditing ws).Functions |> List.find (fun fn -> fn.DisplayName = name)

    /// `lookup` returns "" for a missing key; `describe` and `count` call it directly.
    let private fixture () =
        let ws = AgentWorkspace.create (Snapshot.empty (StableId.createForTesting<SnapshotTag> "result-migration")) |> unwrap "workspace"
        let key = ExpressionRecipe.ParameterSlot 0
        let ws =
            create ws "lookup" [ "key", stringType ] stringType
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.StringEqual(key, ExpressionRecipe.StringLiteral "known"),
                    ExpressionRecipe.StringLiteral "value",
                    ExpressionRecipe.StringLiteral ""))
        let lookup = find ws "lookup"
        let call argument = ExpressionRecipe.DirectCall(lookup.Id, [ (List.exactlyOne lookup.Parameters).Id, argument ])
        let ws = create ws "describe" [ "key", stringType ] stringType (ExpressionRecipe.StringConcat(ExpressionRecipe.StringLiteral "value: ", call key))
        let ws = create ws "unrelated" [ "n", intType ] intType key
        ws

    let private valueSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }

    /// The migration an agent would author: `lookup` returns Option<String>, and `describe` handles None.
    let private request ws =
        let lookup = find ws "lookup"
        let describe = find ws "describe"
        let lookupKey = (List.exactlyOne lookup.Parameters).Id
        let describeKey = ExpressionRecipe.Parameter (List.exactlyOne describe.Parameters).Id
        let targetBody =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.StringEqual(ExpressionRecipe.Parameter lookupKey, ExpressionRecipe.StringLiteral "known"),
                ExpressionRecipe.OptionSome(ExpressionRecipe.StringLiteral "value"),
                ExpressionRecipe.OptionNone stringType)
        let describeBody =
            ExpressionRecipe.MatchOption(
                ExpressionRecipe.DirectCall(lookup.Id, [ lookupKey, describeKey ]),
                ExpressionRecipe.StringConcat(ExpressionRecipe.StringLiteral "value: ", valueSlot),
                ExpressionRecipe.StringLiteral "missing")
        let request: AgentResultTypeMigrationRequest =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              TargetFunctionId = lookup.Id
              NewResultType = optionString
              TargetReplacement = targetBody
              CallerRewrites = [ { CallerFunctionId = describe.Id; Replacement = describeBody } ] }
        request

    let private rejects ws request =
        match AgentResultTypeMigrationWorkflow.simulate ws request with
        | Error(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _)) -> diagnostic.Code
        | other -> failwithf "Invalid migration was accepted: %A" other

    [<Fact>]
    let ``result type migration rewrites the target and callers in one reviewed publication`` () =
        let ws = fixture ()
        let before = AgentWorkspace.snapshotForStructuralEditing ws
        let lookup, describe, unrelated = find ws "lookup", find ws "describe", find ws "unrelated"
        let proposal = AgentResultTypeMigrationWorkflow.simulate ws (request ws) |> unwrap "simulate"
        let review = AgentResultTypeMigrationProposal.review proposal
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(stringType, review.ResultingChange.ResultChange.OldType)
        Assert.Equal(optionString, review.ResultingChange.ResultChange.NewType)
        Assert.Equal<FunctionId list>([ describe.Id ], review.ResultingChange.AffectedCallSites |> List.map (fun site -> site.CallerFunctionId))
        Assert.Equal<FunctionId list>([ lookup.Id; describe.Id ], review.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId))
        let next =
            match AgentResultTypeMigrationWorkflow.commit ws proposal with
            | AgentResultTypeMigrationCommitResult.Committed(next, published) ->
                Assert.Equal(before.Id, published.BaseSnapshotId)
                next
            | other -> failwithf "Commit failed: %A" other
        let after = AgentWorkspace.snapshotForStructuralEditing next
        Assert.Empty(SnapshotValidator.validate after)
        let migrated = after.Functions |> List.find (fun fn -> fn.Id = lookup.Id)
        Assert.Equal(optionString, migrated.ResultType)
        Assert.Equal<SemanticParameter list>(lookup.Parameters, migrated.Parameters)
        Assert.Equal(unrelated, after.Functions |> List.find (fun fn -> fn.Id = unrelated.Id))
        let reopened = SnapshotCodec.deserialize (SnapshotCodec.serialize after |> unwrap "serialize") |> unwrap "deserialize"
        Assert.Equal(optionString, (reopened.Functions |> List.find (fun fn -> fn.Id = lookup.Id)).ResultType)
        Assert.Equal<SemanticParameter list>(describe.Parameters, (reopened.Functions |> List.find (fun fn -> fn.Id = describe.Id)).Parameters)

    [<Fact>]
    let ``result type migration refuses incomplete or mistyped rewrites before publication`` () =
        let ws = fixture ()
        let valid = request ws
        let lookup = find ws "lookup"
        Assert.Equal("ResultTypeMigrationCallerRewriteMismatch", rejects ws { valid with CallerRewrites = [] })
        Assert.Equal("ResultTypeMigrationTypeUnchanged", rejects ws { valid with NewResultType = stringType })
        Assert.Equal("ResultTypeMigrationExpectedBaseMismatch",
            rejects ws { valid with ExpectedBaseSnapshotId = StableId.createForTesting<SnapshotTag> "stale" })
        Assert.Equal("ResultTypeMigrationTargetRewriteInvalid", rejects ws { valid with TargetReplacement = ExpressionRecipe.StringLiteral "" })
        let staleCaller =
            { valid.CallerRewrites.Head with
                Replacement =
                    ExpressionRecipe.StringConcat(
                        ExpressionRecipe.StringLiteral "value: ",
                        ExpressionRecipe.DirectCall(lookup.Id, [ (List.exactlyOne lookup.Parameters).Id, ExpressionRecipe.StringLiteral "k" ])) }
        Assert.Equal("ResultTypeMigrationCallerRewriteInvalid", rejects ws { valid with CallerRewrites = [ staleCaller ] })
        // A callback reference cannot be rewritten by this bounded migration.
        let ws = create ws "all" [ "keys", TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]) ] (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]))
                    (ExpressionRecipe.ListMap(ExpressionRecipe.ParameterSlot 0, lookup.Id, []))
        Assert.Equal("ResultTypeMigrationUnsupportedCallerUse", rejects ws (request ws))
