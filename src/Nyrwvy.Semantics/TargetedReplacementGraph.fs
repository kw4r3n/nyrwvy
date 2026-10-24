namespace Nyrwvy.Core

/// Graph traversal, target lookup and pruning helpers for targeted recipe replacement.
module internal TargetedReplacementGraph =
    open StructuralTransactionInternals
    open ExpressionRecipeSupport
    open ExpressionRecipeMaterialization

    /// The scope frame a nested graph opens for recipe pattern references, if any: branches and
    /// Bool operands open none, arms open one frame per binding slot.
    let private frameOfScope (scope: NestedScope): PatternBindingFrame option =
        let slots =
            scope.Bindings
            |> List.map (fun binding ->
                match binding with
                | NestedBinding.Field(fieldId, pattern) -> RecipePatternSlot.Field fieldId, pattern
                | NestedBinding.Value pattern -> RecipePatternSlot.Value, pattern
                | NestedBinding.Error pattern -> RecipePatternSlot.Error, pattern)

        let frame kind: PatternBindingFrame option = Some { Kind = kind; Slots = slots }

        match scope.Role with
        | NestedGraphRole.WhenTrue
        | NestedGraphRole.WhenFalse
        | NestedGraphRole.BoolAndRight
        | NestedGraphRole.BoolOrRight -> None
        | NestedGraphRole.UnionArm variantIds -> frame (ScopeFrameKind.UnionArm variantIds)
        | NestedGraphRole.BindBody -> frame ScopeFrameKind.BindBody
        | NestedGraphRole.OptionSome -> frame ScopeFrameKind.OptionSome
        | NestedGraphRole.OptionNone -> frame ScopeFrameKind.OptionNone
        | NestedGraphRole.ResultOk -> frame ScopeFrameKind.ResultOk
        | NestedGraphRole.ResultErr -> frame ScopeFrameKind.ResultErr

    let rec internal tryFindTarget
        (frames: PatternBindingFrame list)
        targetNodeId
        (graph: ValueGraph)
        =
        match graph.Nodes |> List.tryFind (fun node -> node.Id = targetNodeId) with
        | Some node -> Some(node, frames)
        | None ->
            graph.Nodes
            |> List.tryPick (fun node ->
                SemanticNodeShape.nestedScopes node
                |> List.tryPick (fun scope ->
                    let innerFrames =
                        match frameOfScope scope with
                        | Some frame -> frame :: frames
                        | None -> frames
                    tryFindTarget innerFrames targetNodeId scope.Graph))

    let rec internal graphResultUses owner (graph: ValueGraph) =
        (graph.ResultNodeId, owner)
        :: (graph.Nodes
            |> List.collect (fun node ->
                SemanticNodeShape.nestedGraphs node
                |> List.collect (graphResultUses (Some node.Id))))

    /// Nodes that use the target node as a value, as full validation sees value uses. An
    /// abrupt expression cannot stand where any of them reads it.
    let internal valueConsumers targetNodeId (target: SemanticFunction) =
        ValueGraph.collectNodes target.Body
        |> List.filter (fun node ->
            SemanticNodeShape.directValueReferences node |> List.contains targetNodeId)

    /// Whether the target node is the result of the function body or of a nested graph.
    let internal isGraphResult targetNodeId (target: SemanticFunction) =
        graphResultUses None target.Body |> List.exists (fun (resultNodeId, _) -> resultNodeId = targetNodeId)

    let rec internal replaceTarget targetNodeId replacementNodes (graph: ValueGraph) =
        if graph.Nodes |> List.exists (fun node -> node.Id = targetNodeId) then
            { graph with
                Nodes =
                    graph.Nodes
                    |> List.collect (fun node ->
                        if node.Id = targetNodeId then replacementNodes else [ node ]) }
        else
            { graph with
                Nodes =
                    graph.Nodes
                    |> List.map (SemanticNodeShape.mapNestedGraphs (replaceTarget targetNodeId replacementNodes)) }

    let rec internal pruneNodes removedNodeIds (graph: ValueGraph) =
        { graph with
            Nodes =
                graph.Nodes
                |> List.choose (fun node ->
                    if Set.contains node.Id removedNodeIds then
                        None
                    else
                        Some(SemanticNodeShape.mapNestedGraphs (pruneNodes removedNodeIds) node)) }

    /// The graph whose own node list holds the target node, at any nesting depth.
    let rec internal tryFindOwningGraph targetNodeId (graph: ValueGraph) =
        if graph.Nodes |> List.exists (fun node -> node.Id = targetNodeId) then
            Some graph
        else
            graph.Nodes
            |> List.tryPick (fun node -> SemanticNodeShape.nestedGraphs node |> List.tryPick (tryFindOwningGraph targetNodeId))

    let internal materializeReplacement
        index
        target
        (original: OriginalTargetContext option)
        frames
        targetNodeId
        recipe
        =
        let owner = { ownerForExistingFunction target with OriginalTarget = original }
        let nodes, generatedRootId = materializeWithContext index owner (InferenceCache()) frames recipe
        nodes
        |> List.map (fun node ->
            if node.Id = generatedRootId then
                { node with Id = targetNodeId }
            else
                node)
