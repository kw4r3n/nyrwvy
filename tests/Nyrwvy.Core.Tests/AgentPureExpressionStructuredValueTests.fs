namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module AgentPureExpressionStructuredValueTests =
    type private Fixture =
        { Snapshot: Snapshot
          StringTargetId: FunctionId
          ProductTargetId: FunctionId
          UnionTargetId: FunctionId
          OptionTargetId: FunctionId
          ResultTargetId: FunctionId
          ProductTypeId: TypeId
          NameFieldId: FieldId
          ScoreFieldId: FieldId
          EmptyVariantId: VariantId
          TextVariantId: VariantId
          TextFieldId: FieldId }

    let private typeId name = StableId.createForTesting<TypeTag> ("type-pure-structured-" + name)
    let private fieldId name = StableId.createForTesting<FieldTag> ("field-pure-structured-" + name)
    let private variantId name = StableId.createForTesting<VariantTag> ("variant-pure-structured-" + name)
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-pure-structured-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-pure-structured-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-pure-structured-" + name)

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private optionStringType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])
    let private resultStringInt64Type = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ stringType; int64Type ])

    let private createFixture (): Fixture =
        let profileTypeId = typeId "profile"
        let nameFieldId = fieldId "profile-name"
        let scoreFieldId = fieldId "profile-score"
        let messageTypeId = typeId "message"
        let emptyVariantId = variantId "message-empty"
        let textVariantId = variantId "message-text"
        let textFieldId = fieldId "message-text-value"

        let profileType: SemanticType =
            { Exported = true; TypeParameters = []; Id = profileTypeId
              DisplayName = "Profile"
              Kind =
                SemanticTypeKind.Product
                    [ { Id = nameFieldId
                        DisplayName = "name"
                        Type = stringType }
                      { Id = scoreFieldId
                        DisplayName = "score"
                        Type = int64Type } ] }

        let messageType: SemanticType =
            { Exported = true; TypeParameters = []; Id = messageTypeId
              DisplayName = "Message"
              Kind =
                SemanticTypeKind.Union
                    [ { Id = emptyVariantId
                        DisplayName = "Empty"
                        Fields = [] }
                      { Id = textVariantId
                        DisplayName = "Text"
                        Fields =
                            [ { Id = textFieldId
                                DisplayName = "value"
                                Type = stringType } ] } ] }

        let stringResultId = nodeId "string-result"
        let stringTarget: SemanticFunction =
            { Exported = true; Id = functionId "string-target"
              DisplayName = "string_target"
              Parameters = []
              ResultType = stringType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = stringResultId; Kind = SemanticNodeKind.StringLiteral "old" } ]
                  ResultNodeId = stringResultId } }

        let oldNameId = nodeId "profile-old-name"
        let oldScoreId = nodeId "profile-old-score"
        let oldProductId = nodeId "profile-old-product"
        let productTarget: SemanticFunction =
            { Exported = true; Id = functionId "product-target"
              DisplayName = "product_target"
              Parameters = []
              ResultType = TypeRef.Named(profileTypeId, [])
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = oldNameId; Kind = SemanticNodeKind.StringLiteral "old" }
                      { Id = oldScoreId; Kind = SemanticNodeKind.Int64Literal 0L }
                      { Id = oldProductId
                        Kind =
                            SemanticNodeKind.ProductConstruct(
                                profileTypeId, [],
                                [ { TargetFieldId = nameFieldId; ValueNodeId = oldNameId }
                                  { TargetFieldId = scoreFieldId; ValueNodeId = oldScoreId } ]
                            ) } ]
                  ResultNodeId = oldProductId } }

        let oldUnionId = nodeId "message-old-union"
        let unionTarget: SemanticFunction =
            { Exported = true; Id = functionId "union-target"
              DisplayName = "union_target"
              Parameters = []
              ResultType = TypeRef.Named(messageTypeId, [])
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = oldUnionId; Kind = SemanticNodeKind.UnionConstruct(emptyVariantId, [], []) } ]
                  ResultNodeId = oldUnionId } }

        let oldOptionId = nodeId "option-old-none"
        let optionTarget: SemanticFunction =
            { Exported = true; Id = functionId "option-target"
              DisplayName = "option_target"
              Parameters = []
              ResultType = optionStringType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = oldOptionId; Kind = SemanticNodeKind.OptionNone stringType } ]
                  ResultNodeId = oldOptionId } }

        let oldErrorId = nodeId "result-old-error"
        let oldResultId = nodeId "result-old-result"
        let resultTarget: SemanticFunction =
            { Exported = true; Id = functionId "result-target"
              DisplayName = "result_target"
              Parameters = []
              ResultType = resultStringInt64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = oldErrorId; Kind = SemanticNodeKind.Int64Literal 0L }
                      { Id = oldResultId; Kind = SemanticNodeKind.ResultErr(stringType, oldErrorId) } ]
                  ResultNodeId = oldResultId } }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = [ profileType; messageType ]
              Effects = []
              Capabilities = []
              Functions = [ stringTarget; productTarget; unionTarget; optionTarget; resultTarget ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          StringTargetId = stringTarget.Id
          ProductTargetId = productTarget.Id
          UnionTargetId = unionTarget.Id
          OptionTargetId = optionTarget.Id
          ResultTargetId = resultTarget.Id
          ProductTypeId = profileTypeId
          NameFieldId = nameFieldId
          ScoreFieldId = scoreFieldId
          EmptyVariantId = emptyVariantId
          TextVariantId = textVariantId
          TextFieldId = textFieldId }

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request fixture targetFunctionId replacement: AgentReplaceFunctionBodyWithExpressionRecipe =
        let target = fixture.Snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = targetFunctionId)
        { TargetFunctionId = targetFunctionId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = target.Body.ResultNodeId
          Replacement = replacement }

    let private simulate workspace structuralRequest =
        match AgentExpressionStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected pure-expression simulation failure: %A" error

    let private assertRoles expected proposal =
        let actual =
            (AgentExpressionStructuralChangeProposal.review proposal).ResultingChange.AddedNodes
            |> List.map (fun node -> node.Role)
        Assert.Equal<string list>(expected, actual)

    let private assertRejectedCode expected = function
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``structured pure authoring materializes product field access and union construction`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        let fieldAccess =
            ExpressionRecipe.FieldAccess(
                ExpressionRecipe.ProductConstruct(
                    fixture.ProductTypeId, [],
                    [ fixture.ScoreFieldId, ExpressionRecipe.Int64Literal 7L
                      fixture.NameFieldId, ExpressionRecipe.StringLiteral "Ada" ]
                ),
                fixture.NameFieldId
            )

        request fixture fixture.StringTargetId fieldAccess
        |> simulate workspace
        |> assertRoles [ "StringLiteral"; "Int64Literal"; "ProductConstruct"; "FieldAccess" ]

        let unionRecipe =
            ExpressionRecipe.UnionConstruct(
                fixture.TextVariantId, [],
                [ fixture.TextFieldId, ExpressionRecipe.StringLiteral "hello" ]
            )

        request fixture fixture.UnionTargetId unionRecipe
        |> simulate workspace
        |> assertRoles [ "StringLiteral"; "UnionConstruct" ]

    [<Fact>]
    let ``structured pure authoring materializes Option and Result construction`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        request
            fixture
            fixture.OptionTargetId
            (ExpressionRecipe.OptionSome(ExpressionRecipe.StringLiteral "value"))
        |> simulate workspace
        |> assertRoles [ "StringLiteral"; "OptionSome" ]

        request fixture fixture.OptionTargetId (ExpressionRecipe.OptionNone stringType)
        |> simulate workspace
        |> assertRoles [ "OptionNone" ]

        request
            fixture
            fixture.ResultTargetId
            (ExpressionRecipe.ResultOk(int64Type, ExpressionRecipe.StringLiteral "ok"))
        |> simulate workspace
        |> assertRoles [ "StringLiteral"; "ResultOk" ]

        request
            fixture
            fixture.ResultTargetId
            (ExpressionRecipe.ResultErr(stringType, ExpressionRecipe.Int64Literal 5L))
        |> simulate workspace
        |> assertRoles [ "Int64Literal"; "ResultErr" ]

    [<Fact>]
    let ``structured pure preflight rejects invalid declaration bindings and explicit types`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        request
            fixture
            fixture.ProductTargetId
            (ExpressionRecipe.ProductConstruct(
                fixture.ProductTypeId, [],
                [ fixture.NameFieldId, ExpressionRecipe.StringLiteral "Ada" ]
            ))
        |> AgentExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionFieldBindingMismatch"

        request
            fixture
            fixture.ProductTargetId
            (ExpressionRecipe.ProductConstruct(
                fixture.ProductTypeId, [],
                [ fixture.NameFieldId, ExpressionRecipe.StringLiteral "Ada"
                  fixture.ScoreFieldId, ExpressionRecipe.StringLiteral "wrong" ]
            ))
        |> AgentExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionFieldBindingTypeMismatch"

        request
            fixture
            fixture.StringTargetId
            (ExpressionRecipe.FieldAccess(
                ExpressionRecipe.UnionConstruct(fixture.EmptyVariantId, [], []),
                fixture.NameFieldId
            ))
        |> AgentExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionFieldAccessTypeMismatch"

        let missingVariant = StableId.createForTesting<VariantTag> "variant-pure-structured-missing"
        request fixture fixture.UnionTargetId (ExpressionRecipe.UnionConstruct(missingVariant, [], []))
        |> AgentExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionUnionVariantNotFound"

        let missingType = StableId.createForTesting<TypeTag> "type-pure-structured-missing"
        request fixture fixture.OptionTargetId (ExpressionRecipe.OptionNone(TypeRef.Named(missingType, [])))
        |> AgentExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionInvalidTypeRef"
