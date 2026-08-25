namespace Nayamnyam.Core

[<RequireQualifiedAccess>]
module internal CSharpBackendValidation =
    type Context =
        { FunctionsById: Map<FunctionId, ExecutionFunction>
          TypesById: Map<TypeId, ExecutionType>
          VariantsById: Map<VariantId, TypeId * ExecutionVariant>
          FieldsById: Map<FieldId, TypeId * VariantId option * ExecutionField>
          CapabilitiesById: Map<CapabilityId, ExecutionCapability>
          OperationsById: Map<CapabilityOperationId, CapabilityId * ExecutionCapabilityOperation>
          BindingsByOperation: Map<CapabilityOperationId, CSharpCapabilityBindingKind> }

    let private firstDuplicateBinding (profile: CSharpBackendProfile) =
        profile.CapabilityBindings
        |> List.countBy (fun binding -> binding.OperationId)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.sortBy (fun (operationId, _) -> StableId.value operationId)
        |> List.tryHead
        |> Option.map (fun (operationId, _) -> CSharpBackendError.DuplicateCapabilityBinding operationId)

    let private collectUsedOperations (program: ExecutionProgram) =
        program.Functions
        |> List.collect (fun functionEntity ->
            functionEntity.Blocks
            |> List.collect (fun block ->
                block.Instructions
                |> List.choose (fun instruction ->
                    match instruction.Kind with
                    | ExecutionInstructionKind.CapabilityInvoke(_, operationId, _) -> Some operationId
                    | _ -> None)))
        |> List.distinct
        |> List.sortBy StableId.value

    let private isSystemConsoleWriteLineCompatible (operation: ExecutionCapabilityOperation) =
        match operation.Parameters, operation.ResultType, operation.FailureType with
        | [ parameter ], TypeRef.Primitive PrimitiveType.Unit, None ->
            parameter.Type = TypeRef.Primitive PrimitiveType.String
        | _ -> false

    let prepare
        (program: ExecutionProgram)
        (profile: CSharpBackendProfile)
        : Result<Context, CSharpBackendError> =
        let functionsById =
            program.Functions
            |> List.map (fun functionEntity -> functionEntity.SemanticFunctionId, functionEntity)
            |> Map.ofList

        let typesById =
            program.Types
            |> List.map (fun typeEntity -> typeEntity.Id, typeEntity)
            |> Map.ofList

        let variantEntries =
            program.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | ExecutionTypeKind.Product _ -> []
                | ExecutionTypeKind.Union variants ->
                    variants |> List.map (fun variant -> variant.Id, (typeEntity.Id, variant)))

        let variantsById = Map.ofList variantEntries

        let fieldEntries =
            program.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | ExecutionTypeKind.Product fields ->
                    fields
                    |> List.map (fun field -> field.Id, (typeEntity.Id, None, field))
                | ExecutionTypeKind.Union variants ->
                    variants
                    |> List.collect (fun variant ->
                        variant.Fields
                        |> List.map (fun field ->
                            field.Id, (typeEntity.Id, Some variant.Id, field))))

        let fieldsById = Map.ofList fieldEntries

        let capabilitiesById =
            program.Capabilities
            |> List.map (fun capability -> capability.Id, capability)
            |> Map.ofList

        let operationEntries =
            program.Capabilities
            |> List.collect (fun capability ->
                capability.Operations
                |> List.map (fun operation -> operation.Id, (capability.Id, operation)))

        let operationsById = Map.ofList operationEntries

        match firstDuplicateBinding profile with
        | Some error -> Error error
        | None ->
            let orderedBindings =
                profile.CapabilityBindings
                |> List.sortBy (fun binding -> StableId.value binding.OperationId)

            match
                orderedBindings
                |> List.tryFind (fun binding -> not (Map.containsKey binding.OperationId operationsById))
            with
            | Some binding -> Error(CSharpBackendError.UnknownCapabilityBindingOperation binding.OperationId)
            | None ->
                let bindingsByOperation =
                    orderedBindings
                    |> List.map (fun binding -> binding.OperationId, binding.Kind)
                    |> Map.ofList

                let usedOperations = collectUsedOperations program

                match
                    usedOperations
                    |> List.tryFind (fun operationId -> not (Map.containsKey operationId operationsById))
                with
                | Some operationId -> Error(CSharpBackendError.UnknownCapabilityOperation operationId)
                | None ->
                    match
                        usedOperations
                        |> List.tryFind (fun operationId -> not (Map.containsKey operationId bindingsByOperation))
                    with
                    | Some operationId -> Error(CSharpBackendError.MissingCapabilityBinding operationId)
                    | None ->
                        match
                            orderedBindings
                            |> List.tryPick (fun binding ->
                                let _, operation = Map.find binding.OperationId operationsById
                                match binding.Kind with
                                | CSharpCapabilityBindingKind.SystemConsoleWriteLine ->
                                    if isSystemConsoleWriteLineCompatible operation then
                                        None
                                    else
                                        Some(CSharpBackendError.IncompatibleSystemConsoleWriteLineBinding binding.OperationId))
                        with
                        | Some error -> Error error
                        | None ->
                            match
                                operationEntries
                                |> List.map snd
                                |> List.map snd
                                |> List.sortBy (fun operation -> StableId.value operation.Id)
                                |> List.tryFind (fun operation -> Option.isSome operation.FailureType)
                            with
                            | Some operation -> Error(CSharpBackendError.UnsupportedCapabilityFailure operation.Id)
                            | None ->
                                Ok
                                    { FunctionsById = functionsById
                                      TypesById = typesById
                                      VariantsById = variantsById
                                      FieldsById = fieldsById
                                      CapabilitiesById = capabilitiesById
                                      OperationsById = operationsById
                                      BindingsByOperation = bindingsByOperation }
