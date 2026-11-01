namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// G2 function type parameters: one body serves several element types; type arguments are derived
/// by matching parameter types at each call or callback.
module GenericFunctionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("generic-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    /// A request-local type parameter, spelled as its name; creation replaces the identity.
    let private t = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "T")
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private request ws name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
          TypeParameters = None
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
        Assert.NotEqual(StableId.createForTesting<TypeParameterTag> "T", fst countParameters.Head)
        Assert.Equal("T", snd countParameters.Head)
        // Each function owns distinct type parameters, and a body type reuses the signature's one.
        Assert.NotEqual(fst countParameters.Head, fst (TypeParameters.ofFunction orElse).Head)
        let emptyOfParameter = fst (TypeParameters.ofFunction emptyOf).Head
        let bodyItemType =
            ValueGraph.collectNodes emptyOf.Body |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.ListEmpty itemType -> Some itemType
                | _ -> None)
        Assert.Equal(TypeRef.TypeParameter(emptyOfParameter), bodyItemType)
        Assert.Equal(stringType, (find ws "defaultName").ResultType)
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        Assert.Contains("\"typeParameter\"", json)
        Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap "restore" |> SnapshotCodec.serialize |> unwrap "reserialize")
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (SnapshotCodec.deserialize json |> unwrap "decode"))
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
                    else { fn with TypeParameters = (find ws "count").TypeParameters; Parameters = fn.Parameters |> List.map (fun parameter -> { parameter with Type = countParameter }) }) }
        Assert.Contains(SnapshotValidator.validate shared, fun issue -> issue.Code = "TypeParameterSharedAcrossFunctions")
        let declared =
            { snapshot with
                Types = [ { Exported = true; TypeParameters = []; Id = id<TypeTag> "box"; DisplayName = "Box"; Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "content"; DisplayName = "content"; Type = countParameter } ] } ] }
        // A type's fields may use only the type's own type parameters, not a function's.
        Assert.Contains(SnapshotValidator.validate declared, fun issue -> issue.Code = "TypeParameterNotDeclared")
        // A result type may only use a type parameter that the function's own parameter types introduce.
        let undeclared =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    if fn.DisplayName <> "defaultName" then fn
                    else { fn with ResultType = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "U") }) }
        let issues = SnapshotValidator.validate undeclared
        Assert.Contains(issues, fun issue ->
            issue.Code = "TypeParameterNotDeclared" && issue.Message.Contains("'defaultName'") && issue.Message.Contains("'U'"))
        Assert.DoesNotContain(SnapshotValidator.validate snapshot, fun issue -> issue.Code = "TypeParameterNotDeclared")

    let private genericWireFunction =
        """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"select","parameters":[{"displayName":"items","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","name":"T"}]}]}},{"displayName":"marker","type":{"kind":"typeParameter","name":"U"}}],"resultType":{"kind":"builtinGeneric","generic":"result","arguments":[{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"typeParameter","name":"T"}]},{"kind":"typeParameter","name":"U"}]},"requiredEffects":[],"body":{"kind":"matchOption","input":{"kind":"parameter","name":"items"},"someBody":{"kind":"resultOk","errorType":{"kind":"typeParameter","name":"U"},"value":{"kind":"patternReference","scopeDepth":0,"slot":"value"}},"noneBody":{"kind":"resultOk","errorType":{"kind":"typeParameter","name":"U"},"value":{"kind":"listEmpty","itemType":{"kind":"typeParameter","name":"T"}}}}}"""
