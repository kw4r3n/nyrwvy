namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Structural rejections render types by display name in wire TypeRef notation, name callbacks
/// with their actual signature, and end with the recipe path of the node that failed
/// (probe 4, friction 4).
module StructuralDiagnosticTextTests =
    let private diagnosticOwnerId<'tag> value = StableId.createForTesting<'tag> ("diagnostic-owner-" + value)

    [<Fact>]
    let ``validator node errors carry the exact owner despite equal display names`` () =
        let ownerId, otherId = diagnosticOwnerId<FunctionTag> "owner", diagnosticOwnerId<FunctionTag> "other"
        let input, result, otherResult = diagnosticOwnerId<GraphNodeTag> "input", diagnosticOwnerId<GraphNodeTag> "result", diagnosticOwnerId<GraphNodeTag> "other-result"
        let make functionId resultType nodes resultId =
            { MustNotWait = false; Exported = true; TypeParameters = []; Id = functionId; DisplayName = "summary"; Parameters = []; ResultType = resultType; RequiredEffects = []; Body = { Nodes = nodes; ResultNodeId = resultId } }
        let owner = make ownerId (TypeRef.Primitive PrimitiveType.String)
                        [ { Id = input; Kind = SemanticNodeKind.StringLiteral(StableId.value otherId) }
                          { Id = result; Kind = SemanticNodeKind.Int64ToString input } ] result
        let other = make otherId (TypeRef.Primitive PrimitiveType.Int64) [ { Id = otherResult; Kind = SemanticNodeKind.Int64Literal 0L } ] otherResult
        let issue = SnapshotValidator.validate { Snapshot.empty (diagnosticOwnerId<SnapshotTag> "base") with Functions = [ other; owner ] }
                    |> List.find (fun issue -> issue.Code = "Int64IntrinsicTypeMismatch")
        Assert.Contains("In function", issue.Message)
        Assert.Contains("summary", issue.Message)
        Assert.Contains(StableId.value ownerId, issue.Message)
        Assert.Contains(StableId.value result, issue.Message)
        Assert.DoesNotContain(StableId.value otherId, issue.Message)

    let private id<'tag> name = StableId.createForTesting<'tag> ("diag-text-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let private tailTypeId = id<TypeTag> "tail"
    let private findingTypeId = id<TypeTag> "finding"
    let private tailLineFieldId = id<FieldTag> "tail-line"
    let private findingLineFieldId = id<FieldTag> "finding-line"
    let private countStepId = id<FunctionTag> "count-step"
    let private cmpLenId = id<FunctionTag> "cmp-len"

    let private snapshot =
        let product typeId name fieldId: SemanticType =
            { Exported = true; TypeParameters = []; Id = typeId
              DisplayName = name
              Kind = SemanticTypeKind.Product [ { Id = fieldId; DisplayName = "line"; Type = int64Type } ] }
        let int64Function functionId name (parameters: (string * TypeRef) list): SemanticFunction =
            let resultNodeId = id<GraphNodeTag> (name + "-result")
            { MustNotWait = false; Exported = true; Id = functionId
              TypeParameters = []
              DisplayName = name
              Parameters =
                parameters
                |> List.map (fun (parameterName, parameterType) ->
                    { Id = id<ParameterTag> (name + "-" + parameterName); DisplayName = parameterName; Type = parameterType })
              ResultType = int64Type
              RequiredEffects = []
              Body = { Nodes = [ { Id = resultNodeId; Kind = SemanticNodeKind.Int64Literal 0L } ]; ResultNodeId = resultNodeId } }
        let value: Snapshot =
            { Snapshot.empty (id<SnapshotTag> "base") with
                Types = [ product tailTypeId "Tail" tailLineFieldId; product findingTypeId "Finding" findingLineFieldId ]
                Functions =
                    [ int64Function countStepId "countStep" [ "line", stringType; "acc", int64Type ]
                      int64Function cmpLenId "cmpLen" [ "a", stringType; "b", stringType ] ] }
        Assert.Empty(SnapshotValidator.validate value)
        value

    let private S = """{"kind":"primitive","primitive":"string"}"""
    let private I = """{"kind":"primitive","primitive":"int64"}"""
    let private listOf item = $$"""{"kind":"builtinGeneric","generic":"list","arguments":[{{item}}]}"""
    let private optionOf item = $$"""{"kind":"builtinGeneric","generic":"option","arguments":[{{item}}]}"""
    let private named name = $$"""{"kind":"named","name":"{{name}}"}"""

    [<Fact>]
    let ``type references render as display names in wire notation`` () =
        let render = DiagnosticText.typeRef snapshot
        Assert.Equal("Option<String>", render (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])))
        Assert.Equal("List<Tail>", render (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Named(tailTypeId, []) ])))
        Assert.Equal("Result<Int64, Finding>", render (TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ int64Type; TypeRef.Named(findingTypeId, []) ])))
        Assert.Equal("(String, Int64) -> Bool", render (TypeRef.Function([ stringType; int64Type ], TypeRef.Primitive PrimitiveType.Bool)))
        let unknown = id<TypeTag> "unknown"
        Assert.Equal($"type '{StableId.value unknown}'", render (TypeRef.Named(unknown, [])))
