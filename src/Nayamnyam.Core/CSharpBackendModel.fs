namespace Nayamnyam.Core

open System
open System.Text

[<RequireQualifiedAccess>]
type CSharpCapabilityBindingKind =
    | SystemConsoleWriteLine

type CSharpCapabilityBinding =
    { OperationId: CapabilityOperationId
      Kind: CSharpCapabilityBindingKind }

type CSharpBackendProfile =
    { CapabilityBindings: CSharpCapabilityBinding list }

type CSharpArtifact =
    { Source: string }

[<RequireQualifiedAccess>]
type CSharpBackendError =
    | DuplicateCapabilityBinding of CapabilityOperationId
    | MissingCapabilityBinding of CapabilityOperationId
    | UnknownCapabilityBindingOperation of CapabilityOperationId
    | IncompatibleSystemConsoleWriteLineBinding of CapabilityOperationId
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
    let empty = { CapabilityBindings = [] }

    let systemConsoleWriteLine operationId =
        { CapabilityBindings =
            [ { OperationId = operationId
                Kind = CSharpCapabilityBindingKind.SystemConsoleWriteLine } ] }

[<RequireQualifiedAccess>]
module CSharpBackendNames =
    let private utf8Hex (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> Convert.ToHexString

    let private stable prefix (id: StableId<'Kind>) =
        prefix + utf8Hex (StableId.value id)

    let typeName (id: TypeId) = stable "NyT_" id
    let fieldName (id: FieldId) = stable "f_" id
    let variantName (id: VariantId) = stable "NyV_" id
    let functionName (id: FunctionId) = stable "NyF_" id
    let parameterName (id: ParameterId) = stable "p_" id
    let capabilityName (id: CapabilityId) = stable "NyC_" id
    let capabilityOperationName (id: CapabilityOperationId) = stable "Op_" id
    let capabilityParameterName (id: CapabilityParameterId) = stable "cp_" id
    let capabilityAdapterName (id: CapabilityId) = stable "NyA_" id

    let executionValueName (id: ExecutionValueId) =
        "v_" + utf8Hex (ExecutionValueId.value id)

    let executionBlockLabel (id: ExecutionBlockId) =
        "L_" + utf8Hex (ExecutionBlockId.value id)

    let jumpTemporaryName (sourceBlockId: ExecutionBlockId) index =
        "jt_" + utf8Hex (ExecutionBlockId.value sourceBlockId) + "_" + string index
