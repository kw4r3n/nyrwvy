namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open Nyrwvy.Core

module internal WireOperation =
    open WireInput

    let operation (element: JsonElement): AgentOperationIntent =
        match requiredString "kind" element with
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
        | "addFunctionParameter" ->
            ensureFields
                "add-function-parameter operation"
                [ "kind"; "functionId"; "displayName"; "type"; "callSites" ]
                [ "kind"; "functionId"; "displayName"; "type"; "callSites" ]
                element
            let plans =
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
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  NewParameterDisplayName = requiredString "displayName" element
                  NewParameterType = typeRef (property "type" element)
                  CallSitePlans = plans }
        | "addProductField" ->
            ensureFields
                "add-product-field operation"
                [ "kind"; "typeId"; "displayName"; "type"; "constructions" ]
                [ "kind"; "typeId"; "displayName"; "type"; "constructions" ]
                element
            let plans: ProductFieldConstructionPlan list =
                array "constructions" element
                |> List.map (fun plan ->
                    ensureFields "product construction plan" [ "constructNodeId"; "valueNodeId" ] [ "constructNodeId"; "valueNodeId" ] plan
                    { ConstructNodeId = id<GraphNodeTag> "constructNodeId" plan
                      ValueNodeId = id<GraphNodeTag> "valueNodeId" plan })
            AgentOperationIntent.AddProductField
                { TargetTypeId = id<TypeTag> "typeId" element
                  NewFieldDisplayName = requiredString "displayName" element
                  NewFieldType = typeRef (property "type" element)
                  ConstructionPlans = plans }
        | "removeProductField" ->
            ensureFields "remove-product-field operation" [ "kind"; "fieldId" ] [ "kind"; "fieldId" ] element
            AgentOperationIntent.RemoveProductField(id<FieldTag> "fieldId" element)
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
        | "resolveMissingArgument" ->
            ensureFields "resolve-missing-argument operation" [ "kind"; "obligationId"; "valueNodeId" ] [ "kind"; "obligationId"; "valueNodeId" ] element
            AgentOperationIntent.ResolveMissingArgument
                { TargetObligationId = id<ObligationTag> "obligationId" element
                  ResolutionValueNodeId = id<GraphNodeTag> "valueNodeId" element }
        | "addRequiredEffect"
        | "removeRequiredEffect" as kind ->
            ensureFields $"{kind} operation" [ "kind"; "functionId"; "effectId" ] [ "kind"; "functionId"; "effectId" ] element
            let intent: AgentFunctionRequiredEffectIntent =
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  EffectId = id<EffectTag> "effectId" element }
            if kind = "addRequiredEffect" then AgentOperationIntent.AddFunctionRequiredEffect intent
            else AgentOperationIntent.RemoveFunctionRequiredEffect intent
        | value -> fail $"Unsupported operation kind '{value}'."
