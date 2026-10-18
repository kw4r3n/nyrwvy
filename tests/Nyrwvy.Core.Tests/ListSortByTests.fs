namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `StringLessThan(left, right): Bool` (ordinal code-point order) and
/// `ListSortBy(list, lessThan, context)`: a stable top-down merge sort under a pure callback.
module ListSortByTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private sortBy source (callback: SemanticFunction) = ExpressionRecipe.ListSortBy(source, callback.Id, [])
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = effects; Body = body }
    let private createWith session name parameters resultType effects body =
        request session name parameters resultType effects body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private create session name parameters resultType body = createWith session name parameters resultType [] body
    let private rejects (session: AuthoringSession) parameters resultType body =
        match session.SimulateCreateFunction(request session "rejected" parameters resultType [] body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwith "The function was accepted."
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-sort-by-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private send session line =
        let response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private publishWire (session: AuthoringSession) (response: JsonDocument) =
        session.Commit(response.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private fieldOf (typeEntity: SemanticType) name =
        match typeEntity.Kind with
        | SemanticTypeKind.Product fields -> (fields |> List.find (fun item -> item.DisplayName = name)).Id
        | SemanticTypeKind.Union _ -> failwith "Expected a Product."

    /// Callbacks shared by the tests: String order, length order, descending Int64 and constant ones.
    type private Callbacks =
        { TextLess: SemanticFunction
          ShorterText: SemanticFunction
          IntGreater: SemanticFunction
          AlwaysTrue: SemanticFunction
          AlwaysFalse: SemanticFunction }

    let private callbacks session =
        let pair t = [ "left", t; "right", t ]
        { TextLess = create session "textLess" (pair textType) boolType (ExpressionRecipe.StringLessThan(slot 0, slot 1))
          ShorterText =
            create session "shorterText" (pair textType) boolType
                (ExpressionRecipe.Int64LessThan(ExpressionRecipe.StringLength(slot 0), ExpressionRecipe.StringLength(slot 1)))
          IntGreater = create session "intGreater" (pair intType) boolType (ExpressionRecipe.Int64GreaterThan(slot 0, slot 1))
          AlwaysTrue = create session "alwaysTrue" (pair intType) boolType (ExpressionRecipe.BoolLiteral true)
          AlwaysFalse = create session "alwaysFalse" (pair intType) boolType (ExpressionRecipe.BoolLiteral false) }

    [<Fact>]
    let ``StringLessThan and ListSortBy check their operands and callbacks in recipes`` () =
        withSession (fun session _ ->
            let fns = callbacks session
            create session "sortTexts" [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) fns.TextLess) |> ignore
            // StringLessThan takes two Strings and yields Bool.
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "a", textType; "b", intType ] boolType (ExpressionRecipe.StringLessThan(slot 0, slot 1)))
            rejects session [ "a", textType; "b", textType ] textType (ExpressionRecipe.StringLessThan(slot 0, slot 1)) |> ignore
            // The source must be a List whose items both callback parameters accept.
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "items", textType ] (listOf textType) (sortBy (slot 0) fns.TextLess))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", rejects session [ "items", listOf intType ] (listOf intType) (sortBy (slot 0) fns.TextLess))
            // The result is the source list type.
            rejects session [ "items", listOf textType ] textType (sortBy (slot 0) fns.TextLess) |> ignore
            // Wrong callback shapes: one parameter, a non-Bool result, a mixed signature, an unknown function.
            let single = create session "single" [ "value", textType ] boolType (ExpressionRecipe.StringIsEmpty(slot 0))
            let lengthDiff =
                create session "lengthDiff" [ "left", textType; "right", textType ] intType
                    (ExpressionRecipe.Int64WrappingSubtract(ExpressionRecipe.StringLength(slot 0), ExpressionRecipe.StringLength(slot 1)))
            let mixed = create session "mixed" [ "left", textType; "right", intType ] boolType (ExpressionRecipe.BoolLiteral true)
            Assert.Contains("PureExpressionCallbackArityMismatch", rejects session [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) single))
            Assert.Contains("PureExpressionCallbackResultTypeMismatch", rejects session [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) lengthDiff))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", rejects session [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) mixed))
            let unknown = StableId.createForTesting<FunctionTag> "function-missing"
            Assert.Contains("PureExpressionCallbackNotFound", rejects session [ "items", listOf textType ] (listOf textType) (ExpressionRecipe.ListSortBy(slot 0, unknown, [])))
            // A comparator with required effects is rejected, as for ListFilter.
            session.SimulateCreateEffect { ExpectedBaseSnapshotId = current session; DisplayName = "Clock" } |> unwrap |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let effectful = createWith session "effectfulLess" [ "left", textType; "right", textType ] boolType [ effect.Id ] (ExpressionRecipe.BoolLiteral true)
            Assert.Contains("PureExpressionCallbackEffectsNotAllowed", rejects session [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) effectful))
            // Context binds a leading parameter; the two remaining ones receive the items.
            let offsetLess =
                create session "offsetLess" [ "offset", intType; "left", intType; "right", intType ] boolType
                    (ExpressionRecipe.Int64LessThan(ExpressionRecipe.Int64WrappingAdd(slot 0, slot 1), slot 2))
            let offset = (offsetLess.Parameters |> List.head).Id
            create session "sortOffset" [ "items", listOf intType ] (listOf intType)
                (ExpressionRecipe.ListSortBy(slot 0, offsetLess.Id, [ offset, ExpressionRecipe.Int64Literal 1L ])) |> ignore
            Assert.Contains("PureExpressionCallbackArityMismatch", rejects session [ "items", listOf intType ] (listOf intType) (ExpressionRecipe.ListSortBy(slot 0, offsetLess.Id, []))))

    [<Fact>]
    let ``persisted StringLessThan and ListSortBy graphs report their own codes`` () =
        let id name = StableId.createForTesting<FunctionTag> ("function-sort-" + name)
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-sort-" + name)
        let parameter owner name t: SemanticParameter = { Id = StableId.createForTesting<ParameterTag> ("parameter-" + owner + "-" + name); DisplayName = name; Type = t }
        let effectId = StableId.createForTesting<EffectTag> "effect-sort-clock"
        let callback name parameterTypes resultType effects: SemanticFunction =
            { Id = id name; DisplayName = name
              Parameters = parameterTypes |> List.mapi (fun index t -> parameter name (string index) t)
              ResultType = resultType; RequiredEffects = effects
              Body = { Nodes = [ { Id = nodeId (name + "-body"); Kind = SemanticNodeKind.BoolLiteral true } ]; ResultNodeId = nodeId (name + "-body") } }
        let fns =
            [ callback "less" [ textType; textType ] boolType []
              callback "single" [ textType ] boolType []
              callback "wrongItem" [ intType; intType ] boolType []
              callback "notBool" [ textType; textType ] boolType []
              callback "effectful" [ textType; textType ] boolType [ effectId ] ]
            |> List.map (fun fn ->
                if fn.DisplayName = "notBool" then
                    { fn with ResultType = intType; Body = { Nodes = [ { Id = nodeId "notBool-body"; Kind = SemanticNodeKind.Int64Literal 0L } ]; ResultNodeId = nodeId "notBool-body" } }
                else fn)
        let validate nodes resultType resultNodeId =
            let fn: SemanticFunction =
                { Id = id "entry"; DisplayName = "entry"; Parameters = []; ResultType = resultType; RequiredEffects = []
                  Body = { Nodes = nodes; ResultNodeId = resultNodeId } }
            let effect: SemanticEffect = { Id = effectId; DisplayName = "Clock" }
            SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-sort") with Functions = fn :: fns; Effects = [ effect ] }
            |> List.map (fun issue -> issue.Code)
        let sortWith source sourceType callbackName =
            validate
                [ { Id = nodeId "source"; Kind = source }
                  { Id = nodeId "sort"; Kind = SemanticNodeKind.ListSortBy(nodeId "source", id callbackName, []) } ]
                sourceType (nodeId "sort")
        let texts = SemanticNodeKind.ListEmpty textType
        Assert.Empty(sortWith texts (listOf textType) "less")
        Assert.Contains("CallbackArityMismatch", sortWith texts (listOf textType) "single")
        Assert.Contains("CallbackParameterTypeMismatch", sortWith texts (listOf textType) "wrongItem")
        Assert.Contains("PredicateResultTypeMismatch", sortWith texts (listOf textType) "notBool")
        Assert.Contains("CallbackEffectsNotAllowed", sortWith texts (listOf textType) "effectful")
        Assert.Contains("UnknownCallbackFunction", sortWith texts (listOf textType) "missing")
        Assert.Contains("HigherOrderSourceTypeMismatch", sortWith (SemanticNodeKind.StringLiteral "x") (listOf textType) "less")
        let compare leftKind rightKind =
            validate
                [ { Id = nodeId "left"; Kind = leftKind }
                  { Id = nodeId "right"; Kind = rightKind }
                  { Id = nodeId "less"; Kind = SemanticNodeKind.StringLessThan(nodeId "left", nodeId "right") } ]
                boolType (nodeId "less")
        Assert.Empty(compare (SemanticNodeKind.StringLiteral "a") (SemanticNodeKind.StringLiteral "b"))
        Assert.Contains("StringIntrinsicTypeMismatch", compare (SemanticNodeKind.StringLiteral "a") (SemanticNodeKind.Int64Literal 1L))

    [<Fact>]
    let ``exact-ID and name-resolved recipes persist reopen and project`` () =
        withSession (fun session path ->
            let fns = callbacks session
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let texts = sprintf """{"kind":"builtinGeneric","generic":"list","arguments":[%s]}""" (primitive "string")
            let signature = sprintf """"parameters":[{"displayName":"items","type":%s}],"resultType":%s,"requiredEffects":[]""" texts texts
            use exactProposal =
                send session (sprintf """{"id":"exact","op":"simulateCreateFunction","displayName":"sortExact",%s,"body":{"kind":"listSortBy","source":{"kind":"parameterSlot","index":0},"functionId":"%s"}}""" signature (StableId.value fns.TextLess.Id))
            publishWire session exactProposal
            use namedProposal =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"sortByName",%s,"body":{"kind":"listSortBy","source":{"kind":"parameter","name":"items"},"function":"shorterText"}}""" signature)
            Assert.Contains("\"kind\":\"listSortBy\"", namedProposal.RootElement.GetRawText())
            publishWire session namedProposal
            let boolSignature = sprintf """"parameters":[{"displayName":"a","type":%s},{"displayName":"b","type":%s}],"resultType":%s,"requiredEffects":[]""" (primitive "string") (primitive "string") (primitive "bool")
            use lessProposal =
                send session (sprintf """{"id":"less","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"lessByName",%s,"body":{"kind":"stringLessThan","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}}""" boolSignature)
            Assert.Contains("\"kind\":\"stringLessThan\"", lessProposal.RootElement.GetRawText())
            publishWire session lessProposal
            use missingFunction =
                AuthoringProtocol.executeLine session (sprintf """{"id":"bad","op":"simulateCreateFunction","displayName":"bad",%s,"body":{"kind":"listSortBy","source":{"kind":"parameterSlot","index":0}}}""" signature) |> JsonDocument.Parse
            Assert.False(missingFunction.RootElement.GetProperty("ok").GetBoolean())
            let named = find session "sortByName"
            let viewer = session.FocusFunction named.Id |> unwrap |> ViewerProjection.focus
            let node = viewer.Graph.Nodes |> List.find (fun item -> item.Id = named.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.ListSortBy, node.Role)
            Assert.Contains("List sort by", ViewerHtml.renderFocusDocument viewer)
            let lessFn = find session "lessByName"
            let lessViewer = session.FocusFunction lessFn.Id |> unwrap |> ViewerProjection.focus
            let lessNode = lessViewer.Graph.Nodes |> List.find (fun item -> item.Id = lessFn.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.StringLessThan, lessNode.Role)
            Assert.Equal<ValueDependencyRole list>([ ValueDependencyRole.LeftOperand; ValueDependencyRole.RightOperand ], lessNode.ValueDependencies |> List.map (fun item -> item.Role))
            session.Close()
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"listSortBy\"", encoded)
            Assert.Contains("\"lessThanFunctionId\"", encoded)
            Assert.Contains("\"kind\":\"stringLessThan\"", encoded)
            let graph = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            Assert.Contains("list-sort-by", nyason)
            Assert.Contains("string-less-than", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            let typedJson = TypedSemanticIrSerializer.serialize typed
            Assert.Contains("\"kind\":\"listSortBy\"", typedJson)
            Assert.Contains("\"kind\":\"stringLessThan\"", typedJson)
            let executionJson = ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize
            Assert.Contains("\"kind\":\"listSortBy\"", executionJson)
            Assert.Contains("\"kind\":\"stringLessThan\"", executionJson)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(named.Body.ResultNodeId, (find reopened "sortByName").Body.ResultNodeId))

    [<Fact; Trait("Suite", "Extended")>]
    let ``StringLessThan and ListSortBy run through runPure`` () =
        withSession (fun session path ->
            let fns = callbacks session
            let row =
                session.SimulateCreateType
                    { TypeParameters = []; ExpectedBaseSnapshotId = current session; DisplayName = "Row"
                      Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "role"; Type = textType }; { DisplayName = "count"; Type = intType } ] }
                |> unwrap |> publish session
                session.Declarations().Types |> List.find (fun item -> item.DisplayName = "Row")
            let rowType = TypeRef.Named(row.Id, [])
            let roleOf value = ExpressionRecipe.FieldAccess(value, fieldOf row "role")
            let countOf value = ExpressionRecipe.FieldAccess(value, fieldOf row "count")
            // Python: rows.sort(key=lambda r: (r.role, -r.count)).
            let roleThenCountDescending =
                create session "roleThenCountDescending" [ "left", rowType; "right", rowType ] boolType
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.StringEqual(roleOf (slot 0), roleOf (slot 1)),
                        ExpressionRecipe.Int64GreaterThan(countOf (slot 0), countOf (slot 1)),
                        ExpressionRecipe.StringLessThan(roleOf (slot 0), roleOf (slot 1))))
            let less = create session "less" [ "a", textType; "b", textType ] boolType (ExpressionRecipe.StringLessThan(slot 0, slot 1))
            let sortTexts = create session "sortTexts" [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) fns.TextLess)
            let sortByLength = create session "sortByLength" [ "items", listOf textType ] (listOf textType) (sortBy (slot 0) fns.ShorterText)
            let sortDescending = create session "sortDescending" [ "items", listOf intType ] (listOf intType) (sortBy (slot 0) fns.IntGreater)
            let sortAlwaysTrue = create session "sortAlwaysTrue" [ "items", listOf intType ] (listOf intType) (sortBy (slot 0) fns.AlwaysTrue)
            let sortAlwaysFalse = create session "sortAlwaysFalse" [ "items", listOf intType ] (listOf intType) (sortBy (slot 0) fns.AlwaysFalse)
            let sortRows = create session "sortRows" [ "rows", listOf rowType ] (listOf rowType) (sortBy (slot 0) roleThenCountDescending)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            // Code-point order: U+1F600 sorts after U+E000 and U+FFFF, where UTF-16 code-unit order
            // (0xD83D ...) would put it first; a proper prefix sorts first; case is significant.
            let emoji = "\U0001F600"
            for left, right, expected in
                [ "a", "b", true; "b", "a", false; "a", "a", false; "", "a", true; "a", "", false; "ab", "abc", true
                  "B", "a", true; "\uFFFF", emoji, true; emoji, "\uFFFF", false; "\uE000", emoji, true; emoji, "\U0001F601", true
                  "a" + emoji, "a\uFFFF", false ] do
                Assert.True(PureRunValue.Bool expected = run less [ PureRunValue.String left; PureRunValue.String right ], sprintf "%A < %A" left right)
            let texts items = PureRunValue.StringList items
            Assert.Equal(texts [ ""; "B"; "a"; "ab"; "b"; "\u00E9"; "\uE000"; "\uFFFF"; emoji ],
                         run sortTexts [ texts [ emoji; "b"; "\uFFFF"; "a"; "\u00E9"; ""; "\uE000"; "ab"; "B" ] ])
            Assert.Equal(texts [], run sortTexts [ texts [] ])
            Assert.Equal(texts [ "only" ], run sortTexts [ texts [ "only" ] ])
            // Stable: items with equal keys keep their input order.
            Assert.Equal(texts [ "a"; "b"; "c"; "bb"; "cc"; "aa"; "ccc" ], run sortByLength [ texts [ "bb"; "a"; "ccc"; "cc"; "b"; "aa"; "c" ] ])
            Assert.Equal(PureRunValue.Int64List [ 9L; 5L; 5L; 3L; 1L; 1L; 0L; -2L ], run sortDescending [ PureRunValue.Int64List [ 1L; 5L; -2L; 9L; 0L; 5L; 3L; 1L ] ])
            // An inconsistent callback still yields the permutation the merge sort defines: always
            // taking the right head reverses the list, never taking it keeps the input order.
            Assert.Equal(PureRunValue.Int64List [ 5L; 4L; 3L; 2L; 1L ], run sortAlwaysTrue [ PureRunValue.Int64List [ 1L; 2L; 3L; 4L; 5L ] ])
            Assert.Equal(PureRunValue.Int64List [ 3L; 1L; 2L ], run sortAlwaysFalse [ PureRunValue.Int64List [ 3L; 1L; 2L ] ])
            let rowValue (role: string) (count: int64) =
                PureRunValue.Product(row.Id, [], [ fieldOf row "role", PureRunValue.String role; fieldOf row "count", PureRunValue.Int64 count ])
            let rows items = PureRunValue.ListValue(rowType, items |> List.map (fun (role, count) -> rowValue role count))
            let input = [ "worker", 2L; "leader", 1L; "worker", 5L; "reviewer", 3L; "leader", 4L; "worker", 2L; "auditor", 1L ]
            let expected = input |> List.sortBy (fun (role, count) -> role, -count)
            let pairs value =
                match value with
                | PureRunValue.ListValue(_, items) ->
                    items |> List.map (function
                        | PureRunValue.Product(_, _, fields) ->
                            let fieldValue name = fields |> List.find (fun (fieldId, _) -> fieldId = fieldOf row name) |> snd
                            (match fieldValue "role" with PureRunValue.String role -> role | other -> failwithf "%A" other),
                            (match fieldValue "count" with PureRunValue.Int64 count -> count | other -> failwithf "%A" other)
                        | other -> failwithf "%A" other)
                | other -> failwithf "%A" other
            Assert.Equal<(string * int64) list>(expected, run sortRows [ rows input ] |> pairs))

    /// `ListMinBy(list, lessThan, context): Option<T>` (order 48): the first smallest item.
    [<Fact>]
    let ``ListMinBy checks its operands and callbacks in recipes and persisted graphs`` () =
        withSession (fun session path ->
            let fns = callbacks session
            let minBy source (callback: SemanticFunction) = ExpressionRecipe.ListMinBy(source, callback.Id, [])
            let optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
            let named = create session "minText" [ "items", listOf textType ] (optionOf textType) (minBy (slot 0) fns.TextLess)
            // The result is Option<T>, not T or List<T>; the source must be a List whose items both
            // callback parameters accept.
            rejects session [ "items", listOf textType ] textType (minBy (slot 0) fns.TextLess) |> ignore
            rejects session [ "items", listOf textType ] (listOf textType) (minBy (slot 0) fns.TextLess) |> ignore
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "items", textType ] (optionOf textType) (minBy (slot 0) fns.TextLess))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", rejects session [ "items", listOf intType ] (optionOf intType) (minBy (slot 0) fns.TextLess))
            let single = create session "single" [ "value", textType ] boolType (ExpressionRecipe.StringIsEmpty(slot 0))
            let lengthDiff =
                create session "lengthDiff" [ "left", textType; "right", textType ] intType
                    (ExpressionRecipe.Int64WrappingSubtract(ExpressionRecipe.StringLength(slot 0), ExpressionRecipe.StringLength(slot 1)))
            Assert.Contains("PureExpressionCallbackArityMismatch", rejects session [ "items", listOf textType ] (optionOf textType) (minBy (slot 0) single))
            Assert.Contains("PureExpressionCallbackResultTypeMismatch", rejects session [ "items", listOf textType ] (optionOf textType) (minBy (slot 0) lengthDiff))
            let unknown = StableId.createForTesting<FunctionTag> "function-missing"
            Assert.Contains("PureExpressionCallbackNotFound", rejects session [ "items", listOf textType ] (optionOf textType) (ExpressionRecipe.ListMinBy(slot 0, unknown, [])))

            // The persisted node keeps its kind through every codec and projection.
            let viewer = session.FocusFunction named.Id |> unwrap |> ViewerProjection.focus
            let node = viewer.Graph.Nodes |> List.find (fun item -> item.Id = named.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.ListMinBy, node.Role)
            Assert.Contains("List min by", ViewerHtml.renderFocusDocument viewer)
            session.Close()
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"listMinBy\"", encoded)
            let graph = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            Assert.Contains("list-min-by", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"listMinBy\"", TypedSemanticIrSerializer.serialize typed)
            let executionJson = ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize
            Assert.Contains("\"kind\":\"listMinBy\"", executionJson)

            // Persisted graphs report the same codes as ListSortBy.
            let entry = graph.Functions |> List.find (fun fn -> fn.Id = named.Id)
            let withCallback callbackId =
                let nodes =
                    entry.Body.Nodes
                    |> List.map (fun item ->
                        match item.Kind with
                        | SemanticNodeKind.ListMinBy(source, _, context) -> { item with Kind = SemanticNodeKind.ListMinBy(source, callbackId, context) }
                        | _ -> item)
                let changed = { entry with Body = { entry.Body with Nodes = nodes } }
                SnapshotValidator.validate { graph with Functions = graph.Functions |> List.map (fun fn -> if fn.Id = entry.Id then changed else fn) }
                |> List.map (fun issue -> issue.Code)
            Assert.Contains("CallbackArityMismatch", withCallback single.Id)
            Assert.Contains("PredicateResultTypeMismatch", withCallback lengthDiff.Id)
            Assert.Contains("CallbackParameterTypeMismatch", withCallback fns.IntGreater.Id)
            Assert.Contains("UnknownCallbackFunction", withCallback unknown))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListMinBy keeps the first smallest item and runs through runPure`` () =
        withSession (fun session path ->
            let fns = callbacks session
            let optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
            let minBy source (callback: SemanticFunction) = ExpressionRecipe.ListMinBy(source, callback.Id, [])
            let minText = create session "minText" [ "items", listOf textType ] (optionOf textType) (minBy (slot 0) fns.TextLess)
            let shortest = create session "shortest" [ "items", listOf textType ] (optionOf textType) (minBy (slot 0) fns.ShorterText)
            // A maximum is the minimum under the swapped comparison.
            let largest = create session "largest" [ "items", listOf intType ] (optionOf intType) (minBy (slot 0) fns.IntGreater)
            let minAlwaysTrue = create session "minAlwaysTrue" [ "items", listOf intType ] (optionOf intType) (minBy (slot 0) fns.AlwaysTrue)
            let minAlwaysFalse = create session "minAlwaysFalse" [ "items", listOf intType ] (optionOf intType) (minBy (slot 0) fns.AlwaysFalse)
            // Context binds the target; the item closest to it wins, the first on a tie.
            let distance a b = ExpressionRecipe.Int64WrappingAbs(ExpressionRecipe.Int64WrappingSubtract(a, b))
            let closer =
                create session "closer" [ "target", intType; "left", intType; "right", intType ] boolType
                    (ExpressionRecipe.Int64LessThan(distance (slot 1) (slot 0), distance (slot 2) (slot 0)))
            let target = (closer.Parameters |> List.head).Id
            let closest =
                create session "closest" [ "items", listOf intType; "target", intType ] (optionOf intType)
                    (ExpressionRecipe.ListMinBy(slot 0, closer.Id, [ target, slot 1 ]))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let texts items = PureRunValue.StringList items
            let ints items = PureRunValue.Int64List items
            Assert.Equal(PureRunValue.StringOption None, run minText [ texts [] ])
            Assert.Equal(PureRunValue.StringOption(Some "only"), run minText [ texts [ "only" ] ])
            Assert.Equal(PureRunValue.StringOption(Some "apple"), run minText [ texts [ "pear"; "apple"; "fig"; "apple" ] ])
            // Ties: the first of the equally short items wins.
            Assert.Equal(PureRunValue.StringOption(Some "bb"), run shortest [ texts [ "ccc"; "bb"; "aa"; "dddd"; "ee" ] ])
            Assert.Equal(PureRunValue.Int64Option(Some 9L), run largest [ ints [ 1L; 5L; -2L; 9L; 0L; 9L ] ])
            Assert.Equal(PureRunValue.Int64Option None, run largest [ ints [] ])
            // Any callback gives a deterministic result: always true keeps replacing, so the last
            // item wins; always false never replaces, so the first item wins.
            Assert.Equal(PureRunValue.Int64Option(Some 5L), run minAlwaysTrue [ ints [ 1L; 2L; 3L; 4L; 5L ] ])
            Assert.Equal(PureRunValue.Int64Option(Some 3L), run minAlwaysFalse [ ints [ 3L; 1L; 2L ] ])
            Assert.Equal(PureRunValue.Int64Option(Some 8L), run closest [ ints [ 1L; 8L; 12L; 15L ]; PureRunValue.Int64 10L ])
            Assert.Equal(PureRunValue.Int64Option(Some 12L), run closest [ ints [ 1L; 12L; 8L; 15L ]; PureRunValue.Int64 10L ]))
