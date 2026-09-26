namespace Nyrwvy.Core

open System
open System.Text

[<RequireQualifiedAccess>]
type CSharpCapabilityBindingKind =
    | SystemConsoleWriteLine
    /// The host implements the generated typed capability interface and supplies it as a function argument.
    | ExternalImplementation

type CSharpCapabilityBinding =
    { OperationId: CapabilityOperationId
      Kind: CSharpCapabilityBindingKind }

type CSharpBackendProfile =
    { CapabilityBindings: CSharpCapabilityBinding list
      /// Host assertion covering every operation and every supplied instance of these capabilities.
      ConcurrentCapabilityIds: Set<CapabilityId> }

type CSharpSymbol<'Id> =
    { Id: 'Id
      Name: string }

type CSharpSymbolManifest =
    { Namespace: string
      FunctionContainer: string
      Types: CSharpSymbol<TypeId> list
      Fields: CSharpSymbol<FieldId> list
      Variants: CSharpSymbol<VariantId> list
      Functions: CSharpSymbol<FunctionId> list
      Parameters: CSharpSymbol<ParameterId> list
      Capabilities: CSharpSymbol<CapabilityId> list
      CapabilityOperations: CSharpSymbol<CapabilityOperationId> list
      CapabilityParameters: CSharpSymbol<CapabilityParameterId> list
      CapabilityAdapters: CSharpSymbol<CapabilityId> list }

type CSharpArtifact =
    { Source: string
      Symbols: CSharpSymbolManifest }

[<RequireQualifiedAccess>]
type CSharpBackendError =
    | DuplicateCapabilityBinding of CapabilityOperationId
    | MissingCapabilityBinding of CapabilityOperationId
    | UnknownCapabilityBindingOperation of CapabilityOperationId
    | IncompatibleSystemConsoleWriteLineBinding of CapabilityOperationId
    | MixedCapabilityBindingKinds of CapabilityId
    | InvalidConcurrentCapabilityBinding of CapabilityId
    | ConcurrentCapabilityBindingRequired of FunctionId * GraphNodeId * CapabilityId * CapabilityOperationId
    | UnsupportedCapabilityFailure of CapabilityOperationId
    | UnknownCallTarget of FunctionId
    | UnknownCapabilityOperation of CapabilityOperationId
    | UnknownNamedType of TypeId
    | UnknownUnionVariant of VariantId
    | UnknownField of FieldId
    | UnknownExecutionValue of FunctionId * ExecutionValueId
    | InvalidExecutionJumpTarget of FunctionId * ExecutionBlockId

[<RequireQualifiedAccess>]
module CSharpBackendProfile =
    let empty: CSharpBackendProfile = { CapabilityBindings = []; ConcurrentCapabilityIds = Set.empty }

    let systemConsoleWriteLine (operationId: CapabilityOperationId): CSharpBackendProfile =
        { empty with
            CapabilityBindings =
                [ { OperationId = operationId
                    Kind = CSharpCapabilityBindingKind.SystemConsoleWriteLine } ] }

    let externalImplementations (operationIds: CapabilityOperationId list): CSharpBackendProfile =
        { empty with
            CapabilityBindings =
                operationIds
                |> List.map (fun operationId ->
                    { OperationId = operationId
                      Kind = CSharpCapabilityBindingKind.ExternalImplementation }) }

    let withConcurrentCapabilities (capabilityIds: CapabilityId list) (profile: CSharpBackendProfile) =
        { profile with ConcurrentCapabilityIds = Set.ofList capabilityIds }

[<RequireQualifiedAccess>]
module CSharpBackendNames =
    let generatedNamespace = "Nyrwvy.Generated"
    let functionContainerName = "NyFunctions"

    let private utf8Hex (value: string) =
        let bytes: byte array = Encoding.UTF8.GetBytes(value)
        Convert.ToHexString(bytes)

    let private stable prefix (id: StableId<'Kind>) =
        prefix + utf8Hex (StableId.value id)

    let typeName (id: TypeId) = stable "NyT_" id
    let fieldName (id: FieldId) = stable "f_" id
    let variantName (id: VariantId) = stable "NyV_" id
    let typeParameterName (id: TypeParameterId) = stable "T_" id
    let functionName (id: FunctionId) = stable "NyF_" id
    let parameterName (id: ParameterId) = stable "p_" id
    let capabilityName (id: CapabilityId) = stable "NyC_" id
    let capabilityOperationName (id: CapabilityOperationId) = stable "Op_" id
    let capabilityParameterName (id: CapabilityParameterId) = stable "cp_" id
    let capabilityAdapterName (id: CapabilityId) = stable "NyA_" id
    let capabilityImplementationName (id: GraphNodeId) = stable "NyI_" id

    let executionValueName (id: ExecutionValueId) =
        "v_" + utf8Hex (ExecutionValueId.value id)

    let executionBlockLabel (id: ExecutionBlockId) =
        "L_" + utf8Hex (ExecutionBlockId.value id)

    let jumpTemporaryName (sourceBlockId: ExecutionBlockId) (index: int) =
        "jt_" + utf8Hex (ExecutionBlockId.value sourceBlockId) + "_" + string index
