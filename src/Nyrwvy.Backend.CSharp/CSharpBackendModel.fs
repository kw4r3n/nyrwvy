namespace Nyrwvy.Core

open System
open System.Text

/// A C value as a mapping-table entry names it ([mapping tables](../../docs/decisions/csharp-mapping-tables.md)).
[<RequireQualifiedAccess>]
type CSharpNativeValue =
    /// A C integer of 8, 16, 32 or 64 bits; Int64 arguments outside its range are a host failure.
    | Integer of bits: int * signed: bool
    /// C `bool` (one byte).
    | Bool
    /// A C `int` read as a Bool (nonzero is true).
    | IntBool
    /// A NUL-terminated UTF-8 `char*`: borrowed for the call as an argument; as a result, borrowed
    /// from the library, or owned and passed to `free` after it is copied.
    | Utf8 of free: string option
    /// `Bytes` as a pointer and a `size_t` length, borrowed for the call (two C arguments).
    | Bytes
    /// An opaque pointer held by a handle capability value; `release` marks the handle closed.
    | Handle of release: bool
    | Void

[<RequireQualifiedAccess>]
type CSharpNativeFailure =
    | NullPointer
    | Negative
    | Nonzero

[<RequireQualifiedAccess>]
type CSharpNativeArgument =
    /// The handle the invoked handle capability value holds.
    | Self of release: bool
    | Parameter of CapabilityParameterId * CSharpNativeValue

type CSharpNativeCall =
    { Library: string
      Symbol: string
      /// In C parameter order.
      Arguments: CSharpNativeArgument list
      Returns: CSharpNativeValue
      Failure: CSharpNativeFailure option }

type CSharpNativeBinding =
    { /// The operation's capability is a handle: its values hold a native pointer.
      Handle: bool
      /// None: explicitly unavailable on this backend; the generated method is a host failure.
      Call: CSharpNativeCall option }

[<RequireQualifiedAccess>]
type CSharpCapabilityBindingKind =
    | SystemConsoleWriteLine
    /// The host implements the generated typed capability interface and supplies it as a function argument.
    | ExternalImplementation
    /// A mapping-table entry: the generated adapter calls a C function through P/Invoke.
    | Native of CSharpNativeBinding

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
    | IncompatibleNativeBinding of CapabilityOperationId * reason: string
    | MixedCapabilityBindingKinds of CapabilityId
    | InvalidConcurrentCapabilityBinding of CapabilityId
    /// Table-bound handles are not synchronized, so their capabilities cannot be asserted concurrent.
    | ConcurrentNativeHandle of CapabilityId
    | ConcurrentCapabilityBindingRequired of FunctionId * GraphNodeId * CapabilityId * CapabilityOperationId
    | UnsupportedCapabilityFailure of CapabilityOperationId
    | UnknownCallTarget of FunctionId
    | UnknownCapabilityOperation of CapabilityOperationId

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
