namespace Nyrwvy.Core

open System
open System.Security.Cryptography
open System.Text

[<Struct; StructuralEquality; StructuralComparison>]
type SnapshotFingerprint = private SnapshotFingerprint of string

[<Struct; StructuralEquality; StructuralComparison>]
type SemanticFingerprint = private SemanticFingerprint of string

[<RequireQualifiedAccess>]
module private FingerprintHash =
    let sha256 domain (canonicalText: string) =
        let domainBytes = Encoding.UTF8.GetBytes(domain + "\u0000")
        let contentBytes = Encoding.UTF8.GetBytes(canonicalText)
        let bytes = Array.zeroCreate<byte> (domainBytes.Length + contentBytes.Length)
        Buffer.BlockCopy(domainBytes, 0, bytes, 0, domainBytes.Length)
        Buffer.BlockCopy(contentBytes, 0, bytes, domainBytes.Length, contentBytes.Length)
        SHA256.HashData(bytes)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()
        |> fun value -> "sha256:" + value

[<RequireQualifiedAccess>]
module SnapshotFingerprint =
    // The domain is a frozen identifier, not a name: its `nayamnyam` spelling is part of every
    // reviewed fingerprint value and must not follow the project name.
    let private domain = "nayamnyam.snapshot-fingerprint.v6"

    let private computeCached: Snapshot -> Result<SnapshotFingerprint, SnapshotCodecError> =
        SnapshotComputationScope.Memoize("snapshotFingerprint", (fun snapshot ->
            SnapshotCodec.serialize snapshot
            |> Result.map (FingerprintHash.sha256 domain >> SnapshotFingerprint)), retain = true)

    let compute (snapshot: Snapshot): Result<SnapshotFingerprint, SnapshotCodecError> = computeCached snapshot

    let value (SnapshotFingerprint value) = value

