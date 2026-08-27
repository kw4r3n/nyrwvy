namespace NyamNyam.Core.Tests

open System
open NyamNyam.Core
open Xunit

module FingerprintTests =
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private snapshotFingerprint snapshot =
        SnapshotFingerprint.compute snapshot |> unwrap "snapshot fingerprint"

    let private semanticFingerprint snapshot =
        SemanticFingerprint.compute snapshot |> unwrap "semantic fingerprint"

    [<Fact>]
    let ``snapshot fingerprint is canonical across nonsemantic collection reordering`` () =
        let fixture = MilestoneZeroGraph.create ()
        let reordered =
            { fixture.Snapshot with
                Types = List.rev fixture.Snapshot.Types
                Effects = List.rev fixture.Snapshot.Effects
                Capabilities = List.rev fixture.Snapshot.Capabilities
                Functions = List.rev fixture.Snapshot.Functions
                Obligations = List.rev fixture.Snapshot.Obligations }

        if snapshotFingerprint fixture.Snapshot <> snapshotFingerprint reordered then
            failwith "Snapshot fingerprint changed under nonsemantic collection reordering."

    [<Fact>]
    let ``snapshot fingerprint includes revision identity while semantic fingerprint excludes it`` () =
        let fixture = MilestoneZeroGraph.create ()
        let anotherRevision =
            { fixture.Snapshot with
                Id = StableIdAllocator.newSnapshotId ()
                ParentId = Some fixture.Snapshot.Id }

        if snapshotFingerprint fixture.Snapshot = snapshotFingerprint anotherRevision then
            failwith "Snapshot fingerprint ignored revision identity."

        if semanticFingerprint fixture.Snapshot <> semanticFingerprint anotherRevision then
            failwith "Semantic fingerprint changed when only snapshot revision identity changed."

    [<Fact>]
    let ``semantic fingerprint ignores display metadata while snapshot fingerprint preserves it`` () =
        let fixture = MilestoneZeroGraph.create ()
        let renamedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = fixture.ValidateFunctionId then
                    { functionEntity with DisplayName = "validate_profile" }
                else
                    functionEntity)
        let renamed = { fixture.Snapshot with Functions = renamedFunctions }

        if semanticFingerprint fixture.Snapshot <> semanticFingerprint renamed then
            failwith "Semantic fingerprint changed for presentation-only display metadata."
        if snapshotFingerprint fixture.Snapshot = snapshotFingerprint renamed then
            failwith "Snapshot fingerprint failed to preserve exact display metadata."

    [<Fact>]
    let ``semantic fingerprint changes when semantic structure changes`` () =
        let fixture = MilestoneZeroGraph.create ()
        let addedField: SemanticField =
            { Id = StableId.createForTesting<FieldTag> "field-fingerprint-policy-mode"
              DisplayName = "mode"
              Type = TypeRef.Primitive PrimitiveType.String }
        let changedTypes =
            fixture.Snapshot.Types
            |> List.map (fun semanticType ->
                if semanticType.Id = fixture.PolicyTypeId then
                    { semanticType with Kind = SemanticTypeKind.Product [ addedField ] }
                else
                    semanticType)
        let changed = { fixture.Snapshot with Types = changedTypes }

        if semanticFingerprint fixture.Snapshot = semanticFingerprint changed then
            failwith "Semantic fingerprint ignored a structural type change."

    [<Fact>]
    let ``fingerprints use explicit versioned SHA-256 text`` () =
        let fixture = MilestoneZeroGraph.create ()
        let values =
            [ SnapshotFingerprint.value (snapshotFingerprint fixture.Snapshot)
              SemanticFingerprint.value (semanticFingerprint fixture.Snapshot) ]

        for value in values do
            if not (value.StartsWith("sha256:", StringComparison.Ordinal)) then
                failwithf "Fingerprint does not expose the SHA-256 algorithm: %s" value
            if value.Length <> 71 then
                failwithf "Fingerprint does not contain exactly 32 hash bytes: %s" value
            let hex = value.Substring("sha256:".Length)
            if hex |> Seq.exists (fun c -> not (Uri.IsHexDigit c)) then
                failwithf "Fingerprint contains non-hexadecimal characters: %s" value

    [<Fact>]
    let ``canonical snapshot round-trip preserves both fingerprints`` () =
        let fixture = MilestoneZeroGraph.create ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap "snapshot serialization"
        let decoded = SnapshotCodec.deserialize json |> unwrap "snapshot deserialization"

        if snapshotFingerprint fixture.Snapshot <> snapshotFingerprint decoded then
            failwith "Snapshot fingerprint changed across canonical persistence round-trip."
        if semanticFingerprint fixture.Snapshot <> semanticFingerprint decoded then
            failwith "Semantic fingerprint changed across canonical persistence round-trip."
