namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
module internal CSharpBackendValidation =
    type Context =
        { FunctionsById: Map<FunctionId, ExecutionFunction>
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
        |> List.collect (fun (functionEntity: ExecutionFunction) ->
            functionEntity.Blocks
            |> List.collect (fun (block: ExecutionBlock) ->
                block.Instructions
                |> List.collect (fun (instruction: ExecutionInstruction) -> ExecutionInstructionKind.capabilityOperations instruction.Kind)))
        |> List.distinct
        |> List.sortBy StableId.value

    let private collectFunctionTargets (program: ExecutionProgram) =
        program.Functions
        |> List.collect ExecutionCallGraph.instructions
        |> List.collect ExecutionCallGraph.targets
        |> List.distinct
        |> List.sortBy StableId.value

    let private boundCapabilityIds (profile: CSharpBackendProfile) operationsById =
        profile.CapabilityBindings
        |> List.map (fun binding -> fst (Map.find binding.OperationId operationsById))
        |> Set.ofList

    let private concurrentBindingProblem (program: ExecutionProgram) (profile: CSharpBackendProfile) operationsById =
        match Set.difference profile.ConcurrentCapabilityIds (boundCapabilityIds profile operationsById) |> Set.toList |> List.tryHead with
        | Some id -> Some(CSharpBackendError.InvalidConcurrentCapabilityBinding id)
        | None ->
            let workers =
                program.Functions
                |> List.collect ExecutionCallGraph.instructions
                |> List.collect (fun instruction -> ExecutionInstructionKind.callbackTargets instruction.Kind)
                |> List.choose (fun (role, functionId) ->
                    match role with
                    | FunctionReferenceRole.Worker -> Some functionId
                    | FunctionReferenceRole.DirectCall
                    | FunctionReferenceRole.PureListCallback
                    | FunctionReferenceRole.EffectListCallback
                    | FunctionReferenceRole.Body
                    | FunctionReferenceRole.FunctionValue
                    | FunctionReferenceRole.CapabilityImplementation _ -> None)
                |> ExecutionCallGraph.reachable program
            program.Functions
            |> List.filter (fun fn -> Set.contains fn.SemanticFunctionId workers)
            |> List.sortBy (fun fn -> StableId.value fn.SemanticFunctionId)
            |> List.tryPick (fun fn ->
                ExecutionCallGraph.instructions fn
                |> List.sortBy (fun instruction -> StableId.value instruction.OriginNodeId)
                |> List.tryPick (fun instruction ->
                    ExecutionInstructionKind.capabilityOperations instruction.Kind
                    |> List.tryPick (fun operationId ->
                        let capabilityId, _ = Map.find operationId operationsById
                        if Set.contains capabilityId profile.ConcurrentCapabilityIds then None
                        else Some(CSharpBackendError.ConcurrentCapabilityBindingRequired(fn.SemanticFunctionId, instruction.OriginNodeId, capabilityId, operationId)))))

    let private isSystemConsoleWriteLineCompatible (operation: ExecutionCapabilityOperation) =
        match operation.Parameters, operation.ResultType, operation.FailureType with
        | [ parameter ], TypeRef.Primitive PrimitiveType.Unit, None ->
            parameter.Type = TypeRef.Primitive PrimitiveType.String
        | _ -> false

    /// Kinds compare by case: two table entries on one capability are not "mixed".
    let private bindingCase (kind: CSharpCapabilityBindingKind) =
        match kind with
        | CSharpCapabilityBindingKind.SystemConsoleWriteLine -> 0
        | CSharpCapabilityBindingKind.ExternalImplementation -> 1
        | CSharpCapabilityBindingKind.Native _ -> 2

    /// A capability whose operations are all native handle bindings.
    let private isNativeHandle (capabilitiesById: Map<CapabilityId, ExecutionCapability>) (bindings: Map<CapabilityOperationId, CSharpCapabilityBindingKind>) capabilityId =
        match Map.tryFind capabilityId capabilitiesById with
        | Some capability when not (List.isEmpty capability.Operations) ->
            capability.Operations |> List.forall (fun operation ->
                match Map.tryFind operation.Id bindings with
                | Some(CSharpCapabilityBindingKind.Native binding) -> binding.Handle
                | _ -> false)
        | _ -> false

    /// Why a table entry does not fit its operation's signature, or None.
    let private nativeProblem
        (capabilitiesById: Map<CapabilityId, ExecutionCapability>)
        (bindings: Map<CapabilityOperationId, CSharpCapabilityBindingKind>)
        (capabilityId: CapabilityId)
        (operation: ExecutionCapabilityOperation)
        (binding: CSharpNativeBinding) =
        let handleType typeRef =
            match typeRef with
            | TypeRef.Capability id -> isNativeHandle capabilitiesById bindings id
            | _ -> false
        let capability = Map.find capabilityId capabilitiesById
        let sameHandleFlag =
            capability.Operations |> List.forall (fun other ->
                match Map.tryFind other.Id bindings with
                | Some(CSharpCapabilityBindingKind.Native item) -> item.Handle = binding.Handle
                | _ -> true)
        if not sameHandleFlag then Some "every operation of a handle capability must be a handle operation"
        else
        match binding.Call with
        | None -> None
        | Some call ->
            let argumentProblem (argument: CSharpNativeArgument) =
                match argument with
                | CSharpNativeArgument.Self _ -> if binding.Handle then None else Some "self is only an argument of a handle capability's operations"
                | CSharpNativeArgument.Parameter(parameterId, value) ->
                    match operation.Parameters |> List.tryFind (fun parameter -> parameter.Id = parameterId) with
                    | None -> Some "an argument names no parameter of the operation"
                    | Some parameter ->
                        let fits =
                            match value, parameter.Type with
                            | CSharpNativeValue.Integer(bits, _), TypeRef.Primitive PrimitiveType.Int64 -> List.contains bits [ 8; 16; 32; 64 ]
                            | (CSharpNativeValue.Bool | CSharpNativeValue.IntBool), TypeRef.Primitive PrimitiveType.Bool -> true
                            | CSharpNativeValue.Utf8 None, TypeRef.Primitive PrimitiveType.String -> true
                            | CSharpNativeValue.Bytes, TypeRef.Primitive PrimitiveType.Bytes -> true
                            | CSharpNativeValue.Handle _, typeRef -> handleType typeRef
                            | _ -> false
                        if fits then None else Some $"argument {StableId.value parameterId}: {value} does not convert its parameter type"
            let parameterIds = call.Arguments |> List.choose (function CSharpNativeArgument.Parameter(id, _) -> Some id | CSharpNativeArgument.Self _ -> None)
            let selfCount = call.Arguments |> List.filter (function CSharpNativeArgument.Self _ -> true | _ -> false) |> List.length
            let releases =
                call.Arguments |> List.filter (function
                    | CSharpNativeArgument.Self true | CSharpNativeArgument.Parameter(_, CSharpNativeValue.Handle true) -> true
                    | _ -> false) |> List.length
            let okType, isResult =
                match operation.ResultType with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ ok; TypeRef.Primitive PrimitiveType.String ]) -> ok, true
                | other -> other, false
            let resultFits =
                match call.Returns, okType with
                | CSharpNativeValue.Void, TypeRef.Primitive PrimitiveType.Unit -> true
                | CSharpNativeValue.Integer(bits, _), TypeRef.Primitive PrimitiveType.Int64 -> List.contains bits [ 8; 16; 32; 64 ]
                | CSharpNativeValue.Integer _, TypeRef.Primitive PrimitiveType.Unit -> Option.isSome call.Failure
                | (CSharpNativeValue.Bool | CSharpNativeValue.IntBool), TypeRef.Primitive PrimitiveType.Bool -> true
                | CSharpNativeValue.Utf8 _, TypeRef.Primitive PrimitiveType.String -> true
                | CSharpNativeValue.Handle false, typeRef -> handleType typeRef
                | _ -> false
            let failureFits =
                match call.Failure, call.Returns with
                | None, _ -> true
                | Some CSharpNativeFailure.NullPointer, (CSharpNativeValue.Utf8 _ | CSharpNativeValue.Handle _) -> true
                | Some CSharpNativeFailure.Negative, CSharpNativeValue.Integer(_, true) -> true
                | Some CSharpNativeFailure.Nonzero, CSharpNativeValue.Integer _ -> okType = TypeRef.Primitive PrimitiveType.Unit
                | Some _, _ -> false
            match call.Arguments |> List.tryPick argumentProblem with
            | Some problem -> Some problem
            | None ->
                if List.sort parameterIds <> List.sort (operation.Parameters |> List.map (fun parameter -> parameter.Id)) then
                    Some "every parameter must be passed exactly once"
                elif binding.Handle && selfCount <> 1 then Some "a handle operation passes self exactly once"
                elif releases > 1 then Some "an operation releases at most one handle"
                elif (match operation.ResultType with TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; error ]) -> error <> TypeRef.Primitive PrimitiveType.String | _ -> false) then
                    Some "a Result's error type must be String"
                elif isResult && Option.isNone call.Failure then Some "a Result<_, String> operation needs failure detection"
                elif not resultFits then Some $"result {call.Returns} does not convert to the operation's result type"
                elif not failureFits then Some $"failure {call.Failure} does not apply to result {call.Returns}"
                else None

    let prepare
        (program: ExecutionProgram)
        (profile: CSharpBackendProfile)
        : Result<Context, CSharpBackendError> =
        let functionsById: Map<FunctionId, ExecutionFunction> =
            program.Functions
            |> List.map (fun functionEntity -> functionEntity.SemanticFunctionId, functionEntity)
            |> Map.ofList

        let capabilitiesById: Map<CapabilityId, ExecutionCapability> =
            program.Capabilities
            |> List.map (fun capability -> capability.Id, capability)
            |> Map.ofList

        let operationEntries: (CapabilityOperationId * (CapabilityId * ExecutionCapabilityOperation)) list =
            program.Capabilities
            |> List.collect (fun capability ->
                capability.Operations
                |> List.map (fun operation -> operation.Id, (capability.Id, operation)))

        let operationsById = Map.ofList operationEntries

        match firstDuplicateBinding profile with
        | Some error -> Error error
        | None ->
            let orderedBindings: CSharpCapabilityBinding list =
                profile.CapabilityBindings
                |> List.sortBy (fun binding -> StableId.value binding.OperationId)

            match
                orderedBindings
                |> List.tryFind (fun binding -> not (Map.containsKey binding.OperationId operationsById))
            with
            | Some binding -> Error(CSharpBackendError.UnknownCapabilityBindingOperation binding.OperationId)
            | None ->
                let bindingsByOperation: Map<CapabilityOperationId, CSharpCapabilityBindingKind> =
                    orderedBindings
                    |> List.map (fun binding -> binding.OperationId, binding.Kind)
                    |> Map.ofList

                match
                    collectFunctionTargets program
                    |> List.tryFind (fun functionId -> not (Map.containsKey functionId functionsById))
                with
                | Some functionId -> Error(CSharpBackendError.UnknownCallTarget functionId)
                | None ->
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
                            let boundCapabilities = boundCapabilityIds profile operationsById

                            let missingAdapterBinding =
                                program.Capabilities
                                |> List.filter (fun capability -> Set.contains capability.Id boundCapabilities)
                                |> List.sortBy (fun capability -> StableId.value capability.Id)
                                |> List.collect (fun capability ->
                                    capability.Operations
                                    |> List.sortBy (fun operation -> StableId.value operation.Id))
                                |> List.tryFind (fun operation -> not (Map.containsKey operation.Id bindingsByOperation))

                            match missingAdapterBinding with
                            | Some operation -> Error(CSharpBackendError.MissingCapabilityBinding operation.Id)
                            | None ->
                                let mixedCapability =
                                    program.Capabilities
                                    |> List.sortBy (fun capability -> StableId.value capability.Id)
                                    |> List.tryFind (fun capability ->
                                        capability.Operations
                                        |> List.choose (fun operation -> Map.tryFind operation.Id bindingsByOperation)
                                        |> List.distinctBy bindingCase
                                        |> List.length > 1)
                                match mixedCapability with
                                | Some capability -> Error(CSharpBackendError.MixedCapabilityBindingKinds capability.Id)
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
                                                    Some(CSharpBackendError.IncompatibleSystemConsoleWriteLineBinding binding.OperationId)
                                            | CSharpCapabilityBindingKind.ExternalImplementation -> None
                                            | CSharpCapabilityBindingKind.Native native ->
                                                let capabilityId, _ = Map.find binding.OperationId operationsById
                                                nativeProblem capabilitiesById bindingsByOperation capabilityId operation native
                                                |> Option.map (fun reason -> CSharpBackendError.IncompatibleNativeBinding(binding.OperationId, reason)))
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
                                            match concurrentBindingProblem program profile operationsById with
                                            | Some error -> Error error
                                            | None ->
                                                Ok
                                                    { FunctionsById = functionsById
                                                      CapabilitiesById = capabilitiesById
                                                      OperationsById = operationsById
                                                      BindingsByOperation = bindingsByOperation }
