namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

/// Parameter/result-type migrations of generic functions keep every function's own type
/// parameters, and a target that already calls itself is migrated with its self calls typed
/// against the new signature.
module SignatureMigrationGenericRecursiveTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private optionOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    /// A request-local type parameter is spelled as its name; creation mints the identity.
    let private typeParameter name = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> name)
    let private int (value: int64) = ExpressionRecipe.Int64Literal value
    let private slot = ExpressionRecipe.ParameterSlot
    let private parameter (fn: SemanticFunction) index = ExpressionRecipe.Parameter (List.item index fn.Parameters).Id
    let private valueSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private workspace name =
        AgentWorkspace.create (Snapshot.empty (StableId.createForTesting<SnapshotTag> name)) |> unwrap "workspace"

    let private create ws name (parameters: (string * TypeRef) list) resultType body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
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

    let private snapshot ws = AgentWorkspace.snapshotForStructuralEditing ws
    let private find ws name = (snapshot ws).Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private byId ws (fn: SemanticFunction) = (snapshot ws).Functions |> List.find (fun candidate -> candidate.Id = fn.Id)
    /// The type parameter a generic function declares through its first parameter.
    let private ownTypeParameter (fn: SemanticFunction) =
        match TypeParameters.ofFunction fn with
        | [ id, _ ] -> TypeRef.TypeParameter id
        | other -> failwithf "%s declares %A" fn.DisplayName other

    let private commitParameterMigration ws request =
        let proposal = AgentParameterTypeMigrationWorkflow.simulate ws request |> unwrap "simulate"
        match AgentParameterTypeMigrationWorkflow.commit ws proposal with
        | AgentParameterTypeMigrationCommitResult.Committed(next, _) -> AgentParameterTypeMigrationProposal.review proposal, next
        | other -> failwithf "Commit failed: %A" other

    let private commitResultMigration ws request =
        let proposal = AgentResultTypeMigrationWorkflow.simulate ws request |> unwrap "simulate"
        match AgentResultTypeMigrationWorkflow.commit ws proposal with
        | AgentResultTypeMigrationCommitResult.Committed(next, _) -> AgentResultTypeMigrationProposal.review proposal, next
        | other -> failwithf "Commit failed: %A" other

    let private rejectsParameterMigration ws request =
        match AgentParameterTypeMigrationWorkflow.simulate ws request with
        | Error(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _)) -> diagnostic
        | other -> failwithf "Invalid migration was accepted: %A" other

    /// Every type mentioned by a body's typed nodes and pattern bindings.
    let private bodyTypes (fn: SemanticFunction) =
        (SemanticFacts.collectPatternBindings fn.Body |> List.map (fun binding -> binding.Type))
        @ (ValueGraph.collectNodes fn.Body
           |> List.collect (fun node ->
               match node.Kind with
               | SemanticNodeKind.ListEmpty itemType
               | SemanticNodeKind.ListConstruct(itemType, _)
               | SemanticNodeKind.OptionNone itemType
               | SemanticNodeKind.ResultOk(itemType, _)
               | SemanticNodeKind.ResultErr(itemType, _) -> [ itemType ]
               | _ -> []))

    let private assertValidAndLowerable ws =
        let graph = snapshot ws
        Assert.Empty(SnapshotValidator.validate graph)
        TypedSemanticIr.project graph |> unwrap "project" |> ExecutionIr.lower |> unwrap "lower" |> ignore

    let private directCalls (fn: SemanticFunction) =
        ValueGraph.collectNodes fn.Body
        |> List.choose (fun node ->
            match node.Kind with
            | SemanticNodeKind.DirectCall(target, arguments) -> Some(target, arguments |> List.map (fun argument -> argument.TargetParameterId))
            | _ -> None)

    /// pick<T>(xs: List<T>, label: Int64): List<T> = match xs[0] with Some v -> [v] | None -> [].
    let private pickBody (t: TypeRef) xs =
        ExpressionRecipe.MatchOption(
            ExpressionRecipe.ListItemAt(xs, int 0L),
            ExpressionRecipe.ListConstruct(t, [ valueSlot ]),
            ExpressionRecipe.ListEmpty t)

    [<Fact>]
    let ``parameter type migration keeps the type parameters of a generic target and a generic caller`` () =
        let t = typeParameter "T"
        let u = typeParameter "U"
        let ws = workspace "generic-parameter-migration"
        let ws = create ws "pick" [ "xs", listOf t; "label", intType ] (listOf t) (pickBody t (slot 0))
        let pick = find ws "pick"
        let pickT = ownTypeParameter pick
        let call xs label = ExpressionRecipe.DirectCall(pick.Id, [ pick.Parameters.[0].Id, xs; pick.Parameters.[1].Id, label ])
        // firstOf<U>(items: List<U>, n: Int64): List<U> = if n = 0 then [] else pick(items, n).
        let ws =
            create ws "firstOf" [ "items", listOf u; "n", intType ] (listOf u)
                (ExpressionRecipe.Conditional(ExpressionRecipe.Int64Equal(slot 1, int 0L), ExpressionRecipe.ListEmpty u, call (slot 0) (slot 1)))
        let ws = create ws "firstName" [ "names", listOf stringType ] (listOf stringType) (call (slot 0) (int 7L))
        let firstOf, firstName = find ws "firstOf", find ws "firstName"
        let firstOfU = ownTypeParameter firstOf
        Assert.NotEqual(pickT, firstOfU)
        let before = snapshot ws
        let request: AgentParameterTypeMigrationRequest =
            { ExpectedBaseSnapshotId = before.Id
              TargetFunctionId = pick.Id
              TargetParameterId = pick.Parameters.[1].Id
              NewParameterType = stringType
              TargetReplacement = pickBody pickT (parameter pick 0)
              CallerRewrites =
                [ { CallerFunctionId = firstOf.Id
                    Replacement =
                      ExpressionRecipe.Conditional(
                          ExpressionRecipe.Int64Equal(parameter firstOf 1, int 0L),
                          ExpressionRecipe.ListEmpty firstOfU,
                          call (parameter firstOf 0) (ExpressionRecipe.Int64ToString(parameter firstOf 1))) }
                  { CallerFunctionId = firstName.Id
                    Replacement = call (parameter firstName 0) (ExpressionRecipe.StringLiteral "first") } ] }
        let review, next = commitParameterMigration ws request
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        assertValidAndLowerable next
        let migrated = byId next pick
        Assert.Equal<TypeRef list>([ listOf pickT; stringType ], migrated.Parameters |> List.map (fun parameter -> parameter.Type))
        Assert.Equal(listOf pickT, migrated.ResultType)
        Assert.Equal<(TypeParameterId * string) list>(TypeParameters.ofFunction pick, TypeParameters.ofFunction migrated)
        Assert.Equal<TypeRef list>([ pickT ], bodyTypes migrated |> List.distinct)
        let migratedCaller = byId next firstOf
        Assert.Equal<SemanticParameter list>(firstOf.Parameters, migratedCaller.Parameters)
        Assert.Equal<TypeRef list>([ firstOfU ], bodyTypes migratedCaller |> List.distinct)
        Assert.Equal<(FunctionId * ParameterId list) list>([ pick.Id, [ pick.Parameters.[0].Id; pick.Parameters.[1].Id ] ], directCalls migratedCaller)
        Assert.Equal<SemanticParameter list>(firstName.Parameters, (byId next firstName).Parameters)

    [<Fact>]
    let ``result type migration keeps the type parameters of a generic target and a generic caller`` () =
        let t = typeParameter "T"
        let u = typeParameter "U"
        let ws = workspace "generic-result-migration"
        // pick<T>(xs: List<T>): Option<T> = xs[0].
        let ws = create ws "pick" [ "xs", listOf t ] (optionOf t) (ExpressionRecipe.ListItemAt(slot 0, int 0L))
        let pick = find ws "pick"
        let pickT = ownTypeParameter pick
        let call xs = ExpressionRecipe.DirectCall(pick.Id, [ pick.Parameters.[0].Id, xs ])
        // firstOrEmpty<U>(items: List<U>): List<U> = match pick(items) with Some v -> [v] | None -> [].
        let ws =
            create ws "firstOrEmpty" [ "items", listOf u ] (listOf u)
                (ExpressionRecipe.MatchOption(call (slot 0), ExpressionRecipe.ListConstruct(u, [ valueSlot ]), ExpressionRecipe.ListEmpty u))
        let firstOrEmpty = find ws "firstOrEmpty"
        let firstOrEmptyU = ownTypeParameter firstOrEmpty
        let request: AgentResultTypeMigrationRequest =
            { ExpectedBaseSnapshotId = (snapshot ws).Id
              TargetFunctionId = pick.Id
              NewResultType = listOf pickT
              TargetReplacement = pickBody pickT (parameter pick 0)
              CallerRewrites =
                [ { CallerFunctionId = firstOrEmpty.Id
                    // The caller now returns the target's list, and binds its own U in a Bind.
                    Replacement =
                      ExpressionRecipe.Bind(
                          call (parameter firstOrEmpty 0),
                          ExpressionRecipe.ListConcat(valueSlot, ExpressionRecipe.ListEmpty firstOrEmptyU)) } ] }
        let review, next = commitResultMigration ws request
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        assertValidAndLowerable next
        let migrated = byId next pick
        Assert.Equal(listOf pickT, migrated.ResultType)
        Assert.Equal<SemanticParameter list>(pick.Parameters, migrated.Parameters)
        Assert.Equal<TypeRef list>([ pickT ], bodyTypes migrated |> List.distinct)
        let migratedCaller = byId next firstOrEmpty
        Assert.Equal(listOf firstOrEmptyU, migratedCaller.ResultType)
        Assert.Equal<TypeRef list>([ listOf firstOrEmptyU; firstOrEmptyU ], bodyTypes migratedCaller |> List.distinct)
        Assert.Equal<(FunctionId * ParameterId list) list>([ pick.Id, [ pick.Parameters.[0].Id ] ], directCalls migratedCaller)

    /// measure(n: Int64): Int64 = if n <= 0 then 0 else 1 + measure(n - 1), calling itself by FunctionId.
    let private recursiveFixture name =
        let ws = workspace name
        let ws = create ws "measure" [ "n", intType ] intType (slot 0)
        let placeholder = find ws "measure"
        let n = parameter placeholder 0
        let recursive =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.Int64LessThanOrEqual(n, int 0L),
                int 0L,
                ExpressionRecipe.Int64WrappingAdd(
                    int 1L,
                    ExpressionRecipe.DirectCall(placeholder.Id, [ placeholder.Parameters.Head.Id, ExpressionRecipe.Int64WrappingSubtract(n, int 1L) ])))
        let proposal =
            AgentExpressionStructuralWorkflow.simulate ws
                { TargetFunctionId = placeholder.Id
                  ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  ExpectedCurrentResultNodeId = placeholder.Body.ResultNodeId
                  Replacement = recursive }
            |> unwrap "replace body"
        let ws =
            match AgentExpressionStructuralWorkflow.commit ws proposal with
            | AgentStructuralCommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let measure = find ws "measure"
        let ws = create ws "twice" [ "x", intType ] intType (ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, slot 0 ]))
        ws, measure, find ws "twice"

    let private assertCallsItself (fn: SemanticFunction) =
        Assert.Contains((fn.Id, [ fn.Parameters.Head.Id ]), directCalls fn)

    [<Fact>]
    let ``parameter type migration accepts a target that calls itself by FunctionId or SelfCall`` () =
        for selfCallByFunctionId in [ true; false ] do
            let ws, measure, twice = recursiveFixture (if selfCallByFunctionId then "recursive-by-id" else "recursive-by-slot")
            assertCallsItself measure
            let n = parameter measure 0
            let rest = ExpressionRecipe.StringSlice(n, int 1L, ExpressionRecipe.StringLength n)
            // measure(n: String) = if n is empty then 0 else 1 + measure(n[1..]).
            let selfCall =
                if selfCallByFunctionId then ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, rest ])
                else ExpressionRecipe.SelfCall [ 0, rest ]
            let request: AgentParameterTypeMigrationRequest =
                { ExpectedBaseSnapshotId = (snapshot ws).Id
                  TargetFunctionId = measure.Id
                  TargetParameterId = measure.Parameters.Head.Id
                  NewParameterType = stringType
                  TargetReplacement =
                    ExpressionRecipe.Conditional(ExpressionRecipe.StringIsEmpty n, int 0L, ExpressionRecipe.Int64WrappingAdd(int 1L, selfCall))
                  CallerRewrites =
                    [ { CallerFunctionId = twice.Id
                        Replacement = ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, ExpressionRecipe.Int64ToString(parameter twice 0) ]) } ] }
            let review, next = commitParameterMigration ws request
            // The target's own call sites are neither callers to rewrite nor affected call sites.
            Assert.Equal<FunctionId list>([ twice.Id ], review.ResultingChange.AffectedCallSites |> List.map (fun site -> site.CallerFunctionId))
            Assert.Equal<FunctionId list>([ measure.Id; twice.Id ], review.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId))
            assertValidAndLowerable next
            let migrated = byId next measure
            Assert.Equal(stringType, migrated.Parameters.Head.Type)
            Assert.Equal<SemanticParameter list>([ { measure.Parameters.Head with Type = stringType } ], migrated.Parameters)
            Assert.Equal<(FunctionId * ParameterId list) list>([ measure.Id, [ measure.Parameters.Head.Id ] ], directCalls migrated)
            Assert.Equal<(FunctionId * ParameterId list) list>([ measure.Id, [ measure.Parameters.Head.Id ] ], directCalls (byId next twice))

    [<Fact>]
    let ``result type migration accepts a target that calls itself`` () =
        let ws, measure, twice = recursiveFixture "recursive-result"
        let n = parameter measure 0
        // measure(n): Option<Int64> = if n <= 0 then None else match measure(n - 1) with Some m -> Some (m + 1) | None -> None.
        let targetReplacement =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.Int64LessThanOrEqual(n, int 0L),
                ExpressionRecipe.OptionNone intType,
                ExpressionRecipe.MatchOption(
                    ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, ExpressionRecipe.Int64WrappingSubtract(n, int 1L) ]),
                    ExpressionRecipe.OptionSome(ExpressionRecipe.Int64WrappingAdd(valueSlot, int 1L)),
                    ExpressionRecipe.OptionNone intType))
        let request: AgentResultTypeMigrationRequest =
            { ExpectedBaseSnapshotId = (snapshot ws).Id
              TargetFunctionId = measure.Id
              NewResultType = optionOf intType
              TargetReplacement = targetReplacement
              CallerRewrites =
                [ { CallerFunctionId = twice.Id
                    Replacement =
                      ExpressionRecipe.MatchOption(
                          ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, parameter twice 0 ]),
                          valueSlot,
                          int 0L) } ] }
        let review, next = commitResultMigration ws request
        Assert.Equal<FunctionId list>([ twice.Id ], review.ResultingChange.AffectedCallSites |> List.map (fun site -> site.CallerFunctionId))
        assertValidAndLowerable next
        let migrated = byId next measure
        Assert.Equal(optionOf intType, migrated.ResultType)
        Assert.Equal<(FunctionId * ParameterId list) list>([ measure.Id, [ measure.Parameters.Head.Id ] ], directCalls migrated)

    [<Fact>]
    let ``a target's self call is typed against the new signature`` () =
        let ws, measure, twice = recursiveFixture "recursive-mistyped"
        let n = parameter measure 0
        let callerRewrite =
            { CallerFunctionId = twice.Id
              Replacement = ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, ExpressionRecipe.Int64ToString(parameter twice 0) ]) }
        let request selfCall: AgentParameterTypeMigrationRequest =
            { ExpectedBaseSnapshotId = (snapshot ws).Id
              TargetFunctionId = measure.Id
              TargetParameterId = measure.Parameters.Head.Id
              NewParameterType = stringType
              TargetReplacement = ExpressionRecipe.Conditional(ExpressionRecipe.StringIsEmpty n, int 0L, selfCall)
              CallerRewrites = [ callerRewrite ] }
        // Passing the old Int64 to the migrated parameter is a type error, not a call to the old function.
        let mistyped = rejectsParameterMigration ws (request (ExpressionRecipe.DirectCall(measure.Id, [ measure.Parameters.Head.Id, int 1L ])))
        Assert.Equal("ParameterTypeMigrationTargetRewriteInvalid", mistyped.Code)
        Assert.Contains("PureExpressionCallArgumentTypeMismatch", mistyped.Message)
        // A self call must bind the target's own parameters.
        let foreign = rejectsParameterMigration ws (request (ExpressionRecipe.DirectCall(measure.Id, [ twice.Parameters.Head.Id, n ])))
        Assert.Equal("ParameterTypeMigrationTargetRewriteInvalid", foreign.Code)
        Assert.Contains("ParameterTypeMigrationTargetSelfCallArgumentNotFound", foreign.Message)
