namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// G2 function type parameters: one body serves several element types; type arguments are derived
/// by matching parameter types at each call or callback.
module GenericFunctionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("generic-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    /// A request-local type parameter; creation replaces the placeholder identity.
    let private t = TypeRef.TypeParameter(id<TypeParameterTag> "request-T", "T")
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private request ws name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
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
    let private call ws name (arguments: (string * ExpressionRecipe) list) =
        let target = find ws name
        ExpressionRecipe.DirectCall(target.Id, arguments |> List.map (fun (parameter, value) ->
            (target.Parameters |> List.find (fun item -> item.DisplayName = parameter)).Id, value))

    let private fixture () =
        let slot = ExpressionRecipe.ParameterSlot
        let value = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
        let ws = AgentWorkspace.create (Snapshot.empty (id<SnapshotTag> "base")) |> unwrap "workspace"
        let ws = create ws "count" [ "items", listOf t ] intType (ExpressionRecipe.ListLength(slot 0))
        let ws = create ws "wrap" [ "value", t ] (optionOf t) (ExpressionRecipe.OptionSome(slot 0))
        let ws = create ws "orElse" [ "value", optionOf t; "fallback", t ] t (ExpressionRecipe.MatchOption(slot 0, value, slot 1))
        let ws = create ws "emptyOf" [ "items", listOf t ] (listOf t) (ExpressionRecipe.ListEmpty t)
        let ws = create ws "countTwice" [ "items", listOf t ] intType (ExpressionRecipe.Int64WrappingAdd(call ws "count" [ "items", slot 0 ], call ws "count" [ "items", slot 0 ]))
        let ws = create ws "countNames" [ "names", listOf stringType ] intType (call ws "count" [ "items", slot 0 ])
        let ws = create ws "countNumbers" [ "numbers", listOf intType ] intType (call ws "countTwice" [ "items", slot 0 ])
        let ws = create ws "wrapAll" [ "numbers", listOf intType ] (listOf (optionOf intType)) (ExpressionRecipe.ListMap(slot 0, (find ws "wrap").Id, []))
        let ws = create ws "defaultName" [ "name", optionOf stringType ] stringType (call ws "orElse" [ "value", slot 0; "fallback", ExpressionRecipe.StringLiteral "anon" ])
        create ws "noNames" [ "names", listOf stringType ] (listOf stringType) (call ws "emptyOf" [ "items", slot 0 ])

    [<Fact>]
    let ``generic functions get system-owned type parameters and persist`` () =
        let ws = fixture ()
        let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
        Assert.Empty(SnapshotValidator.validate snapshot)
        let count, orElse, emptyOf = find ws "count", find ws "orElse", find ws "emptyOf"
        let countParameters = TypeParameters.ofFunction count
        Assert.Equal(1, countParameters.Length)
        Assert.NotEqual(id<TypeParameterTag> "request-T", fst countParameters.Head)
        Assert.Equal("T", snd countParameters.Head)
        // Each function owns distinct type parameters, and a body type reuses the signature's one.
        Assert.NotEqual(fst countParameters.Head, fst (TypeParameters.ofFunction orElse).Head)
        let emptyOfParameter = fst (TypeParameters.ofFunction emptyOf).Head
        let bodyItemType =
            ValueGraph.collectNodes emptyOf.Body |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.ListEmpty itemType -> Some itemType
                | _ -> None)
        Assert.Equal(TypeRef.TypeParameter(emptyOfParameter, "T"), bodyItemType)
        Assert.Equal(stringType, (find ws "defaultName").ResultType)
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        Assert.Contains("\"typeParameter\"", json)
        Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap "restore" |> SnapshotCodec.serialize |> unwrap "reserialize")
        let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (NyasonCodec.deserialize nyason |> unwrap "nyason decode"))
        let typed = TypedSemanticIr.project snapshot |> unwrap "typed"
        let artifact = CSharpBackend.emit (ExecutionIr.lower typed |> unwrap "lower") CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains(CSharpBackendNames.functionName count.Id + "<" + CSharpBackendNames.typeParameterName (fst countParameters.Head) + ">", artifact.Source)

    [<Fact>]
    let ``generic calls derive consistent type arguments and generic bodies stay opaque`` () =
        let ws = fixture ()
        let slot = ExpressionRecipe.ParameterSlot
        let reject name parameters resultType body =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request ws name parameters resultType body) with
            | Error(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _)) -> diagnostic.Code
            | other -> failwithf "Invalid generic use accepted: %A" other
        // T cannot be both String (from the option) and Int64 (from the fallback).
        Assert.Equal("PureExpressionCallArgumentTypeMismatch",
            reject "conflict" [ "name", optionOf stringType ] stringType (call ws "orElse" [ "value", slot 0; "fallback", ExpressionRecipe.Int64Literal 0L ]))
        // A type parameter used only in the result is not introduced by any parameter.
        Assert.NotEqual<string>("", reject "make" [ "n", intType ] t (ExpressionRecipe.ListLength(ExpressionRecipe.ListEmpty t)))
        Assert.Equal("PureExpressionOperandTypeMismatch", reject "addOne" [ "value", t ] intType (ExpressionRecipe.Int64WrappingAdd(slot 0, ExpressionRecipe.Int64Literal 1L)))
        // Persisted graphs: a type parameter cannot be shared across functions or used in declared types.
        let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
        let countParameter = (find ws "count").Parameters.Head.Type
        let shared =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    if fn.DisplayName <> "countTwice" then fn
                    else { fn with Parameters = fn.Parameters |> List.map (fun parameter -> { parameter with Type = countParameter }) }) }
        Assert.Contains(SnapshotValidator.validate shared, fun issue -> issue.Code = "TypeParameterSharedAcrossFunctions")
        let declared =
            { snapshot with
                Types = [ { Id = id<TypeTag> "box"; DisplayName = "Box"; Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "content"; DisplayName = "content"; Type = countParameter } ] } ] }
        Assert.Contains(SnapshotValidator.validate declared, fun issue -> issue.Code = "TypeParameterOutsideFunction")
        // A result type may only use a type parameter that the function's own parameter types introduce.
        let undeclared =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    if fn.DisplayName <> "defaultName" then fn
                    else { fn with ResultType = TypeRef.TypeParameter(id<TypeParameterTag> "unintroduced", "U") }) }
        let issues = SnapshotValidator.validate undeclared
        Assert.Contains(issues, fun issue ->
            issue.Code = "TypeParameterNotDeclared" && issue.Message.Contains("'defaultName'") && issue.Message.Contains("'U'"))
        Assert.DoesNotContain(SnapshotValidator.validate snapshot, fun issue -> issue.Code = "TypeParameterNotDeclared")

    [<Fact; Trait("Suite", "Extended")>]
    let ``one generic body runs for several element types`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-generic-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot (fixture ()) |> unwrap "serialize")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let run name value = (PureRun.run session (functionId name) [ value ] None |> unwrap name).Actual
            Assert.Equal(PureRunValue.Int64 2L, run "countNames" (PureRunValue.StringList [ "a"; "b" ]))
            Assert.Equal(PureRunValue.Int64 6L, run "countNumbers" (PureRunValue.Int64List [ 1L; 2L; 3L ]))
            Assert.Equal(PureRunValue.ListValue(optionOf intType, [ PureRunValue.Int64Option(Some 1L); PureRunValue.Int64Option(Some 2L) ]),
                run "wrapAll" (PureRunValue.Int64List [ 1L; 2L ]))
            Assert.Equal(PureRunValue.String "anon", run "defaultName" (PureRunValue.StringOption None))
            Assert.Equal(PureRunValue.String "ada", run "defaultName" (PureRunValue.StringOption(Some "ada")))
            Assert.Equal(PureRunValue.StringList [], run "noNames" (PureRunValue.StringList [ "x" ]))
            match PureRun.run session (functionId "count") [ PureRunValue.StringList [] ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunGenericEntryUnsupported", code)
            | other -> failwithf "Generic entry ran: %A" other
            let line =
                """{"id":"create","op":"simulateCreateFunctionByName","displayName":"firstOr","parameters":[{"displayName":"items","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","name":"E"}]}},{"displayName":"fallback","type":{"kind":"typeParameter","name":"E"}}],"resultType":{"kind":"typeParameter","name":"E"},"requiredEffects":[],"body":{"kind":"parameter","name":"fallback"}}"""
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use committed = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
            Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            let firstOr = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "firstOr")
            Assert.Equal<(TypeParameterId * string) list>(TypeParameters.occurring firstOr.ResultType, TypeParameters.ofFunction firstOr)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
