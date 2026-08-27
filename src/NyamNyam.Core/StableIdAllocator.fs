namespace NyamNyam.Core

open System

[<RequireQualifiedAccess>]
type StableIdTextError =
    | MissingDomainPrefix of expectedPrefix: string
    | InvalidPayload of StableIdError

/// Production stable-ID allocation and typed external text forms.
///
/// The StableId payload is an opaque UUIDv7 string. The generic StableId<'Kind>
/// type is the in-memory domain authority; textual prefixes are transport and
/// inspection tags and are therefore not duplicated inside the identity payload.
[<RequireQualifiedAccess>]
module StableIdAllocator =
    let private allocate<'Kind> (): StableId<'Kind> =
        let payload = Guid.CreateVersion7().ToString("D")
        match StableId.tryCreate<'Kind> payload with
        | Ok id -> id
        | Error error -> invalidOp $"Generated UUIDv7 was rejected as a stable ID: {error}."

    let newTypeId (): TypeId = allocate<TypeTag> ()
    let newFieldId (): FieldId = allocate<FieldTag> ()
    let newVariantId (): VariantId = allocate<VariantTag> ()
    let newFunctionId (): FunctionId = allocate<FunctionTag> ()
    let newParameterId (): ParameterId = allocate<ParameterTag> ()
    let newPatternBindingId (): PatternBindingId = allocate<PatternBindingTag> ()
    let newGraphNodeId (): GraphNodeId = allocate<GraphNodeTag> ()
    let newEffectId (): EffectId = allocate<EffectTag> ()
    let newCapabilityId (): CapabilityId = allocate<CapabilityTag> ()
    let newCapabilityOperationId (): CapabilityOperationId = allocate<CapabilityOperationTag> ()
    let newCapabilityParameterId (): CapabilityParameterId = allocate<CapabilityParameterTag> ()
    let newObligationId (): ObligationId = allocate<ObligationTag> ()
    let newSnapshotId (): SnapshotId = allocate<SnapshotTag> ()
    let newTransactionId (): TransactionId = allocate<TransactionTag> ()

[<RequireQualifiedAccess>]
module StableIdText =
    let private render prefix (id: StableId<'Kind>) =
        prefix + ":" + StableId.value id

    let private parse<'Kind>
        (prefix: string)
        (text: string)
        : Result<StableId<'Kind>, StableIdTextError> =
        let expected = prefix + ":"
        if isNull text || not (text.StartsWith(expected, StringComparison.Ordinal)) then
            Error(StableIdTextError.MissingDomainPrefix prefix)
        else
            StableId.tryCreate<'Kind> (text.Substring(expected.Length))
            |> Result.mapError StableIdTextError.InvalidPayload

    let typeId (id: TypeId) = render "type" id
    let fieldId (id: FieldId) = render "field" id
    let variantId (id: VariantId) = render "variant" id
    let functionId (id: FunctionId) = render "function" id
    let parameterId (id: ParameterId) = render "parameter" id
    let patternBindingId (id: PatternBindingId) = render "pattern-binding" id
    let graphNodeId (id: GraphNodeId) = render "node" id
    let effectId (id: EffectId) = render "effect" id
    let capabilityId (id: CapabilityId) = render "capability" id
    let capabilityOperationId (id: CapabilityOperationId) = render "capability-operation" id
    let capabilityParameterId (id: CapabilityParameterId) = render "capability-parameter" id
    let obligationId (id: ObligationId) = render "obligation" id
    let snapshotId (id: SnapshotId) = render "snapshot" id
    let transactionId (id: TransactionId) = render "transaction" id

    let parseTypeId text: Result<TypeId, StableIdTextError> = parse<TypeTag> "type" text
    let parseFieldId text: Result<FieldId, StableIdTextError> = parse<FieldTag> "field" text
    let parseVariantId text: Result<VariantId, StableIdTextError> = parse<VariantTag> "variant" text
    let parseFunctionId text: Result<FunctionId, StableIdTextError> = parse<FunctionTag> "function" text
    let parseParameterId text: Result<ParameterId, StableIdTextError> = parse<ParameterTag> "parameter" text
    let parsePatternBindingId text: Result<PatternBindingId, StableIdTextError> = parse<PatternBindingTag> "pattern-binding" text
    let parseGraphNodeId text: Result<GraphNodeId, StableIdTextError> = parse<GraphNodeTag> "node" text
    let parseEffectId text: Result<EffectId, StableIdTextError> = parse<EffectTag> "effect" text
    let parseCapabilityId text: Result<CapabilityId, StableIdTextError> = parse<CapabilityTag> "capability" text
    let parseCapabilityOperationId text: Result<CapabilityOperationId, StableIdTextError> = parse<CapabilityOperationTag> "capability-operation" text
    let parseCapabilityParameterId text: Result<CapabilityParameterId, StableIdTextError> = parse<CapabilityParameterTag> "capability-parameter" text
    let parseObligationId text: Result<ObligationId, StableIdTextError> = parse<ObligationTag> "obligation" text
    let parseSnapshotId text: Result<SnapshotId, StableIdTextError> = parse<SnapshotTag> "snapshot" text
    let parseTransactionId text: Result<TransactionId, StableIdTextError> = parse<TransactionTag> "transaction" text
