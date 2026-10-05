namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
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

    let private request ws name (parameters: (string * TypeRef) list) resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
          DisplayName = name
          Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = []
          Body = body }

    let private create ws name parameters resultType body =
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request ws name parameters resultType body) |> unwrap ("create " + name)
        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
        | other -> failwithf "Commit failed: %A" other

    let private find ws name = (AgentWorkspace.snapshotForStructuralEditing ws).Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private parameter (fn: SemanticFunction) name = (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id

    /// Callbacks take a context parameter first; the same callbacks serve any runtime threshold or factor.
    let private fixture () =
        let ws = AgentWorkspace.create (Snapshot.empty (id<SnapshotTag> "base")) |> unwrap "workspace"
        let slot = ExpressionRecipe.ParameterSlot
        let ws = create ws "above" [ "threshold", intType; "item", intType ] boolType (ExpressionRecipe.Int64GreaterThan(slot 1, slot 0))
        let ws = create ws "scale" [ "factor", intType; "item", intType ] intType (ExpressionRecipe.Int64WrappingMultiply(slot 0, slot 1))
        let ws =
            create ws "sumAbove" [ "threshold", intType; "total", intType; "item", intType ] intType
                (ExpressionRecipe.Conditional(ExpressionRecipe.Int64GreaterThan(slot 2, slot 0), ExpressionRecipe.Int64WrappingAdd(slot 1, slot 2), slot 1))
        let above, scale, sumAbove = find ws "above", find ws "scale", find ws "sumAbove"
        let items, bound = slot 0, slot 1
        let ws = create ws "filterAbove" [ "items", intList; "threshold", intType ] intList (ExpressionRecipe.ListFilter(items, above.Id, [ parameter above "threshold", bound ]))
        let ws = create ws "scaleAll" [ "items", intList; "factor", intType ] intList (ExpressionRecipe.ListMap(items, scale.Id, [ parameter scale "factor", bound ]))
        let ws =
            create ws "totalAbove" [ "items", intList; "threshold", intType ] intType
                (ExpressionRecipe.ListFold(items, ExpressionRecipe.Int64Literal 0L, sumAbove.Id, [ parameter sumAbove "threshold", bound ]))
        let ws = create ws "anyAbove" [ "items", intList; "threshold", intType ] boolType (ExpressionRecipe.ListAny(items, above.Id, [ parameter above "threshold", bound ]))
        let ws = create ws "allAbove" [ "items", intList; "threshold", intType ] boolType (ExpressionRecipe.ListAll(items, above.Id, [ parameter above "threshold", bound ]))
        create ws "plainLength" [ "items", intList ] intType (ExpressionRecipe.ListLength items)

    [<Fact>]
    let ``context callbacks author persist and keep context-free graphs byte-identical`` () =
        let ws = fixture ()
        let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
        Assert.Empty(SnapshotValidator.validate snapshot)
        let filterAbove = find ws "filterAbove"
        let bindings =
            ValueGraph.collectNodes filterAbove.Body |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.ListFilter(_, _, context) -> Some context
                | _ -> None)
        Assert.Equal<ParameterId list>([ parameter (find ws "above") "threshold" ], bindings |> List.map (fun binding -> binding.TargetParameterId))
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        Assert.Contains("\"context\"", json)
        let restored = SnapshotCodec.deserialize json |> unwrap "restore"
        Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap "reserialize")
        let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (NyasonCodec.deserialize nyason |> unwrap "nyason restore"))
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
        let snapshot = AgentWorkspace.snapshotForStructuralEditing (fixture ())
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
        let ws = fixture ()
        let above = find ws "above"
        let reject body =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request ws "bad" [ "items", intList; "threshold", intType ] intList body) with
            | Error(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _)) -> diagnostic.Code
            | other -> failwithf "Invalid context accepted: %A" other
        let items, bound = ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1
        Assert.Equal("PureExpressionListCallbackArityMismatch", reject (ExpressionRecipe.ListFilter(items, above.Id, [])))
        Assert.Equal("PureExpressionCallArgumentTargetNotFound", reject (ExpressionRecipe.ListFilter(items, above.Id, [ id<ParameterTag> "foreign", bound ])))
        Assert.Equal("PureExpressionListCallbackContextTypeMismatch",
            reject (ExpressionRecipe.ListFilter(items, above.Id, [ parameter above "threshold", ExpressionRecipe.BoolLiteral true ])))
        Assert.Equal("PureExpressionDuplicateCallArgument",
            reject (ExpressionRecipe.ListFilter(items, above.Id, [ parameter above "threshold", bound; parameter above "threshold", bound ])))
        Assert.Equal("PureExpressionListCallbackArityMismatch",
            reject (ExpressionRecipe.ListFilter(items, above.Id, [ parameter above "threshold", bound; parameter above "item", bound ])))
        // A persisted graph whose context value has the wrong type is rejected by the validator.
        let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
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
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "ListCallbackContextTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``context callbacks run with runtime values and are authored over the wire`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-callback-context-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot (fixture ()) |> unwrap "serialize")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name threshold = (PureRun.run session (functionId name) [ PureRunValue.Int64List [ 1L; 5L; 10L ]; PureRunValue.Int64 threshold ] None |> unwrap name).Actual
            Assert.Equal(PureRunValue.Int64List [ 5L; 10L ], run "filterAbove" 4L)
            Assert.Equal(PureRunValue.Int64List [ 10L ], run "filterAbove" 5L)
            Assert.Equal(PureRunValue.Int64List [ 3L; 15L; 30L ], run "scaleAll" 3L)
            Assert.Equal(PureRunValue.Int64 15L, run "totalAbove" 1L)
            Assert.Equal(PureRunValue.Bool true, run "anyAbove" 9L)
            Assert.Equal(PureRunValue.Bool false, run "allAbove" 1L)
            Assert.Equal(PureRunValue.Bool true, run "allAbove" 0L)
            let line =
                """{"id":"create","op":"simulateCreateFunctionByName","displayName":"countAbove","parameters":[{"displayName":"items","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"int64"}]}},{"displayName":"limit","type":{"kind":"primitive","primitive":"int64"}}],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"listLength","value":{"kind":"listFilter","source":{"kind":"parameter","name":"items"},"function":"above","context":[{"parameter":"threshold","value":{"kind":"parameter","name":"limit"}}]}}}"""
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use committed = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
            Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            Assert.Equal(PureRunValue.Int64 2L, run "countAbove" 2L)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
