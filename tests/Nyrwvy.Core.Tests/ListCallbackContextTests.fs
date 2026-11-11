namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// G2: List callbacks that receive explicit context arguments besides the iteration values.
module ListCallbackContextTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("callback-context-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private intList = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private fnId name = id<FunctionTag> name
    let private paramId name = id<ParameterTag> name

    let private makeFn name parameters resultType body : SemanticFunction =
        { Id = fnId name
          DisplayName = name
          Exported = true
          MustNotWait = false
          TypeParameters = []
          Parameters = parameters |> List.map (fun (pid, displayName, itemType) -> { Id = pid; DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = []
          Body = body }

    let private buildFor prefix tree = GraphBuilder.build (fun path -> id<GraphNodeTag> (prefix + "-" + path)) tree

    let private find snapshot name = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private parameter (fn: SemanticFunction) name = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id

    /// Callbacks take a context parameter first; the same callbacks serve any runtime threshold or factor.
    let private fixture () =
        let above =
            makeFn "above"
                [ paramId "above-threshold", "threshold", intType; paramId "above-item", "item", intType ] boolType
                (buildFor "above"
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.Int64GreaterThan(ids[1], ids[0]))
                        [ GraphBuilder.param (paramId "above-threshold"); GraphBuilder.param (paramId "above-item") ]))
        let scale =
            makeFn "scale"
                [ paramId "scale-factor", "factor", intType; paramId "scale-item", "item", intType ] intType
                (buildFor "scale"
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.Int64WrappingMultiply(ids[0], ids[1]))
                        [ GraphBuilder.param (paramId "scale-factor"); GraphBuilder.param (paramId "scale-item") ]))
        let sumAbove =
            let node name kind = { Id = id<GraphNodeTag> name; Kind = kind }
            let paramRef name pid = node name (SemanticNodeKind.ParameterReference pid)
            let condId = id<GraphNodeTag> "sumAbove-cond"
            let whenTrue: ValueGraph =
                let addId = id<GraphNodeTag> "sumAbove-true-add"
                { Nodes =
                    [ paramRef "sumAbove-true-total" (paramId "sumAbove-total")
                      paramRef "sumAbove-true-item" (paramId "sumAbove-item")
                      node "sumAbove-true-add" (SemanticNodeKind.Int64WrappingAdd(id<GraphNodeTag> "sumAbove-true-total", id<GraphNodeTag> "sumAbove-true-item")) ]
                  ResultNodeId = addId }
            let whenFalse: ValueGraph =
                { Nodes = [ paramRef "sumAbove-false-total" (paramId "sumAbove-total") ]
                  ResultNodeId = id<GraphNodeTag> "sumAbove-false-total" }
            let body: ValueGraph =
                { Nodes =
                    [ paramRef "sumAbove-cond-threshold" (paramId "sumAbove-threshold")
                      paramRef "sumAbove-cond-item" (paramId "sumAbove-item")
                      { Id = condId; Kind = SemanticNodeKind.Int64GreaterThan(id<GraphNodeTag> "sumAbove-cond-item", id<GraphNodeTag> "sumAbove-cond-threshold") }
                      node "sumAbove-root" (SemanticNodeKind.Conditional(condId, whenTrue, whenFalse)) ]
                  ResultNodeId = id<GraphNodeTag> "sumAbove-root" }
            makeFn "sumAbove"
                [ paramId "sumAbove-threshold", "threshold", intType; paramId "sumAbove-total", "total", intType; paramId "sumAbove-item", "item", intType ]
                intType body
        let withContext name kindOf callbackId targetId resultType =
            makeFn name
                [ paramId (name + "-items"), "items", intList; paramId (name + "-threshold"), "threshold", intType ] resultType
                (buildFor name
                    (GraphBuilder.node
                        (fun ids -> kindOf ids[0] callbackId [ { TargetParameterId = targetId; ValueNodeId = ids[1] } ])
                        [ GraphBuilder.param (paramId (name + "-items")); GraphBuilder.param (paramId (name + "-threshold")) ]))
        let filterAbove = withContext "filterAbove" (fun list cb ctx -> SemanticNodeKind.ListFilter(list, cb, ctx)) (fnId "above") (paramId "above-threshold") intList
        let scaleAll = withContext "scaleAll" (fun list cb ctx -> SemanticNodeKind.ListMap(list, cb, ctx)) (fnId "scale") (paramId "scale-factor") intList
        let anyAbove = withContext "anyAbove" (fun list cb ctx -> SemanticNodeKind.ListAny(list, cb, ctx)) (fnId "above") (paramId "above-threshold") boolType
        let allAbove = withContext "allAbove" (fun list cb ctx -> SemanticNodeKind.ListAll(list, cb, ctx)) (fnId "above") (paramId "above-threshold") boolType
        let totalAbove =
            makeFn "totalAbove"
                [ paramId "totalAbove-items", "items", intList; paramId "totalAbove-threshold", "threshold", intType ] intType
                (buildFor "totalAbove"
                    (GraphBuilder.node
                        (fun ids -> SemanticNodeKind.ListFold(ids[0], ids[1], fnId "sumAbove", [ { TargetParameterId = paramId "sumAbove-threshold"; ValueNodeId = ids[2] } ]))
                        [ GraphBuilder.param (paramId "totalAbove-items"); GraphBuilder.leaf (SemanticNodeKind.Int64Literal 0L); GraphBuilder.param (paramId "totalAbove-threshold") ]))
        let plainLength =
            makeFn "plainLength" [ paramId "plainLength-items", "items", intList ] intType
                (buildFor "plainLength"
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.ListLength ids[0]) [ GraphBuilder.param (paramId "plainLength-items") ]))
        { Snapshot.empty (id<SnapshotTag> "base") with
            Functions = [ above; scale; sumAbove; filterAbove; scaleAll; totalAbove; anyAbove; allAbove; plainLength ] }

    [<Fact>]
    let ``context callbacks author persist and keep context-free graphs byte-identical`` () =
        let snapshot = fixture ()
        Assert.Empty(SnapshotValidator.validate snapshot)
        let filterAbove = find snapshot "filterAbove"
        let bindings =
            ValueGraph.collectNodes filterAbove.Body |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.ListFilter(_, _, context) -> Some context
                | _ -> None)
        Assert.Equal<ParameterId list>([ parameter (find snapshot "above") "threshold" ], bindings |> List.map (fun binding -> binding.TargetParameterId))
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        Assert.Contains("\"context\"", json)
        let restored = SnapshotCodec.deserialize json |> unwrap "restore"
        Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap "reserialize")
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (SnapshotCodec.deserialize json |> unwrap "restore"))
        let typed = TypedSemanticIr.project snapshot |> unwrap "typed"
        Assert.Contains("\"context\"", TypedSemanticIrSerializer.serialize typed)
        Assert.Contains("\"context\"", ExecutionIrSerializer.serialize (ExecutionIr.lower typed |> unwrap "lower"))
        // A context-free callback serializes without the property, exactly as before.
        let plain = { snapshot with Functions = snapshot.Functions |> List.filter (fun fn -> fn.DisplayName = "above") }
        Assert.DoesNotContain("\"context\"", SnapshotCodec.serialize plain |> unwrap "plain")
        Assert.Throws<Exception>(fun () ->
            json.Replace("\"context\":[", "\"context\":[],\"unused\":[") |> SnapshotCodec.deserialize |> unwrap "tampered" |> ignore) |> ignore

    [<Fact>]
    let ``persisted callback context is omitted rather than written empty`` () =
        let snapshot = fixture ()
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        let contexts = Text.RegularExpressions.Regex("\"context\":\\[[^\\]]*\\]")
        Assert.Equal(5, contexts.Matches(json).Count)
        // An empty array is not another spelling of "no context": the decoder rejects it.
        match contexts.Replace(json, "\"context\":[]", 1) |> SnapshotCodec.deserialize with
        | Error error -> Assert.Contains("Callback context must be omitted rather than empty.", sprintf "%A" error)
        | Ok _ -> failwith "An empty callback context was accepted."
        // Omitting it decodes to an empty context, which the validator then checks against the callback arity.
        let omitted = contexts.Replace(json, "", 1).Replace(",,", ",").Replace(",}", "}")
        match SnapshotCodec.deserialize omitted with
        | Error error -> Assert.DoesNotContain("omitted rather than empty", sprintf "%A" error)
        | Ok _ -> failwith "A callback without its required context was accepted."

    [<Fact>]
    let ``context bindings are checked in recipes and in persisted graphs`` () =
        let snapshot = fixture ()
        let above = find snapshot "above"
        let badWith prefix kindOf children =
            let bad =
                makeFn "bad"
                    [ paramId "bad-items", "items", intList; paramId "bad-threshold", "threshold", intType ] intList
                    (buildFor prefix (GraphBuilder.node kindOf children))
            SnapshotValidator.validate { snapshot with Functions = snapshot.Functions @ [ bad ] }
        let items = GraphBuilder.param (paramId "bad-items")
        let bound = GraphBuilder.param (paramId "bad-threshold")
        let filterWith context children =
            badWith "bad" (fun ids -> SemanticNodeKind.ListFilter(ids[0], above.Id, context ids)) children
        Assert.NotEmpty(filterWith (fun _ -> []) [ items ])
        Assert.NotEmpty(filterWith (fun ids -> [ { TargetParameterId = id<ParameterTag> "foreign"; ValueNodeId = ids[1] } ]) [ items; bound ])
        Assert.Contains(
            filterWith (fun ids -> [ { TargetParameterId = parameter above "threshold"; ValueNodeId = ids[1] } ])
                [ items; GraphBuilder.leaf (SemanticNodeKind.BoolLiteral true) ],
            fun issue -> issue.Code = "CallbackContextTypeMismatch")
        Assert.NotEmpty(filterWith (fun ids -> [ { TargetParameterId = parameter above "threshold"; ValueNodeId = ids[1] }; { TargetParameterId = parameter above "threshold"; ValueNodeId = ids[1] } ]) [ items; bound ])
        Assert.NotEmpty(filterWith (fun ids -> [ { TargetParameterId = parameter above "threshold"; ValueNodeId = ids[1] }; { TargetParameterId = parameter above "item"; ValueNodeId = ids[1] } ]) [ items; bound ])
        // A persisted graph whose context value has the wrong type is rejected by the validator.
        let broken =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    if fn.DisplayName <> "filterAbove" then fn
                    else
                        let threshold = fn.Parameters |> List.find (fun item -> item.DisplayName = "threshold")
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes = fn.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference parameterId when parameterId = threshold.Id ->
                                            { node with Kind = SemanticNodeKind.BoolLiteral true }
                                        | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "CallbackContextTypeMismatch")
