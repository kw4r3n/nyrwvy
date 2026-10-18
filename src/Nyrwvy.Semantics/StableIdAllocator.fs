namespace Nyrwvy.Core

open System

/// Production stable-ID allocation and typed text forms.
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
    let newTypeParameterId (): TypeParameterId = allocate<TypeParameterTag> ()
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

