namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringLength and StringSlice: code-point length and clamped code-point slicing.
module StringSliceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-slice-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private caseTypeId = id<TypeTag> "case"
    let private textFieldId = id<FieldTag> "text"
    let private startFieldId = id<FieldTag> "start"
    let private endFieldId = id<FieldTag> "end"
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private caseType: SemanticType =
        { Exported = true; TypeParameters = []; Id = caseTypeId
          DisplayName = "SliceCase"
          Kind =
            SemanticTypeKind.Product
                [ { Id = textFieldId; DisplayName = "text"; Type = stringType }
                  { Id = startFieldId; DisplayName = "start"; Type = intType }
                  { Id = endFieldId; DisplayName = "end"; Type = intType } ] }

    [<Fact>]
    let ``length and slice reject mistyped operands in recipes and persisted graphs`` () =
        let parameterId index = id<ParameterTag> $"input{index}"
        let slot index = GraphBuilder.param (parameterId index)
        let int value = GraphBuilder.leaf (SemanticNodeKind.Int64Literal value)

        let snapshotWith (parameterTypes: TypeRef list) resultType body =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> "measure"
                  DisplayName = "measure"
                  Exported = true
                  TypeParameters = []
                  Parameters = parameterTypes |> List.mapi (fun index t -> { Id = parameterId index; DisplayName = $"input{index}"; Type = t })
                  ResultType = resultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body = GraphBuilder.build (id<GraphNodeTag>) body }

            { Snapshot.empty (id<SnapshotTag> "base") with Functions = [ fn ] }

        let issues parameterTypes resultType body = SnapshotValidator.validate (snapshotWith parameterTypes resultType body)
        let intrinsic parameterTypes resultType body =
            Assert.Contains(issues parameterTypes resultType body, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
        let unary kind = GraphBuilder.node (fun ids -> kind ids[0])
        let ternary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1], ids[2]))

        intrinsic [ intType ] intType (unary SemanticNodeKind.StringLength [ slot 0 ])
        intrinsic [ stringType; stringType ] stringType (ternary SemanticNodeKind.StringSlice [ slot 0; slot 1; int 0L ])
        intrinsic [ stringType ] stringType (ternary SemanticNodeKind.StringSlice [ slot 0; int 0L; slot 0 ])
        // The length is Int64, not String.
        Assert.Contains(
            issues [ stringType ] stringType (unary SemanticNodeKind.StringLength [ slot 0 ]),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let committed = snapshotWith [ stringType; intType ] stringType (ternary SemanticNodeKind.StringSlice [ slot 0; slot 1; slot 1 ])
        Assert.Empty(SnapshotValidator.validate committed)
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId 1 ->
                                        { node with Kind = SemanticNodeKind.StringLiteral "1" }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
