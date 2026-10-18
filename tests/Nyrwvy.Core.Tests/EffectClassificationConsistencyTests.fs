namespace Nyrwvy.Core.Tests

open System
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

/// "Which node is an observable effect" is decided twice: by `GraphOrder` on semantic nodes (abort
/// ordering before publication) and by `ExecutionIrAnalysis` on typed nodes (effect ordering at
/// lowering). The nominal/local classifications are probed through the public ordering check: a
/// candidate node is placed next to an unordered `Abort` witness, and the check reports an ordering
/// conflict exactly when the candidate is observable. Every union case is built by reflection, so a
/// new node kind is covered automatically and a nominal/local classification mismatch fails here.
/// Lowering and proposal admission share `GraphOrder`'s transitive closure; full snapshot validation
/// retains the historical callee-contract rule so persisted older graphs remain openable for repair.
module EffectClassificationConsistencyTests =
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private probeId = StableId.createForTesting<FunctionTag> "a-effect-probe"
    let private effectfulTargetId = StableId.createForTesting<FunctionTag> "t-effectful-target"
    let private pureTargetId = StableId.createForTesting<FunctionTag> "t-pure-target"
    let private effectId = StableId.createForTesting<EffectTag> "effect-probe"
    let private witnessId = StableId.createForTesting<GraphNodeTag> "witness-abort"

    type private Variant =
        { Name: string
          TargetFunctionId: FunctionId
          NestedEffectful: bool }

    /// Function operands target an effectful function and nested graphs hold a capability call.
    let private effectfulVariant = { Name = "effectful"; TargetFunctionId = effectfulTargetId; NestedEffectful = true }
    /// Function operands target a pure function and nested graphs hold only a literal.
    let private pureVariant = { Name = "pure"; TargetFunctionId = pureTargetId; NestedEffectful = false }

    let mutable private counter = 0

    let private freshNode () =
        counter <- counter + 1
        StableId.createForTesting<GraphNodeTag> $"probe-node-{counter}"

    let private semanticGraph (variant: Variant): ValueGraph =
        let id = freshNode ()
        let kind =
            if variant.NestedEffectful then
                SemanticNodeKind.CapabilityInvoke(freshNode (), StableId.createForTesting "probe-operation", [])
            else
                SemanticNodeKind.UnitLiteral
        { Nodes = [ { Id = id; Kind = kind } ]; ResultNodeId = id }

    let private typedGraph (variant: Variant): TypedSemanticGraph =
        let id = freshNode ()
        let kind =
            if variant.NestedEffectful then
                TypedSemanticNodeKind.CapabilityInvoke(freshNode (), StableId.createForTesting "probe-operation", [])
            else
                TypedSemanticNodeKind.UnitLiteral
        { Nodes = [ { OriginNodeId = id; Completion = GraphCompletion.Normal unitType; Kind = kind } ]
          ResultNodeId = id }

    let private isStableId (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<StableId<obj>>

    /// Builds an arbitrary value of `t`: node operands are fresh (dangling) ids, function operands
    /// are the variant's target, nested graphs follow the variant, lists hold one item.
    let rec private build (variant: Variant) (depth: int) (t: Type): obj =
        if depth > 12 then failwithf "Probe value construction recursed too deeply at %s." t.FullName
        let next = build variant (depth + 1)
        if t = typeof<GraphNodeId> then box (freshNode ())
        elif t = typeof<FunctionId> then box variant.TargetFunctionId
        elif t = typeof<ValueGraph> then box (semanticGraph variant)
        elif t = typeof<TypedSemanticGraph> then box (typedGraph variant)
        elif t = typeof<TypeRef> then box unitType
        elif isStableId t then
            FSharpValue.MakeUnion(FSharpType.GetUnionCases(t, true).[0], [| box $"probe-{freshNode () |> StableId.value}" |], true)
        elif t = typeof<string> then box "probe"
        elif t = typeof<bool> then box false
        elif t = typeof<int> then box 0
        elif t = typeof<int64> then box 0L
        elif t = typeof<uint64> then box 0UL
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<obj>> then
            let item = next (t.GetGenericArguments().[0])
            let cases = FSharpType.GetUnionCases(t)
            let empty = FSharpValue.MakeUnion(cases |> Array.find (fun case -> case.Name = "Empty"), [||])
            FSharpValue.MakeUnion(cases |> Array.find (fun case -> case.Name = "Cons"), [| item; empty |])
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<obj>> then
            FSharpValue.MakeUnion(FSharpType.GetUnionCases(t).[0], [||])
        elif FSharpType.IsTuple t then
            FSharpValue.MakeTuple(FSharpType.GetTupleElements t |> Array.map next, t)
        elif FSharpType.IsRecord(t, true) then
            FSharpValue.MakeRecord(t, FSharpType.GetRecordFields(t, true) |> Array.map (fun field -> next field.PropertyType), true)
        elif FSharpType.IsUnion(t, true) then
            let cases = FSharpType.GetUnionCases(t, true)
            let case = cases |> Array.tryFind (fun case -> case.GetFields().Length = 0) |> Option.defaultValue cases.[0]
            FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun field -> next field.PropertyType), true)
        else
            failwithf "No probe value for %s; extend EffectClassificationConsistencyTests.build." t.FullName

    let private makeCase<'Kind> (variant: Variant) (case: UnionCaseInfo): 'Kind =
        FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun field -> build variant 0 field.PropertyType), true)
        :?> 'Kind

    let private semanticCases = FSharpType.GetUnionCases(typeof<SemanticNodeKind>, true)
    let private typedCases = FSharpType.GetUnionCases(typeof<TypedSemanticNodeKind>, true)

    let private semanticFunction id effects body: SemanticFunction =
        { Id = id
          DisplayName = StableId.value id
          Parameters = []
          ResultType = unitType
          RequiredEffects = effects
          Body = body }

    let private semanticObservable (variant: Variant) (case: UnionCaseInfo) =
        let candidate: SemanticNode = { Id = freshNode (); Kind = makeCase<SemanticNodeKind> variant case }
        let witness: SemanticNode = { Id = witnessId; Kind = SemanticNodeKind.Abort(freshNode ()) }
        let targets =
            [ semanticFunction effectfulTargetId [ effectId ] (semanticGraph pureVariant)
              semanticFunction pureTargetId [] (semanticGraph pureVariant) ]
            |> List.map (fun fn -> fn.Id, fn)
            |> Map.ofList
        let probe = semanticFunction probeId [] { Nodes = [ candidate; witness ]; ResultNodeId = witnessId }
        match GraphOrder.tryAbortOrderConflict targets probe with
        | Some(first, second) -> first = candidate.Id || second = candidate.Id
        | None -> false

    let private typedFunction id effects body: TypedSemanticFunction =
        { Id = id
          Parameters = []
          ResultType = unitType
          RequiredEffects = effects
          Body = body }

    let private typedEffectful (variant: Variant) (case: UnionCaseInfo) =
        let candidate: TypedSemanticNode =
            { OriginNodeId = freshNode ()
              Completion = GraphCompletion.Normal unitType
              Kind = makeCase<TypedSemanticNodeKind> variant case }
        let witness: TypedSemanticNode =
            { OriginNodeId = witnessId
              Completion = GraphCompletion.Aborts
              Kind = TypedSemanticNodeKind.Abort(freshNode ()) }
        let latentId = StableId.createForTesting<FunctionTag> "t-latent-hop"
        let body (kind: TypedSemanticNodeKind): TypedSemanticGraph =
            let id = freshNode ()
            { Nodes = [ { OriginNodeId = id; Completion = GraphCompletion.Normal unitType; Kind = kind } ]; ResultNodeId = id }
        let target =
            match variant.Name with
            | "intrinsic" -> typedFunction effectfulTargetId [] (body TypedSemanticNodeKind.CancellationRequested)
            | "transitive" -> typedFunction effectfulTargetId [] (body (TypedSemanticNodeKind.DirectCall(latentId, [])))
            | "calleeAbort" -> typedFunction effectfulTargetId [] (body (TypedSemanticNodeKind.Abort(freshNode ())))
            | _ -> typedFunction effectfulTargetId [ effectId ] (typedGraph pureVariant)
        let program: TypedSemanticProgram =
            { SourceSnapshotId = StableId.createForTesting "effect-probe-snapshot"
              Types = []
              Effects = [ effectId ]
              Capabilities = []
              Functions =
                [ typedFunction probeId [] { Nodes = [ candidate; witness ]; ResultNodeId = witnessId }
                  target
                  typedFunction latentId [] (body TypedSemanticNodeKind.CancellationRequested)
                  typedFunction pureTargetId [] (typedGraph pureVariant) ] }
        // Effect ordering is validated before anything else is lowered (the probe sorts first), so
        // any later failure of this synthetic program means the ordering check passed.
        try
            match ExecutionIr.lower program with
            | Error(ExecutionIrError.AmbiguousEffectOrder(functionId, first, second)) ->
                functionId = probeId && (first = candidate.OriginNodeId || second = candidate.OriginNodeId)
            | _ -> false
        with _ -> false

    let private caseNames (cases: UnionCaseInfo array) = cases |> Array.map (fun case -> case.Name) |> Set.ofArray

    [<Fact>]
    let ``typed IR node kinds mirror semantic node kinds one to one`` () =
        Assert.Equal<Set<string>>(caseNames semanticCases, caseNames typedCases)

    [<Theory>]
    [<InlineData("effectful")>]
    [<InlineData("pure")>]
    let ``GraphOrder and ExecutionIrAnalysis classify every node kind alike`` (variantName: string) =
        let variant = if variantName = "effectful" then effectfulVariant else pureVariant
        let typedByName = typedCases |> Array.map (fun case -> case.Name, case) |> Map.ofArray
        let mismatches =
            semanticCases
            |> Array.choose (fun case ->
                match Map.tryFind case.Name typedByName with
                | None -> None // reported by the one-to-one test
                | Some typedCase ->
                    let semantic = semanticObservable variant case
                    let typed = typedEffectful variant typedCase
                    if semantic = typed then None
                    else Some $"{case.Name}: GraphOrder observable={semantic}, ExecutionIrAnalysis effectful={typed}")
            |> List.ofArray
        Assert.True(List.isEmpty mismatches, String.Join(Environment.NewLine, mismatches))

    [<Fact>]
    let ``effect probes distinguish intrinsic, callee-dependent and pure node kinds`` () =
        let observable variant name =
            semanticCases |> Array.find (fun case -> case.Name = name) |> semanticObservable variant
        let intrinsic =
            [ "Abort"; "CapabilityInvoke"; "UseResource"; "TaskScope"; "TaskStart"; "TaskWait"; "TaskCancel"; "CancellationRequested" ]
        let calleeDependent = [ "DirectCall"; "ListEffectFold"; "ParallelAll" ]
        let nestedDependent = [ "Conditional"; "BoolAnd"; "BoolOr"; "MatchUnion"; "Bind"; "MatchOption"; "MatchResult" ]
        for name in intrinsic do
            Assert.True(observable pureVariant name, $"{name} is observable regardless of its operands.")
        for name in calleeDependent @ nestedDependent do
            Assert.True(observable effectfulVariant name, $"{name} is observable with an effectful operand.")
            Assert.False(observable pureVariant name, $"{name} is not observable with pure operands.")
        for name in [ "ListMap"; "ListFold"; "FunctionInvoke"; "Int64Literal" ] do
            Assert.False(observable effectfulVariant name, $"{name} is never observable.")

    [<Fact>]
    let ``runtime ordering closure terminates pure and seeded recursive call cycles`` () =
        let a, b, c = probeId, effectfulTargetId, pureTargetId
        let pureCycle = Map.ofList [ a, (false, [ b ]); b, (false, [ a ]); c, (false, []) ]
        Assert.Empty(GraphOrder.observableClosure pureCycle)
        let seeded = pureCycle |> Map.add b (true, [ a ])
        Assert.Equal<Set<FunctionId>>(set [ a; b ], GraphOrder.observableClosure seeded)
        let downstream = seeded |> Map.add c (false, [ a ])
        Assert.Equal<Set<FunctionId>>(set [ a; b; c ], GraphOrder.observableClosure downstream)

    [<Fact>]
    let ``lowering propagates intrinsic helpers through calls effect folders and workers`` () =
        for name in [ "DirectCall"; "ListEffectFold"; "ParallelAll" ] do
            let typedCase = typedCases |> Array.find (fun case -> case.Name = name)
            for variantName in [ "intrinsic"; "transitive" ] do
                let variant = { Name = variantName; TargetFunctionId = effectfulTargetId; NestedEffectful = false }
                Assert.True(typedEffectful variant typedCase, $"{variantName} {name} lost runtime observability.")
            let abortOnly = { Name = "calleeAbort"; TargetFunctionId = effectfulTargetId; NestedEffectful = false }
            Assert.False(typedEffectful abortOnly typedCase, $"{name} treats a callee's local Abort Result as a runtime effect.")

    /// The proposal-time rule (`GraphOrder.semanticObservableFunctions`) reaches the same verdict as
    /// lowering for intrinsic and transitive helpers and for a callee whose only exit is Abort.
    let private semanticRuntimeObservable (variantName: string) (case: UnionCaseInfo) =
        let variant = { Name = variantName; TargetFunctionId = effectfulTargetId; NestedEffectful = false }
        let candidate: SemanticNode = { Id = freshNode (); Kind = makeCase<SemanticNodeKind> variant case }
        let witness: SemanticNode = { Id = witnessId; Kind = SemanticNodeKind.Abort(freshNode ()) }
        let latentId = StableId.createForTesting<FunctionTag> "t-latent-hop"
        let body kind: ValueGraph =
            let id = freshNode ()
            { Nodes = [ { Id = id; Kind = kind } ]; ResultNodeId = id }
        let target =
            match variantName with
            | "intrinsic" -> semanticFunction effectfulTargetId [] (body SemanticNodeKind.CancellationRequested)
            | "transitive" -> semanticFunction effectfulTargetId [] (body (SemanticNodeKind.DirectCall(latentId, [])))
            | _ -> semanticFunction effectfulTargetId [] (body (SemanticNodeKind.Abort(freshNode ())))
        let probe = semanticFunction probeId [] { Nodes = [ candidate; witness ]; ResultNodeId = witnessId }
        let functions =
            [ probe
              target
              semanticFunction latentId [] (body SemanticNodeKind.CancellationRequested)
              semanticFunction pureTargetId [] (semanticGraph pureVariant) ]
        match GraphOrder.tryEffectOrderConflict (GraphOrder.semanticObservableFunctions functions) probe with
        | Some(first, second) -> first = candidate.Id || second = candidate.Id
        | None -> false

    [<Fact>]
    let ``proposal ordering and lowering agree on transitive helper observability for every node kind`` () =
        let typedByName = typedCases |> Array.map (fun case -> case.Name, case) |> Map.ofArray
        let mismatches =
            [ for variantName in [ "intrinsic"; "transitive"; "calleeAbort" ] do
                for case in semanticCases do
                    match Map.tryFind case.Name typedByName with
                    | None -> ()
                    | Some typedCase ->
                        let variant = { Name = variantName; TargetFunctionId = effectfulTargetId; NestedEffectful = false }
                        let semantic = semanticRuntimeObservable variantName case
                        let typed = typedEffectful variant typedCase
                        if semantic <> typed then
                            yield $"{variantName} {case.Name}: proposal observable={semantic}, lowering effectful={typed}" ]
        Assert.True(List.isEmpty mismatches, String.Join(Environment.NewLine, mismatches))
