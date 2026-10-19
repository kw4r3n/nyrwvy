namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Changing a Product field's type together with every construction and read in one publication.
module ProductFieldTypeMigrationTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("field-type-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private optionInt = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ intType ])
    let private itemId = id<TypeTag> "item"
    let private quantityId = id<FieldTag> "quantity"
    let private labelId = id<FieldTag> "label"
    let private tagId = id<TypeTag> "tag"
    let private tagNameId = id<FieldTag> "tag-name"
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private create ws name parameterType resultType body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              TypeParameters = None
              DisplayName = name
              Parameters = [ { DisplayName = "input"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap ("create " + name)
        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
        | other -> failwithf "Commit failed: %A" other

    let private find ws name = (AgentWorkspace.snapshotForStructuralEditing ws).Functions |> List.find (fun fn -> fn.DisplayName = name)

    /// `make` constructs Item; `describe` and `total` read its Int64 quantity; `unrelated` does neither.
    let private fixture () =
        let types: SemanticType list =
            [ { Exported = true; TypeParameters = []; Id = itemId; DisplayName = "Item"
                Kind = SemanticTypeKind.Product
                    [ { Id = quantityId; DisplayName = "quantity"; Type = intType }
                      { Id = labelId; DisplayName = "label"; Type = stringType } ] }
              { Exported = true; TypeParameters = []; Id = tagId; DisplayName = "Tag"
                Kind = SemanticTypeKind.Union [ { Id = id<VariantTag> "named"; DisplayName = "Named"; Fields = [ { Id = tagNameId; DisplayName = "name"; Type = stringType } ] } ] } ]
        let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "base") with Types = types } |> unwrap "workspace"
        let input = ExpressionRecipe.ParameterSlot 0
        let itemType = TypeRef.Named(itemId, [])
        let ws = create ws "make" intType itemType (ExpressionRecipe.ProductConstruct(itemId, [], [ quantityId, input; labelId, ExpressionRecipe.StringLiteral "box" ]))
        let ws =
            create ws "describe" itemType stringType
                (ExpressionRecipe.StringConcat(ExpressionRecipe.FieldAccess(input, labelId), ExpressionRecipe.Int64ToString(ExpressionRecipe.FieldAccess(input, quantityId))))
        let ws = create ws "total" itemType intType (ExpressionRecipe.FieldAccess(input, quantityId))
        create ws "unrelated" stringType stringType input

    let private valueSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }

    let private request ws: AgentProductFieldTypeMigrationRequest =
        let parameter name = ExpressionRecipe.Parameter (List.exactlyOne (find ws name).Parameters).Id
        let quantity name = ExpressionRecipe.FieldAccess(parameter name, quantityId)
        { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
          FieldId = quantityId
          NewFieldType = optionInt
          FunctionRewrites =
            [ { FunctionId = (find ws "make").Id
                Replacement = ExpressionRecipe.ProductConstruct(itemId, [], [ quantityId, ExpressionRecipe.OptionSome(parameter "make"); labelId, ExpressionRecipe.StringLiteral "box" ]) }
              { FunctionId = (find ws "describe").Id
                Replacement =
                    ExpressionRecipe.MatchOption(
                        quantity "describe",
                        ExpressionRecipe.StringConcat(ExpressionRecipe.FieldAccess(parameter "describe", labelId), ExpressionRecipe.Int64ToString valueSlot),
                        ExpressionRecipe.FieldAccess(parameter "describe", labelId)) }
              { FunctionId = (find ws "total").Id
                Replacement = ExpressionRecipe.MatchOption(quantity "total", valueSlot, ExpressionRecipe.Int64Literal 0L) } ] }

    let private rejects ws request =
        match AgentProductFieldTypeMigrationWorkflow.simulate ws request with
        | Error(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _)) -> diagnostic.Code
        | other -> failwithf "Invalid field migration accepted: %A" other

    [<Fact>]
    let ``field type migration rewrites constructions and reads in one reviewed publication`` () =
        let ws = fixture ()
        let before = AgentWorkspace.snapshotForStructuralEditing ws
        let proposal = AgentProductFieldTypeMigrationWorkflow.simulate ws (request ws) |> unwrap "simulate"
        let review = AgentProductFieldTypeMigrationProposal.review proposal
        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(intType, review.ResultingChange.FieldChange.OldType)
        Assert.Equal(optionInt, review.ResultingChange.FieldChange.NewType)
        Assert.Equal<Set<FunctionId>>(
            set [ (find ws "make").Id; (find ws "describe").Id; (find ws "total").Id ],
            review.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId) |> Set.ofList)
        let next =
            match AgentProductFieldTypeMigrationWorkflow.commit ws proposal with
            | AgentProductFieldTypeMigrationCommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let after = AgentWorkspace.snapshotForStructuralEditing next
        Assert.Empty(SnapshotValidator.validate after)
        let item = after.Types |> List.find (fun item -> item.Id = itemId)
        match item.Kind with
        | SemanticTypeKind.Product fields ->
            Assert.Equal<FieldId list>([ quantityId; labelId ], fields |> List.map (fun field -> field.Id))
            Assert.Equal(optionInt, (fields |> List.find (fun field -> field.Id = quantityId)).Type)
        | _ -> failwith "Item must stay a Product."
        for name in [ "make"; "describe"; "total"; "unrelated" ] do
            let original = before.Functions |> List.find (fun fn -> fn.DisplayName = name)
            let migrated = after.Functions |> List.find (fun fn -> fn.Id = original.Id)
            Assert.Equal<SemanticParameter list>(original.Parameters, migrated.Parameters)
        Assert.Equal(find ws "unrelated", after.Functions |> List.find (fun fn -> fn.DisplayName = "unrelated"))

    [<Fact>]
    let ``field type migration refuses incomplete stale or mistyped rewrites`` () =
        let ws = fixture ()
        let valid = request ws
        Assert.Equal("FieldTypeMigrationRewriteMismatch", rejects ws { valid with FunctionRewrites = valid.FunctionRewrites.Tail })
        Assert.Equal("FieldTypeMigrationRewriteMismatch",
            rejects ws { valid with FunctionRewrites = valid.FunctionRewrites @ [ { FunctionId = (find ws "unrelated").Id; Replacement = ExpressionRecipe.StringLiteral "" } ] })
        Assert.Equal("FieldTypeMigrationDuplicateRewrite", rejects ws { valid with FunctionRewrites = valid.FunctionRewrites.Head :: valid.FunctionRewrites })
        Assert.Equal("FieldTypeMigrationTypeUnchanged", rejects ws { valid with NewFieldType = intType })
        Assert.Equal("FieldTypeMigrationExpectedBaseMismatch", rejects ws { valid with ExpectedBaseSnapshotId = id<SnapshotTag> "stale" })
        Assert.Equal("FieldTypeMigrationFieldNotFound", rejects ws { valid with FieldId = tagNameId })
        // `total` still returns the field unchanged, which is now Option<Int64>, not Int64.
        let stale =
            valid.FunctionRewrites |> List.map (fun rewrite ->
                if rewrite.FunctionId = (find ws "total").Id then
                    { rewrite with Replacement = ExpressionRecipe.FieldAccess(ExpressionRecipe.Parameter (List.exactlyOne (find ws "total").Parameters).Id, quantityId) }
                else rewrite)
        Assert.Equal("FieldTypeMigrationRewriteInvalid", rejects ws { valid with FunctionRewrites = stale })

    [<Fact>]
    let ``field type migration checks the new field type before binding rewrites`` () =
        let ws = fixture ()
        let valid = request ws
        let unknown = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Named(id<TypeTag> "missing", []) ])
        Assert.Equal("FieldTypeMigrationUnknownNamedType", rejects ws { valid with NewFieldType = unknown })
        Assert.Equal("FieldTypeMigrationUnknownCapabilityType", rejects ws { valid with NewFieldType = TypeRef.Capability(id<CapabilityTag> "missing") })
        Assert.Equal(
            "FieldTypeMigrationTypeParameterNotAllowed",
            rejects ws { valid with NewFieldType = TypeRef.TypeParameter(id<TypeParameterTag> "t") })
        Assert.Equal(
            "FieldTypeMigrationBuiltinGenericArityMismatch",
            rejects ws { valid with NewFieldType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ intType; intType ]) })

    [<Fact; Trait("Suite", "Extended")>]
    let ``field type migration is authored over the wire and runs through the common runner`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-field-type-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let ws = fixture ()
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap "serialize")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let parameterId name = StableId.value (List.exactlyOne (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Parameters).Id
            let parameter name = sprintf """{"kind":"parameter","parameterId":"%s"}""" (parameterId name)
            let quantity name = sprintf """{"kind":"fieldAccess","source":%s,"fieldId":"%s"}""" (parameter name) (StableId.value quantityId)
            let label name = sprintf """{"kind":"fieldAccess","source":%s,"fieldId":"%s"}""" (parameter name) (StableId.value labelId)
            let value = """{"kind":"patternReference","scopeDepth":0,"slot":"value"}"""
            let make =
                sprintf """{"kind":"productConstruct","typeId":"%s","fields":[{"fieldId":"%s","value":{"kind":"optionSome","value":%s}},{"fieldId":"%s","value":{"kind":"stringLiteral","value":"box"}}]}"""
                    (StableId.value itemId) (StableId.value quantityId) (parameter "make") (StableId.value labelId)
            let describe =
                sprintf """{"kind":"matchOption","input":%s,"someBody":{"kind":"stringConcat","left":%s,"right":{"kind":"int64ToString","value":%s}},"noneBody":%s}"""
                    (quantity "describe") (label "describe") value (label "describe")
            let total = sprintf """{"kind":"matchOption","input":%s,"someBody":%s,"noneBody":{"kind":"int64Literal","value":0}}""" (quantity "total") value
            let rewrite name body = sprintf """{"functionId":"%s","replacement":%s}""" (StableId.value (functionId name)) body
            let line =
                sprintf """{"id":"field","op":"simulateProductFieldTypeMigration","fieldId":"%s","newFieldType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]},"functionRewrites":[%s,%s,%s]}"""
                    (StableId.value quantityId) (rewrite "make" make) (rewrite "describe" describe) (rewrite "total" total)
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let result = proposed.RootElement.GetProperty("result")
            Assert.Equal("productFieldTypeMigration", result.GetProperty("review").GetProperty("kind").GetString())
            use committed =
                AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" (result.GetProperty("proposalId").GetString()))
                |> JsonDocument.Parse
            Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            let item quantity = PureRunValue.Product(itemId, [], [ quantityId, PureRunValue.Int64Option quantity; labelId, PureRunValue.String "box" ] |> List.sortBy (fst >> StableId.value))
            let made = PureRun.run session (functionId "make") [ PureRunValue.Int64 3L ] (Some(item (Some 3L))) |> unwrap "make"
            Assert.Equal(Some true, made.Matches)
            Assert.Equal(PureRunValue.String "box3", (PureRun.run session (functionId "describe") [ item (Some 3L) ] None |> unwrap "describe").Actual)
            Assert.Equal(PureRunValue.String "box", (PureRun.run session (functionId "describe") [ item None ] None |> unwrap "describe none").Actual)
            Assert.Equal(PureRunValue.Int64 0L, (PureRun.run session (functionId "total") [ item None ] None |> unwrap "total").Actual)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private json value = JsonSerializer.Serialize value
    let private element (text: string) = use doc = JsonDocument.Parse text in doc.RootElement.Clone()
    let private namedBodies =
        [ "make", """{"kind":"productConstruct","type":"Item","fields":[{"field":"quantity","value":{"kind":"optionSome","value":{"kind":"parameter","name":"input"}}},{"field":"label","value":{"kind":"stringLiteral","value":"box"}}]}"""
          "describe", """{"kind":"matchOption","input":{"kind":"fieldAccess","type":"Item","field":"quantity","source":{"kind":"parameter","name":"input"}},"someBody":{"kind":"stringConcat","left":{"kind":"fieldAccess","type":"Item","field":"label","source":{"kind":"parameter","name":"input"}},"right":{"kind":"int64ToString","value":{"kind":"patternReference","scopeDepth":0,"slot":"value"}}},"noneBody":{"kind":"fieldAccess","type":"Item","field":"label","source":{"kind":"parameter","name":"input"}}}"""
          "total", """{"kind":"matchOption","input":{"kind":"fieldAccess","type":"Item","field":"quantity","source":{"kind":"parameter","name":"input"}},"someBody":{"kind":"patternReference","scopeDepth":0,"slot":"value"},"noneBody":{"kind":"int64Literal","value":0}}""" ]

    let private namedLine typeSelector functionSelector bodies =
        json {| id = "named-field"; op = "simulateProductFieldTypeMigrationByName"; ``type`` = element typeSelector; field = "quantity"
                newFieldType = element """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
                functionRewrites = bodies |> List.map (fun (name, body) -> {| ``function`` = element (functionSelector name); replacement = element body |}) |}

    let private withSession ws run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-field-names-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let path = Path.Combine(root, "app.json")
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap "serialize")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            run session
        finally Directory.Delete(root, true)

    let private execute session line = AuthoringProtocol.executeLine session line |> element
    let private namedDefault bodies = namedLine (json "Item") json bodies

    [<Fact>]
    let ``named field migration preserves identities and uses the atomic engine`` () =
        let ws = fixture ()
        withSession ws (fun session ->
            let before = session.Declarations()
            let proposal = execute session (namedDefault namedBodies)
            Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
            let result = proposal.GetProperty("result")
            Assert.Equal("productFieldTypeMigration", result.GetProperty("review").GetProperty("kind").GetString())
            session.Commit(result.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            let after = session.Declarations()
            Assert.Equal<TypeId list>(before.Types |> List.map (fun t -> t.Id), after.Types |> List.map (fun t -> t.Id))
            for original in before.Functions do
                let changed = after.Functions |> List.find (fun f -> f.Id = original.Id)
                Assert.Equal<SemanticParameter list>(original.Parameters, changed.Parameters)
            match (after.Types |> List.find (fun t -> t.Id = itemId)).Kind with
            | SemanticTypeKind.Product fields -> Assert.Equal(optionInt, (fields |> List.find (fun f -> f.Id = quantityId)).Type)
            | _ -> failwith "Expected Product")

    [<Fact>]
    let ``named field migration refuses stale duplicate and type-invalid repairs without publication`` () =
        withSession (fixture ()) (fun session ->
            let snapshot = session.SnapshotId
            let valid = namedDefault namedBodies
            let stale = valid.Replace("\"op\":", "\"expectedSnapshotId\":\"stale-field-base\",\"op\":")
            let diagnostic line =
                let response = execute session line
                Assert.False(response.GetProperty("ok").GetBoolean())
                Assert.Equal(snapshot, session.SnapshotId)
                response.GetProperty("error").GetProperty("details").GetProperty("diagnostics").[0].GetProperty("code").GetString()
            Assert.Equal("FieldTypeMigrationExpectedBaseMismatch", diagnostic stale)
            Assert.Equal("FieldTypeMigrationDuplicateRewrite", diagnostic (namedDefault (namedBodies.Head :: namedBodies)))
            let invalid = namedBodies |> List.map (fun (name, body) -> name, if name = "total" then """{"kind":"boolLiteral","value":true}""" else body)
            Assert.Equal("FieldTypeMigrationRewriteInvalid", diagnostic (namedDefault invalid)))

    [<Fact>]
    let ``missing and unrelated field repairs report names identities ownership and totals`` () =
        withSession (fixture ()) (fun session ->
            let response = execute session (namedDefault [ namedBodies.Head; "unrelated", """{"kind":"stringLiteral","value":""}""" ])
            Assert.False(response.GetProperty("ok").GetBoolean())
            let details = response.GetProperty("error").GetProperty("details")
            let message = details.GetProperty("diagnostics").[0].GetProperty("message").GetString()
            Assert.Contains("'describe'", message)
            Assert.Contains("'total'", message)
            let repair = details.GetProperty("repair")
            Assert.Equal(2, repair.GetProperty("missing").GetProperty("totalCount").GetInt32())
            Assert.Equal(1, repair.GetProperty("unrelated").GetProperty("totalCount").GetInt32())
            for entry in repair.GetProperty("missing").GetProperty("items").EnumerateArray() do
                let fn = session.Declarations().Functions |> List.find (fun f -> StableId.value f.Id = entry.GetProperty("id").GetString())
                Assert.Equal(fn.DisplayName, entry.GetProperty("displayName").GetString())
                Assert.Equal("local", entry.GetProperty("ownership").GetString())
                Assert.Equal(0, entry.GetProperty("sourceCount").GetInt32()))

    [<Fact>]
    let ``many missing field repairs bound diagnostic previews and expose omitted counts`` () =
        let ws = [ 0..8 ] |> List.fold (fun ws i -> create ws ("reader" + string i) (TypeRef.Named(itemId, [])) intType (ExpressionRecipe.FieldAccess(ExpressionRecipe.ParameterSlot 0, quantityId))) (fixture ())
        withSession ws (fun session ->
            let response = execute session (namedDefault [])
            let details = response.GetProperty("error").GetProperty("details")
            let missing = details.GetProperty("repair").GetProperty("missing")
            Assert.Equal(12, missing.GetProperty("totalCount").GetInt32())
            Assert.Equal(5, missing.GetProperty("items").GetArrayLength())
            Assert.True(missing.GetProperty("truncated").GetBoolean())
            Assert.True(details.GetProperty("diagnostics").[0].GetProperty("message").GetString().Length < 2048))

    [<Fact>]
    let ``field migration selectors reject unknown IDs and guards without name fallback`` () =
        withSession (fixture ()) (fun session ->
            for selector in
                [ json {| id = "missing-field-owner"; name = "Item" |}
                  json {| id = StableId.value itemId; name = "Item"; dependencySnapshotId = "stale-pin" |}
                  json {| id = StableId.value itemId; name = "Item"; ownership = "imported" |} ] do
                let response = execute session (namedLine selector json namedBodies)
                Assert.False(response.GetProperty("ok").GetBoolean())
                Assert.Equal("ProtocolError", response.GetProperty("error").GetProperty("code").GetString()))

    [<Fact>]
    let ``ambiguous field repair function names require an exact guarded selector`` () =
        let original = create (fixture ()) "otherTotal" intType intType (ExpressionRecipe.ParameterSlot 0) |> AgentWorkspace.snapshotForStructuralEditing
        // Persisted/imported graphs may contain equal display names even though the creator
        // prevents introducing them into a workspace by name.
        let ws = AgentWorkspace.create { original with Functions = original.Functions |> List.map (fun f -> if f.DisplayName = "otherTotal" then { f with DisplayName = "total" } else f) } |> unwrap "ambiguous fixture"
        withSession ws (fun session ->
            let response = execute session (namedDefault namedBodies)
            Assert.Equal("ProtocolError", response.GetProperty("error").GetProperty("code").GetString())
            Assert.Contains("Ambiguous", response.GetProperty("error").GetProperty("message").GetString())
            let original = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "total" && f.Parameters.Head.Type = TypeRef.Named(itemId, []))
            let selector name = if name = "total" then json {| id = StableId.value original.Id; name = "total"; ownership = "local" |} else json name
            let line = namedLine (json {| id = StableId.value itemId; name = "Item"; ownership = "local" |}) selector namedBodies
            let qualified = execute session line
            Assert.True(qualified.GetProperty("ok").GetBoolean(), qualified.ToString()))

    [<Fact>]
    let ``ambiguous Product owner names bind the field only after an exact owner selector`` () =
        let original = AgentWorkspace.snapshotForStructuralEditing (fixture ())
        let other: SemanticType =
            { Exported = true; TypeParameters = []; Id = id<TypeTag> "other-item"; DisplayName = "Item"
              Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "other-quantity"; DisplayName = "quantity"; Type = stringType } ] }
        let ws = AgentWorkspace.create { original with Types = other :: original.Types } |> unwrap "ambiguous types"
        withSession ws (fun session ->
            let ambiguous = execute session (namedDefault namedBodies)
            Assert.Equal("ProtocolError", ambiguous.GetProperty("error").GetProperty("code").GetString())
            Assert.Contains("Ambiguous", ambiguous.GetProperty("error").GetProperty("message").GetString())
            let selector = json {| id = StableId.value itemId; name = "Item"; ownership = "local" |}
            let guarded = namedBodies |> List.map (fun (name, body) -> name, body.Replace("\"type\":\"Item\"", "\"type\":" + selector))
            let proposal = execute session (namedLine selector json guarded)
            Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
            session.Commit(proposal.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap "guarded commit" |> ignore
            let untouched = session.Declarations().Types |> List.find (fun t -> t.Id = other.Id)
            Assert.Equal(other, untouched))

    [<Fact>]
    let ``named field migration preserves imported ownership and pin guards`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-imported-field-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let library = AgentWorkspace.snapshotForStructuralEditing (fixture ())
            let libraryPath = Path.Combine(root, "library.json")
            File.WriteAllText(libraryPath, SnapshotCodec.serialize library |> unwrap "library")
            use session = AuthoringSession.createNewWithDependencies (Path.Combine(root, "app.json")) [ libraryPath ] |> unwrap "app"
            let snapshot = session.SnapshotId
            let selector = json {| id = StableId.value itemId; name = "Item"; ownership = "imported"; dependencySnapshotId = StableId.value library.Id |}
            let rejected = execute session (namedLine selector json namedBodies)
            Assert.Equal("SnapshotCodecFailure", rejected.GetProperty("error").GetProperty("code").GetString())
            Assert.Contains("ImportedDeclarationModified", rejected.GetRawText())
            Assert.Equal(snapshot, session.SnapshotId)
            let missing = execute session (namedLine selector json [])
            let item = missing.GetProperty("error").GetProperty("details").GetProperty("repair").GetProperty("missing").GetProperty("items").[0]
            Assert.Equal("imported", item.GetProperty("ownership").GetString())
            Assert.Equal(StableId.value library.Id, item.GetProperty("sources").[0].GetProperty("snapshotId").GetString())
        finally Directory.Delete(root, true)
