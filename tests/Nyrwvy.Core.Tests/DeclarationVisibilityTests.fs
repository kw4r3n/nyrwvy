namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Declaration visibility (docs/decisions/declaration-visibility.md): private by default, explicit
/// at snapshot schema 21, every declaration of an older document exported, and a
/// dependency's private declarations hidden from its dependents.
module DeclarationVisibilityTests =
    let private unwrap label = function Ok value -> value | Error error -> failwithf "%s: %A" label error
    let private id<'tag> value = StableId.createForTesting<'tag> value
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private int64Json = """{"kind":"primitive","primitive":"int64"}"""

    let private execute (session: AuthoringSession) (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore
        proposal

    /// The error code of a rejected request, or of the diagnostic that rejected a simulation, with the whole reply.
    let private rejection session line =
        use reply = execute session line
        let root = reply.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.GetRawText())
        let error = root.GetProperty("error")
        let mutable details = Unchecked.defaultof<JsonElement>
        let mutable diagnostics = Unchecked.defaultof<JsonElement>
        let source =
            if error.TryGetProperty("details", &details) && details.ValueKind = JsonValueKind.Object
               && details.TryGetProperty("diagnostics", &diagnostics) && diagnostics.GetArrayLength() > 0 then
                diagnostics[0]
            else error
        source.GetProperty("code").GetString(), root.GetRawText()

    let private withDirectory run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-visibility-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try run root
        finally Directory.Delete(root, true)

    let private functionJson name exported (parameters: (string * string) list) resultType body =
        let signature = parameters |> List.map (fun (displayName, typeRef) -> $"""{{"displayName":"{displayName}","type":{typeRef}}}""") |> String.concat ","
        let visibility = if exported then """"exported":true,""" else ""
        $"""{{"id":"{name}","op":"simulateCreateFunctionByName",{visibility}"displayName":"{name}","parameters":[{signature}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private call name (arguments: (string * string) list) =
        let bindings = arguments |> List.map (fun (parameter, value) -> $"""{{"parameter":"{parameter}","value":{value}}}""") |> String.concat ","
        $"""{{"kind":"directCall","function":"{name}","arguments":[{bindings}]}}"""

    /// A library with an exported `shared(value)` that calls a private `helper(value)`, a private
    /// type `Secret`, an exported type `Public`, a private effect and a private capability.
    let private writeLibrary path =
        use library = AuthoringSession.createNew path |> unwrap "create library"
        publish library (functionJson "helper" false [ "value", int64Json ] int64Json (parameter "value")) |> ignore
        publish library (functionJson "shared" true [ "value", int64Json ] int64Json (call "helper" [ "value", parameter "value" ])) |> ignore
        publish library """{"id":"secret","op":"simulateCreateTypeByName","displayName":"Secret","typeKind":{"kind":"product","fields":[]}}""" |> ignore
        publish library """{"id":"public","op":"simulateCreateTypeByName","exported":true,"displayName":"Public","typeKind":{"kind":"product","fields":[]}}""" |> ignore
        publish library """{"id":"io","op":"simulateCreateEffect","displayName":"io"}""" |> ignore
        publish library $"""{{"id":"host","op":"simulateCreateCapabilityByName","displayName":"Host","operations":[{{"displayName":"ping","parameters":[],"resultType":{int64Json},"effect":"io"}}]}}""" |> ignore
        library.Close()

    let private declarationNames (element: JsonElement) (collection: string) =
        element.GetProperty(collection).EnumerateArray() |> Seq.map (fun item -> item.GetProperty("displayName").GetString()) |> List.ofSeq |> List.sort

    let private exportedOf (element: JsonElement) (collection: string) name =
        element.GetProperty(collection).EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("displayName").GetString() = name)
        |> fun item -> item.GetProperty("exported").GetBoolean()

    // ---- Codecs and schema -------------------------------------------------------------------

    let private declarations exported: Snapshot =
        let effectId = id<EffectTag> "effect-io"
        { Snapshot.empty (id<SnapshotTag> "snapshot-visibility") with
            Types = [ { Id = id<TypeTag> "type-box"; DisplayName = "Box"; Exported = exported; TypeParameters = []; Kind = SemanticTypeKind.Product [] } ]
            Effects = [ { Id = effectId; DisplayName = "io"; Exported = exported } ]
            Capabilities =
                [ { Id = id<CapabilityTag> "capability-host"; DisplayName = "Host"; Exported = exported
                    Operations = [ { Id = id<CapabilityOperationTag> "operation-ping"; DisplayName = "ping"; Parameters = []; ResultType = int64Type; FailureType = None; EffectId = effectId } ] } ]
            Functions =
                [ { Id = id<FunctionTag> "function-identity"; DisplayName = "identity"; Exported = exported
                    TypeParameters = []
                    Parameters = [ { Id = id<ParameterTag> "parameter-value"; DisplayName = "value"; Type = int64Type } ]
                    ResultType = int64Type
                    RequiredEffects = []
                    Body = { Nodes = [ { Id = id<GraphNodeTag> "node-value"; Kind = SemanticNodeKind.ParameterReference(id<ParameterTag> "parameter-value") } ]; ResultNodeId = id<GraphNodeTag> "node-value" } } ] }

    [<Fact>]
    let ``an all-exported snapshot keeps the baseline bytes and a private declaration needs schema 21`` () =
        let legacy = declarations true
        let json = SnapshotCodec.serializeCanonical legacy |> unwrap "legacy json"
        Assert.Contains($"\"schemaVersion\":{SnapshotSchema.baseline.Json}", json)
        Assert.DoesNotContain("exported", json)
        Assert.Empty(SnapshotSchema.usedFeatures legacy)

        let mixed = { legacy with Functions = legacy.Functions |> List.map (fun fn -> { fn with Exported = false }) }
        Assert.Equal<Set<SnapshotSchemaFeature>>(Set.singleton SnapshotSchemaFeature.DeclarationVisibility, SnapshotSchema.usedFeatures mixed)
        let minimum = SnapshotSchema.minimumVersion SnapshotSchemaFeature.DeclarationVisibility
        Assert.Equal(21, minimum.Json)
        Assert.Equal("nayamnyam.snapshot.v17", minimum.Nyason)
        let mixedJson = SnapshotCodec.serializeCanonical mixed |> unwrap "mixed json"
        Assert.Contains("\"schemaVersion\":21", mixedJson)
        use document = JsonDocument.Parse mixedJson
        let root = document.RootElement
        // The exported declarations carry the marker; the private function omits it.
        for collection in [ "types"; "effects"; "capabilities" ] do
            Assert.True(root.GetProperty(collection).[0].GetProperty("exported").GetBoolean(), collection)
        Assert.False(root.GetProperty("functions").[0].TryGetProperty("exported") |> fst)
        let restored = SnapshotCodec.deserialize mixedJson |> unwrap "mixed round trip"
        Assert.Equal(mixed, restored)
        Assert.Equal(mixedJson, SnapshotCodec.serializeCanonical restored |> unwrap "mixed again")
        // Rejected one version below: the marker is an unknown property there.
        Assert.True(Result.isError (SnapshotCodec.deserialize (mixedJson.Replace("\"schemaVersion\":21", "\"schemaVersion\":20"))))
        // An all-private snapshot writes no marker at all, so the version gate alone rejects it below 21.
        let allPrivate = declarations false
        let privateJson = SnapshotCodec.serializeCanonical allPrivate |> unwrap "private json"
        Assert.Contains("\"schemaVersion\":21", privateJson)
        Assert.DoesNotContain("exported", privateJson)
        Assert.Equal(allPrivate, SnapshotCodec.deserialize privateJson |> unwrap "private round trip")
        // The same bytes declared at version 20 are a legal older document whose declarations have no
        // visibility: they read as exported (the version-gated default), not as a rejection.
        let downgraded = SnapshotCodec.deserialize (privateJson.Replace("\"schemaVersion\":21", "\"schemaVersion\":20")) |> unwrap "downgraded"
        Assert.Equal(declarations true, downgraded)

    [<Fact>]
    let ``a document below schema 21 reads every declaration as exported and keeps its fingerprints`` () =
        let legacy = declarations true
        let json = SnapshotCodec.serializeCanonical legacy |> unwrap "legacy json"
        let restored = SnapshotCodec.deserialize json |> unwrap "legacy read"
        Assert.True(restored.Types.Head.Exported && restored.Effects.Head.Exported && restored.Capabilities.Head.Exported && restored.Functions.Head.Exported)
        Assert.Equal(json, SnapshotCodec.serializeCanonical restored |> unwrap "legacy again")
        Assert.Equal(SnapshotFingerprint.compute legacy |> unwrap "legacy fingerprint", SnapshotFingerprint.compute restored |> unwrap "restored fingerprint")
        Assert.Equal(SemanticFingerprint.compute legacy |> unwrap "legacy semantic", SemanticFingerprint.compute restored |> unwrap "restored semantic")
        Assert.Equal(legacy, SnapshotCodec.deserialize json |> unwrap "legacy read")
        // Visibility is part of both fingerprints once a declaration is private.
        let mixed = { legacy with Functions = legacy.Functions |> List.map (fun fn -> { fn with Exported = false }) }
        Assert.NotEqual(SnapshotFingerprint.compute legacy |> unwrap "legacy", SnapshotFingerprint.compute mixed |> unwrap "mixed")
        Assert.NotEqual(SemanticFingerprint.compute legacy |> unwrap "legacy", SemanticFingerprint.compute mixed |> unwrap "mixed")
        // The marker has one spelling: absent below 21, `true` or omitted at 21.
        let withMarker = json.Replace("\"displayName\":\"identity\"", "\"displayName\":\"identity\",\"exported\":true")
        Assert.True(Result.isError (SnapshotCodec.deserialize withMarker), "exported is unknown below schema 21")
        let mixedJson = SnapshotCodec.serialize mixed |> unwrap "mixed json"
        let falseMarker = mixedJson.Replace("\"displayName\":\"identity\"", "\"displayName\":\"identity\",\"exported\":false")
        match SnapshotCodec.deserialize falseMarker with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("'exported' must be true or omitted", message)
        | other -> failwithf "exported false was accepted: %A" other
        let legacyFixture = (TestGraph.create ()).Snapshot
        let fixtureJson = SnapshotCodec.serializeCanonical legacyFixture |> unwrap "fixture json"
        Assert.DoesNotContain("\"exported\"", fixtureJson)
        Assert.True((SnapshotCodec.deserialize fixtureJson |> unwrap "fixture read").Functions |> List.forall (fun fn -> fn.Exported))

    [<Fact>]
    let ``dependencies with the same identity and different visibility do not combine`` () =
        let exported = declarations true
        let other = { declarations false with Id = id<SnapshotTag> "snapshot-other" }
        match DependencyGraph.combine [ exported; other ] with
        | Error(DependencyCombinationError.ConflictingDeclaration _) -> ()
        | result -> failwithf "Different visibility merged: %A" result

    // ---- Authoring: creation and setExported ---------------------------------------------------

    [<Fact>]
    let ``declarations are private unless created with exported and setExported changes one visibility`` () =
        withDirectory (fun root ->
            let path = Path.Combine(root, "workspace.json")
            use session = AuthoringSession.createNew path |> unwrap "create"
            publish session (functionJson "quiet" false [ "value", int64Json ] int64Json (parameter "value")) |> ignore
            publish session (functionJson "loud" true [ "value", int64Json ] int64Json (parameter "value")) |> ignore
            publish session """{"id":"t1","op":"simulateCreateTypeByName","displayName":"Hidden","typeKind":{"kind":"product","fields":[]}}""" |> ignore
            publish session """{"id":"t2","op":"simulateCreateTypesByName","types":[{"displayName":"Shown","exported":true,"typeKind":{"kind":"product","fields":[]}},{"displayName":"Also","typeKind":{"kind":"product","fields":[]}}]}""" |> ignore
            publish session """{"id":"e1","op":"simulateCreateEffect","displayName":"io"}""" |> ignore
            publish session """{"id":"e2","op":"simulateCreateEffect","exported":true,"displayName":"net"}""" |> ignore
            publish session $"""{{"id":"c1","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Host","operations":[{{"displayName":"ping","parameters":[],"resultType":{int64Json},"effect":"io"}}]}}""" |> ignore
            let listed = result session """{"id":"d","op":"declarations"}"""
            Assert.False(exportedOf listed "functions" "quiet")
            Assert.True(exportedOf listed "functions" "loud")
            Assert.False(exportedOf listed "types" "Hidden")
            Assert.True(exportedOf listed "types" "Shown")
            Assert.False(exportedOf listed "types" "Also")
            Assert.False(exportedOf listed "effects" "io")
            Assert.True(exportedOf listed "effects" "net")
            Assert.True(exportedOf listed "capabilities" "Host")
            let read name = (result session $"""{{"id":"r","op":"readFunction","function":"{name}"}}""").GetProperty("function").GetProperty("exported").GetBoolean()
            Assert.False(read "quiet")
            Assert.True(read "loud")
            let declared = session.Declarations()
            Assert.False((declared.Functions |> List.find (fun fn -> fn.DisplayName = "quiet")).Exported)
            Assert.True((declared.Types |> List.find (fun t -> t.DisplayName = "Shown")).Exported)
            let stored = File.ReadAllText path
            Assert.Contains("\"schemaVersion\":21", stored)

            // Exact form, with the review's visibilityChanges, then the by-name form.
            let quiet = declared.Functions |> List.find (fun fn -> fn.DisplayName = "quiet")
            let review = publish session $"""{{"id":"x","op":"simulateOperations","operations":[{{"kind":"setExported","functionId":"{StableId.value quiet.Id}","exported":true}}]}}"""
            let changes = review.GetProperty("review").GetProperty("diff").GetProperty("visibilityChanges")
            Assert.Equal(1, changes.GetArrayLength())
            Assert.Equal("function", changes[0].GetProperty("kind").GetString())
            Assert.Equal(StableId.value quiet.Id, changes[0].GetProperty("id").GetString())
            Assert.True(changes[0].GetProperty("exported").GetBoolean())
            Assert.Contains(StableId.value quiet.Id, review.GetProperty("review").GetProperty("affectedFunctionIds").GetRawText())
            Assert.True((session.Declarations().Functions |> List.find (fun fn -> fn.Id = quiet.Id)).Exported)
            publish session """{"id":"y","op":"simulateOperationsByName","operations":[{"kind":"setExported","type":"Shown","exported":false},{"kind":"setExported","effect":"io","exported":true},{"kind":"setExported","capability":"Host","exported":false},{"kind":"setExported","function":"loud","exported":false}]}""" |> ignore
            let after = result session """{"id":"d2","op":"declarations"}"""
            Assert.False(exportedOf after "types" "Shown")
            Assert.True(exportedOf after "effects" "io")
            Assert.False(exportedOf after "capabilities" "Host")
            Assert.False(exportedOf after "functions" "loud")
            // A reviewed batch that sets and resets is no change; the same visibility is rejected.
            Assert.Equal("NoChange", fst (rejection session """{"id":"z","op":"simulateOperationsByName","operations":[{"kind":"setExported","function":"loud","exported":true},{"kind":"setExported","function":"loud","exported":false}]}"""))
            Assert.Equal("VisibilityUnchanged", fst (rejection session """{"id":"z","op":"simulateOperationsByName","operations":[{"kind":"setExported","function":"loud","exported":false}]}"""))
            Assert.Equal("ProtocolError", fst (rejection session """{"id":"z","op":"simulateOperationsByName","operations":[{"kind":"setExported","function":"loud","type":"Shown","exported":true}]}"""))
            Assert.Equal("TargetNotFound", fst (rejection session """{"id":"z","op":"simulateOperations","operations":[{"kind":"setExported","effectId":"effect-missing","exported":true}]}"""))
            session.Close()
            // The stored visibility survives a reopen through the codec.
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            Assert.True((reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "quiet")).Exported)
            Assert.False((reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "loud")).Exported))

    [<Fact>]
    let ``the viewer shows a function's visibility`` () =
        let snapshot = declarations false
        let focus = SemanticQuery.focusFunction snapshot (id<FunctionTag> "function-identity") |> unwrap "focus" |> ViewerProjection.focus
        Assert.False(focus.Function.Exported)
        Assert.Contains("Visibility: private", ViewerHtml.renderFocusDocument focus)
        let exported = SemanticQuery.focusFunction (declarations true) (id<FunctionTag> "function-identity") |> unwrap "focus" |> ViewerProjection.focus
        Assert.Contains("Visibility: exported", ViewerHtml.renderFocusDocument exported)

    // ---- Dependencies --------------------------------------------------------------------------

    [<Fact>]
    let ``a dependency's private declarations are hidden from declarations, name resolution and new references`` () =
        withDirectory (fun root ->
            let libraryPath = Path.Combine(root, "library.json")
            writeLibrary libraryPath
            let library = SnapshotCodec.deserialize (File.ReadAllText libraryPath) |> unwrap "library"
            let helper = library.Functions |> List.find (fun fn -> fn.DisplayName = "helper")
            let secret = library.Types |> List.find (fun t -> t.DisplayName = "Secret")
            let io = library.Effects.Head
            let host = library.Capabilities.Head
            let shared = library.Functions |> List.find (fun fn -> fn.DisplayName = "shared")
            use consumer = AuthoringSession.createNewWithDependency (Path.Combine(root, "consumer.json")) (Some libraryPath) |> unwrap "consumer"
            let visible = consumer.Declarations()
            Assert.Equal<string list>([ "shared" ], visible.Functions |> List.map (fun fn -> fn.DisplayName))
            Assert.Equal<string list>([ "Public" ], visible.Types |> List.map (fun t -> t.DisplayName))
            Assert.Empty(visible.Effects)
            Assert.Empty(visible.Capabilities)
            Assert.DoesNotContain(visible.Provenance, fun p -> p.Id = StableId.value helper.Id)
            let listed = result consumer """{"id":"d","op":"declarations"}"""
            Assert.Equal<string list>([ "shared" ], declarationNames listed "functions")
            Assert.Equal<string list>([ "Public" ], declarationNames listed "types")
            // The resolved graph still holds the private helper that `shared` calls.
            match consumer.ProjectExecution() with
            | Ok program -> Assert.Equal(2, program.Functions.Length)
            | Error error -> failwithf "The dependency did not lower: %A" error

            // By name, a private declaration is unknown.
            let code, text = rejection consumer (functionJson "sneaky" false [ "value", int64Json ] int64Json (call "helper" [ "value", parameter "value" ]))
            Assert.Equal("ProtocolError", code)
            Assert.Contains("Unknown function", text) // the quotes around the name are JSON-escaped
            let code, _ = rejection consumer """{"id":"t","op":"simulateCreateTypeByName","displayName":"Wrap","typeKind":{"kind":"product","fields":[{"displayName":"inner","type":{"kind":"named","name":"Secret"}}]}}"""
            Assert.Equal("ProtocolError", code)
            // By exact ID, a new reference to a private declaration is rejected before registration.
            let request body resultType requiredEffects : AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = consumer.SnapshotId; DisplayName = "direct"; Exported = false
                  TypeParameters = None
                  Parameters = [ { DisplayName = "value"; Type = int64Type } ]; ResultType = resultType; RequiredEffects = requiredEffects; Body = body }
            let expectNotExported (simulated: Result<AuthoringProposal, AuthoringSessionError>) =
                match simulated with
                | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                    Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationNotExported")
                    Assert.Contains("does not export", issues.Head.Message)
                | other -> failwithf "A reference to a private declaration was accepted: %A" other
            consumer.SimulateCreateFunction(request (ExpressionRecipe.DirectCall(helper.Id, [ helper.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ])) int64Type []) |> expectNotExported
            consumer.SimulateCreateFunction(request (ExpressionRecipe.ParameterSlot 0) int64Type [ io.Id ]) |> expectNotExported
            consumer.SimulateCreateType { ExpectedBaseSnapshotId = consumer.SnapshotId; DisplayName = "Wrap"; Exported = true; TypeParameters = []
                                          Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "inner"; Type = TypeRef.Named(secret.Id, []) } ] } |> expectNotExported
            consumer.SimulateCreateType { ExpectedBaseSnapshotId = consumer.SnapshotId; DisplayName = "Holder"; Exported = true; TypeParameters = []
                                          Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "host"; Type = TypeRef.Capability host.Id } ] } |> expectNotExported
            Assert.Empty(result consumer """{"id":"p","op":"proposals"}""" |> fun element -> element.GetProperty("proposals").EnumerateArray() |> List.ofSeq)
            // Making an imported declaration exported is an edit of it.
            let _, text = rejection consumer $"""{{"id":"x","op":"simulateOperations","operations":[{{"kind":"setExported","functionId":"{StableId.value helper.Id}","exported":true}}]}}"""
            Assert.Contains("ImportedDeclarationModified", text)
            let _, text = rejection consumer $"""{{"id":"x","op":"simulateOperations","operations":[{{"kind":"setExported","effectId":"{StableId.value io.Id}","exported":true}}]}}"""
            Assert.Contains("ImportedDeclarationModified", text)
            // The exported function is usable, and the local function is private by default.
            let created = publish consumer (functionJson "use" false [ "value", int64Json ] int64Json (call "shared" [ "value", parameter "value" ]))
            Assert.False(created.GetProperty("review").GetProperty("createdFunction").GetProperty("exported").GetBoolean())
            Assert.Contains(StableId.value shared.Id, File.ReadAllText(Path.Combine(root, "consumer.json")))
            // A structural operation that makes a local declaration refer to a private import is rejected too.
            let useId = created.GetProperty("review").GetProperty("createdFunction").GetProperty("id").GetString()
            let _, text = rejection consumer $"""{{"id":"x","op":"simulateOperations","operations":[{{"kind":"addRequiredEffect","functionId":"{useId}","effectId":"{StableId.value io.Id}"}}]}}"""
            Assert.Contains("ImportedDeclarationNotExported", text))

    [<Fact>]
    let ``an upgrade preview lists exported changes first and a reference to a now-private declaration is incompatible`` () =
        withDirectory (fun root ->
            let v1 = Path.Combine(root, "library-v1.json")
            writeLibrary v1
            let library = SnapshotCodec.deserialize (File.ReadAllText v1) |> unwrap "library"
            let shared = library.Functions |> List.find (fun fn -> fn.DisplayName = "shared")
            let helper = library.Functions |> List.find (fun fn -> fn.DisplayName = "helper")
            use consumer = AuthoringSession.createNewWithDependency (Path.Combine(root, "consumer.json")) (Some v1) |> unwrap "consumer"
            publish consumer (functionJson "use" false [ "value", int64Json ] int64Json (call "shared" [ "value", parameter "value" ])) |> ignore

            // v2: a private addition sorts after an exported one, and so do the changes.
            let v2 = Path.Combine(root, "library-v2.json")
            File.Copy(v1, v2)
            do
                use library = AuthoringSession.openExisting v2 |> unwrap "open v2"
                publish library (functionJson "extraPrivate" false [] int64Json """{"kind":"int64Literal","value":"1"}""") |> ignore
                publish library (functionJson "extraPublic" true [] int64Json """{"kind":"int64Literal","value":"2"}""") |> ignore
                publish library """{"id":"r","op":"simulateOperationsByName","operations":[{"kind":"renameFunction","function":"helper","displayName":"helper2"},{"kind":"renameFunction","function":"shared","displayName":"shared2"},{"kind":"setExported","type":"Secret","exported":true}]}""" |> ignore
                library.Close()
            let preview = consumer.PreviewDependencyUpgrade(v2, None) |> unwrap "preview v2"
            Assert.Empty(preview.Issues)
            Assert.Equal<bool list>([ true; false ], preview.Added |> List.map (fun item -> item.Exported))
            Assert.Equal<string list>([ "extraPublic"; "extraPrivate" ],
                preview.Added |> List.map (fun item -> (SnapshotCodec.deserialize (File.ReadAllText v2) |> unwrap "v2").Functions |> List.find (fun fn -> StableId.value fn.Id = item.Id) |> fun fn -> fn.DisplayName))
            // `Secret` became exported (reported with its new visibility), `shared` changed, `helper` is private.
            Assert.Equal<(string * bool) list>([ "function", true; "type", true; "function", false ], preview.Changed |> List.map (fun item -> item.Kind, item.Exported))
            Assert.Equal(StableId.value helper.Id, (List.last preview.Changed).Id)
            let wire = result consumer $"""{{"id":"pv","op":"previewDependencyUpgrade","source":"{v2.Replace("\\", "\\\\")}"}}"""
            Assert.True(wire.GetProperty("compatible").GetBoolean())
            Assert.True(wire.GetProperty("added").[0].GetProperty("exported").GetBoolean())
            Assert.False(wire.GetProperty("added").[1].GetProperty("exported").GetBoolean())

            // v3: `shared` becomes private while `use` still calls it.
            let v3 = Path.Combine(root, "library-v3.json")
            File.Copy(v1, v3)
            do
                use library = AuthoringSession.openExisting v3 |> unwrap "open v3"
                publish library """{"id":"r","op":"simulateOperationsByName","operations":[{"kind":"setExported","function":"shared","exported":false}]}""" |> ignore
                library.Close()
            let breaking = consumer.PreviewDependencyUpgrade(v3, None) |> unwrap "preview v3"
            Assert.Contains(breaking.Issues, fun issue -> issue.Code = "ImportedDeclarationNotExported" && issue.Message.Contains "shared")
            Assert.Equal<(string * bool) list>([ "function", false ], breaking.Changed |> List.map (fun item -> item.Kind, item.Exported))
            Assert.Equal(StableId.value shared.Id, breaking.Changed.Head.Id)
            let wire = result consumer $"""{{"id":"pv3","op":"previewDependencyUpgrade","source":"{v3.Replace("\\", "\\\\")}"}}"""
            Assert.False(wire.GetProperty("compatible").GetBoolean())
            Assert.Contains("ImportedDeclarationNotExported", wire.GetProperty("issues").GetRawText())
            let change = AuthoringDependencyChangeRequest.Upgrades [ { Source = v3; Replaces = StableId.value library.Id } ]
            match consumer.SimulateDependencyUpgrade(change, { Fields = []; Functions = []; Operations = [] }, consumer.SnapshotId) with
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationNotExported")
            | other -> failwithf "An upgrade that hides a referenced declaration was accepted: %A" other
            // v2 upgrades: the renamed `shared2` stays exported, so the upgrade is accepted.
            let change = AuthoringDependencyChangeRequest.Upgrades [ { Source = v2; Replaces = StableId.value library.Id } ]
            let proposal = consumer.SimulateDependencyUpgrade(change, { Fields = []; Functions = []; Operations = [] }, consumer.SnapshotId) |> unwrap "upgrade v2"
            consumer.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit v2" |> ignore
            Assert.Equal<string list>([ "extraPublic"; "shared2"; "use" ], consumer.Declarations().Functions |> List.map (fun fn -> fn.DisplayName) |> List.sort))
