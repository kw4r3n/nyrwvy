namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `ValueEqual(left, right): Bool`: structural equality of two values of one equatable type.
module ValueEqualTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private resultOf ok error = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ ok; error ])
    let private taskType = TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ textType ])
    let private str = ExpressionRecipe.StringLiteral
    let private int = ExpressionRecipe.Int64Literal
    let private slot = ExpressionRecipe.ParameterSlot
    let private equal left right = ExpressionRecipe.ValueEqual(left, right)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
          TypeParameters = None
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = []; Body = body }
    let private create session name parameters resultType body =
        request session name parameters resultType body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private rejects (session: AuthoringSession) name parameters body =
        match session.SimulateCreateFunction(request session name parameters boolType body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwithf "Function '%s' was accepted." name
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-value-equal-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private snapshot path = SnapshotCodec.deserialize(File.ReadAllText path) |> unwrap
    let private send session line =
        let response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private publishWire (session: AuthoringSession) (response: JsonDocument) =
        session.Commit(response.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore

    let private field name t: AgentTypeFieldIntent = { DisplayName = name; Type = t }
    let private createType (session: AuthoringSession) name kind =
        session.SimulateCreateType { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = current session; DisplayName = name; Kind = kind } |> unwrap |> publish session
        session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)
    let private product session name fields = createType session name (AgentTypeDeclarationKindIntent.Product(fields |> List.map (fun (n, t) -> field n t)))
    let private fieldOf (typeEntity: SemanticType) name =
        match typeEntity.Kind with
        | SemanticTypeKind.Product fields -> (fields |> List.find (fun item -> item.DisplayName = name)).Id
        | SemanticTypeKind.Union variants -> (variants |> List.collect (fun variant -> variant.Fields) |> List.find (fun item -> item.DisplayName = name)).Id
    let private variantOf (typeEntity: SemanticType) name =
        match typeEntity.Kind with
        | SemanticTypeKind.Union variants -> (variants |> List.find (fun variant -> variant.DisplayName = name)).Id
        | SemanticTypeKind.Product _ -> failwith "Expected a Union."

    /// Declared types covering Products with Option fields, Unions, a recursive Union and Products
    /// holding lists, plus types that are not equatable only through their fields.
    type private Fixture =
        { Key: SemanticType
          Shape: SemanticType
          Chain: SemanticType
          Box: SemanticType
          Holder: SemanticType }

    let private fixture (session: AuthoringSession) =
        let key = product session "Key" [ "role", textType; "backend", optionOf textType; "provider", optionOf textType ]
        let shape =
            createType session "Shape"
                (AgentTypeDeclarationKindIntent.Union
                    [ { DisplayName = "Circle"; Fields = [ field "radius" intType ] }
                      { DisplayName = "Square"; Fields = [ field "side" intType ] }
                      { DisplayName = "Dot"; Fields = [] } ])
        // A recursive Union: Link { value, tags, next: Chain } | End.
        let chainStart = createType session "Chain" (AgentTypeDeclarationKindIntent.Union [ { DisplayName = "End"; Fields = [] } ])
        session.SimulateAddUnionVariantLike(
            { ExpectedBaseSnapshotId = current session
              TargetTypeId = chainStart.Id
              SourceVariantId = variantOf chainStart "End"
              NewVariantDisplayName = "Link" },
            [ field "value" textType; field "tags" (listOf textType); field "next" (TypeRef.Named(chainStart.Id, [])) ])
        |> unwrap |> publish session
        let chain = session.Declarations().Types |> List.find (fun item -> item.Id = chainStart.Id)
        let box = product session "Box" [ "keys", listOf (TypeRef.Named(key.Id, [])); "outcome", resultOf intType textType ]
        let measure = product session "Measure" [ "label", textType; "value", floatType ]
        let holder = product session "Holder" [ "measures", listOf (TypeRef.Named(measure.Id, [])) ]
        { Key = key; Shape = shape; Chain = chain; Box = box; Holder = holder }

    [<Fact>]
    let ``EquatableTypes follows declared types transitively including recursion`` () =
        withSession (fun session _ ->
            let types = fixture session
            let all = session.Declarations().Types
            let equatable = EquatableTypes.isEquatable all
            for t in
                [ textType; intType; boolType; TypeRef.Primitive PrimitiveType.Unit
                  TypeRef.Named(types.Key.Id, []); TypeRef.Named(types.Shape.Id, []); TypeRef.Named(types.Chain.Id, []); TypeRef.Named(types.Box.Id, [])
                  listOf (optionOf (resultOf (TypeRef.Named(types.Key.Id, [])) (listOf textType))) ] do
                Assert.True(equatable t, sprintf "%A" t)
            for t in
                [ floatType; optionOf floatType; TypeRef.Function([ intType ], intType); taskType
                  TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
                  TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "t")
                  TypeRef.Named(types.Holder.Id, []); resultOf intType (TypeRef.Named(types.Holder.Id, [])) ] do
                Assert.False(equatable t, sprintf "%A" t)
            match EquatableTypes.firstNotEquatable all (optionOf (TypeRef.Named(types.Holder.Id, []))) with
            | Some(path, offending) ->
                Assert.Equal<string list>([ "Option value"; "Holder.measures"; "List item"; "Measure.value" ], path)
                Assert.Equal(floatType, offending)
            | None -> failwith "Holder was equatable."
            // A recursive type is not equatable when a field anywhere on the cycle is not.
            let recursiveId = StableId.createForTesting<TypeTag> "type-tree"
            let tree payload: SemanticType =
                { Exported = true; TypeParameters = []; Id = recursiveId; DisplayName = "Tree"
                  Kind =
                    SemanticTypeKind.Product
                        [ { Id = StableId.createForTesting<FieldTag> "field-children"; DisplayName = "children"; Type = listOf (TypeRef.Named(recursiveId, [])) }
                          { Id = StableId.createForTesting<FieldTag> "field-payload"; DisplayName = "payload"; Type = payload } ] }
            Assert.True(EquatableTypes.isEquatable [ tree textType ] (TypeRef.Named(recursiveId, [])))
            Assert.False(EquatableTypes.isEquatable [ tree floatType ] (TypeRef.Named(recursiveId, [])))
            Assert.Contains("Tree.payload", EquatableTypes.explain [ tree floatType ] (TypeRef.Named(recursiveId, []))))

    [<Fact>]
    let ``ValueEqual checks its operands in recipes and persisted graphs`` () =
        withSession (fun session _ ->
            let types = fixture session
            let named (t: SemanticType) = TypeRef.Named(t.Id, [])
            // Accepted: declared, recursive and nested builtin types.
            [ textType; boolType; TypeRef.Primitive PrimitiveType.Unit; named types.Key; named types.Shape; named types.Chain; named types.Box
              listOf (optionOf (resultOf (named types.Key) (listOf textType))) ]
            |> List.iteri (fun index t -> create session ("equal" + string index) [ "a", t; "b", t ] boolType (equal (slot 0) (slot 1)) |> ignore)
            // Rejected with the path to the offending part.
            let notEquatable = "ValueEqualTypeNotEquatable"
            let floatError = rejects session "floats" [ "a", floatType; "b", floatType ] (equal (slot 0) (slot 1))
            Assert.Contains(notEquatable, floatError)
            Assert.Contains("Float64Equal", floatError)
            let holderError = rejects session "holders" [ "a", named types.Holder; "b", named types.Holder ] (equal (slot 0) (slot 1))
            Assert.Contains(notEquatable, holderError)
            Assert.Contains("Holder.measures -> List item -> Measure.value", holderError)
            let functionType = TypeRef.Function([ intType ], intType)
            Assert.Contains(notEquatable, rejects session "functions" [ "a", listOf functionType; "b", listOf functionType ] (equal (slot 0) (slot 1)))
            Assert.Contains(notEquatable, rejects session "tasks" [ "a", optionOf taskType; "b", optionOf taskType ] (equal (slot 0) (slot 1)))
            let typeParameter = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "T")
            Assert.Contains(notEquatable, rejects session "generic" [ "a", typeParameter; "b", typeParameter ] (equal (slot 0) (slot 1)))
            // Operands of different types are an operand mismatch, as for other constructs.
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session "mixed" [ "a", textType; "b", intType ] (equal (slot 0) (slot 1)))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session "mixedOption" [ "a", optionOf textType; "b", textType ] (equal (slot 0) (slot 1)))
            // The result is Bool.
            match session.SimulateCreateFunction(request session "notText" [ "a", textType; "b", textType ] textType (equal (slot 0) (slot 1))) with
            | Ok _ -> failwith "A String result was accepted."
            | Error _ -> ()
            // Persisted graphs report their own codes.
            let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-value-equal-" + name)
            let graphWith leftKind rightKind =
                let body: ValueGraph =
                    { Nodes =
                        [ { Id = nodeId "left"; Kind = leftKind }
                          { Id = nodeId "right"; Kind = rightKind }
                          { Id = nodeId "equal"; Kind = SemanticNodeKind.ValueEqual(nodeId "left", nodeId "right") } ]
                      ResultNodeId = nodeId "equal" }
                let fn: SemanticFunction =
                    { Exported = true; Id = StableId.createForTesting<FunctionTag> "function-value-equal"
                      TypeParameters = []
                      DisplayName = "equal"; Parameters = []; ResultType = boolType; RequiredEffects = []; Body = body }
                SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-value-equal") with Functions = [ fn ] }
                |> List.map (fun issue -> issue.Code)
            Assert.Empty(graphWith (SemanticNodeKind.StringLiteral "x") (SemanticNodeKind.StringLiteral "y"))
            Assert.Empty(graphWith (SemanticNodeKind.ListEmpty(optionOf intType)) (SemanticNodeKind.ListEmpty(optionOf intType)))
            Assert.Contains("ValueEqualOperandTypeMismatch", graphWith (SemanticNodeKind.StringLiteral "x") (SemanticNodeKind.Int64Literal 1L))
            let floatLiteral = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0)
            Assert.Contains("ValueEqualTypeNotEquatable", graphWith floatLiteral floatLiteral)
            Assert.Contains("ValueEqualTypeNotEquatable", graphWith (SemanticNodeKind.ListEmpty floatType) (SemanticNodeKind.ListEmpty floatType)))

    [<Fact>]
    let ``exact-ID and name-resolved ValueEqual recipes persist reopen and project`` () =
        withSession (fun session path ->
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let optionText = sprintf """{"kind":"builtinGeneric","generic":"option","arguments":[%s]}""" (primitive "string")
            let signature = sprintf """"parameters":[{"displayName":"a","type":%s},{"displayName":"b","type":%s}],"resultType":%s,"requiredEffects":[]""" optionText optionText (primitive "bool")
            use exactProposal =
                send session (sprintf """{"id":"exact","op":"simulateCreateFunction","exported":true,"displayName":"same",%s,"body":{"kind":"valueEqual","left":{"kind":"parameterSlot","index":0},"right":{"kind":"parameterSlot","index":1}}}""" signature)
            publishWire session exactProposal
            use namedProposal =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"sameByName",%s,"body":{"kind":"valueEqual","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}}""" signature)
            Assert.Contains("\"kind\":\"valueEqual\"", namedProposal.RootElement.GetRawText())
            publishWire session namedProposal
            use missingRight =
                AuthoringProtocol.executeLine session (sprintf """{"id":"bad","op":"simulateCreateFunction","exported":true,"displayName":"bad",%s,"body":{"kind":"valueEqual","left":{"kind":"parameterSlot","index":0}}}""" signature) |> JsonDocument.Parse
            Assert.False(missingRight.RootElement.GetProperty("ok").GetBoolean())
            let named = find session "sameByName"
            let viewer = session.FocusFunction named.Id |> unwrap |> ViewerProjection.focus
            let node = viewer.Graph.Nodes |> List.find (fun item -> item.Id = named.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.Operation "ValueEqual", node.Role)
            Assert.Equal<ValueDependencyRole list>([ ValueDependencyRole.LeftOperand; ValueDependencyRole.RightOperand ], node.ValueDependencies |> List.map (fun item -> item.Role))
            Assert.Contains("value equal", ViewerHtml.renderFocusDocument viewer)
            session.Close()
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"valueEqual\"", encoded)
            let graph = snapshot path
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let restored = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"valueEqual\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"kind\":\"valueEqual\"", ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(named.Body.ResultNodeId, (find reopened "sameByName").Body.ResultNodeId))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ValueEqual compares values structurally through runPure`` () =
        withSession (fun session path ->
            let types = fixture session
            let keyType = TypeRef.Named(types.Key.Id, [])
            let sameKey = create session "sameKey" [ "a", keyType; "b", keyType ] boolType (equal (slot 0) (slot 1))
            let sameTexts = create session "sameTexts" [ "a", listOf textType; "b", listOf textType ] boolType (equal (slot 0) (slot 1))
            // Products holding lists: each side is built from its own list instance, so reference
            // equality of the lists would give false.
            let boxOf keys outcome =
                ExpressionRecipe.ProductConstruct(types.Box.Id, [], [ fieldOf types.Box "keys", keys; fieldOf types.Box "outcome", outcome ])
            let keyOf role backend =
                ExpressionRecipe.ProductConstruct(
                    types.Key.Id, [],
                    [ fieldOf types.Key "role", str role
                      fieldOf types.Key "backend", (match backend with Some text -> ExpressionRecipe.OptionSome(str text) | None -> ExpressionRecipe.OptionNone textType)
                      fieldOf types.Key "provider", ExpressionRecipe.OptionNone textType ])
            let keys roles = ExpressionRecipe.ListConstruct(keyType, roles |> List.map (fun (role, backend) -> keyOf role backend))
            let ok value = ExpressionRecipe.ResultOk(textType, int value)
            let boxes =
                [ "boxesSame", boxOf (keys [ "a", Some "x"; "b", None ]) (ok 1L), boxOf (keys [ "a", Some "x"; "b", None ]) (ok 1L), true
                  "boxesOrder", boxOf (keys [ "a", Some "x"; "b", None ]) (ok 1L), boxOf (keys [ "b", None; "a", Some "x" ]) (ok 1L), false
                  "boxesLength", boxOf (keys [ "a", None ]) (ok 1L), boxOf (keys [ "a", None; "a", None ]) (ok 1L), false
                  "boxesOption", boxOf (keys [ "a", Some "x" ]) (ok 1L), boxOf (keys [ "a", None ]) (ok 1L), false
                  "boxesResult", boxOf (keys []) (ok 1L), boxOf (keys []) (ExpressionRecipe.ResultErr(intType, str "1")), false ]
            let shape variant fields =
                ExpressionRecipe.UnionConstruct(variantOf types.Shape variant, [], fields |> List.map (fun (name, value) -> fieldOf types.Shape name, int value))
            let shapes =
                [ "shapesSame", shape "Circle" [ "radius", 1L ], shape "Circle" [ "radius", 1L ], true
                  "shapesField", shape "Circle" [ "radius", 1L ], shape "Circle" [ "radius", 2L ], false
                  "shapesVariant", shape "Circle" [ "radius", 1L ], shape "Square" [ "side", 1L ], false
                  "shapesEmpty", shape "Dot" [], shape "Dot" [], true ]
            let rec chainOf (values: (string * string list) list) =
                match values with
                | [] -> ExpressionRecipe.UnionConstruct(variantOf types.Chain "End", [], [])
                | (value, tags) :: rest ->
                    ExpressionRecipe.UnionConstruct(
                        variantOf types.Chain "Link", [],
                        [ fieldOf types.Chain "value", str value
                          fieldOf types.Chain "tags", ExpressionRecipe.ListConstruct(textType, tags |> List.map str)
                          fieldOf types.Chain "next", chainOf rest ])
            let chains =
                [ "chainsSame", chainOf [ "a", [ "t" ]; "b", []; "c", [ "u"; "v" ] ], chainOf [ "a", [ "t" ]; "b", []; "c", [ "u"; "v" ] ], true
                  "chainsDeepTag", chainOf [ "a", [ "t" ]; "b", []; "c", [ "u"; "v" ] ], chainOf [ "a", [ "t" ]; "b", []; "c", [ "v"; "u" ] ], false
                  "chainsLonger", chainOf [ "a", [] ], chainOf [ "a", []; "b", [] ], false
                  "chainsEnd", chainOf [], chainOf [], true ]
            let constant = [ for name, left, right, expected in boxes @ shapes @ chains -> create session name [] boolType (equal left right), expected ]
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            for fn, expected in constant do
                Assert.True(PureRunValue.Bool expected = run fn [], fn.DisplayName)
            let key role backend provider =
                PureRunValue.Product(
                    types.Key.Id, [],
                    [ fieldOf types.Key "role", PureRunValue.String role
                      fieldOf types.Key "backend", PureRunValue.StringOption backend
                      fieldOf types.Key "provider", PureRunValue.StringOption provider ])
            for left, right, expected in
                [ key "member" (Some "codex") None, key "member" (Some "codex") None, true
                  key "member" None None, key "member" None None, true
                  key "member" (Some "codex") None, key "leader" (Some "codex") None, false
                  key "member" (Some "codex") None, key "member" None None, false
                  key "member" None (Some "x"), key "member" None (Some "y"), false
                  key "member" (Some "") None, key "member" None None, false ] do
                Assert.Equal(PureRunValue.Bool expected, run sameKey [ left; right ])
            for left, right, expected in
                [ [ "a"; "b" ], [ "a"; "b" ], true
                  [ "a"; "b" ], [ "b"; "a" ], false
                  [ "a" ], [ "a"; "a" ], false
                  [], [], true
                  [ "A" ], [ "a" ], false ] do
                Assert.Equal(PureRunValue.Bool expected, run sameTexts [ PureRunValue.StringList left; PureRunValue.StringList right ]))
