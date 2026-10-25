namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
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

    let private id<'tag> name = StableId.createForTesting<'tag> ("fingerprint-" + name)

    let private listOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private optionOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])
    let private resultOf okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])

    let private nestedTypeRef typeParameter =
        resultOf (listOf (optionOf typeParameter)) (optionOf (listOf typeParameter))

    let private genericSnapshot (typeParameterId, displayName) parameterType resultType body =
        { Snapshot.empty (id<SnapshotTag> "generic-snapshot") with
            Functions =
                [ { Exported = true; Id = id<FunctionTag> "generic-function"
                    DisplayName = "generic"
                    TypeParameters = [ typeParameterId, displayName ]
                    Parameters =
                        [ { Id = id<ParameterTag> "value"
                            DisplayName = "value"
                            Type = parameterType } ]
                    ResultType = resultType
                    RequiredEffects = []
                    Body = body } ] }

    let private assertTypeParameterMetadata (createSnapshot: TypeParameterId -> string -> Snapshot) =
        let original = createSnapshot (id<TypeParameterTag> "type-parameter") "T"
        let renamed = createSnapshot (id<TypeParameterTag> "type-parameter") "Element"
        let reidentified = createSnapshot (id<TypeParameterTag> "other-type-parameter") "T"
        let originalSnapshotFingerprint = snapshotFingerprint original
        let originalSemanticFingerprint = semanticFingerprint original

        if originalSnapshotFingerprint = snapshotFingerprint renamed then
            failwith "Snapshot fingerprint ignored a type parameter display-name change."
        if originalSemanticFingerprint <> semanticFingerprint renamed then
            failwith "Semantic fingerprint changed for a type parameter display-name change."
        if originalSnapshotFingerprint = snapshotFingerprint reidentified then
            failwith "Snapshot fingerprint ignored a type parameter stable-ID change."
        if originalSemanticFingerprint = semanticFingerprint reidentified then
            failwith "Semantic fingerprint ignored a type parameter stable-ID change."

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``function signature type parameter names are presentation only`` (nested: bool) =
        let createSnapshot typeParameterId displayName =
            let typeParameter = TypeRef.TypeParameter typeParameterId
            let valueType = if nested then nestedTypeRef typeParameter else typeParameter
            let resultNodeId = id<GraphNodeTag> "identity-result"
            let body: ValueGraph =
                { Nodes =
                    [ { Id = resultNodeId
                        Kind = SemanticNodeKind.ParameterReference(id<ParameterTag> "value") } ]
                  ResultNodeId = resultNodeId }
            genericSnapshot (typeParameterId, displayName) valueType valueType body

        assertTypeParameterMetadata createSnapshot

    [<Theory>]
    [<InlineData("list-empty")>]
    [<InlineData("list-construct")>]
    [<InlineData("option-none")>]
    [<InlineData("result-ok")>]
    [<InlineData("result-err")>]
    let ``nested graph node type parameter names are presentation only`` (nodeKind: string) =
        let createSnapshot typeParameterId displayName =
            let itemType = TypeRef.TypeParameter typeParameterId |> nestedTypeRef
            let inputNodeId = id<GraphNodeTag> "input"
            let conditionNodeId = id<GraphNodeTag> "condition"
            let resultNodeId = id<GraphNodeTag> "conditional-result"
            let resultType, kind =
                match nodeKind with
                | "list-empty" -> listOf itemType, SemanticNodeKind.ListEmpty itemType
                | "list-construct" -> listOf itemType, SemanticNodeKind.ListConstruct(itemType, [ inputNodeId ])
                | "option-none" -> optionOf itemType, SemanticNodeKind.OptionNone itemType
                | "result-ok" -> resultOf itemType itemType, SemanticNodeKind.ResultOk(itemType, inputNodeId)
                | "result-err" -> resultOf itemType itemType, SemanticNodeKind.ResultErr(itemType, inputNodeId)
                | other -> failwithf "Unexpected test node kind: %s" other
            let branch name: ValueGraph =
                let nodeId = id<GraphNodeTag> name
                { Nodes = [ { Id = nodeId; Kind = kind } ]
                  ResultNodeId = nodeId }
            let body: ValueGraph =
                { Nodes =
                    [ { Id = inputNodeId
                        Kind = SemanticNodeKind.ParameterReference(id<ParameterTag> "value") }
                      { Id = conditionNodeId; Kind = SemanticNodeKind.BoolLiteral true }
                      { Id = resultNodeId
                        Kind = SemanticNodeKind.Conditional(conditionNodeId, branch "when-true", branch "when-false") } ]
                  ResultNodeId = resultNodeId }
            genericSnapshot (typeParameterId, displayName) itemType resultType body

        assertTypeParameterMetadata createSnapshot

    [<Theory>]
    [<InlineData("bind")>]
    [<InlineData("match-option")>]
    [<InlineData("match-result")>]
    let ``pattern binding type parameter names are presentation only`` (nodeKind: string) =
        let createSnapshot typeParameterId displayName =
            let itemType = TypeRef.TypeParameter typeParameterId |> nestedTypeRef
            let inputNodeId = id<GraphNodeTag> "input"
            let resultNodeId = id<GraphNodeTag> "match-result"
            let binding name: PatternBinding =
                { Id = id<PatternBindingTag> name
                  DisplayName = Some name
                  Type = itemType }
            let bindingBody name (boundValue: PatternBinding): ValueGraph =
                let valueNodeId = id<GraphNodeTag> (name + "-value")
                let listNodeId = id<GraphNodeTag> (name + "-list")
                { Nodes =
                    [ { Id = valueNodeId; Kind = SemanticNodeKind.PatternReference boundValue.Id }
                      { Id = listNodeId; Kind = SemanticNodeKind.ListConstruct(itemType, [ valueNodeId ]) } ]
                  ResultNodeId = listNodeId }
            let valueArm name: ValueMatchArm =
                let boundValue = binding name
                { ValueBinding = boundValue; Body = bindingBody name boundValue }
            let parameterType, kind =
                match nodeKind with
                | "bind" -> itemType, SemanticNodeKind.Bind(inputNodeId, valueArm "bound")
                | "match-option" ->
                    let noneNodeId = id<GraphNodeTag> "none"
                    let noneBody: ValueGraph =
                        { Nodes = [ { Id = noneNodeId; Kind = SemanticNodeKind.ListEmpty itemType } ]
                          ResultNodeId = noneNodeId }
                    optionOf itemType, SemanticNodeKind.MatchOption(inputNodeId, valueArm "some", noneBody)
                | "match-result" ->
                    let errorBinding = binding "error"
                    let errorArm = { ErrorBinding = errorBinding; Body = bindingBody "error" errorBinding }
                    resultOf itemType itemType, SemanticNodeKind.MatchResult(inputNodeId, valueArm "ok", errorArm)
                | other -> failwithf "Unexpected test node kind: %s" other
            let body: ValueGraph =
                { Nodes =
                    [ { Id = inputNodeId
                        Kind = SemanticNodeKind.ParameterReference(id<ParameterTag> "value") }
                      { Id = resultNodeId; Kind = kind } ]
                  ResultNodeId = resultNodeId }
            genericSnapshot (typeParameterId, displayName) parameterType (listOf itemType) body

        assertTypeParameterMetadata createSnapshot

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

    [<Theory>]
    [<InlineData("empty", "sha256:c0041b103b92b69915c21878b53763996e26b17759443957d909466c298cd7b3", "sha256:4d0eb753fcd58a541a10f71e6176b595df149673906e8e73186d0075d7d6cf62")>]
    [<InlineData("milestone", "sha256:16d40073c7312051310deb367266f189e5000ea89bf7ea3f01be685996314969", "sha256:4f0135d81e18f50eba891b4dd933178e43fbea69227763bd64818d1618b8f335")>]
    [<InlineData("unicode", "sha256:e5b2ac9e36c1c8ea578ba8c7eb6291595bc3f433bce77aaea3fcc8d1e323c405", "sha256:4f0135d81e18f50eba891b4dd933178e43fbea69227763bd64818d1618b8f335")>]
    let ``fingerprint bytes retain pre-optimization golden values`` kind expectedSnapshot expectedSemantic =
        let snapshot =
            match kind with
            | "empty" -> Snapshot.empty (StableId.createForTesting<SnapshotTag> "fingerprint-frozen-empty")
            | "milestone" -> MilestoneZeroGraph.create().Snapshot
            | "unicode" ->
                let fixture = MilestoneZeroGraph.create().Snapshot
                { fixture with Functions = fixture.Functions |> List.mapi (fun i f -> { f with DisplayName = $"鴎_🎵_e\u0301_{i}" }) }
            | _ -> failwith "unknown golden fixture"
        Assert.Equal(expectedSnapshot, snapshotFingerprint snapshot |> SnapshotFingerprint.value)
        Assert.Equal(expectedSemantic, semanticFingerprint snapshot |> SemanticFingerprint.value)

    [<Fact>]
    let ``normalization retains existing handling of invalid presentation and parent metadata`` () =
        let snapshot = MilestoneZeroGraph.create().Snapshot
        let invalid =
            { snapshot with
                ParentId = Some snapshot.Id
                Functions = snapshot.Functions |> List.map (fun f -> { f with DisplayName = "" }) }
        Assert.NotEmpty(SnapshotValidator.validate invalid)
        Assert.Equal(semanticFingerprint snapshot, semanticFingerprint invalid)

    [<Fact>]
    let ``semantic fingerprint still rejects invalid normalized structure`` () =
        let snapshot = MilestoneZeroGraph.create().Snapshot
        let invalid =
            { snapshot with
                Functions = snapshot.Functions |> List.mapi (fun index f ->
                    if index = 0 then
                        { f with ResultType = TypeRef.Primitive PrimitiveType.Unit }
                    else f) }
        match SemanticFingerprint.compute invalid with
        | Error(SnapshotCodecError.InvalidSnapshot issues) ->
            Assert.Contains(issues, fun issue -> issue.Code = "FunctionResultTypeMismatch")
        | other -> failwithf "Expected invalid normalized structure, got %A" other
