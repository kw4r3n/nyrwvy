namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `ProductUpdate` (post-M2 order 32): a copy of a product value with one or more named fields
/// replaced and every other field kept, so a one-field change no longer restates the whole record.
module ProductUpdateTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private slot = ExpressionRecipe.ParameterSlot
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = []; Body = body }
    let private rejects (session: AuthoringSession) parameters resultType body =
        match session.SimulateCreateFunction(request session "rejected" parameters resultType body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwith "The function was accepted."
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-product-update-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private execute session line = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
    let private send session line =
        let response = execute session line
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response

    let private q (text: string) = JsonSerializer.Serialize text
    let private obj (fields: (string * string) list) = "{" + (fields |> List.map (fun (key, value) -> q key + ":" + value) |> String.concat ",") + "}"
    let private arr (items: string list) = "[" + String.concat "," items + "]"
    let private node kind fields = obj (("kind", q kind) :: fields)
    let private prim name = node "primitive" [ "primitive", q name ]
    let private S, I, B = prim "string", prim "int64", prim "bool"
    let private L item = node "builtinGeneric" [ "generic", q "list"; "arguments", arr [ item ] ]
    let private N name = node "named" [ "name", q name ]
    let private p name = node "parameter" [ "name", q name ]
    let private i (value: int64) = node "int64Literal" [ "value", string value ]
    let private s (text: string) = node "stringLiteral" [ "value", q text ]
    let private b (value: bool) = node "boolLiteral" [ "value", (if value then "true" else "false") ]
    let private v depth = node "patternReference" [ "scopeDepth", string depth; "slot", q "value" ]
    let private bin kind left right = node kind [ "left", left; "right", right ]
    let private cat = bin "stringConcat"
    let private args (items: (string * string) list) = arr (items |> List.map (fun (name, value) -> obj [ "parameter", q name; "value", value ]))
    let private call name items = node "directCall" [ "function", q name; "arguments", args items ]
    let private fieldValues (items: (string * string) list) = arr (items |> List.map (fun (name, value) -> obj [ "field", q name; "value", value ]))
    let private field source typeName name = node "fieldAccess" [ "source", source; "type", q typeName; "field", q name ]
    let private product typeName items = node "productConstruct" [ "type", q typeName; "fields", fieldValues items ]
    let private update source typeName items = node "productUpdate" [ "source", source; "type", q typeName; "fields", fieldValues items ]
    let private bind value body = node "bind" [ "value", value; "body", body ]
    let private cond test whenTrue whenFalse = node "conditional" [ "condition", test; "whenTrue", whenTrue; "whenFalse", whenFalse ]
    let private list itemType items = node "listConstruct" [ "itemType", itemType; "items", arr items ]
    let private fold source seed name = node "listFold" [ "source", source; "seed", seed; "function", q name ]

    let private commit (session: AuthoringSession) (response: JsonDocument) =
        let result = response.RootElement.GetProperty("result")
        session.Commit(result.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
        result
    let private fn session name (parameters: (string * string) list) result body =
        let parameters = parameters |> List.map (fun (name, t) -> obj [ "displayName", q name; "type", t ]) |> arr
        use response =
            send session (sprintf """{"id":"%s","op":"simulateCreateFunctionByName","exported":true,"displayName":"%s","parameters":%s,"resultType":%s,"requiredEffects":[],"body":%s}""" name name parameters result body)
        commit session response |> ignore

    /// `State {name: String, count: Int64, tags: List<String>, done: Bool}` and `Other {name: String}`.
    let private declareTypes (session: AuthoringSession) =
        let fields (items: (string * string) list) = items |> List.map (fun (name, t) -> obj [ "displayName", q name; "type", t ]) |> arr
        let productType name items = obj [ "displayName", q name; "typeKind", node "product" [ "fields", fields items ] ]
        use types =
            send session (sprintf """{"id":"types","op":"simulateCreateTypesByName","types":%s}"""
                (arr [ productType "State" [ "name", S; "count", I; "tags", L S; "done", B ]
                       productType "Other" [ "name", S ] ]))
        commit session types |> ignore
        let declared = session.Declarations().Types
        let state = declared |> List.find (fun item -> item.DisplayName = "State")
        let other = declared |> List.find (fun item -> item.DisplayName = "Other")
        let fieldsOf (item: SemanticType) =
            match item.Kind with
            | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.DisplayName, field.Id) |> Map.ofList
            | SemanticTypeKind.Union _ -> failwith "not a product"
        state, fieldsOf state, other, fieldsOf other

    [<Fact>]
    let ``ProductUpdate checks its source, field set and value types in recipes and persisted graphs`` () =
        withSession (fun session _ ->
            let state, stateFields, other, otherFields = declareTypes session
            let stateType = TypeRef.Named(state.Id, [])
            let updateIds fields = ExpressionRecipe.ProductUpdate(state.Id, slot 0, fields)
            let accepted = request session "rename" [ "st", stateType; "name", textType ] stateType (updateIds [ stateFields["name"], slot 1 ])
            session.SimulateCreateFunction accepted |> unwrap |> ignore
            let reject body = rejects session [ "st", stateType; "name", textType ] stateType body
            Assert.Contains("PureExpressionEmptyProductUpdate", reject (updateIds []))
            Assert.Contains("PureExpressionDuplicateFieldBinding", reject (updateIds [ stateFields["name"], slot 1; stateFields["name"], slot 1 ]))
            Assert.Contains("PureExpressionFieldBindingMismatch", reject (updateIds [ otherFields["name"], slot 1 ]))
            Assert.Contains("PureExpressionFieldBindingTypeMismatch", reject (updateIds [ stateFields["count"], slot 1 ]))
            Assert.Contains("PureExpressionProductUpdateSourceMismatch", reject (ExpressionRecipe.ProductUpdate(state.Id, slot 1, [ stateFields["name"], slot 1 ])))
            Assert.Contains(
                "PureExpressionProductUpdateSourceMismatch",
                rejects session [ "st", stateType ] (TypeRef.Named(other.Id, [])) (ExpressionRecipe.ProductUpdate(other.Id, slot 0, [ otherFields["name"], ExpressionRecipe.StringLiteral "x" ])))
            // The result has the product type, never a field type.
            Assert.NotEqual<string>("", rejects session [ "st", stateType; "name", textType ] textType (updateIds [ stateFields["name"], slot 1 ]))
            // By name, the fields resolve within the named type; another type's field is unknown there.
            let byName body =
                sprintf """{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"f","parameters":[{"displayName":"st","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}""" (N "State") (N "State") body
            use missing = execute session (byName (update (p "st") "State" [ "title", s "x" ]))
            Assert.False(missing.RootElement.GetProperty("ok").GetBoolean())
            use noSource = execute session (byName (node "productUpdate" [ "type", q "State"; "fields", fieldValues [ "name", s "x" ] ]))
            Assert.Equal("ProtocolError", noSource.RootElement.GetProperty("error").GetProperty("code").GetString()))
        // Persisted graphs are checked by the validator, not only by recipe preflight.
        let stateId = StableId.createForTesting<TypeTag> "type-update-state"
        let nameId = StableId.createForTesting<FieldTag> "field-update-name"
        let countId = StableId.createForTesting<FieldTag> "field-update-count"
        let foreignId = StableId.createForTesting<FieldTag> "field-update-foreign"
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-update-" + name)
        let stateType: SemanticType =
            { Exported = true; TypeParameters = []; Id = stateId; DisplayName = "State"
              Kind = SemanticTypeKind.Product [ { Id = nameId; DisplayName = "name"; Type = textType }; { Id = countId; DisplayName = "count"; Type = intType } ] }
        let foreignType: SemanticType =
            { Exported = true; TypeParameters = []; Id = StableId.createForTesting<TypeTag> "type-update-foreign"; DisplayName = "Foreign"
              Kind = SemanticTypeKind.Product [ { Id = foreignId; DisplayName = "x"; Type = textType } ] }
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-update-st"
        let validate (source: SemanticNodeKind) (bindings: (FieldId * SemanticNodeKind) list) =
            let values = bindings |> List.mapi (fun index (_, kind) -> ({ Id = nodeId (string index); Kind = kind }: SemanticNode))
            let sourceNode: SemanticNode = { Id = nodeId "source"; Kind = source }
            let updateNode: SemanticNode =
                { Id = nodeId "update"
                  Kind = SemanticNodeKind.ProductUpdate(stateId, sourceNode.Id, List.map2 (fun (fieldId, _) (value: SemanticNode) -> { TargetFieldId = fieldId; ValueNodeId = value.Id }) bindings values) }
            let fn: SemanticFunction =
                { Exported = true; Id = StableId.createForTesting<FunctionTag> "function-update-entry"; DisplayName = "entry"
                  Parameters = [ { Id = parameterId; DisplayName = "st"; Type = TypeRef.Named(stateId, []) } ]
                  ResultType = TypeRef.Named(stateId, []); RequiredEffects = []
                  Body = { Nodes = sourceNode :: values @ [ updateNode ]; ResultNodeId = updateNode.Id } }
            SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-update") with Types = [ stateType; foreignType ]; Functions = [ fn ] }
            |> List.map (fun issue -> issue.Code)
        let validateWithSource = validate (SemanticNodeKind.ParameterReference parameterId)
        let text = SemanticNodeKind.StringLiteral "x"
        let number = SemanticNodeKind.Int64Literal 1L
        Assert.Contains("EmptyProductUpdate", validate text [])
        Assert.Contains("ProductUpdateSourceTypeMismatch", validate text [ nameId, text ])
        Assert.Contains("FieldBindingMismatch", validateWithSource [ foreignId, text ])
        Assert.Contains("DuplicateFieldBinding", validateWithSource [ nameId, text; nameId, text ])
        Assert.Contains("FieldValueTypeMismatch", validateWithSource [ countId, text ])
        Assert.Empty(validateWithSource [ countId, number ])
        Assert.Empty(validateWithSource [ countId, number; nameId, text ])

    [<Fact>]
    let ``ProductUpdate persists, reopens, projects, lowers, emits a with-expression and blocks removing an updated field`` () =
        withSession (fun session path ->
            let _, stateFields, _, _ = declareTypes session
            use named =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"bump","parameters":[{"displayName":"st","type":%s},{"displayName":"tag","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}"""
                    (N "State") S (N "State")
                    (update (p "st") "State"
                        [ "count", bin "int64WrappingAdd" (field (p "st") "State" "count") (i 1L)
                          "tags", node "listAppendItem" [ "list", field (p "st") "State" "tags"; "item", p "tag" ] ]))
            Assert.Contains("\"kind\":\"productUpdate\"", named.RootElement.GetRawText())
            commit session named |> ignore
            let bump = find session "bump"
            let viewer = session.FocusFunction bump.Id |> unwrap |> ViewerProjection.focus
            let updateNode = viewer.Graph.Nodes |> List.find (fun item -> item.Role = ViewerNodeRole.Operation "ProductUpdate")
            Assert.Equal(ValueDependencyRole.Source, updateNode.ValueDependencies.Head.Role)
            Assert.Equal(3, updateNode.ValueDependencies.Length)
            Assert.Contains("product update", ViewerHtml.renderFocusDocument viewer)
            session.Close()
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"productUpdate\"", encoded)
            let graph = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            Assert.Contains("product-update", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"productUpdate\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains("\"kind\":\"productUpdate\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            Assert.Contains(" with { ", source)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(bump.Body.ResultNodeId, (find reopened "bump").Body.ResultNodeId)
            // An updated field is a use of that field: it cannot be removed under the update,
            // while a field the update keeps can.
            let remove fieldId =
                execute reopened (sprintf """{"id":"remove","op":"simulateOperations","operations":[{"kind":"removeProductField","fieldId":"%s"}]}""" (StableId.value fieldId))
            use blocked = remove stateFields["count"]
            Assert.Contains("ProductFieldStillReferenced", blocked.RootElement.GetRawText())
            use kept = remove stateFields["done"]
            Assert.True(kept.RootElement.GetProperty("ok").GetBoolean(), kept.RootElement.ToString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ProductUpdate replaces only the bound fields and leaves the source value unchanged through runPure`` () =
        withSession (fun session path ->
            declareTypes session |> ignore
            let st = N "State"
            let show value =
                bind value
                    (cat (cat (cat (cat (cat (cat (field (v 0) "State" "name") (s ":")) (node "int64ToString" [ "value", field (v 0) "State" "count" ])) (s ":"))
                                   (node "stringJoin" [ "list", field (v 0) "State" "tags"; "separator", s "," ])) (s ":"))
                        (cond (field (v 0) "State" "done") (s "y") (s "n")))
            fn session "show" [ "st", st ] S (show (p "st"))
            fn session "fresh" [ "name", S ] st (product "State" [ "name", p "name"; "count", i 0L; "tags", node "listEmpty" [ "itemType", S ]; "done", b false ])
            fn session "bump" [ "st", st; "tag", S ] st
                (update (p "st") "State"
                    [ "count", bin "int64WrappingAdd" (field (p "st") "State" "count") (i 1L)
                      "tags", node "listAppendItem" [ "list", field (p "st") "State" "tags"; "item", p "tag" ] ])
            let showOf value = call "show" [ "st", value ]
            fn session "table" [ "name", S; "tags", L S ] (L S)
                (bind (call "fresh" [ "name", p "name" ])
                    (bind (fold (p "tags") (v 0) "bump")
                        (list S
                            [ showOf (v 1)
                              showOf (v 0)
                              // One field, and the same field twice in a row: the later update wins.
                              showOf (update (v 0) "State" [ "done", b true ])
                              showOf (update (update (v 0) "State" [ "name", s "a" ]) "State" [ "name", s "b" ])
                              // A field value may read the source, including the field it replaces.
                              showOf (update (v 0) "State" [ "name", cat (field (v 0) "State" "name") (s "!"); "count", i 99L ])
                              // Every field at once is a full copy.
                              showOf (update (v 1) "State" [ "name", s "all"; "count", i 7L; "tags", list S [ s "z" ]; "done", b true ])
                              // The source itself is unchanged after being updated.
                              showOf (v 0) ])))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let result =
                PureRun.run reopened (find reopened "table").Id [ PureRunValue.String "run"; PureRunValue.StringList [ "x"; "y"; "z" ] ] None |> unwrap
            Assert.Equal(
                PureRunValue.StringList
                    [ "run:0::n"; "run:3:x,y,z:n"; "run:3:x,y,z:y"; "b:3:x,y,z:n"; "run!:99:x,y,z:n"; "all:7:z:y"; "run:3:x,y,z:n" ],
                result.Actual))
