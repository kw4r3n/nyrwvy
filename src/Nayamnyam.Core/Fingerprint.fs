namespace Nayamnyam.Core

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
    let private domain = "nayamnyam.snapshot-fingerprint.v1"

    let compute (snapshot: Snapshot): Result<SnapshotFingerprint, SnapshotCodecError> =
        SnapshotCodec.serialize snapshot
        |> Result.map (FingerprintHash.sha256 domain >> SnapshotFingerprint)

    let value (SnapshotFingerprint value) = value

[<RequireQualifiedAccess>]
module SemanticFingerprint =
    let private domain = "nayamnyam.semantic-fingerprint.v1"

    let private neutralSnapshotId: SnapshotId =
        match StableId.tryCreate<SnapshotTag> "semantic-fingerprint-root" with
        | Ok value -> value
        | Error error -> invalidOp $"Internal semantic-fingerprint root ID is invalid: {error}."

    /// Computes a revision-neutral fingerprint of semantic snapshot state.
    ///
    /// Snapshot identity and parent revision identity are intentionally removed.
    /// Stable semantic identities, obligations, and transaction provenance carried
    /// by those obligations remain part of the state. This is therefore not an
    /// alpha-equivalence or behavioral-equivalence fingerprint.
    let compute (snapshot: Snapshot): Result<SemanticFingerprint, SnapshotCodecError> =
        let revisionNeutral =
            { snapshot with
                Id = neutralSnapshotId
                ParentId = None }

        SnapshotCodec.serialize revisionNeutral
        |> Result.map (FingerprintHash.sha256 domain >> SemanticFingerprint)

    let value (SemanticFingerprint value) = value
