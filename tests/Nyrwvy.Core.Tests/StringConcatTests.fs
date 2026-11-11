namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Xunit

module StringConcatTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("concat-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String

    [<Fact>]
    let ``StringConcat rejects mistyped operands in recipes and persisted graphs`` () =
        let leftId = id<ParameterTag> "left"
        let rightId = id<ParameterTag> "right"

        let snapshotWith body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "join"
                  DisplayName = "join"
                  Exported = true
                  TypeParameters = []
                  Parameters =
                    [ { Id = leftId; DisplayName = "left"; Type = stringType }
                      { Id = rightId; DisplayName = "right"; Type = stringType } ]
                  ResultType = stringType
                  RequiredEffects = []
                  MustNotWait = false
                  Body = body }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let concat left right =
            GraphBuilder.build
                (id<GraphNodeTag>)
                (GraphBuilder.node (fun ids -> SemanticNodeKind.StringConcat(ids[0], ids[1])) [ left; right ])

        let invalid =
            snapshotWith (concat (GraphBuilder.leaf (SemanticNodeKind.BoolLiteral true)) (GraphBuilder.param rightId))

        Assert.Contains(SnapshotValidator.validate invalid, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
        let created = snapshotWith (concat (GraphBuilder.param leftId) (GraphBuilder.param rightId))
        Assert.Empty(SnapshotValidator.validate created)
        let broken =
            { created with
                Functions = created.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference parameterId when parameterId = leftId ->
                                        { node with Kind = SemanticNodeKind.BoolLiteral true }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
