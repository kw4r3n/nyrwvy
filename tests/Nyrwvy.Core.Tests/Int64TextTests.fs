namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Int64ToString and StringParseInt64: canonical decimal conversion between Int64 and String.
module Int64TextTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("int64-text-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionInt = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ intType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private buildSnapshot name parameterType resultType kindOf =
        let functionId = id<FunctionTag> name
        let parameterId = id<ParameterTag> (name + "-input")
        let body =
            GraphBuilder.build (id<GraphNodeTag>)
                (GraphBuilder.node (fun ids -> kindOf ids[0]) [ GraphBuilder.param parameterId ])
        let fn: SemanticFunction =
            { Id = functionId
              DisplayName = "convert"
              Exported = true
              MustNotWait = false
              TypeParameters = []
              Parameters = [ { Id = parameterId; DisplayName = "input"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

    [<Fact>]
    let ``conversions reject mistyped operands in recipes and persisted graphs`` () =
        Assert.Contains(
            SnapshotValidator.validate (buildSnapshot "to-string-wrong" stringType stringType SemanticNodeKind.Int64ToString),
            fun issue -> issue.Code = "Int64IntrinsicTypeMismatch")
        Assert.Contains(
            SnapshotValidator.validate (buildSnapshot "parse-wrong" intType optionInt SemanticNodeKind.StringParseInt64),
            fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
        // The parse result is Option<Int64>, not Int64.
        Assert.Contains(
            SnapshotValidator.validate (buildSnapshot "parse-result" stringType intType SemanticNodeKind.StringParseInt64),
            fun issue -> issue.Code = "FunctionResultTypeMismatch")
        let snapshot = buildSnapshot "to-string" intType stringType SemanticNodeKind.Int64ToString
        Assert.Empty(SnapshotValidator.validate snapshot)
        let broken =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference _ -> { node with Kind = SemanticNodeKind.StringLiteral "1" }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "Int64IntrinsicTypeMismatch")
