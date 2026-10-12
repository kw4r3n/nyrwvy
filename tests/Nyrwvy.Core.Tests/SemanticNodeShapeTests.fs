namespace Nyrwvy.Core.Tests

open System
open System.Collections
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

/// Reflection fixtures shared by the shape tests: a value of any payload type, and every value of
/// one type that a payload holds.
module internal NodeShapeFixtures =
    let isGeneric (definition: Type) (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = definition

    let isStableId = isGeneric (typedefof<StableId<GraphNodeTag>>)
    let isList = isGeneric (typedefof<list<obj>>)
    let isOption = isGeneric (typedefof<option<obj>>)

    let makeUnion (case: UnionCaseInfo) (fields: obj[]) = FSharpValue.MakeUnion(case, fields, true)

    /// A fixture value of any payload type. Every stable ID is fresh, so references, nested graphs
    /// and bindings can be told apart; lists carry two items so list-shaped operands are exercised.
    let rec synthesize (nextId: unit -> string) (t: Type): obj =
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

    /// Every value of type `target` that `value` holds, in field order, without descending into
    /// values whose type `stop` names (a value of the target type itself is not descended into).
    let rec collect (target: Type) (stop: Type -> bool) (t: Type) (value: obj): obj list =
        if t = target then [ value ]
        elif stop t then []
        elif isList t then
            let itemType = t.GetGenericArguments().[0]
            (value :?> IEnumerable) |> Seq.cast<obj> |> Seq.collect (collect target stop itemType) |> List.ofSeq
        elif isOption t then
            if isNull value then []
            else collect target stop (t.GetGenericArguments().[0]) (FSharpValue.GetUnionFields(value, t) |> snd |> Array.exactlyOne)
        elif FSharpType.IsTuple t then
            Seq.zip (FSharpType.GetTupleElements t) (FSharpValue.GetTupleFields value)
            |> Seq.collect (fun (elementType, element) -> collect target stop elementType element)
            |> List.ofSeq
        elif FSharpType.IsRecord(t, true) then
            FSharpType.GetRecordFields(t, true)
            |> Seq.collect (fun field -> collect target stop field.PropertyType (field.GetValue value))
            |> List.ofSeq
        elif FSharpType.IsUnion(t, true) && not (isStableId t) then
            let case, fields = FSharpValue.GetUnionFields(value, t, true)
            Seq.zip (case.GetFields()) fields
            |> Seq.collect (fun (field, fieldValue) -> collect target stop field.PropertyType fieldValue)
            |> List.ofSeq
        else
            []

    /// A representative value of every case of a union, with fresh stable IDs per case.
    let everyCase<'Union> () : (string * 'Union) list =
        FSharpType.GetUnionCases(typeof<'Union>, true)
        |> Array.map (fun case ->
            let counter = ref 0
            let nextId () =
                counter.Value <- counter.Value + 1
                $"{case.Name}-{counter.Value}"
            case.Name, makeUnion case (case.GetFields() |> Array.map (fun field -> synthesize nextId field.PropertyType)) :?> 'Union)
        |> List.ofArray

    let caseName (value: obj) =
        let case, _ = FSharpValue.GetUnionFields(value, value.GetType(), true)
        case.Name

    /// Runs one check per case and reports every case that fails it.
    let forEveryCase (cases: (string * 'Union) list) (check: 'Union -> unit) =
        let failures =
            cases
            |> List.choose (fun (name, value) ->
                try
                    check value
                    None
                with error -> Some $"{name}: {error.Message}")
        Assert.True(not (List.isEmpty cases))
        Assert.True(List.isEmpty failures, String.Join("\n", failures))

/// Guards the one definition of node references and nested graphs (`SemanticNodeShape`) against
/// the shape of `SemanticNodeKind` itself. A representative node of every union case is built by
/// reflection, so a case added to the union cannot be left out, and the payload each case really
/// holds (node IDs, nested graphs, pattern bindings outside those graphs) must match what the
/// shared functions report or rewrite.
module SemanticNodeShapeTests =
    open NodeShapeFixtures

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

            let folded = SemanticNodeShape.nestedScopes node |> List.fold (fun graphs scope -> scope.Graph :: graphs) []
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

    let private semanticKinds = everyCase<SemanticNodeKind> ()

    let private nodeOfKind (kind: SemanticNodeKind): SemanticNode =
        { Id = StableId.createForTesting<GraphNodeTag> "node"; Kind = kind }

    /// Payload values of one type outside nested graphs and pattern bindings (the shape of
    /// `nestedScopes` covers both).
    let private payloadValues<'T> (kind: SemanticNodeKind): 'T list =
        let stop (t: Type) = t = typeof<ValueGraph> || t = typeof<PatternBinding>
        collect typeof<'T> stop typeof<SemanticNodeKind> (box kind) |> List.map unbox<'T>

    /// The role every function-naming kind gives its function; every other kind names none.
    let private expectedRoles =
        Map.ofList
            [ "DirectCall", "DirectCall"
              "ListFold", "PureListCallback"
              "ListMap", "PureListCallback"
              "MapFold", "PureListCallback"
              "MapFromList", "PureListCallback"
              "ListFilter", "PureListCallback"
              "MapFilter", "PureListCallback"
              "ListSortBy", "PureListCallback"
              "ListMinBy", "PureListCallback"
              "MapMapValues", "PureListCallback"
              "ListAny", "PureListCallback"
              "ListFindIndex", "PureListCallback"
              "ListAll", "PureListCallback"
              "ListEffectFold", "EffectListCallback"
              "ParallelAll", "Worker"
              "TaskStart", "Worker"
              "UseResource", "Body"
              "TaskScope", "Body"
              "ExceptionCatch", "Body"
              "CancellationShield", "Body"
              "FunctionReference", "FunctionValue"
              "CapabilityConstruct", "CapabilityImplementation" ]

    [<Fact>]
    let ``payload type refs are exactly the types a kind states outside pattern bindings and nested graphs`` () =
        forEveryCase semanticKinds (fun kind ->
            let node = nodeOfKind kind
            Assert.Equal<TypeRef list>(payloadValues<TypeRef> kind, SemanticNodeShape.payloadTypeRefs node))

    [<Fact>]
    let ``mapPayloadTypeRefs rewrites every payload type and nothing else`` () =
        forEveryCase semanticKinds (fun kind ->
            let node = nodeOfKind kind
            Assert.Equal(node, SemanticNodeShape.mapPayloadTypeRefs id node)
            let mark (typeRef: TypeRef) = TypeRef.Function([ typeRef ], typeRef)
            let marked = SemanticNodeShape.mapPayloadTypeRefs mark node
            Assert.Equal<TypeRef list>(payloadValues<TypeRef> kind |> List.map mark, payloadValues<TypeRef> marked.Kind)
            Assert.Equal(payloadOf node, payloadOf marked)
            Assert.Equal<FunctionId list>(payloadValues<FunctionId> kind, payloadValues<FunctionId> marked.Kind))

    [<Fact>]
    let ``function references are exactly the functions a kind names, with their bound arguments`` () =
        forEveryCase semanticKinds (fun kind ->
            let references = SemanticNodeShape.functionReferences (nodeOfKind kind)
            Assert.Equal<FunctionId list>(payloadValues<FunctionId> kind, references |> List.map (fun reference -> reference.FunctionId))
            Assert.Equal<ArgumentBinding list>(payloadValues<ArgumentBinding> kind, references |> List.collect (fun reference -> reference.Arguments))
            let expected = Map.tryFind (caseName kind) expectedRoles |> Option.toList
            Assert.Equal<string list>(expected, references |> List.map (fun reference -> caseName reference.Role) |> List.distinct))

    [<Fact>]
    let ``invoked and implemented operations are exactly the capability operations a kind names`` () =
        forEveryCase semanticKinds (fun kind ->
            let node = nodeOfKind kind
            let implemented =
                SemanticNodeShape.functionReferences node
                |> List.choose (fun reference ->
                    match reference.Role with
                    | FunctionReferenceRole.CapabilityImplementation operationId -> Some(operationId, reference.FunctionId)
                    | _ -> None)
            match kind with
            | SemanticNodeKind.CapabilityConstruct(_, implementations) ->
                Assert.Equal<(CapabilityOperationId * FunctionId) list>(implementations, implemented)
            | _ -> Assert.Empty implemented
            Assert.Equal<CapabilityOperationId list>(
                payloadValues<CapabilityOperationId> kind,
                SemanticNodeShape.invokedOperations node @ (implemented |> List.map fst)
            ))

    [<Fact>]
    let ``intrinsically effectful kinds are exactly the capability, resource, concurrency and task kinds`` () =
        let expected =
            set [ "CapabilityInvoke"; "UseResource"; "ParallelAll"; "CancellationRequested"
                  "ExceptionCatch"; "ExceptionRethrow"; "CancellationShield"; "TaskScope"; "TaskStart"; "TaskWait"; "TaskCancel"; "OneShotCreate"; "OneShotComplete"; "OneShotWait" ]
        forEveryCase semanticKinds (fun kind ->
            Assert.Equal(Set.contains (caseName kind) expected, SemanticNodeShape.isIntrinsicallyEffectful (nodeOfKind kind)))

    [<Fact>]
    let ``tryDirectCall finds exactly DirectCall nodes`` () =
        forEveryCase semanticKinds (fun kind ->
            match kind, SemanticNodeShape.tryDirectCall (nodeOfKind kind) with
            | SemanticNodeKind.DirectCall(functionId, arguments), found -> Assert.Equal(Some(functionId, arguments), found)
            | _, found -> Assert.Equal(None, found))

/// Guards the exhaustive classifications next to the Execution model (`TypedSemanticNodeKind.subgraphs`
/// and `ExecutionInstructionKind.uses`) against the shape of the unions, as above.
module ExecutionShapeTests =
    open NodeShapeFixtures

    [<Fact>]
    let ``typed subgraphs are exactly the graphs a kind owns, in evaluation order`` () =
        forEveryCase (everyCase<TypedSemanticNodeKind> ()) (fun kind ->
            let owned =
                collect typeof<TypedSemanticGraph> (fun _ -> false) typeof<TypedSemanticNodeKind> (box kind)
                |> List.map unbox<TypedSemanticGraph>
            Assert.Equal<TypedSemanticGraph list>(owned, TypedSemanticNodeKind.subgraphs kind))

    [<Fact>]
    let ``typed value references are exactly the node IDs a kind holds outside its subgraphs`` () =
        forEveryCase (everyCase<TypedSemanticNodeKind> ()) (fun kind ->
            let held =
                collect typeof<GraphNodeId> (fun t -> t = typeof<TypedSemanticGraph>) typeof<TypedSemanticNodeKind> (box kind)
                |> List.map unbox<GraphNodeId>
            Assert.Equal<GraphNodeId list>(
                held |> List.sortBy StableId.value,
                TypedSemanticNodeKind.valueReferences kind |> List.sortBy StableId.value
            ))

    let private instructionKinds = everyCase<ExecutionInstructionKind> ()

    let private instructionValues<'T> (kind: ExecutionInstructionKind): 'T list =
        collect typeof<'T> (fun _ -> false) typeof<ExecutionInstructionKind> (box kind) |> List.map unbox<'T>

    [<Fact>]
    let ``callback targets are exactly the functions an instruction names, with the semantic roles`` () =
        let semanticRoles =
            everyCase<SemanticNodeKind> ()
            |> List.collect (fun (name, kind) ->
                SemanticNodeShape.functionReferences { Id = StableId.createForTesting<GraphNodeTag> "node"; Kind = kind }
                |> List.map (fun reference -> name, caseName reference.Role))
            |> List.distinct
            |> Map.ofList
        forEveryCase instructionKinds (fun kind ->
            let targets = ExecutionInstructionKind.callbackTargets kind
            Assert.Equal<FunctionId list>(instructionValues<FunctionId> kind, targets |> List.map snd)
            let expected = Map.tryFind (caseName kind) semanticRoles |> Option.toList
            Assert.Equal<string list>(expected, targets |> List.map (fst >> caseName) |> List.distinct))

    [<Fact>]
    let ``capability operations are exactly the operations an instruction invokes`` () =
        forEveryCase instructionKinds (fun kind ->
            let implemented =
                match kind with
                | ExecutionInstructionKind.CapabilityConstruct(_, implementations) -> implementations |> List.map fst
                | _ -> []
            Assert.Equal<CapabilityOperationId list>(
                instructionValues<CapabilityOperationId> kind,
                ExecutionInstructionKind.capabilityOperations kind @ implemented
            ))

    [<Fact>]
    let ``runtime uses and implementation classes are claimed by the expected instructions`` () =
        let expected =
            Map.ofList
                [ "ParallelAll", [ "ConcurrencyRuntime" ]
                  "CancellationRequested", [ "ConcurrencyRuntime" ]
                  "CancellationShield", [ "ConcurrencyRuntime" ]
                  "TaskScope", [ "TaskRuntime" ]
                  "TaskStart", [ "TaskRuntime" ]
                  "TaskWait", [ "TaskRuntime" ]
                  "OneShotCreate", [ "TaskRuntime" ]
                  "OneShotComplete", [ "TaskRuntime" ]
                  "OneShotWait", [ "TaskRuntime" ]
                  "TaskCancel", [ "TaskRuntime" ]
                  "CapabilityConstruct", [ "CapabilityImplementationClass" ] ]
        forEveryCase instructionKinds (fun kind ->
            let claimed =
                ExecutionInstructionKind.uses kind
                |> List.filter (fun used ->
                    match used with
                    | ExecutionInstructionUse.Callback _
                    | ExecutionInstructionUse.CapabilityOperation _ -> false
                    | ExecutionInstructionUse.CapabilityImplementationClass _
                    | ExecutionInstructionUse.ConcurrencyRuntime
                    | ExecutionInstructionUse.TaskRuntime -> true)
            Assert.Equal<string list>(Map.tryFind (caseName kind) expected |> Option.defaultValue [], claimed |> List.map caseName)
            match kind, claimed with
            | ExecutionInstructionKind.CapabilityConstruct(capabilityId, implementations), [ used ] ->
                Assert.Equal(ExecutionInstructionUse.CapabilityImplementationClass(capabilityId, implementations), used)
            | _ -> ())
