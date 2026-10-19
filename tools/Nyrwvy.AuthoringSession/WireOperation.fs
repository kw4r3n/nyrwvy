namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

module internal WireOperation =
    open WireInput

    let private callSitePlans (element: JsonElement) =
        array "callSites" element
        |> List.map (fun plan ->
            match requiredString "kind" plan with
            | "bind" ->
                ensureFields "bind call-site plan" [ "kind"; "callNodeId"; "valueNodeId" ] [ "kind"; "callNodeId"; "valueNodeId" ] plan
                AgentCallPlan.BindExistingValue(
                    id<GraphNodeTag> "callNodeId" plan,
                    id<GraphNodeTag> "valueNodeId" plan
                )
            | "leaveMissing" ->
                ensureFields "leave-missing call-site plan" [ "kind"; "callNodeId" ] [ "kind"; "callNodeId" ] plan
                AgentCallPlan.LeaveMissing(id<GraphNodeTag> "callNodeId" plan)
            | value -> fail $"Unsupported call-site plan '{value}'.")

    let private constructionPlans (element: JsonElement): ProductFieldConstructionPlan list =
        array "constructions" element
        |> List.map (fun plan ->
            ensureFields "product construction plan" [ "constructNodeId"; "valueNodeId" ] [ "constructNodeId"; "valueNodeId" ] plan
            { ConstructNodeId = id<GraphNodeTag> "constructNodeId" plan
              ValueNodeId = id<GraphNodeTag> "valueNodeId" plan })

    /// The implementation plans of `addCapabilityOperation`: one per `capabilityConstruct` node of
    /// the Capability, naming the implementing function by `functionId` or (`...ByName`) `function`.
    let private implementationPlans (resolveFunction: JsonElement -> FunctionId) functionKey (element: JsonElement): AgentCapabilityImplementationPlan list =
        array "implementations" element
        |> List.map (fun plan ->
            ensureFields "capability implementation plan" [ "constructNodeId"; functionKey ] [ "constructNodeId"; functionKey ] plan
            { ConstructNodeId = id<GraphNodeTag> "constructNodeId" plan
              ImplementationFunctionId = resolveFunction plan })

    let private failureType (_: JsonElement -> TypeRef) (item: JsonElement) = retiredFailureType item

    let private resolveMissingArgument (element: JsonElement) =
        ensureFields "resolve-missing-argument operation" [ "kind"; "obligationId"; "valueNodeId" ] [ "kind"; "obligationId"; "valueNodeId" ] element
        AgentOperationIntent.ResolveMissingArgument
            { TargetObligationId = id<ObligationTag> "obligationId" element
              ResolutionValueNodeId = id<GraphNodeTag> "valueNodeId" element }

    /// `setExported` names exactly one declaration by `typeId`, `functionId`, `effectId` or
    /// `capabilityId` (exact form) or `type`, `function`, `effect` or `capability` (by name).
    let private setExported (keys: (string * (unit -> AgentVisibilityTarget)) list) (element: JsonElement) =
        let names = keys |> List.map fst
        ensureFields "set-exported operation" ("kind" :: "exported" :: names) [ "kind"; "exported" ] element
        match keys |> List.filter (fun (key, _) -> (tryProperty key element).IsSome) with
        | [ _, target ] -> AgentOperationIntent.SetExported(target (), requiredBool "exported" element)
        | _ -> fail ("A set-exported operation names exactly one declaration with one of " + String.concat ", " names + ".")

    let operation (element: JsonElement): AgentOperationIntent =
        match requiredString "kind" element with
        | "setExported" ->
            setExported
                [ "typeId", (fun () -> AgentVisibilityTarget.Type(id<TypeTag> "typeId" element))
                  "functionId", (fun () -> AgentVisibilityTarget.Function(id<FunctionTag> "functionId" element))
                  "effectId", (fun () -> AgentVisibilityTarget.Effect(id<EffectTag> "effectId" element))
                  "capabilityId", (fun () -> AgentVisibilityTarget.Capability(id<CapabilityTag> "capabilityId" element)) ]
                element
        | "renameFunction" ->
            ensureFields "rename-function operation" [ "kind"; "functionId"; "displayName" ] [ "kind"; "functionId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Function(id<FunctionTag> "functionId" element),
                requiredString "displayName" element
            )
        | "renameType" ->
            ensureFields "rename-type operation" [ "kind"; "typeId"; "displayName" ] [ "kind"; "typeId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Type(id<TypeTag> "typeId" element),
                requiredString "displayName" element
            )
        | "renameField" ->
            ensureFields "rename-field operation" [ "kind"; "fieldId"; "displayName" ] [ "kind"; "fieldId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Field(id<FieldTag> "fieldId" element),
                requiredString "displayName" element
            )
        | "renameVariant" ->
            ensureFields "rename-variant operation" [ "kind"; "variantId"; "displayName" ] [ "kind"; "variantId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Variant(id<VariantTag> "variantId" element),
                requiredString "displayName" element
            )
        | "renameParameter" ->
            ensureFields "rename-parameter operation" [ "kind"; "parameterId"; "displayName" ] [ "kind"; "parameterId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Parameter(id<ParameterTag> "parameterId" element),
                requiredString "displayName" element
            )
        | "renameEffect" ->
            ensureFields "rename-effect operation" [ "kind"; "effectId"; "displayName" ] [ "kind"; "effectId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Effect(id<EffectTag> "effectId" element),
                requiredString "displayName" element
            )
        | "renameCapability" ->
            ensureFields "rename-capability operation" [ "kind"; "capabilityId"; "displayName" ] [ "kind"; "capabilityId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Capability(id<CapabilityTag> "capabilityId" element),
                requiredString "displayName" element
            )
        | "renameCapabilityOperation" ->
            ensureFields "rename-capability-operation operation" [ "kind"; "operationId"; "displayName" ] [ "kind"; "operationId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.CapabilityOperation(id<CapabilityOperationTag> "operationId" element),
                requiredString "displayName" element
            )
        | "renameCapabilityParameter" ->
            ensureFields "rename-capability-parameter operation" [ "kind"; "parameterId"; "displayName" ] [ "kind"; "parameterId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.CapabilityParameter(id<CapabilityParameterTag> "parameterId" element),
                requiredString "displayName" element
            )
        | "addFunctionParameter" ->
            ensureFields
                "add-function-parameter operation"
                [ "kind"; "functionId"; "displayName"; "type"; "callSites" ]
                [ "kind"; "functionId"; "displayName"; "type"; "callSites" ]
                element
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  NewParameterDisplayName = requiredString "displayName" element
                  NewParameterType = typeRef (property "type" element)
                  CallSitePlans = callSitePlans element }
        | "addProductField" ->
            ensureFields
                "add-product-field operation"
                [ "kind"; "typeId"; "displayName"; "type"; "constructions" ]
                [ "kind"; "typeId"; "displayName"; "type"; "constructions" ]
                element
            AgentOperationIntent.AddProductField
                { TargetTypeId = id<TypeTag> "typeId" element
                  NewFieldDisplayName = requiredString "displayName" element
                  NewFieldType = typeRef (property "type" element)
                  ConstructionPlans = constructionPlans element }
        | "addCapabilityOperation" ->
            ensureFields
                "add-capability-operation operation"
                [ "kind"; "capabilityId"; "displayName"; "parameters"; "resultType"; "failureType"; "effectId"; "implementations" ]
                [ "kind"; "capabilityId"; "displayName"; "parameters"; "resultType"; "effectId"; "implementations" ]
                element
            AgentOperationIntent.AddCapabilityOperation
                { TargetCapabilityId = id<CapabilityTag> "capabilityId" element
                  Operation =
                    { DisplayName = requiredString "displayName" element
                      Parameters =
                        array "parameters" element
                        |> List.map (fun parameter ->
                            ensureFields "capability parameter" [ "displayName"; "type" ] [ "displayName"; "type" ] parameter
                            { DisplayName = requiredString "displayName" parameter; Type = typeRef (property "type" parameter) })
                      ResultType = typeRef (property "resultType" element)
                      FailureType = failureType typeRef element
                      EffectId = id<EffectTag> "effectId" element }
                  ImplementationPlans = implementationPlans (id<FunctionTag> "functionId") "functionId" element }
        | "removeProductField" ->
            ensureFields "remove-product-field operation" [ "kind"; "fieldId" ] [ "kind"; "fieldId" ] element
            AgentOperationIntent.RemoveProductField(id<FieldTag> "fieldId" element)
        | "removeFunctionParameter" ->
            ensureFields "remove-function-parameter operation" [ "kind"; "parameterId" ] [ "kind"; "parameterId" ] element
            AgentOperationIntent.RemoveFunctionParameter(id<ParameterTag> "parameterId" element)
        | "removeUnionVariant" ->
            ensureFields "remove-union-variant operation" [ "kind"; "variantId" ] [ "kind"; "variantId" ] element
            AgentOperationIntent.RemoveUnionVariant(id<VariantTag> "variantId" element)
        | "deleteFunction" ->
            ensureFields "delete-function operation" [ "kind"; "functionId" ] [ "kind"; "functionId" ] element
            AgentOperationIntent.DeleteFunction(id<FunctionTag> "functionId" element)
        | "deleteType" ->
            ensureFields "delete-type operation" [ "kind"; "typeId" ] [ "kind"; "typeId" ] element
            AgentOperationIntent.DeleteType(id<TypeTag> "typeId" element)
        | "deleteEffect" ->
            ensureFields "delete-effect operation" [ "kind"; "effectId" ] [ "kind"; "effectId" ] element
            AgentOperationIntent.DeleteEffect(id<EffectTag> "effectId" element)
        | "deleteCapability" ->
            ensureFields "delete-capability operation" [ "kind"; "capabilityId" ] [ "kind"; "capabilityId" ] element
            AgentOperationIntent.DeleteCapability(id<CapabilityTag> "capabilityId" element)
        | "deleteCapabilityOperation" ->
            ensureFields "delete-capability-operation operation" [ "kind"; "operationId" ] [ "kind"; "operationId" ] element
            AgentOperationIntent.DeleteCapabilityOperation(id<CapabilityOperationTag> "operationId" element)
        | "resolveMissingArgument" -> resolveMissingArgument element
        | "addRequiredEffect"
        | "removeRequiredEffect" as kind ->
            ensureFields $"{kind} operation" [ "kind"; "functionId"; "effectId" ] [ "kind"; "functionId"; "effectId" ] element
            let intent: AgentFunctionRequiredEffectIntent =
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  EffectId = id<EffectTag> "effectId" element }
            if kind = "addRequiredEffect" then AgentOperationIntent.AddFunctionRequiredEffect intent
            else AgentOperationIntent.RemoveFunctionRequiredEffect intent
        | value -> fail $"Unsupported operation kind '{value}'."

    /// Type parameters an existing function already declares keep their identity when a
    /// name-resolved type names them; other names introduce new parameters, as the exact form does.
    let private bindDeclaredTypeParameters (fn: SemanticFunction) (typeRef: TypeRef) =
        let declared = TypeParameters.ofFunction fn
        let substitution =
            TypeParameters.occurring typeRef
            |> List.choose (fun (id, name) ->
                declared
                |> List.tryFind (fun (_, declaredName) -> declaredName = name)
                |> Option.map (fun (declaredId, declaredName) -> id, TypeRef.TypeParameter(declaredId, declaredName)))
            |> Map.ofList
        TypeParameters.substitute substitution typeRef

    /// Name-resolved operations (`simulateOperationsByName`). Every name is resolved against the
    /// current snapshot before the batch is simulated, with the same unknown/ambiguous rejection as
    /// the other `…ByName` requests; the result is the ordinary exact-ID operation. Node IDs
    /// (call sites, constructions, obligations) stay exact: nodes have no names.
    let operationByName (declarations: AuthoringDeclarations) (element: JsonElement): AgentOperationIntent =
        let resolveFunction () = WireSymbolic.resolveFunction declarations (requiredString "function" element)
        let resolveType () = WireSymbolic.resolveType declarations (requiredString "type" element)
        let resolveEffect () = WireSymbolic.resolveEffect declarations (requiredString "effect" element)
        let resolveCapability () = WireSymbolic.resolveCapability declarations (requiredString "capability" element)
        let resolveOperation () = WireSymbolic.capabilityOperation (resolveCapability ()) (requiredString "operation" element)
        let resolveField () =
            let owner = resolveType ()
            match tryProperty "variant" element with
            | Some _ -> WireSymbolic.variantField (WireSymbolic.unionVariant owner (requiredString "variant" element)) (requiredString "field" element)
            | None -> WireSymbolic.productField owner (requiredString "field" element)
        let rename kind keys target =
            ensureFields $"{kind} operation" ("kind" :: "displayName" :: keys) ("kind" :: "displayName" :: keys) element
            AgentOperationIntent.RenameDisplayName(target (), requiredString "displayName" element)
        match requiredString "kind" element with
        | "setExported" ->
            setExported
                [ "type", (fun () -> AgentVisibilityTarget.Type (resolveType ()).Id)
                  "function", (fun () -> AgentVisibilityTarget.Function (resolveFunction ()).Id)
                  "effect", (fun () -> AgentVisibilityTarget.Effect (resolveEffect ()).Id)
                  "capability", (fun () -> AgentVisibilityTarget.Capability (resolveCapability ()).Id) ]
                element
        | "renameFunction" -> rename "rename-function" [ "function" ] (fun () -> AgentRenameTarget.Function (resolveFunction ()).Id)
        | "renameType" -> rename "rename-type" [ "type" ] (fun () -> AgentRenameTarget.Type (resolveType ()).Id)
        | "renameField" ->
            ensureFields "rename-field operation" [ "kind"; "type"; "variant"; "field"; "displayName" ] [ "kind"; "type"; "field"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Field (resolveField ()).Id, requiredString "displayName" element)
        | "renameVariant" ->
            rename "rename-variant" [ "type"; "variant" ] (fun () ->
                AgentRenameTarget.Variant (WireSymbolic.unionVariant (resolveType ()) (requiredString "variant" element)).Id)
        | "renameParameter" ->
            rename "rename-parameter" [ "function"; "parameter" ] (fun () ->
                AgentRenameTarget.Parameter (WireSymbolic.functionParameter (resolveFunction ()) (requiredString "parameter" element)).Id)
        | "renameEffect" -> rename "rename-effect" [ "effect" ] (fun () -> AgentRenameTarget.Effect (resolveEffect ()).Id)
        | "renameCapability" -> rename "rename-capability" [ "capability" ] (fun () -> AgentRenameTarget.Capability (resolveCapability ()).Id)
        | "renameCapabilityOperation" ->
            rename "rename-capability-operation" [ "capability"; "operation" ] (fun () -> AgentRenameTarget.CapabilityOperation (resolveOperation ()).Id)
        | "renameCapabilityParameter" ->
            rename "rename-capability-parameter" [ "capability"; "operation"; "parameter" ] (fun () ->
                AgentRenameTarget.CapabilityParameter (WireSymbolic.capabilityParameter (resolveOperation ()) (requiredString "parameter" element)).Id)
        | "addFunctionParameter" ->
            ensureFields "add-function-parameter operation" [ "kind"; "function"; "displayName"; "type"; "callSites" ] [ "kind"; "function"; "displayName"; "type"; "callSites" ] element
            let owner = resolveFunction ()
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = owner.Id
                  NewParameterDisplayName = requiredString "displayName" element
                  NewParameterType = WireSymbolic.typeRef declarations (property "type" element) |> bindDeclaredTypeParameters owner
                  CallSitePlans = callSitePlans element }
        | "addProductField" ->
            // `type` selects the owning Product, so the new field's type is `fieldType`.
            ensureFields "add-product-field operation" [ "kind"; "type"; "displayName"; "fieldType"; "constructions" ] [ "kind"; "type"; "displayName"; "fieldType"; "constructions" ] element
            AgentOperationIntent.AddProductField
                { TargetTypeId = (resolveType ()).Id
                  NewFieldDisplayName = requiredString "displayName" element
                  NewFieldType = WireSymbolic.typeRef declarations (property "fieldType" element)
                  ConstructionPlans = constructionPlans element }
        | "addCapabilityOperation" ->
            ensureFields
                "add-capability-operation operation"
                [ "kind"; "capability"; "displayName"; "parameters"; "resultType"; "failureType"; "effect"; "implementations" ]
                [ "kind"; "capability"; "displayName"; "parameters"; "resultType"; "effect"; "implementations" ]
                element
            let typeRef = WireSymbolic.typeRef declarations
            AgentOperationIntent.AddCapabilityOperation
                { TargetCapabilityId = (resolveCapability ()).Id
                  Operation =
                    { DisplayName = requiredString "displayName" element
                      Parameters =
                        array "parameters" element
                        |> List.map (fun parameter ->
                            ensureFields "capability parameter" [ "displayName"; "type" ] [ "displayName"; "type" ] parameter
                            { DisplayName = requiredString "displayName" parameter; Type = typeRef (property "type" parameter) })
                      ResultType = typeRef (property "resultType" element)
                      FailureType = failureType typeRef element
                      EffectId = (resolveEffect ()).Id }
                  ImplementationPlans =
                    implementationPlans (fun plan -> (WireSymbolic.resolveFunctionAt declarations "function" plan).Id) "function" element }
        | "removeProductField" ->
            ensureFields "remove-product-field operation" [ "kind"; "type"; "field" ] [ "kind"; "type"; "field" ] element
            AgentOperationIntent.RemoveProductField (WireSymbolic.productField (resolveType ()) (requiredString "field" element)).Id
        | "resolveMissingArgument" -> resolveMissingArgument element
        | "addRequiredEffect"
        | "removeRequiredEffect" as kind ->
            ensureFields $"{kind} operation" [ "kind"; "function"; "effect" ] [ "kind"; "function"; "effect" ] element
            let intent: AgentFunctionRequiredEffectIntent = { TargetFunctionId = (resolveFunction ()).Id; EffectId = (resolveEffect ()).Id }
            if kind = "addRequiredEffect" then AgentOperationIntent.AddFunctionRequiredEffect intent
            else AgentOperationIntent.RemoveFunctionRequiredEffect intent
        | "deleteFunction" ->
            ensureFields "delete-function operation" [ "kind"; "function" ] [ "kind"; "function" ] element
            AgentOperationIntent.DeleteFunction (resolveFunction ()).Id
        | "deleteType" ->
            ensureFields "delete-type operation" [ "kind"; "type" ] [ "kind"; "type" ] element
            AgentOperationIntent.DeleteType (resolveType ()).Id
        | "deleteEffect" ->
            ensureFields "delete-effect operation" [ "kind"; "effect" ] [ "kind"; "effect" ] element
            AgentOperationIntent.DeleteEffect (resolveEffect ()).Id
        | "deleteCapability" ->
            ensureFields "delete-capability operation" [ "kind"; "capability" ] [ "kind"; "capability" ] element
            AgentOperationIntent.DeleteCapability (resolveCapability ()).Id
        | "deleteCapabilityOperation" ->
            ensureFields "delete-capability-operation operation" [ "kind"; "capability"; "operation" ] [ "kind"; "capability"; "operation" ] element
            AgentOperationIntent.DeleteCapabilityOperation (resolveOperation ()).Id
        | "removeFunctionParameter" ->
            ensureFields "remove-function-parameter operation" [ "kind"; "function"; "parameter" ] [ "kind"; "function"; "parameter" ] element
            AgentOperationIntent.RemoveFunctionParameter (WireSymbolic.functionParameter (resolveFunction ()) (requiredString "parameter" element)).Id
        | "removeUnionVariant" ->
            ensureFields "remove-union-variant operation" [ "kind"; "type"; "variant" ] [ "kind"; "type"; "variant" ] element
            AgentOperationIntent.RemoveUnionVariant (WireSymbolic.unionVariant (resolveType ()) (requiredString "variant" element)).Id
        | value ->
            fail $"Unsupported name-resolved operation kind '{value}'. simulateOperationsByName accepts the rename operations (renameFunction, renameType, renameField, renameVariant, renameParameter, renameEffect, renameCapability, renameCapabilityOperation, renameCapabilityParameter), setExported, addFunctionParameter, addProductField, addCapabilityOperation, addUnionVariant, addRequiredEffect, removeRequiredEffect, resolveMissingArgument, removeProductField, removeFunctionParameter, removeUnionVariant, deleteFunction, deleteType, deleteEffect, deleteCapability and deleteCapabilityOperation, each naming its targets by display name."

    /// Type parameter names in a new variant's field types resolve to the Union's own declared
    /// type parameters; a name the Union does not declare is rejected, since a variant cannot
    /// introduce one.
    let private bindUnionTypeParameters (owner: SemanticType) (typeRef: TypeRef) =
        let substitution =
            TypeParameters.occurring typeRef
            |> List.map (fun (id, name) ->
                match owner.TypeParameters |> List.tryFind (fun (_, declaredName) -> declaredName = name) with
                | Some(declaredId, declaredName) -> id, TypeRef.TypeParameter(declaredId, declaredName)
                | None -> fail $"Union '{owner.DisplayName}' declares no type parameter named '{name}'; a new variant's field types may use only the Union's own type parameters.")
            |> Map.ofList
        TypeParameters.substitute substitution typeRef

    let private unionOwner (owner: SemanticType) =
        match owner.Kind with
        | SemanticTypeKind.Union _ -> owner
        | SemanticTypeKind.Product _ -> fail $"Type '{owner.DisplayName}' is a Product; addUnionVariant targets a Union."

    /// The new variant of `addUnionVariant`, with its system-owned variant and field IDs allocated here.
    let private newVariant (owner: SemanticType) (fieldType: JsonElement -> TypeRef) (element: JsonElement) =
        let fields =
            array "fields" element
            |> List.map (fun field ->
                ensureFields "variant field" [ "displayName"; "type" ] [ "displayName"; "type" ] field
                ({ DisplayName = requiredString "displayName" field
                   Type = fieldType (property "type" field) |> bindUnionTypeParameters owner }: AgentTypeFieldIntent))
        AgentUnionVariantArmPlans.allocateVariant (requiredString "displayName" element) fields

    /// The arm plans of `addUnionVariant`: one `{ matchNodeId, body }` per `matchUnion` over the Union.
    let private armRecipes (body: GraphNodeId -> JsonElement -> ExpressionRecipe) (element: JsonElement): AgentUnionVariantArmRecipe list =
        array "arms" element
        |> List.map (fun arm ->
            ensureFields "union match arm plan" [ "matchNodeId"; "body" ] [ "matchNodeId"; "body" ] arm
            let matchNodeId = id<GraphNodeTag> "matchNodeId" arm
            { MatchNodeId = matchNodeId; Body = body matchNodeId (property "body" arm) })

    /// `{ "kind": "addUnionVariant", "typeId", "displayName", "fields": [{ "displayName", "type" }],
    /// "arms": [{ "matchNodeId", "body": <recipe> }] }`. The variant and field IDs are allocated here
    /// (system-owned); an arm body names a new field in a pattern reference by `"field": "<name>"`
    /// in place of `fieldId`. Every other kind is the ordinary exact-ID operation.
    let operationRequest (declarations: AuthoringDeclarations) (element: JsonElement): AuthoringOperationRequest =
        match requiredString "kind" element with
        | "addUnionVariant" ->
            ensureFields "add-union-variant operation" [ "kind"; "typeId"; "displayName"; "fields"; "arms" ] [ "kind"; "typeId"; "displayName"; "fields"; "arms" ] element
            let typeId = id<TypeTag> "typeId" element
            let owner =
                declarations.Types
                |> List.tryFind (fun item -> item.Id = typeId)
                |> Option.defaultWith (fun () -> fail $"Unknown type '{StableId.value typeId}'.")
                |> unionOwner
            let variant = newVariant owner typeRef element
            AuthoringOperationRequest.AddUnionVariant(owner.Id, variant, armRecipes (fun _ body -> WireRecipe.recipeWithNewFields variant.Fields body) element)
        | _ -> AuthoringOperationRequest.Intent(operation element)

    /// The name-resolved `addUnionVariant`: `type` selects the Union, field types and arm bodies are
    /// name-resolved, and an arm body names a new field as `{ "type", "variant": <new>, "field" }`,
    /// since the recipe is resolved against the declarations with the new variant already present.
    /// Parameter names in an arm body resolve against the function that owns the match node.
    let operationRequestByName (declarations: AuthoringDeclarations) (element: JsonElement): AuthoringOperationRequest =
        match requiredString "kind" element with
        | "addUnionVariant" ->
            ensureFields "add-union-variant operation" [ "kind"; "type"; "displayName"; "fields"; "arms" ] [ "kind"; "type"; "displayName"; "fields"; "arms" ] element
            let owner = WireSymbolic.resolveTypeAt declarations "type" element |> unionOwner
            let variant = newVariant owner (WireSymbolic.typeRef declarations) element
            let extended =
                { declarations with
                    Types =
                        declarations.Types
                        |> List.map (fun item ->
                            match item.Kind with
                            | SemanticTypeKind.Union variants when item.Id = owner.Id -> { item with Kind = SemanticTypeKind.Union(variants @ [ variant ]) }
                            | _ -> item) }
            let body matchNodeId element =
                let fn =
                    declarations.Functions
                    |> List.tryFind (fun fn -> ValueGraph.collectNodes fn.Body |> List.exists (fun node -> node.Id = matchNodeId))
                    |> Option.defaultWith (fun () -> fail $"Unknown match node '{StableId.value matchNodeId}'; arm plans name an existing matchUnion node by its exact ID.")
                WireSymbolic.recipeForExistingFunction extended fn element
            AuthoringOperationRequest.AddUnionVariant(owner.Id, variant, armRecipes body element)
        | _ -> AuthoringOperationRequest.Intent(operationByName declarations element)
