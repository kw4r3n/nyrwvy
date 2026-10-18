namespace Nyrwvy.Core

/// One arm recipe of an `addUnionVariant` request: the body the match node gains for the new
/// variant. The recipe is evaluated in that arm's scope: the new variant's fields are the pattern
/// slots of the arm's own frame (depth 0), and the match's enclosing frames follow at depths 1
/// and beyond, as a targeted replacement inside the arm would see them.
type AgentUnionVariantArmRecipe =
    { MatchNodeId: GraphNodeId
      Body: ExpressionRecipe }

/// Prepares the `addUnionVariant` operation of `simulateOperations`: allocates the system-owned
/// variant and field IDs and materializes one arm per exhaustive `matchUnion` over the Union, so
/// the operation itself (`Transaction.addUnionVariant`) carries only materialized graphs and can be
/// re-simulated identically at commit.
[<RequireQualifiedAccess>]
module AgentUnionVariantArmPlans =
    open StructuralTransactionInternals
    open ExpressionRecipeSupport
    open ExpressionRecipeInference
    open ExpressionRecipeMaterialization
    open TargetedReplacementGraph

    /// The new variant with fresh system-owned variant and field IDs. Field types may name the
    /// Union's own type parameters.
    let allocateVariant (displayName: string) (fields: AgentTypeFieldIntent list): SemanticVariant =
        { Id = StableIdAllocator.newVariantId ()
          DisplayName = displayName
          Fields =
            fields
            |> List.map (fun field ->
                ({ Id = StableIdAllocator.newFieldId ()
                   DisplayName = field.DisplayName
                   Type = field.Type }: SemanticField)) }

    /// Binds and type-checks every arm recipe against the current snapshot extended with the new
    /// variant (so a body may already construct it), each in its match's lexical scope, and
    /// materializes it with fresh node and pattern-binding IDs. The exhaustive-plan rule itself
    /// (one plan per match, no stray plan) is applied when the operation is simulated.
    let prepare
        (workspace: AgentWorkspace)
        (targetTypeId: TypeId)
        (variant: SemanticVariant)
        (arms: AgentUnionVariantArmRecipe list)
        : Result<AgentAddUnionVariantIntent, TransactionDiagnostic> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        match baseSnapshot.Types |> List.tryFind (fun item -> item.Id = targetTypeId) with
        | None -> Error(diagnostic "TargetNotFound" "The target type was not found.")
        | Some { Kind = SemanticTypeKind.Product _ } ->
            Error(diagnostic "TargetNotUnion" "The target type of addUnionVariant must be a Union.")
        | Some({ Kind = SemanticTypeKind.Union variants } as owner) ->
            let unionVariantIds = variants |> List.map (fun item -> item.Id) |> Set.ofList
            let extended =
                { baseSnapshot with
                    Types =
                        baseSnapshot.Types
                        |> List.map (fun item ->
                            if item.Id = owner.Id then { item with Kind = SemanticTypeKind.Union(variants @ [ variant ]) } else item) }
            let index = buildSnapshotIndex extended
            let describe (fn: SemanticFunction) = StableId.describe fn.DisplayName fn.Id

            let prepareArm (arm: AgentUnionVariantArmRecipe): Result<UnionVariantArmPlan, TransactionDiagnostic> =
                let located =
                    baseSnapshot.Functions
                    |> List.tryPick (fun fn -> tryFindTarget [] arm.MatchNodeId fn.Body |> Option.map (fun (node, frames) -> fn, node, frames))
                match located with
                | None ->
                    Error(diagnostic "UnionMatchArmPlanTargetNotFound" $"Match node '{StableId.value arm.MatchNodeId}' does not exist in any function.")
                | Some(fn, node, frames) ->
                    let site = $"match '{StableId.value arm.MatchNodeId}' in function {describe fn}"
                    match node.Kind with
                    | SemanticNodeKind.MatchUnion(inputNodeId, existingArms) when
                        existingArms |> List.exists (fun existing -> existing.VariantIds |> List.exists (fun id -> Set.contains id unionVariantIds)) ->
                        let completion = SemanticFacts.createNodeCompletionResolver baseSnapshot fn
                        // A generic Union's arm binds the fields at the match input's type arguments.
                        let arguments =
                            match completion inputNodeId with
                            | Some(GraphCompletion.Normal(TypeRef.Named(_, arguments))) -> arguments
                            | _ -> []
                        let bindings: PatternFieldBinding list =
                            variant.Fields
                            |> List.map (fun field ->
                                { SourceFieldId = field.Id
                                  Binding =
                                    { Id = StableIdAllocator.newPatternBindingId ()
                                      DisplayName = Some field.DisplayName
                                      Type = TypeParameters.instantiate owner arguments field.Type } })
                        let frame: PatternBindingFrame =
                            { Kind = ScopeFrameKind.UnionArm [ variant.Id ]
                              Slots = bindings |> List.map (fun item -> RecipePatternSlot.Field item.SourceFieldId, item.Binding) }
                        let armFrames = frame :: frames
                        match validateRecipeBounds recipeChildren arm.Body with
                        | Error error -> Error error
                        | Ok() ->
                            match completion arm.MatchNodeId with
                            | None ->
                                Error(diagnostic "UnionMatchArmTypeUnavailable" $"The type of {site} could not be derived from the validated base snapshot.")
                            | Some matchType ->
                                match inferTypeWithFrames (Some "body") index fn (bindingTypeFrames armFrames) arm.Body with
                                | Error error -> Error error
                                | Ok(GraphCompletion.Normal bodyType) when matchType <> GraphCompletion.Aborts && GraphCompletion.Normal bodyType <> matchType ->
                                    Error(
                                        diagnostic
                                            "UnionMatchArmTypeMismatch"
                                            $"The arm body for {site} produces {DiagnosticText.typeRef extended bodyType}, but the match produces {DiagnosticText.completion extended matchType}."
                                    )
                                | Ok _ ->
                                    let nodes, resultNodeId = materialize index fn armFrames arm.Body
                                    Ok
                                        { MatchNodeId = arm.MatchNodeId
                                          FieldBindings = bindings
                                          Body = { Nodes = nodes; ResultNodeId = resultNodeId } }
                    | SemanticNodeKind.MatchUnion _ ->
                        Error(diagnostic "UnionMatchArmPlanTargetMismatch" $"The {site} does not match over Union '{owner.DisplayName}'.")
                    | _ ->
                        Error(diagnostic "UnionMatchArmPlanTargetMismatch" $"Node '{StableId.value arm.MatchNodeId}' in function {describe fn} is not a matchUnion.")

            let duplicate =
                arms |> List.map (fun arm -> arm.MatchNodeId) |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1)
            match duplicate with
            | Some(matchNodeId, _) ->
                Error(diagnostic "UnionMatchArmPlanMismatch" $"Match node '{StableId.value matchNodeId}' has more than one arm plan.")
            | None ->
                let rec loop acc remaining =
                    match remaining with
                    | [] -> Ok(List.rev acc)
                    | arm :: rest ->
                        match prepareArm arm with
                        | Error error -> Error error
                        | Ok plan -> loop (plan :: acc) rest
                loop [] arms
                |> Result.map (fun plans ->
                    { TargetTypeId = targetTypeId
                      Variant = variant
                      ArmPlans = plans })
