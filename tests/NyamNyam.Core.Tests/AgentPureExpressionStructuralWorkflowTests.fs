namespace NyamNyam.Core.Tests

open Microsoft.FSharp.Reflection
open NyamNyam.Core
open Xunit

module AgentPureExpressionStructuralWorkflowTests =
    type private Fixture =
        { Snapshot: Snapshot
          StringTargetId: FunctionId
          BoolTargetId: FunctionId
          UnitTargetId: FunctionId
          IntTargetId: FunctionId
          FloatTargetId: FunctionId
          TextParameterId: ParameterId
          SubstringParameterId: ParameterId
          CountParameterId: ParameterId
          FloatParameterId: ParameterId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-pure-structural-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-pure-structural-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-pure-structural-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-pure-structural-" + name)

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private functionWithBody id name parameters resultType resultNodeId kind: SemanticFunction =
        { Id = id
          DisplayName = name
          Parameters = parameters
          ResultType = resultType
          RequiredEffects = []
          Body =
            { Nodes = [ { Id = resultNodeId; Kind = kind } ]
              ResultNodeId = resultNodeId } }

    let private createFixture (): Fixture =
        let textParameter: SemanticParameter =
            { Id = parameterId "bool-text"
              DisplayName = "text"
              Type = stringType }
        let substringParameter: SemanticParameter =
            { Id = parameterId "bool-substring"
              DisplayName = "substring"
              Type = stringType }
        let countParameter: SemanticParameter =
            { Id = parameterId "bool-count"
              DisplayName = "count"
              Type = int64Type }
        let floatParameter: SemanticParameter =
            { Id = parameterId "bool-float"
              DisplayName = "float"
              Type = float64Type }
        let intTargetParameter: SemanticParameter =
            { Id = parameterId "int-count"
              DisplayName = "count"
              Type = int64Type }
        let floatTargetParameter: SemanticParameter =
            { Id = parameterId "float-value"
              DisplayName = "value"
              Type = float64Type }

        let stringTarget =
            functionWithBody
                (functionId "string")
                "string_target"
                []
                stringType
                (nodeId "string-result")
                (SemanticNodeKind.StringLiteral "old")

        let boolTarget =
            functionWithBody
                (functionId "bool")
                "bool_target"
                [ textParameter; substringParameter; countParameter; floatParameter ]
                boolType
                (nodeId "bool-result")
                (SemanticNodeKind.BoolLiteral false)

        let unitTarget =
            functionWithBody
                (functionId "unit")
                "unit_target"
                []
                unitType
                (nodeId "unit-result")
                SemanticNodeKind.UnitLiteral

        let intTarget =
            functionWithBody
                (functionId "int")
                "int_target"
                [ intTargetParameter ]
                int64Type
                (nodeId "int-result")
                (SemanticNodeKind.Int64Literal 0L)

        let floatTarget =
            functionWithBody
                (functionId "float")
                "float_target"
                [ floatTargetParameter ]
                float64Type
                (nodeId "float-result")
                (SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 0.0))

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ stringTarget; boolTarget; unitTarget; intTarget; floatTarget ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          StringTargetId = stringTarget.Id
          BoolTargetId = boolTarget.Id
          UnitTargetId = unitTarget.Id
          IntTargetId = intTarget.Id
          FloatTargetId = floatTarget.Id
          TextParameterId = textParameter.Id
          SubstringParameterId = substringParameter.Id
          CountParameterId = countParameter.Id
          FloatParameterId = floatParameter.Id }

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request (fixture: Fixture) targetFunctionId replacement: AgentReplaceFunctionBodyWithPureExpressionRecipe =
        let target = fixture.Snapshot.Functions |> List.find (fun functionEntity -> functionEntity.Id = targetFunctionId)
        { TargetFunctionId = targetFunctionId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = target.Body.ResultNodeId
          Replacement = replacement }

    let private simulate workspace structuralRequest =
        match AgentPureExpressionStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected pure-expression simulation failure: %A" error

    let private assertRejectedCode expected = function
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    [<Fact>]
    let ``pure expression workflow materializes scalar literals across result types`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let cases =
            [ fixture.StringTargetId, PureExpressionRecipe.StringLiteral "next", "StringLiteral"
              fixture.BoolTargetId, PureExpressionRecipe.BoolLiteral true, "BoolLiteral"
              fixture.UnitTargetId, PureExpressionRecipe.UnitLiteral, "UnitLiteral"
              fixture.IntTargetId, PureExpressionRecipe.Int64Literal 42L, "Int64Literal"
              fixture.FloatTargetId, PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 2.5), "Float64Literal" ]

        for targetId, recipe, role in cases do
            let proposal = request fixture targetId recipe |> simulate workspace
            let review = AgentPureExpressionStructuralChangeProposal.review proposal
            Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
            Assert.Equal(role, (List.exactlyOne review.ResultingChange.AddedNodes).Role)

    [<Fact>]
    let ``pure expression workflow composes String Int64 and Float64 recipes under one typed surface`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let cases =
            [ PureExpressionRecipe.StringContains(
                  PureExpressionRecipe.Parameter fixture.TextParameterId,
                  PureExpressionRecipe.Parameter fixture.SubstringParameterId
              ),
              [ "ParameterReference"; "ParameterReference"; "StringContains" ]
              PureExpressionRecipe.Int64LessThan(
                  PureExpressionRecipe.Int64WrappingAdd(
                      PureExpressionRecipe.Parameter fixture.CountParameterId,
                      PureExpressionRecipe.Int64Literal 1L
                  ),
                  PureExpressionRecipe.Int64Literal 5L
              ),
              [ "ParameterReference"; "Int64Literal"; "Int64WrappingAdd"; "Int64Literal"; "Int64LessThan" ]
              PureExpressionRecipe.Float64IsFinite(
                  PureExpressionRecipe.Float64Divide(
                      PureExpressionRecipe.Parameter fixture.FloatParameterId,
                      PureExpressionRecipe.Float64Literal(Float64Bits.ofFloat 2.0)
                  )
              ),
              [ "ParameterReference"; "Float64Literal"; "Float64Divide"; "Float64IsFinite" ] ]

        for recipe, expectedRoles in cases do
            let proposal = request fixture fixture.BoolTargetId recipe |> simulate workspace
            let roles =
                (AgentPureExpressionStructuralChangeProposal.review proposal).ResultingChange.AddedNodes
                |> List.map (fun node -> node.Role)
            if roles <> expectedRoles then
                failwithf "Unexpected materialization order. Expected %A, got %A" expectedRoles roles

    [<Fact>]
    let ``pure expression preflight rejects result operand and parameter type violations`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        request fixture fixture.BoolTargetId (PureExpressionRecipe.Int64Literal 1L)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionResultTypeMismatch"

        request fixture fixture.BoolTargetId (PureExpressionRecipe.StringIsEmpty(PureExpressionRecipe.Int64Literal 1L))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionOperandTypeMismatch"

        let foreignParameter = StableId.createForTesting<ParameterTag> "parameter-pure-structural-foreign"
        request fixture fixture.BoolTargetId (PureExpressionRecipe.Parameter foreignParameter)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionParameterOutOfScope"

    [<Fact>]
    let ``pure expression request exposes construction intent without raw graph authority`` () =
        let requestFields =
            FSharpType.GetRecordFields(typeof<AgentReplaceFunctionBodyWithPureExpressionRecipe>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList

        let requestNames = requestFields |> List.map fst
        if requestNames <> [ "TargetFunctionId"; "ExpectedBaseSnapshotId"; "ExpectedCurrentResultNodeId"; "Replacement" ] then
            failwithf "Pure-expression request authority surface changed: %A" requestNames

        let cases = FSharpType.GetUnionCases(typeof<PureExpressionRecipe>)
        let caseNames = cases |> Array.map (fun case -> case.Name) |> Array.toList
        let expectedCases =
            [ "Parameter"
              "StringLiteral"
              "BoolLiteral"
              "UnitLiteral"
              "Int64Literal"
              "Float64Literal"
              "StringIsEmpty"
              "StringContains"
              "Int64WrappingAdd"
              "Int64WrappingSubtract"
              "Int64WrappingMultiply"
              "Int64Equal"
              "Int64LessThan"
              "Int64LessThanOrEqual"
              "Int64GreaterThan"
              "Int64GreaterThanOrEqual"
              "Float64Add"
              "Float64Subtract"
              "Float64Multiply"
              "Float64Divide"
              "Float64Equal"
              "Float64LessThan"
              "Float64IsNaN"
              "Float64IsFinite"
              "Float64IsInfinite" ]

        if caseNames <> expectedCases then
            failwithf "Pure-expression recipe authority surface changed: %A" caseNames

        let forbiddenPayloadTypes =
            [ typeof<GraphNodeId>
              typeof<Snapshot>
              typeof<ValueGraph>
              typeof<SemanticNode>
              typeof<SemanticNodeKind> ]

        let recipeFields =
            cases
            |> Array.collect (fun case -> case.GetFields())
            |> Array.map (fun field -> field.PropertyType)

        if recipeFields
           |> Array.exists (fun fieldType -> forbiddenPayloadTypes |> List.exists ((=) fieldType)) then
            failwithf "Pure-expression recipe exposes raw graph authority: %A" recipeFields
