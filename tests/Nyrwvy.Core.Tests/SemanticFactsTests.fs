namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
open Xunit

module SemanticFactsTests =
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ])
    let private nodeId index = StableId.createForTesting<GraphNodeTag> $"node-facts-{index}"

    let private functionWith resultType nodes: SemanticFunction =
        { Id = StableId.createForTesting<FunctionTag> "function-facts"
          DisplayName = "facts"
          Parameters = []
          ResultType = resultType
          RequiredEffects = []
          Body = { Nodes = nodes; ResultNodeId = (List.last nodes).Id } }

    let private snapshotWith functions =
        { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-facts") with Functions = functions }

    let private sharedLists invalidLeaf depth =
        [ for index in 0 .. depth ->
            { Id = nodeId index
              Kind =
                if index = 0 then
                    if invalidLeaf then SemanticNodeKind.StringLiteral "not a list"
                    else SemanticNodeKind.ListEmpty intType
                else SemanticNodeKind.ListConcat(nodeId (index - 1), nodeId (index - 1)) } ]
        |> functionWith listType

    let private allocatedBytes action =
        let before = GC.GetAllocatedBytesForCurrentThread()
        action ()
        GC.GetAllocatedBytesForCurrentThread() - before

    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    let ``shared list type resolution memoizes both successes and failures`` invalidLeaf reverseNodes =
        let makeInput depth =
            let original = sharedLists invalidLeaf depth
            if reverseNodes then
                { original with Body = { original.Body with Nodes = List.rev original.Body.Nodes } }
            else original
        let expected = if invalidLeaf then None else Some listType
        let small = makeInput 12
        let resolveSmall () =
            let resolve = SemanticFacts.createNodeTypeResolver (snapshotWith [ small ]) small
            Assert.Equal<TypeRef option>(expected, resolve small.Body.ResultNodeId)
        resolveSmall () // warm the code path before measuring allocations
        let allocated = allocatedBytes resolveSmall
        // The old tree traversal allocates several MB even for this bounded DAG.
        // Check it first so a regression fails promptly, before the large case.
        Assert.True(allocated < 1_000_000L, $"Shared type resolution allocated {allocated} bytes.")

        let large = makeInput 64
        let snapshot = snapshotWith [ large ]
        let resolve = SemanticFacts.createNodeTypeResolver snapshot large
        for _ in 1 .. 3 do
            Assert.Equal<TypeRef option>(expected, resolve large.Body.ResultNodeId)
        if invalidLeaf then
            Assert.NotEmpty(SnapshotValidator.validate snapshot)
        else
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap
            let loaded = SnapshotCodec.deserialize json |> unwrap
            // The codec canonicalizes node order, so compare canonical content.
            Assert.Equal(json, SnapshotCodec.serialize loaded |> unwrap)
            Assert.Equal(SnapshotFingerprint.compute snapshot |> unwrap, SnapshotFingerprint.compute loaded |> unwrap)
            TypedSemanticIr.project loaded |> unwrap |> ignore

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``recursive type dependencies still reject cycles regardless of query order`` reverseQueries =
        let functionEntity =
            functionWith listType
                [ { Id = nodeId 0; Kind = SemanticNodeKind.ListEmpty intType }
                  { Id = nodeId 1; Kind = SemanticNodeKind.ListConcat(nodeId 2, nodeId 0) }
                  { Id = nodeId 2; Kind = SemanticNodeKind.ListConcat(nodeId 1, nodeId 1) }
                  { Id = nodeId 3; Kind = SemanticNodeKind.ListConcat(nodeId 2, nodeId 2) } ]
        let snapshot = snapshotWith [ functionEntity ]
        let resolve = SemanticFacts.createNodeTypeResolver snapshot functionEntity
        let queries = if reverseQueries then [ 3; 2; 1 ] else [ 1; 2; 3 ]
        for _ in 1 .. 2 do
            for index in queries do
                Assert.Equal<TypeRef option>(None, resolve (nodeId index))
            Assert.Equal<TypeRef option>(None, resolve (nodeId 99))
            Assert.Equal<TypeRef option>(Some listType, resolve (nodeId 0))
        Assert.Contains(SnapshotValidator.validate snapshot, fun issue -> issue.Code = "GraphNodeDependencyCycle")

    [<Fact>]
    let ``a resolver memo cannot leak between functions with reused node IDs`` () =
        let first = functionWith intType [ { Id = nodeId 0; Kind = SemanticNodeKind.Int64Literal 1L } ]
        let second =
            { functionWith listType [ { Id = nodeId 0; Kind = SemanticNodeKind.ListEmpty intType } ] with
                Id = StableId.createForTesting<FunctionTag> "function-other" }
        let create = SemanticFacts.createNodeTypeResolverFactory (snapshotWith [ first; second ])
        let resolveFirst = create first
        let resolveSecond = create second
        for _ in 1 .. 2 do
            Assert.Equal<TypeRef option>(Some intType, resolveFirst (nodeId 0))
            Assert.Equal<TypeRef option>(Some listType, resolveSecond (nodeId 0))

    [<Fact>]
    let ``snapshot indexes cannot leak across revisions retaining the same snapshot ID`` () =
        let targetId = StableId.createForTesting<FunctionTag> "function-target"
        let operationId = StableId.createForTesting<CapabilityOperationTag> "operation-facts"
        let fieldId = StableId.createForTesting<FieldTag> "field-facts"
        let variantId = StableId.createForTesting<VariantTag> "variant-facts"
        let ownerId suffix = StableId.createForTesting<TypeTag> $"type-union-{suffix}"
        let probe =
            functionWith intType
                [ { Id = nodeId 0; Kind = SemanticNodeKind.DirectCall(targetId, []) }
                  { Id = nodeId 1; Kind = SemanticNodeKind.CapabilityInvoke(nodeId 99, operationId, []) }
                  { Id = nodeId 2; Kind = SemanticNodeKind.FieldAccess(nodeId 99, fieldId) }
                  { Id = nodeId 3; Kind = SemanticNodeKind.UnionConstruct(variantId, [], []) } ]
        // These are type-fact fixtures, not valid executable bodies: referenced
        // operand values are deliberately absent, as they do not determine types.
        let revision suffix resultType =
            { snapshotWith [ { probe with Id = targetId; ResultType = resultType } ] with
                Types =
                    [ { TypeParameters = []; Id = StableId.createForTesting<TypeTag> "type-product"
                        DisplayName = "Product"
                        Kind = SemanticTypeKind.Product [ { Id = fieldId; DisplayName = "field"; Type = resultType } ] }
                      { TypeParameters = []; Id = ownerId suffix
                        DisplayName = "Union"
                        Kind = SemanticTypeKind.Union [ { Id = variantId; DisplayName = "Case"; Fields = [] } ] } ]
                Capabilities =
                    [ { Id = StableId.createForTesting<CapabilityTag> "capability-facts"
                        DisplayName = "Capability"
                        Operations =
                            [ { Id = operationId
                                DisplayName = "operation"
                                Parameters = []
                                ResultType = resultType
                                FailureType = None
                                EffectId = StableId.createForTesting<EffectTag> "effect-facts" } ] } ] }
        let first = revision "first" intType
        let second = revision "second" listType
        Assert.Equal(first.Id, second.Id)
        let resolveFirst = SemanticFacts.createNodeTypeResolverFactory first probe
        let resolveSecond = SemanticFacts.createNodeTypeResolverFactory second probe
        for _ in 1 .. 2 do
            for index in 0 .. 2 do
                Assert.Equal<TypeRef option>(Some intType, resolveFirst (nodeId index))
                Assert.Equal<TypeRef option>(Some listType, resolveSecond (nodeId index))
            Assert.Equal<TypeRef option>(Some(TypeRef.Named(ownerId "first", [])), resolveFirst (nodeId 3))
            Assert.Equal<TypeRef option>(Some(TypeRef.Named(ownerId "second", [])), resolveSecond (nodeId 3))

    [<Theory>]
    [<InlineData("validate")>]
    [<InlineData("typed-ir")>]
    let ``snapshot passes do not rebuild whole snapshot indexes per function`` operation =
        let input count =
            [ for index in 1 .. count ->
                { functionWith intType [ { Id = nodeId index; Kind = SemanticNodeKind.Int64Literal 1L } ] with
                    Id = StableId.createForTesting<FunctionTag> $"function-scale-{index}"
                    DisplayName = $"function_{index}" } ]
            |> snapshotWith
        let run snapshot =
            match operation with
            | "validate" -> Assert.Empty(SnapshotValidator.validate snapshot)
            | "typed-ir" -> TypedSemanticIr.project snapshot |> unwrap |> ignore
            | _ -> failwith "Unknown pass"
        let small, large = input 100, input 400
        run small
        run large
        let smallBytes = allocatedBytes (fun () -> run small)
        let largeBytes = allocatedBytes (fun () -> run large)
        // Four times as many fixed-size functions permits O(n log n) map costs,
        // but not the old O(n^2) index rebuilding. No machine-speed threshold.
        Assert.True(largeBytes < 6L * smallBytes, $"{operation}: 100 functions={smallBytes} bytes; 400={largeBytes} bytes.")
