namespace Nyrwvy.Core.Tests

open System.Threading
open Nyrwvy.Core
open Xunit

/// A flat dependency chain `n0 -> n1 -> ... -> nN` in one body is bounded only by the node count,
/// not by the codec's nesting limit. Every pass over the chain must therefore keep its own stack:
/// these cases run on a thread with a 1 MB stack, where recursion over a few thousand nodes
/// already overflowed (a StackOverflowException kills the process; no test could catch it).
module DependencyChainTests =
    let private chainLength = 20_000

    /// Runs the action on a thread with a 1 MB stack and rethrows its failure.
    let private onSmallStack (action: unit -> 'T): 'T =
        let mutable outcome = Unchecked.defaultof<Result<'T, exn>>
        let thread = Thread((fun () -> outcome <- (try Ok(action ()) with error -> Error error)), 1_048_576)
        thread.Start()
        thread.Join()
        match outcome with
        | Ok value -> value
        | Error error -> raise error

    // Node IDs sort top-first, so a search that starts from the smallest ID begins at the result
    // node and must follow the whole chain down to the leaf before anything is memoised.
    let private nodeId (index: int) = StableId.createForTesting<GraphNodeTag> $"node-chain-{index:D6}"

    let private chain (leaf: SemanticNodeKind) (link: GraphNodeId -> SemanticNodeKind) (length: int): ValueGraph =
        { Nodes =
            [ for index in 0 .. length ->
                { Id = nodeId index
                  Kind = if index = length then leaf else link (nodeId (index + 1)) } ]
          ResultNodeId = nodeId 0 }

    let private functionWith (resultType: TypeRef) (body: ValueGraph): SemanticFunction =
        { Id = StableId.createForTesting<FunctionTag> "function-chain"
          DisplayName = "chain"
          Parameters = []
          ResultType = resultType
          RequiredEffects = []
          Body = body }

    let private snapshotWith (functionEntity: SemanticFunction) =
        { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-chain") with Functions = [ functionEntity ] }

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private int64ListType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ int64Type ])

    [<Fact>]
    let ``a long flat chain validates and round-trips through the codec on a small stack`` () =
        let snapshot =
            chain (SemanticNodeKind.Int64Literal 1L) (fun previous -> SemanticNodeKind.Int64WrappingAdd(previous, previous)) chainLength
            |> functionWith int64Type
            |> snapshotWith
        let restored =
            onSmallStack (fun () ->
                Assert.Empty(SnapshotValidator.validate snapshot)
                match SnapshotCodec.serialize snapshot |> Result.bind SnapshotCodec.deserialize with
                | Ok restored -> restored
                | Error error -> failwithf "Unexpected codec error: %A" error)
        Assert.Equal(chainLength + 1, (ValueGraph.collectNodes restored.Functions.Head.Body).Length)

    [<Fact>]
    let ``a long flat chain closed into a cycle is reported on a small stack`` () =
        let graph = chain (SemanticNodeKind.Int64WrappingAdd(nodeId 0, nodeId 0)) (fun previous -> SemanticNodeKind.Int64WrappingAdd(previous, previous)) chainLength
        let issues = onSmallStack (fun () -> SnapshotValidator.validate (snapshotWith (functionWith int64Type graph)))
        Assert.Equal<string list>([ "GraphNodeDependencyCycle" ], issues |> List.map (fun issue -> issue.Code))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``the type resolver follows a long operand chain on a small stack`` (invalidLeaf: bool) =
        // ListReverse takes its type from its operand, so the result type is only known at the leaf.
        let leaf = if invalidLeaf then SemanticNodeKind.StringLiteral "not a list" else SemanticNodeKind.ListEmpty int64Type
        let functionEntity = chain leaf SemanticNodeKind.ListReverse chainLength |> functionWith int64ListType
        let resolved =
            onSmallStack (fun () ->
                let resolve = SemanticFacts.createNodeTypeResolver (snapshotWith functionEntity) functionEntity
                resolve functionEntity.Body.ResultNodeId)
        Assert.Equal<TypeRef option>((if invalidLeaf then None else Some int64ListType), resolved)

    [<Fact>]
    let ``effect ordering follows a long dependency chain on a small stack`` () =
        // The last node reaches the first only through the whole chain, so the pair is ordered.
        let dependencies =
            [ for index in 0 .. chainLength - 1 -> nodeId index, [ nodeId (index + 1) ] ] @ [ nodeId chainLength, [] ]
            |> Map.ofList
        let unordered = onSmallStack (fun () -> GraphOrder.tryUnorderedPair dependencies [ nodeId 0; nodeId chainLength ])
        Assert.Equal<(GraphNodeId * GraphNodeId) option>(None, unordered)
