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
