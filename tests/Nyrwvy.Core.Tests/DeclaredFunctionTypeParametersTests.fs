namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
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
    let ``a derived list is written in the pre-21 form, which reads back to the same snapshot and bytes`` () =
        let derived = snapshotOf (second [ tId, "T"; uId, "U" ])
        Assert.Empty(SnapshotValidator.validate derived)
        Assert.Equal(SnapshotSchema.baseline, SnapshotSchema.required derived)
        let json = SnapshotCodec.serializeCanonical derived |> unwrap "json"
        // Occurrences carry their names and the function lists none, exactly as before schema 21.
        Assert.Contains("\"kind\":\"typeParameter\",\"typeParameterId\":\"declared-T\",\"displayName\":\"T\"", json)
        Assert.DoesNotContain("\"typeParameters\"", json)
        let restored = SnapshotCodec.deserialize json |> unwrap "restore"
        Assert.Equal(derived, restored)
        Assert.Equal<(TypeParameterId * string) list>([ tId, "T"; uId, "U" ], restored.Functions.Head.TypeParameters)
        Assert.Equal(json, SnapshotCodec.serializeCanonical restored |> unwrap "reserialize")
        Assert.Equal(SnapshotFingerprint.compute derived, SnapshotFingerprint.compute restored)
        Assert.Equal(SemanticFingerprint.compute derived, SemanticFingerprint.compute restored)
        Assert.Equal(derived, SnapshotCodec.deserialize json |> unwrap "restore")

    [<Fact>]
    let ``a legacy document with names on occurrences derives the list in first-appearance order`` () =
        // Hand-written schema 9 document: the names live on the occurrences, U before T in the body
        // but T first in the parameter types, so the derived list is (T, U).
        let json =
            """{"schemaVersion":9,"snapshotId":"declared-legacy","types":[],"effects":[],"capabilities":[],"functions":[{"id":"declared-second","displayName":"second","parameters":[{"id":"declared-first","displayName":"first","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","typeParameterId":"declared-T","displayName":"Item"}]}},{"id":"declared-second","displayName":"second","type":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"Marker"}}],"resultType":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"Marker"},"requiredEffects":[],"body":{"nodes":[{"id":"declared-second-result","kind":"parameterReference","parameterId":"declared-second"}],"resultNodeId":"declared-second-result"}}],"obligations":[]}"""
        let restored = SnapshotCodec.deserialize json |> unwrap "legacy"
        Assert.Equal<(TypeParameterId * string) list>([ tId, "Item"; uId, "Marker" ], restored.Functions.Head.TypeParameters)
        Assert.Equal(json, SnapshotCodec.serializeCanonical restored |> unwrap "reserialize")
        // Declaring the same list explicitly is the same semantic state.
        let explicitTwin = snapshotOf { second [ tId, "Item"; uId, "Marker" ] with Id = id<FunctionTag> "second" }
        Assert.Equal(SemanticFingerprint.compute explicitTwin, SemanticFingerprint.compute restored)
        // A document at schema 21 may not spell a name on an occurrence.
        let relabelled = json.Replace("\"schemaVersion\":9", "\"schemaVersion\":21")
        match SnapshotCodec.deserialize relabelled with
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

    let private withWireSession test =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-declared-type-parameters-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap "create session"
            test session path
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private execute (session: AuthoringSession) (line: string) =
        use document = JsonDocument.Parse(AuthoringProtocol.executeLine session line)
        document.RootElement.Clone()

    let private publish session line =
        let proposed = execute session line
        Assert.True(proposed.GetProperty("ok").GetBoolean(), proposed.ToString())
        session.Commit(proposed.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private creation (typeParameters: string) =
        let declared = if typeParameters = "" then "" else $"\"typeParameters\":{typeParameters},"
        $"""{{"id":"create","op":"simulateCreateFunctionByName","displayName":"second",{declared}"parameters":[{{"displayName":"first","type":{{"kind":"builtinGeneric","generic":"list","arguments":[{{"kind":"typeParameter","name":"T"}}]}}}},{{"displayName":"second","type":{{"kind":"typeParameter","name":"U"}}}}],"resultType":{{"kind":"typeParameter","name":"U"}},"requiredEffects":[],"body":{{"kind":"parameter","name":"second"}}}}"""

    let private declaredNames (session: AuthoringSession) =
        let declarations = execute session """{"id":"declarations","op":"declarations"}"""
        let fn = declarations.GetProperty("result").GetProperty("functions").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("displayName").GetString() = "second")
        fn.GetProperty("typeParameters").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("displayName").GetString()) |> List.ofSeq

    [<Fact>]
    let ``wire creation declares the listed type parameters in order, or derives them from the parameter types`` () =
        withWireSession (fun session _ ->
            publish session (creation "")
            Assert.Equal<string list>([ "T"; "U" ], declaredNames session)
            let fn = session.Declarations().Functions |> List.exactlyOne
            Assert.True(TypeParameters.isDerived fn)
            // Wire output names each occurrence from the owner's list and shows the list on readFunction.
            let read = execute session """{"id":"read","op":"readFunction","function":"second"}"""
            let shown = read.GetProperty("result").GetProperty("function")
            Assert.Equal<string list>([ "T"; "U" ], shown.GetProperty("typeParameters").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("displayName").GetString()) |> List.ofSeq)
            Assert.Equal("U", shown.GetProperty("resultType").GetProperty("displayName").GetString())
            Assert.Equal(StableId.value (fst fn.TypeParameters.[1]), shown.GetProperty("resultType").GetProperty("typeParameterId").GetString()))
        withWireSession (fun session path ->
            publish session (creation """["U","T"]""")
            Assert.Equal<string list>([ "U"; "T" ], declaredNames session)
            let fn = session.Declarations().Functions |> List.exactlyOne
            Assert.False(TypeParameters.isDerived fn)
            session.Close()
            let stored = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "stored"
            Assert.Equal(SnapshotSchema.declaredFunctionTypeParameters, SnapshotSchema.required stored)
            Assert.Equal<(TypeParameterId * string) list>(fn.TypeParameters, stored.Functions.Head.TypeParameters)
            // Fresh system-owned identities, not the request-local names.
            Assert.All(fn.TypeParameters, fun (id, name) -> Assert.NotEqual<string>(name, StableId.value id)))

    [<Fact>]
    let ``wire creation rejects a list that misses a mentioned type parameter or lists an uninferable one`` () =
        withWireSession (fun session _ ->
            let reject typeParameters =
                let response = execute session (creation typeParameters)
                Assert.False(response.GetProperty("ok").GetBoolean(), response.ToString())
                response.GetProperty("error").GetProperty("details").GetProperty("diagnostics").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("code").GetString() + ": " + item.GetProperty("message").GetString())
                |> String.concat "\n"
            Assert.Contains("FunctionCreationTypeParameterNotDeclared", reject """["T"]""")
            Assert.Contains("'U'", reject """["T"]""")
            Assert.Contains("FunctionCreationTypeParameterNotInferable", reject """["T","U","V"]""")
            Assert.Contains("'V'", reject """["T","U","V"]""")
            Assert.Contains("FunctionCreationDuplicateTypeParameter", reject """["T","U","T"]""")
            Assert.Empty(session.Declarations().Functions))

    [<Fact>]
    let ``a snapshot using both schema-21 constructs round-trips in both codecs and legacy fingerprints stay unchanged`` () =
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
        // A legacy document (names on occurrences, no list, no visibility) still reads as the same
        // all-exported snapshot and re-serializes to its own bytes, so both fingerprints are unchanged.
        let legacy =
            """{"schemaVersion":9,"snapshotId":"declared-legacy","types":[],"effects":[],"capabilities":[],"functions":[{"id":"declared-second","displayName":"second","parameters":[{"id":"declared-first","displayName":"first","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","typeParameterId":"declared-T","displayName":"T"}]}},{"id":"declared-second","displayName":"second","type":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"U"}}],"resultType":{"kind":"typeParameter","typeParameterId":"declared-U","displayName":"U"},"requiredEffects":[],"body":{"nodes":[{"id":"declared-second-result","kind":"parameterReference","parameterId":"declared-second"}],"resultNodeId":"declared-second-result"}}],"obligations":[]}"""
        let restored = SnapshotCodec.deserialize legacy |> unwrap "legacy"
        Assert.True(restored.Functions.Head.Exported)
        Assert.Equal<(TypeParameterId * string) list>([ tId, "T"; uId, "U" ], restored.Functions.Head.TypeParameters)
        Assert.Equal(legacy, SnapshotCodec.serializeCanonical restored |> unwrap "legacy reserialize")
        let derivedTwin = { snapshotOf (second [ tId, "T"; uId, "U" ]) with Id = id<SnapshotTag> "legacy" }
        Assert.Equal(SnapshotFingerprint.compute restored, SnapshotFingerprint.compute derivedTwin)
        Assert.Equal(SemanticFingerprint.compute restored, SemanticFingerprint.compute derivedTwin)