[<RequireQualifiedAccess>]
module SemanticFingerprint =
    // The domain is a frozen identifier, not a name: its `nayamnyam` spelling is part of every
    // reviewed fingerprint value and must not follow the project name.
    let private domain = "nayamnyam.semantic-fingerprint.v6"
    let private displayPlaceholder = "_"

    let private stable<'Kind> text: StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> invalidOp $"Internal semantic-fingerprint stable ID is invalid: {error}."

    let private neutralSnapshotId: SnapshotId = stable<SnapshotTag> "semantic-fingerprint-root"
    let private neutralTransactionId: TransactionId = stable<TransactionTag> "semantic-fingerprint-transaction"

    let rec private normalizeTypeRef (typeRef: TypeRef): TypeRef =
        match typeRef with
        | TypeRef.Function(arguments, result) -> TypeRef.Function(List.map normalizeTypeRef arguments, normalizeTypeRef result)
        | TypeRef.TypeParameter(id, _) -> TypeRef.TypeParameter(id, displayPlaceholder)
        | TypeRef.BuiltinGeneric(generic, arguments) ->
            TypeRef.BuiltinGeneric(generic, arguments |> List.map normalizeTypeRef)
        | TypeRef.Named(typeId, arguments) -> TypeRef.Named(typeId, arguments |> List.map normalizeTypeRef)
        | TypeRef.Primitive _
        | TypeRef.Capability _ -> typeRef

    let private normalizeField (field: SemanticField) =
        { field with
            DisplayName = displayPlaceholder
            Type = normalizeTypeRef field.Type }

    let private normalizeVariant (variant: SemanticVariant) =
        { variant with
            DisplayName = displayPlaceholder
            Fields = variant.Fields |> List.map normalizeField }

    let private normalizeType (semanticType: SemanticType) =
        let kind =
            match semanticType.Kind with
            | SemanticTypeKind.Product fields ->
                SemanticTypeKind.Product(fields |> List.map normalizeField)
            | SemanticTypeKind.Union variants ->
                SemanticTypeKind.Union(variants |> List.map normalizeVariant)

        { semanticType with
            DisplayName = displayPlaceholder
            TypeParameters = semanticType.TypeParameters |> List.map (fun (id, _) -> id, displayPlaceholder)
            Kind = kind }

    let private normalizeEffect (effect: SemanticEffect) =
        { effect with DisplayName = displayPlaceholder }

    let private normalizeCapabilityParameter (parameter: SemanticCapabilityParameter) =
        { parameter with
            DisplayName = displayPlaceholder
            Type = normalizeTypeRef parameter.Type }

    let private normalizeCapabilityOperation (operation: SemanticCapabilityOperation) =
        { operation with
            DisplayName = displayPlaceholder
            Parameters = operation.Parameters |> List.map normalizeCapabilityParameter
            ResultType = normalizeTypeRef operation.ResultType
            FailureType = operation.FailureType |> Option.map normalizeTypeRef }

    let private normalizeCapability (capability: SemanticCapability) =
        { capability with
            DisplayName = displayPlaceholder
            Operations = capability.Operations |> List.map normalizeCapabilityOperation }

    let private normalizeParameter (parameter: SemanticParameter) =
        { parameter with
            DisplayName = displayPlaceholder
            Type = normalizeTypeRef parameter.Type }

    let private normalizePatternBinding (binding: PatternBinding) =
        { binding with
            DisplayName = None
            Type = normalizeTypeRef binding.Type }

    /// Type-parameter display names live in payload types and pattern bindings; the two shared
    /// shape functions enumerate both for every node kind.
    let private normalizeNode (node: SemanticNode) =
        node
        |> SemanticNodeShape.mapPayloadTypeRefs normalizeTypeRef
        |> SemanticNodeShape.mapNested normalizePatternBinding id

    let private normalizeFunction (functionEntity: SemanticFunction) =
        { functionEntity with
            DisplayName = displayPlaceholder
            Parameters = functionEntity.Parameters |> List.map normalizeParameter
            ResultType = normalizeTypeRef functionEntity.ResultType
            Body = functionEntity.Body |> ValueGraph.mapNodes normalizeNode }

    let private obligationKey (obligation: MissingArgumentObligation) =
        let statusKey =
            match obligation.Status with
            | ObligationStatus.Open -> "open"
            | ObligationStatus.Resolved resolution ->
                "resolved:" + StableId.value resolution.ValueNodeId
        StableId.value obligation.CallNodeId,
        StableId.value obligation.ParameterId,
        statusKey

    let private normalizeObligations (obligations: MissingArgumentObligation list) =
        obligations
        |> List.sortBy obligationKey
        |> List.mapi (fun index obligation ->
            let normalizedId: ObligationId =
                stable<ObligationTag> $"semantic-fingerprint-obligation-{index:D8}"
            let status =
                match obligation.Status with
                | ObligationStatus.Open -> ObligationStatus.Open
                | ObligationStatus.Resolved resolution ->
                    ObligationStatus.Resolved
                        { resolution with TransactionId = neutralTransactionId }
            { obligation with
                Id = normalizedId
                OriginTransactionId = neutralTransactionId
                Status = status })

    /// Computes a revision- and presentation-neutral fingerprint of semantic state.
    ///
    /// Snapshot revision identity, display metadata, obligation identity, and
    /// transaction provenance are normalized away. Stable semantic identities,
    /// type/value/effect/capability structure, blocking-obligation relationships,
    /// and obligation resolution values remain significant. This is still not an
    /// alpha-equivalence or behavioral-equivalence fingerprint: changing semantic
    /// stable identities changes this fingerprint.
    let private computeUncached (snapshot: Snapshot): Result<SemanticFingerprint, SnapshotCodecError> =
        let normalized =
            { Id = neutralSnapshotId
              ParentId = None
              Types = snapshot.Types |> List.map normalizeType
              Effects = snapshot.Effects |> List.map normalizeEffect
              Capabilities = snapshot.Capabilities |> List.map normalizeCapability
              Functions = snapshot.Functions |> List.map normalizeFunction
              Obligations = snapshot.Obligations |> normalizeObligations }

        SnapshotCodec.serialize normalized
        |> Result.map (FingerprintHash.sha256 domain >> SemanticFingerprint)

    let private computeCached = SnapshotComputationScope.Memoize("semanticFingerprint", computeUncached, retain = true)
    let compute (snapshot: Snapshot): Result<SemanticFingerprint, SnapshotCodecError> = computeCached snapshot

    let value (SemanticFingerprint value) = value
