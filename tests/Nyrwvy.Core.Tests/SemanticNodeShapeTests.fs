namespace Nyrwvy.Core.Tests

open System
open System.Collections
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

/// Guards the one definition of node references and nested graphs (`SemanticNodeShape`) against
/// the shape of `SemanticNodeKind` itself. A representative node of every union case is built by
/// reflection, so a case added to the union cannot be left out, and the payload each case really
/// holds (node IDs, nested graphs, pattern bindings outside those graphs) must match what the
/// shared functions report or rewrite.
module SemanticNodeShapeTests =
    /// What a node kind's fields hold outside its nested graphs.
    type private Payload =
        { References: GraphNodeId list
          Graphs: ValueGraph list
          Bindings: PatternBinding list }

    let private emptyPayload = { References = []; Graphs = []; Bindings = [] }

    let private merge (payloads: Payload seq) =
        payloads
        |> Seq.fold
            (fun merged payload ->
                { References = merged.References @ payload.References
                  Graphs = merged.Graphs @ payload.Graphs
                  Bindings = merged.Bindings @ payload.Bindings })
            emptyPayload

    let private isGeneric (definition: Type) (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = definition

    let private isStableId = isGeneric (typedefof<StableId<GraphNodeTag>>)
    let private isList = isGeneric (typedefof<list<obj>>)
    let private isOption = isGeneric (typedefof<option<obj>>)

    let private makeUnion (case: UnionCaseInfo) (fields: obj[]) = FSharpValue.MakeUnion(case, fields, true)

    /// A fixture value of any payload type. Every stable ID is fresh, so references, nested graphs
    /// and bindings can be told apart; lists carry two items so list-shaped operands are exercised.
    let rec private synthesize (nextId: unit -> string) (t: Type): obj =
        if t = typeof<string> then box "text"
        elif t = typeof<bool> then box true
        elif t = typeof<int64> then box 1L
        elif t = typeof<uint64> then box 1UL
        elif t = typeof<int> then box 1
        elif isStableId t then
            let case = FSharpType.GetUnionCases(t, true) |> Array.exactlyOne
            makeUnion case [| box (nextId ()) |]
        elif isList t then
            let itemType = t.GetGenericArguments().[0]
            let cases = FSharpType.GetUnionCases t
            let empty = cases |> Array.find (fun case -> case.Name = "Empty")
            let cons = cases |> Array.find (fun case -> case.Name = "Cons")
            [ 1; 2 ]
            |> List.fold (fun tail _ -> makeUnion cons [| synthesize nextId itemType; tail |]) (makeUnion empty [||])
        elif isOption t then
            let some = FSharpType.GetUnionCases t |> Array.find (fun case -> case.Name = "Some")
            makeUnion some [| synthesize nextId (t.GetGenericArguments().[0]) |]
        elif FSharpType.IsTuple t then
            FSharpValue.MakeTuple(FSharpType.GetTupleElements t |> Array.map (synthesize nextId), t)
        elif FSharpType.IsRecord(t, true) then
            let fields = FSharpType.GetRecordFields(t, true) |> Array.map (fun field -> synthesize nextId field.PropertyType)
            FSharpValue.MakeRecord(t, fields, true)
        elif FSharpType.IsUnion(t, true) then
            let case = FSharpType.GetUnionCases(t, true).[0]
            makeUnion case (case.GetFields() |> Array.map (fun field -> synthesize nextId field.PropertyType))
        else
            failwith $"No fixture value for {t.FullName}."

    /// Collects the node IDs, nested graphs and pattern bindings a value holds, without looking
    /// inside nested graphs.
    let rec private walk (t: Type) (value: obj): Payload =
        if t = typeof<GraphNodeId> then { emptyPayload with References = [ unbox value ] }
        elif t = typeof<ValueGraph> then { emptyPayload with Graphs = [ unbox value ] }
        elif t = typeof<PatternBinding> then { emptyPayload with Bindings = [ unbox value ] }
        elif isList t then
            let itemType = t.GetGenericArguments().[0]
            (value :?> IEnumerable) |> Seq.cast<obj> |> Seq.map (walk itemType) |> merge
        elif isOption t then
            if isNull value then emptyPayload
            else walk (t.GetGenericArguments().[0]) (FSharpValue.GetUnionFields(value, t) |> snd |> Array.exactlyOne)
        elif FSharpType.IsTuple t then
            Seq.zip (FSharpType.GetTupleElements t) (FSharpValue.GetTupleFields value)
            |> Seq.map (fun (elementType, element) -> walk elementType element)
            |> merge
        elif FSharpType.IsRecord(t, true) then
            FSharpType.GetRecordFields(t, true)
            |> Seq.map (fun field -> walk field.PropertyType (field.GetValue value))
            |> merge
        elif FSharpType.IsUnion(t, true) then
            let case, fields = FSharpValue.GetUnionFields(value, t, true)
            Seq.zip (case.GetFields()) fields
            |> Seq.map (fun (field, fieldValue) -> walk field.PropertyType fieldValue)
            |> merge
        else
            emptyPayload

    let private nodeKinds = FSharpType.GetUnionCases typeof<SemanticNodeKind>

    let private nodeOf (case: UnionCaseInfo): SemanticNode * Payload =
        let counter = ref 0
        let nextId () =
            counter.Value <- counter.Value + 1
            $"{case.Name}-{counter.Value}"
        let kind =
            makeUnion case (case.GetFields() |> Array.map (fun field -> synthesize nextId field.PropertyType))
            :?> SemanticNodeKind
        let node: SemanticNode = { Id = StableId.createForTesting<GraphNodeTag> $"{case.Name}-node"; Kind = kind }
        node, walk typeof<SemanticNodeKind> (box kind)

    let private payloadOf (node: SemanticNode) = walk typeof<SemanticNodeKind> (box node.Kind)

    let private sortIds (ids: GraphNodeId list) = ids |> List.sortBy StableId.value
    let private sortGraphs (graphs: ValueGraph list) = graphs |> List.sortBy (fun graph -> StableId.value graph.ResultNodeId)
    let private sortBindings (bindings: PatternBinding list) = bindings |> List.sortBy (fun binding -> StableId.value binding.Id)

    /// Runs one check per node kind and reports every kind that fails it.
    let private forEveryKind (check: SemanticNode -> Payload -> unit) =
        let failures =
            nodeKinds
            |> Array.choose (fun case ->
                let node, payload = nodeOf case
                try
                    check node payload
                    None
                with error -> Some $"{case.Name}: {error.Message}")
        Assert.True(nodeKinds.Length > 0)
        Assert.True(Array.isEmpty failures, String.Join("\n", failures))

    [<Fact>]
    let ``direct value references are exactly the node IDs a kind holds outside nested graphs`` () =
        forEveryKind (fun node payload ->
            Assert.Equal<GraphNodeId list>(sortIds payload.References, sortIds (SemanticNodeShape.directValueReferences node)))

    [<Fact>]
    let ``nested graphs are exactly the graphs a kind owns`` () =
        forEveryKind (fun node payload ->
            Assert.Equal<ValueGraph list>(sortGraphs payload.Graphs, sortGraphs (SemanticNodeShape.nestedGraphs node)))

    [<Fact>]
    let ``nested scope bindings are exactly the pattern bindings a kind holds outside nested graphs`` () =
        forEveryKind (fun node payload ->
            let reported =
                SemanticNodeShape.nestedScopes node
                |> List.collect (fun scope -> scope.Bindings |> List.map NestedBinding.pattern)
            Assert.Equal<PatternBinding list>(sortBindings payload.Bindings, sortBindings reported))

    [<Fact>]
    let ``mapValueReferences rewrites every direct reference and nothing else`` () =
        forEveryKind (fun node payload ->
            let visited = ResizeArray<GraphNodeId>()
            let unchanged =
                SemanticNodeShape.mapValueReferences
                    (fun id ->
                        visited.Add id
                        id)
                    node
            Assert.Equal(node, unchanged)
            Assert.Equal<GraphNodeId list>(sortIds (SemanticNodeShape.directValueReferences node), sortIds (List.ofSeq visited))

            let rename (id: GraphNodeId) = StableId.createForTesting<GraphNodeTag> $"renamed-{StableId.value id}"
            let renamed = SemanticNodeShape.mapValueReferences rename node |> payloadOf
            Assert.Equal<GraphNodeId list>(sortIds (payload.References |> List.map rename), sortIds renamed.References)
            Assert.Equal<ValueGraph list>(sortGraphs payload.Graphs, sortGraphs renamed.Graphs)
            Assert.Equal<PatternBinding list>(sortBindings payload.Bindings, sortBindings renamed.Bindings))

    [<Fact>]
    let ``mapNested rewrites every nested graph and scope binding and nothing else`` () =
        forEveryKind (fun node payload ->
            Assert.Equal(node, SemanticNodeShape.mapNested id id node)

            let markGraph (graph: ValueGraph) = { graph with Nodes = [] }
            let markBinding (binding: PatternBinding) = { binding with DisplayName = Some "marked" }
            let marked = SemanticNodeShape.mapNested markBinding markGraph node |> payloadOf
            Assert.Equal<ValueGraph list>(sortGraphs (payload.Graphs |> List.map markGraph), sortGraphs marked.Graphs)
            Assert.Equal<PatternBinding list>(sortBindings (payload.Bindings |> List.map markBinding), sortBindings marked.Bindings)
            Assert.Equal<GraphNodeId list>(sortIds payload.References, sortIds marked.References)

            let folded = SemanticNodeShape.foldNestedGraphs (fun graphs scope -> scope.Graph :: graphs) [] node
            Assert.Equal<ValueGraph list>(sortGraphs payload.Graphs, sortGraphs folded))

    [<Fact>]
    let ``review value dependencies name the same nodes as direct value references`` () =
        forEveryKind (fun node _ ->
            let reviewed = ValueDependency.ofNode node |> List.map (fun dependency -> dependency.NodeId)
            Assert.Equal<GraphNodeId list>(sortIds (SemanticNodeShape.directValueReferences node), sortIds reviewed))

    let private nodeId name = StableId.createForTesting<GraphNodeTag> name

    [<Fact>]
    let ``dependencies follow free references of nested graphs but not their local nodes`` () =
        let outer: SemanticNode = { Id = nodeId "outer"; Kind = SemanticNodeKind.StringLiteral "outer" }
        let condition: SemanticNode = { Id = nodeId "condition"; Kind = SemanticNodeKind.BoolLiteral true }
        let local: SemanticNode = { Id = nodeId "local"; Kind = SemanticNodeKind.StringIsEmpty outer.Id }
        let localResult: SemanticNode = { Id = nodeId "local-result"; Kind = SemanticNodeKind.BoolNot local.Id }
        let whenTrue: ValueGraph = { Nodes = [ local; localResult ]; ResultNodeId = localResult.Id }
        let other: SemanticNode = { Id = nodeId "other"; Kind = SemanticNodeKind.BoolLiteral false }
        let whenFalse: ValueGraph = { Nodes = [ other ]; ResultNodeId = nodeId "other" }
        let conditional: SemanticNode = { Id = nodeId "conditional"; Kind = SemanticNodeKind.Conditional(condition.Id, whenTrue, whenFalse) }
        let graph: ValueGraph = { Nodes = [ outer; condition; conditional ]; ResultNodeId = conditional.Id }

        Assert.Equal<GraphNodeId list>([ condition.Id; outer.Id ], SemanticNodeShape.dependencies conditional)
        Assert.Equal<GraphNodeId list>([ outer.Id ], SemanticNodeShape.freeReferences whenTrue)
        Assert.Equal<GraphNodeId list>(
            [ "outer"; "condition"; "conditional"; "local"; "local-result"; "other" ] |> List.map nodeId,
            ValueGraph.collectNodes graph |> List.map (fun node -> node.Id)
        )

        let renamed = ValueGraph.mapNodes (fun node -> { node with Id = nodeId $"renamed-{StableId.value node.Id}" }) graph
        Assert.Equal<GraphNodeId list>(
            [ "outer"; "condition"; "conditional"; "local"; "local-result"; "other" ] |> List.map (fun name -> nodeId $"renamed-{name}"),
            ValueGraph.collectNodes renamed |> List.map (fun node -> node.Id)
        )
