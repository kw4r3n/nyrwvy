namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Declared function type parameters (snapshot schema 21): a function lists its type parameters in
/// order, occurrences carry no display name, older documents derive the list from the parameter
/// types in first-appearance order and keep their bytes and fingerprints.
module DeclaredFunctionTypeParametersTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("declared-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private tId, uId = id<TypeParameterTag> "T", id<TypeParameterTag> "U"
    let private t, u = TypeRef.TypeParameter tId, TypeRef.TypeParameter uId
    let private firstId, secondId = id<ParameterTag> "first", id<ParameterTag> "second"

    /// `second(first: List<T>, second: U): U = second`, with the type parameters in the given order.
    let private second (typeParameters: (TypeParameterId * string) list): SemanticFunction =
        let resultNodeId = id<GraphNodeTag> "second-result"
        { MustNotWait = false; Id = id<FunctionTag> "second"
          Exported = true
          DisplayName = "second"
          TypeParameters = typeParameters
          Parameters =
            [ { Id = firstId; DisplayName = "first"; Type = listOf t }
              { Id = secondId; DisplayName = "second"; Type = u } ]
          ResultType = u
          RequiredEffects = []
          Body = { Nodes = [ { Id = resultNodeId; Kind = SemanticNodeKind.ParameterReference secondId } ]; ResultNodeId = resultNodeId } }

    let private snapshotOf (fn: SemanticFunction) = { Snapshot.empty (id<SnapshotTag> "snapshot") with Functions = [ fn ] }

    [<Fact>]
    let ``a derived list is written in the pre-21 canonical form, and the current form reads back to the same snapshot`` () =
        let derived = snapshotOf (second [ tId, "T"; uId, "U" ])
        Assert.Empty(SnapshotValidator.validate derived)
        Assert.Equal(SnapshotSchema.baseline, SnapshotSchema.required derived)
        let json = SnapshotCodec.serializeCanonical derived |> unwrap "json"
        // Occurrences carry their names and the function lists none, exactly as before schema 21.
        Assert.Contains("\"kind\":\"typeParameter\",\"typeParameterId\":\"declared-T\",\"displayName\":\"T\"", json)
        Assert.DoesNotContain("\"typeParameters\"", json)
        let restored = SnapshotCodec.deserialize (SnapshotCodec.serialize derived |> unwrap "current") |> unwrap "restore"
        Assert.Equal(derived, restored)
        Assert.Equal(json, SnapshotCodec.serializeCanonical restored |> unwrap "reserialize")

    [<Fact>]
    let ``a schema 21 document may not spell a name on a type parameter occurrence`` () =
        let json =
            """{"schemaVersion":21,"snapshotId":"declared-legacy","types":[],"effects":[],"capabilities":[],"functions":[{"id":"declared-second","displayName":"second","parameters":[{"id":"declared-first","displayName":"first","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","typeParameterId":"declared-T","displayName":"Item"}]}},{"id":"declared-second","displayName":"second","type":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"Marker"}}],"resultType":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"Marker"},"requiredEffects":[],"body":{"nodes":[{"id":"declared-second-result","kind":"parameterReference","parameterId":"declared-second"}],"resultNodeId":"declared-second-result"}}],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("displayName", message)
        | other -> failwithf "Schema 21 accepted a named occurrence: %A" other

    [<Fact>]
    let ``a declared order other than first appearance needs schema 21 and is written without occurrence names`` () =
        let reordered = snapshotOf (second [ uId, "U"; tId, "T" ])
        Assert.Empty(SnapshotValidator.validate reordered)
        Assert.Equal(SnapshotSchema.declaredFunctionTypeParameters, SnapshotSchema.required reordered)
        let json = SnapshotCodec.serialize reordered |> unwrap "json"
        Assert.Contains("\"typeParameters\":[{\"typeParameterId\":\"declared-U\",\"displayName\":\"U\"},{\"typeParameterId\":\"declared-T\",\"displayName\":\"T\"}]", json)
        Assert.Contains("{\"kind\":\"typeParameter\",\"typeParameterId\":\"declared-T\"}", json)
        Assert.DoesNotContain("\"displayName\":\"T\"}}", json)
        let restored = SnapshotCodec.deserialize json |> unwrap "restore"
        Assert.Equal(reordered, restored)
        Assert.Equal(reordered, SnapshotCodec.deserialize json |> unwrap "restore")
        // The order is a semantic fact of its own: the two orders fingerprint differently.
        Assert.NotEqual(SemanticFingerprint.compute reordered, SemanticFingerprint.compute (snapshotOf (second [ tId, "T"; uId, "U" ])))

    [<Fact>]
    let ``the validator requires every declared type parameter to be mentioned by a parameter type and every mention to be declared`` () =
        let unused = snapshotOf { second [ tId, "T"; uId, "U"; id<TypeParameterTag> "V", "V" ] with ResultType = u }
        Assert.Contains(SnapshotValidator.validate unused, fun issue -> issue.Code = "TypeParameterNotInferable" && issue.Message.Contains "'V'")
        let undeclared = snapshotOf (second [ tId, "T" ])
        Assert.Contains(SnapshotValidator.validate undeclared, fun issue -> issue.Code = "TypeParameterNotDeclared" && issue.Message.Contains "'declared-U'")
        let duplicateName = snapshotOf (second [ tId, "T"; uId, "T" ])
        Assert.Contains(SnapshotValidator.validate duplicateName, fun issue -> issue.Code = "DuplicateTypeParameterName")
        let shared =
            { snapshotOf (second [ tId, "T"; uId, "U" ]) with
                Types = [ { Exported = true; Id = id<TypeTag> "box"; DisplayName = "Box"; TypeParameters = [ tId, "T" ]; Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "content"; DisplayName = "content"; Type = t } ] } ] }
        Assert.Contains(SnapshotValidator.validate shared, fun issue -> issue.Code = "TypeParameterSharedAcrossFunctions")

    [<Fact>]
    let ``the C# backend orders a generic method's type parameters as declared`` () =
        let emit (fn: SemanticFunction) =
            let typed = TypedSemanticIr.project (snapshotOf fn) |> unwrap "typed"
            (CSharpBackend.emit (ExecutionIr.lower typed |> unwrap "lower") CSharpBackendProfile.empty |> unwrap "emit").Source
        let name = CSharpBackendNames.functionName (id<FunctionTag> "second")
        let tName, uName = CSharpBackendNames.typeParameterName tId, CSharpBackendNames.typeParameterName uId
        Assert.Contains(name + "<" + tName + ", " + uName + ">", emit (second [ tId, "T"; uId, "U" ]))
        Assert.Contains(name + "<" + uName + ", " + tName + ">", emit (second [ uId, "U"; tId, "T" ]))

    let private creation (typeParameters: string) =
        let declared = if typeParameters = "" then "" else $"\"typeParameters\":{typeParameters},"
        $"""{{"id":"create","op":"simulateCreateFunctionByName","displayName":"second",{declared}"parameters":[{{"displayName":"first","type":{{"kind":"builtinGeneric","generic":"list","arguments":[{{"kind":"typeParameter","name":"T"}}]}}}},{{"displayName":"second","type":{{"kind":"typeParameter","name":"U"}}}}],"resultType":{{"kind":"typeParameter","name":"U"}},"requiredEffects":[],"body":{{"kind":"parameter","name":"second"}}}}"""

    [<Fact>]
    let ``a snapshot using both schema-21 constructs round-trips in both codecs and the legacy canonical bytes stay unchanged`` () =
        // Declared order (U, T) and a private helper beside an exported generic function: both
        // `DeclaredFunctionTypeParameters` and `DeclarationVisibility` are in use.
        let exportedReordered = second [ uId, "U"; tId, "T" ]
        let helperResult = id<GraphNodeTag> "helper-result"
        let privateHelper: SemanticFunction =
            { MustNotWait = false; Exported = false
              Id = id<FunctionTag> "helper"
              DisplayName = "helper"
              TypeParameters = []
              Parameters = []
              ResultType = intType
              RequiredEffects = []
              Body = { Nodes = [ { Id = helperResult; Kind = SemanticNodeKind.Int64Literal 1L } ]; ResultNodeId = helperResult } }
        let mixed = { Snapshot.empty (id<SnapshotTag> "mixed") with Functions = [ privateHelper; exportedReordered ] }
        Assert.Empty(SnapshotValidator.validate mixed)
        Assert.Equal<Set<SnapshotSchemaFeature>>(
            Set.ofList [ SnapshotSchemaFeature.DeclaredFunctionTypeParameters; SnapshotSchemaFeature.DeclarationVisibility ],
            SnapshotSchema.usedFeatures mixed)
        Assert.Equal(21, (SnapshotSchema.required mixed).Json)
        let json = SnapshotCodec.serializeCanonical mixed |> unwrap "json"
        Assert.Contains("\"schemaVersion\":21", json)
        Assert.Contains("\"exported\":true", json)
        Assert.Contains("\"typeParameters\":[{\"typeParameterId\":\"declared-U\"", json)
        Assert.DoesNotContain("\"displayName\":\"T\"}}", json)
        Assert.Equal(mixed, SnapshotCodec.deserialize json |> unwrap "json restore")
        // The frozen canonical form of a derived list is still the pre-21 legacy bytes (no list, no
        // visibility, names on occurrences), so both fingerprints are unchanged.
        let legacy =
            """{"schemaVersion":9,"snapshotId":"declared-legacy","types":[],"effects":[],"capabilities":[],"functions":[{"id":"declared-second","displayName":"second","parameters":[{"id":"declared-first","displayName":"first","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","typeParameterId":"declared-T","displayName":"T"}]}},{"id":"declared-second","displayName":"second","type":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"U"}}],"resultType":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"U"},"requiredEffects":[],"body":{"nodes":[{"id":"declared-second-result","kind":"parameterReference","parameterId":"declared-second"}],"resultNodeId":"declared-second-result"}}],"obligations":[]}"""
        let derivedTwin = { snapshotOf (second [ tId, "T"; uId, "U" ]) with Id = id<SnapshotTag> "legacy" }
        Assert.Equal(legacy, SnapshotCodec.serializeCanonical derivedTwin |> unwrap "legacy bytes")
