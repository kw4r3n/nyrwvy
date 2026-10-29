namespace Nyrwvy.Core

open System

/// Production stable-ID allocation and typed text forms.
///
/// The StableId payload is an opaque UUIDv7 string. The generic StableId<'Kind>
/// type is the in-memory domain authority; textual prefixes are transport and
/// inspection tags and are therefore not duplicated inside the identity payload.
[<RequireQualifiedAccess>]
module StableIdAllocator =
    /// `NYRWVY_TEST_ID_SEED`, read once per process: when set, new IDs are deterministic, so two
    /// hosts given the same requests write the same IDs (docs/decisions/zig-session-host.md,
    /// "How parity with F# is checked"). Never set it outside tests: seeded IDs repeat across processes.
    let private testSeed = lazy (Environment.GetEnvironmentVariable "NYRWVY_TEST_ID_SEED" |> Option.ofObj |> Option.filter (fun seed -> seed <> ""))
    let mutable private allocated = 0L

    /// The `n`-th ID (from 1) of a process seeded with `seed`: a UUIDv7 whose timestamp is
    /// 1 700 000 000 000 ms plus `n` and whose other bits come from SHA-256 of `seed:n`.
    let seededPayload (seed: string) (n: int64) =
        let hash = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes($"{seed}:{n}"))
        let milliseconds = 1_700_000_000_000L + n
        let bytes = Array.zeroCreate<byte> 16
        for index in 0 .. 5 do bytes[index] <- byte (milliseconds >>> (8 * (5 - index)))
        bytes[6] <- 0x70uy ||| (hash[0] &&& 0x0Fuy)
        bytes[7] <- hash[1]
        bytes[8] <- 0x80uy ||| (hash[2] &&& 0x3Fuy)
        Array.blit hash 3 bytes 9 7
        Guid(ReadOnlySpan bytes, true).ToString("D")

    let private allocate<'Kind> (): StableId<'Kind> =
        let payload =
            match testSeed.Value with
            | Some seed -> seededPayload seed (Threading.Interlocked.Increment &allocated)
            | None -> Guid.CreateVersion7().ToString("D")
        match StableId.tryCreate<'Kind> payload with
        | Ok id -> id
        | Error error -> invalidOp $"Generated UUIDv7 was rejected as a stable ID: {error}."

    /// A deterministic ID derived from a seed: the first 16 bytes of the seed's UTF-8 SHA-256 as a
    /// GUID. Re-simulating the same request derives the same ID. Callers make the seed unique to
    /// the identity it names (for example transaction, node and field IDs together).
    let derived<'Kind> (seed: string): StableId<'Kind> =
        let hash = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes seed)
        let payload = Guid(ReadOnlySpan(hash, 0, 16)).ToString("D")
        match StableId.tryCreate<'Kind> payload with
        | Ok id -> id
        | Error error -> invalidOp $"Derived GUID was rejected as a stable ID: {error}."

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

