namespace Nayamnyam.Core

[<RequireQualifiedAccess>]
module internal ExecutionIrDeclarations =
    let projectType (typeEntity: TypedSemanticType): ExecutionType =
        let projectField (field: TypedSemanticField): ExecutionField =
            { Id = field.Id
              Type = field.Type }

        let projectVariant (variant: TypedSemanticVariant): ExecutionVariant =
            { Id = variant.Id
              Fields =
                variant.Fields
                |> List.sortBy (fun field -> StableId.value field.Id)
                |> List.map projectField }

        let kind =
            match typeEntity.Kind with
            | TypedSemanticTypeKind.Product fields ->
                ExecutionTypeKind.Product(
                    fields
                    |> List.sortBy (fun field -> StableId.value field.Id)
                    |> List.map projectField
                )
            | TypedSemanticTypeKind.Union variants ->
                ExecutionTypeKind.Union(
                    variants
                    |> List.sortBy (fun variant -> StableId.value variant.Id)
                    |> List.map projectVariant
                )

        { Id = typeEntity.Id
          Kind = kind }

    let projectCapability (capability: TypedSemanticCapability): ExecutionCapability =
        let projectParameter
            (parameter: TypedSemanticCapabilityParameter)
            : ExecutionCapabilityParameter =
            { Id = parameter.Id
              Type = parameter.Type }

        let projectOperation
            (operation: TypedSemanticCapabilityOperation)
            : ExecutionCapabilityOperation =
            { Id = operation.Id
              Parameters = operation.Parameters |> List.map projectParameter
              ResultType = operation.ResultType
              FailureType = operation.FailureType
              EffectId = operation.EffectId }

        { Id = capability.Id
          Operations =
            capability.Operations
            |> List.sortBy (fun operation -> StableId.value operation.Id)
            |> List.map projectOperation }
