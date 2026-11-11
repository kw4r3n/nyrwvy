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
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private fnId name = id<FunctionTag> name
    let private paramId name = id<ParameterTag> name
    let private tpId name = id<TypeParameterTag> name

    let private makeFn name typeParameters parameters resultType body : SemanticFunction =
        { Id = fnId name
          DisplayName = name
          Exported = true
          MustNotWait = false
          TypeParameters = typeParameters
          Parameters = parameters |> List.map (fun (pid, displayName, itemType) -> { Id = pid; DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = []
          Body = body }

    let private callBody rootName targetId bindings children =
        GraphBuilder.build (fun path -> id<GraphNodeTag> (rootName + "-" + path))
            (GraphBuilder.node
                (fun ids ->
                    SemanticNodeKind.DirectCall(
                        targetId,
                        bindings |> List.mapi (fun index target -> { TargetParameterId = target; ValueNodeId = ids[index] })))
                children)

    let private fixture () =
        let tpCount, tpWrap, tpOrElse, tpEmpty, tpCountTwice = tpId "count-T", tpId "wrap-T", tpId "orElse-T", tpId "emptyOf-T", tpId "countTwice-T"
        let tCount = TypeRef.TypeParameter tpCount
        let tWrap = TypeRef.TypeParameter tpWrap
        let tOrElse = TypeRef.TypeParameter tpOrElse
        let tEmpty = TypeRef.TypeParameter tpEmpty
        let tCountTwice = TypeRef.TypeParameter tpCountTwice
        let count =
            makeFn "count" [ tpCount, "T" ] [ paramId "count-items", "items", listOf tCount ] intType
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("count-" + path))
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.ListLength ids[0]) [ GraphBuilder.param (paramId "count-items") ]))
        let wrap =
            makeFn "wrap" [ tpWrap, "T" ] [ paramId "wrap-value", "value", tWrap ] (optionOf tWrap)
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("wrap-" + path))
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.OptionSome ids[0]) [ GraphBuilder.param (paramId "wrap-value") ]))
        let orElse =
            let bindingId = id<PatternBindingTag> "orElse-bound"
            let someRefId = id<GraphNodeTag> "orElse-some-ref"
            let someBody: ValueGraph =
                { Nodes = [ { Id = someRefId; Kind = SemanticNodeKind.PatternReference bindingId } ]
                  ResultNodeId = someRefId }
            let noneRefId = id<GraphNodeTag> "orElse-none-ref"
            let noneBody: ValueGraph =
                { Nodes = [ { Id = noneRefId; Kind = SemanticNodeKind.ParameterReference(paramId "orElse-fallback") } ]
                  ResultNodeId = noneRefId }
            let inputId = id<GraphNodeTag> "orElse-input"
            let rootId = id<GraphNodeTag> "orElse-root"
            let arm: ValueMatchArm =
                { ValueBinding = { Id = bindingId; DisplayName = None; Type = tOrElse }
                  Body = someBody }
            let body: ValueGraph =
                { Nodes =
                    [ { Id = inputId; Kind = SemanticNodeKind.ParameterReference(paramId "orElse-value") }
                      { Id = rootId; Kind = SemanticNodeKind.MatchOption(inputId, arm, noneBody) } ]
                  ResultNodeId = rootId }
            makeFn "orElse" [ tpOrElse, "T" ]
                [ paramId "orElse-value", "value", optionOf tOrElse; paramId "orElse-fallback", "fallback", tOrElse ]
                tOrElse body
        let emptyOf =
            makeFn "emptyOf" [ tpEmpty, "T" ] [ paramId "emptyOf-items", "items", listOf tEmpty ] (listOf tEmpty)
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("emptyOf-" + path))
                    (GraphBuilder.leaf (SemanticNodeKind.ListEmpty tEmpty)))
        let callCount name =
            GraphBuilder.node
                (fun ids -> SemanticNodeKind.DirectCall(fnId "count", [ { TargetParameterId = paramId "count-items"; ValueNodeId = ids[0] } ]))
                [ GraphBuilder.param (paramId name) ]
        let countTwice =
            makeFn "countTwice" [ tpCountTwice, "T" ] [ paramId "countTwice-items", "items", listOf tCountTwice ] intType
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("countTwice-" + path))
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.Int64WrappingAdd(ids[0], ids[1])) [ callCount "countTwice-items"; callCount "countTwice-items" ]))
        let countNames =
            makeFn "countNames" [] [ paramId "countNames-names", "names", listOf stringType ] intType
                (callBody "countNames-body" (fnId "count") [ paramId "count-items" ] [ GraphBuilder.param (paramId "countNames-names") ])
        let countNumbers =
            makeFn "countNumbers" [] [ paramId "countNumbers-numbers", "numbers", listOf intType ] intType
                (callBody "countNumbers-body" (fnId "countTwice") [ paramId "countTwice-items" ] [ GraphBuilder.param (paramId "countNumbers-numbers") ])
        let wrapAll =
            makeFn "wrapAll" [] [ paramId "wrapAll-numbers", "numbers", listOf intType ] (listOf (optionOf intType))
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("wrapAll-" + path))
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.ListMap(ids[0], fnId "wrap", [])) [ GraphBuilder.param (paramId "wrapAll-numbers") ]))
        let defaultName =
            makeFn "defaultName" [] [ paramId "defaultName-name", "name", optionOf stringType ] stringType
                (callBody "defaultName-body" (fnId "orElse") [ paramId "orElse-value"; paramId "orElse-fallback" ]
                    [ GraphBuilder.param (paramId "defaultName-name"); GraphBuilder.leaf (SemanticNodeKind.StringLiteral "anon") ])
        let noNames =
            makeFn "noNames" [] [ paramId "noNames-names", "names", listOf stringType ] (listOf stringType)
                (callBody "noNames-body" (fnId "emptyOf") [ paramId "emptyOf-items" ] [ GraphBuilder.param (paramId "noNames-names") ])
        { Snapshot.empty (id<SnapshotTag> "base") with
            Functions = [ count; wrap; orElse; emptyOf; countTwice; countNames; countNumbers; wrapAll; defaultName; noNames ] }

    let private find snapshot name = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = name)

    [<Fact>]
    let ``generic functions get system-owned type parameters and persist`` () =
        let snapshot = fixture ()
        Assert.Empty(SnapshotValidator.validate snapshot)
        let count, orElse, emptyOf = find snapshot "count", find snapshot "orElse", find snapshot "emptyOf"
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
        Assert.Equal(stringType, (find snapshot "defaultName").ResultType)
        let json = SnapshotCodec.serialize snapshot |> unwrap "json"
        Assert.Contains("\"typeParameter\"", json)
        Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap "restore" |> SnapshotCodec.serialize |> unwrap "reserialize")
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (SnapshotCodec.deserialize json |> unwrap "decode"))

    [<Fact>]
    let ``generic calls derive consistent type arguments and generic bodies stay opaque`` () =
        let snapshot = fixture ()
        let withExtra fn = { snapshot with Functions = snapshot.Functions @ [ fn ] }
        // T cannot be both String (from the option) and Int64 (from the fallback).
        let conflict =
            makeFn "conflict" [] [ paramId "conflict-name", "name", optionOf stringType ] stringType
                (callBody "conflict-body" (fnId "orElse") [ paramId "orElse-value"; paramId "orElse-fallback" ]
                    [ GraphBuilder.param (paramId "conflict-name"); GraphBuilder.leaf (SemanticNodeKind.Int64Literal 0L) ])
        Assert.Contains(SnapshotValidator.validate (withExtra conflict), fun issue -> issue.Code = "ArgumentTypeMismatch")
        // A type parameter used only in the result is not introduced by any parameter.
        let tpMake = tpId "make-T"
        let make =
            makeFn "make" [ tpMake, "T" ] [ paramId "make-n", "n", intType ] (TypeRef.TypeParameter tpMake)
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("make-" + path))
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.ListLength ids[0]) [ GraphBuilder.leaf (SemanticNodeKind.ListEmpty(TypeRef.TypeParameter tpMake)) ]))
        Assert.Contains(SnapshotValidator.validate (withExtra make), fun issue -> issue.Code = "TypeParameterNotInferable")
        let tpAdd = tpId "addOne-T"
        let addOne =
            makeFn "addOne" [ tpAdd, "T" ] [ paramId "addOne-value", "value", TypeRef.TypeParameter tpAdd ] intType
                (GraphBuilder.build (fun path -> id<GraphNodeTag> ("addOne-" + path))
                    (GraphBuilder.node (fun ids -> SemanticNodeKind.Int64WrappingAdd(ids[0], ids[1]))
                        [ GraphBuilder.param (paramId "addOne-value"); GraphBuilder.leaf (SemanticNodeKind.Int64Literal 1L) ]))
        Assert.Contains(SnapshotValidator.validate (withExtra addOne), fun issue -> issue.Code = "Int64OperandTypeMismatch")
        // Persisted graphs: a type parameter cannot be shared across functions or used in declared types.
        let countParameter = (find snapshot "count").Parameters.Head.Type
        let shared =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    if fn.DisplayName <> "countTwice" then fn
                    else { fn with TypeParameters = (find snapshot "count").TypeParameters; Parameters = fn.Parameters |> List.map (fun parameter -> { parameter with Type = countParameter }) }) }
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
