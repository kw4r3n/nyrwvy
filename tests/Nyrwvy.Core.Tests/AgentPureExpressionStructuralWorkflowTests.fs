namespace Nyrwvy.Core.Tests

open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

module AgentPureExpressionStructuralWorkflowTests =
    type private Fixture =
        { Snapshot: Snapshot
          StringTargetId: FunctionId
          BoolTargetId: FunctionId
          UnitTargetId: FunctionId
          IntTargetId: FunctionId
          FloatTargetId: FunctionId
          ListTargetId: FunctionId
          StringListTargetId: FunctionId
          TextParameterId: ParameterId
          SubstringParameterId: ParameterId
          CountParameterId: ParameterId
          FloatParameterId: ParameterId
          ListParameterId: ParameterId
          ListItemParameterId: ParameterId
          MapperFunctionId: FunctionId
          PredicateFunctionId: FunctionId
          ZeroArityPredicateFunctionId: FunctionId
          WrongInputPredicateFunctionId: FunctionId
          WrongResultPredicateFunctionId: FunctionId
          EffectfulPredicateFunctionId: FunctionId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-pure-structural-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-pure-structural-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-pure-structural-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-pure-structural-" + name)
    let private effectId name = StableId.createForTesting<EffectTag> ("effect-pure-structural-" + name)

    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private int64ListType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ int64Type ])
    let private stringListType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ])

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
        let listParameter: SemanticParameter =
            { Id = parameterId "list-source"
              DisplayName = "source"
              Type = int64ListType }
        let listItemParameter: SemanticParameter =
            { Id = parameterId "list-item"
              DisplayName = "item"
              Type = int64Type }

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

        let listTarget =
            functionWithBody
                (functionId "list")
                "list_target"
                [ listParameter; listItemParameter ]
                int64ListType
                (nodeId "list-result")
                (SemanticNodeKind.ListEmpty int64Type)

        let stringListTarget =
            functionWithBody
                (functionId "string-list")
                "string_list_target"
                []
                stringListType
                (nodeId "string-list-result")
                (SemanticNodeKind.ListEmpty stringType)

        let mapperParameter: SemanticParameter =
            { Id = parameterId "mapper-value"
              DisplayName = "value"
              Type = int64Type }
        let mapper =
            functionWithBody
                (functionId "mapper")
                "mapper"
                [ mapperParameter ]
                stringType
                (nodeId "mapper-result")
                (SemanticNodeKind.StringLiteral "mapped")

        let predicateParameter: SemanticParameter =
            { Id = parameterId "predicate-value"
              DisplayName = "value"
              Type = int64Type }
        let predicate =
            functionWithBody
                (functionId "predicate")
                "predicate"
                [ predicateParameter ]
                boolType
                (nodeId "predicate-result")
                (SemanticNodeKind.BoolLiteral true)

        let zeroArityPredicate =
            functionWithBody
                (functionId "predicate-zero-arity")
                "predicate_zero_arity"
                []
                boolType
                (nodeId "predicate-zero-arity-result")
                (SemanticNodeKind.BoolLiteral true)

        let wrongInputParameter: SemanticParameter =
            { Id = parameterId "predicate-wrong-input-value"
              DisplayName = "value"
              Type = stringType }
        let wrongInputPredicate =
            functionWithBody
                (functionId "predicate-wrong-input")
                "predicate_wrong_input"
                [ wrongInputParameter ]
                boolType
                (nodeId "predicate-wrong-input-result")
                (SemanticNodeKind.BoolLiteral true)

        let wrongResultParameter: SemanticParameter =
            { Id = parameterId "predicate-wrong-result-value"
              DisplayName = "value"
              Type = int64Type }
        let wrongResultPredicate =
            functionWithBody
                (functionId "predicate-wrong-result")
                "predicate_wrong_result"
                [ wrongResultParameter ]
                int64Type
                (nodeId "predicate-wrong-result-result")
                (SemanticNodeKind.Int64Literal 1L)

        let callbackEffect: SemanticEffect =
            { Id = effectId "callback"
              DisplayName = "callback_effect" }
        let effectfulParameter: SemanticParameter =
            { Id = parameterId "predicate-effectful-value"
              DisplayName = "value"
              Type = int64Type }
        let effectfulPredicate =
            { functionWithBody
                (functionId "predicate-effectful")
                "predicate_effectful"
                [ effectfulParameter ]
                boolType
                (nodeId "predicate-effectful-result")
                (SemanticNodeKind.BoolLiteral true) with
                RequiredEffects = [ callbackEffect.Id ] }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = [ callbackEffect ]
              Capabilities = []
              Functions =
                [ stringTarget
                  boolTarget
                  unitTarget
                  intTarget
                  floatTarget
                  listTarget
                  stringListTarget
                  mapper
                  predicate
                  zeroArityPredicate
                  wrongInputPredicate
                  wrongResultPredicate
                  effectfulPredicate ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          StringTargetId = stringTarget.Id
          BoolTargetId = boolTarget.Id
          UnitTargetId = unitTarget.Id
          IntTargetId = intTarget.Id
          FloatTargetId = floatTarget.Id
          ListTargetId = listTarget.Id
          StringListTargetId = stringListTarget.Id
          TextParameterId = textParameter.Id
          SubstringParameterId = substringParameter.Id
          CountParameterId = countParameter.Id
          FloatParameterId = floatParameter.Id
          ListParameterId = listParameter.Id
          ListItemParameterId = listItemParameter.Id
          MapperFunctionId = mapper.Id
          PredicateFunctionId = predicate.Id
          ZeroArityPredicateFunctionId = zeroArityPredicate.Id
          WrongInputPredicateFunctionId = wrongInputPredicate.Id
          WrongResultPredicateFunctionId = wrongResultPredicate.Id
          EffectfulPredicateFunctionId = effectfulPredicate.Id }

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

    let private assertRoles expectedRoles proposal =
        let roles =
            (AgentPureExpressionStructuralChangeProposal.review proposal).ResultingChange.AddedNodes
            |> List.map (fun node -> node.Role)
        if roles <> expectedRoles then
            failwithf "Unexpected materialization order. Expected %A, got %A" expectedRoles roles

    let private intList values =
        PureExpressionRecipe.ListConstruct(
            int64Type,
            values |> List.map PureExpressionRecipe.Int64Literal
        )

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
            request fixture fixture.BoolTargetId recipe
            |> simulate workspace
            |> assertRoles expectedRoles

    [<Fact>]
    let ``pure expression workflow materializes List construction and query recipes`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let cases =
            [ fixture.ListTargetId,
              PureExpressionRecipe.ListConstruct(
                  int64Type,
                  [ PureExpressionRecipe.Int64Literal 1L
                    PureExpressionRecipe.Int64Literal 2L ]
              ),
              [ "Int64Literal"; "Int64Literal"; "ListConstruct" ]
              fixture.ListTargetId,
              PureExpressionRecipe.ListAppendItem(
                  PureExpressionRecipe.Parameter fixture.ListParameterId,
                  PureExpressionRecipe.Parameter fixture.ListItemParameterId
              ),
              [ "ParameterReference"; "ParameterReference"; "ListAppendItem" ]
              fixture.ListTargetId,
              PureExpressionRecipe.ListConcat(
                  PureExpressionRecipe.Parameter fixture.ListParameterId,
                  PureExpressionRecipe.ListEmpty int64Type
              ),
              [ "ParameterReference"; "ListEmpty"; "ListConcat" ]
              fixture.IntTargetId,
              PureExpressionRecipe.ListLength(
                  PureExpressionRecipe.ListConstruct(
                      int64Type,
                      [ PureExpressionRecipe.Int64Literal 10L
                        PureExpressionRecipe.Int64Literal 20L ]
                  )
              ),
              [ "Int64Literal"; "Int64Literal"; "ListConstruct"; "ListLength" ] ]

        for targetId, recipe, expectedRoles in cases do
            request fixture targetId recipe
            |> simulate workspace
            |> assertRoles expectedRoles

    [<Fact>]
    let ``pure expression workflow materializes higher-order List recipes with existing pure callbacks`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let cases =
            [ fixture.StringListTargetId,
              PureExpressionRecipe.ListMap(intList [ 1L; 2L ], fixture.MapperFunctionId, []),
              [ "Int64Literal"; "Int64Literal"; "ListConstruct"; "ListMap" ]
              fixture.ListTargetId,
              PureExpressionRecipe.ListFilter(intList [ 1L; 2L ], fixture.PredicateFunctionId, []),
              [ "Int64Literal"; "Int64Literal"; "ListConstruct"; "ListFilter" ]
              fixture.BoolTargetId,
              PureExpressionRecipe.ListAny(intList [ 1L ], fixture.PredicateFunctionId, []),
              [ "Int64Literal"; "ListConstruct"; "ListAny" ]
              fixture.BoolTargetId,
              PureExpressionRecipe.ListAll(intList [ 1L ], fixture.PredicateFunctionId, []),
              [ "Int64Literal"; "ListConstruct"; "ListAll" ] ]

        for targetId, recipe, expectedRoles in cases do
            let proposal = request fixture targetId recipe |> simulate workspace
            assertRoles expectedRoles proposal

        let mapProposal =
            request
                fixture
                fixture.StringListTargetId
                (PureExpressionRecipe.ListMap(intList [ 1L ], fixture.MapperFunctionId, []))
            |> simulate workspace
        let mapNode =
            (AgentPureExpressionStructuralChangeProposal.review mapProposal).ResultingChange.AddedNodes
            |> List.last
        Assert.Equal(Some fixture.MapperFunctionId, mapNode.FunctionId)

    [<Fact>]
    let ``pure expression preflight rejects result operand parameter explicit List type and callback violations`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        request fixture fixture.StringTargetId (PureExpressionRecipe.StringLiteral null)
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionNullStringLiteral"

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

        request
            fixture
            fixture.ListTargetId
            (PureExpressionRecipe.ListConstruct(stringType, [ PureExpressionRecipe.Int64Literal 1L ]))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionOperandTypeMismatch"

        let missingType = StableId.createForTesting<TypeTag> "type-pure-structural-missing"
        request fixture fixture.ListTargetId (PureExpressionRecipe.ListEmpty(TypeRef.Named(missingType, [])))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionInvalidTypeRef"

        let missingCallback = StableId.createForTesting<FunctionTag> "function-pure-structural-missing-callback"
        request fixture fixture.BoolTargetId (PureExpressionRecipe.ListAny(intList [ 1L ], missingCallback, []))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionCallbackNotFound"

        request
            fixture
            fixture.BoolTargetId
            (PureExpressionRecipe.ListAny(intList [ 1L ], fixture.ZeroArityPredicateFunctionId, []))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionCallbackArityMismatch"

        request
            fixture
            fixture.BoolTargetId
            (PureExpressionRecipe.ListAny(intList [ 1L ], fixture.WrongInputPredicateFunctionId, []))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionCallbackInputTypeMismatch"

        request
            fixture
            fixture.BoolTargetId
            (PureExpressionRecipe.ListAny(intList [ 1L ], fixture.WrongResultPredicateFunctionId, []))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionCallbackResultTypeMismatch"

        request
            fixture
            fixture.BoolTargetId
            (PureExpressionRecipe.ListAny(intList [ 1L ], fixture.EffectfulPredicateFunctionId, []))
        |> AgentPureExpressionStructuralWorkflow.simulate workspace
        |> assertRejectedCode "PureExpressionCallbackEffectsNotAllowed"

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
              "ParameterSlot"
              "PatternReference"
              "StringLiteral"
              "BoolLiteral"
              "UnitLiteral"
              "Int64Literal"
              "Float64Literal"
              "StringIsEmpty"
              "Int64ToString"
              "Int64ToHexString"
              "StringParseInt64"
              "StringParseInt64Hex"
              "Float64ToString"
              "StringParseFloat64"
              "StringCodePoints"
              "StringToLower"
              "StringToUpper"
              "StringDecodeUtf8Lossy"
              "StringEncodeUtf8"
              "Float64RoundToInt64"
              "Float64FloorToInt64"
              "Float64Sqrt"
              "Float64Abs"
              "Float64Negate"
              "Float64Floor"
              "Float64Ceiling"
              "Float64Truncate"
              "StringUtf8ByteCount"
              "StringFromCodePoint"
              "StringToCodePoint"
              "StringLength"
              "StringSlice"
              "StringContains"
              "StringIndexOf"
              "StringSpan"
              "StringSpanBack"
              "StringLastIndexOf"
              "StringStartsWith"
              "StringEndsWith"
              "StringSplit"
              "StringSplitLines"
              "StringReplace"
              "StringTrim"
              "StringTrimStart"
              "StringTrimEnd"
              "StringJoin"
              "StringEqual"
              "StringLessThan"
              "ValueEqual"
              "StringConcat"
              "Int64WrappingAdd"
              "Int64WrappingSubtract"
              "Int64WrappingMultiply"
              "Int64FloorDivide"
              "Int64FloorModulo"
              "Int64Min"
              "Int64Max"
              "Int64BitAnd"
              "Int64BitOr"
              "Int64BitXor"
              "Int64ShiftLeft"
              "Int64ShiftRight"
              "Int64CheckedAdd"
              "Int64CheckedSubtract"
              "Int64CheckedMultiply"
              "ListRange"
              "StringRepeat"
              "Float64Round"
              "Int64Equal"
              "Int64LessThan"
              "Int64LessThanOrEqual"
              "Int64GreaterThan"
              "Int64GreaterThanOrEqual"
              "Float64Add"
              "Float64Min"
              "Float64Max"
              "Float64Subtract"
              "Float64Multiply"
              "Float64Divide"
              "Float64Equal"
              "Float64LessThan"
              "Float64IsNaN"
              "Float64IsFinite"
              "Float64IsInfinite"
              "Int64ToFloat64"
              "Int64WrappingAbs"
              "Int64BitNot"
              "Bits32FromInt64"
              "Bits32ToInt64"
              "Bits32Not"
              "Bits32Xor"
              "Bits32And"
              "Bits32Or"
              "Bits32Add"
              "Bits32RotateRight"
              "Bits32ShiftRight"
              "Bits32ShiftLeft"
              "ListEmpty"
              "SetEmpty"
              "MapEmpty"
              "ListConstruct"
              "ListLength"
              "MapValues"
              "MapKeys"
              "SetToList"
              "MapSize"
              "SetFromList"
              "SetSize"
              "ListReverse"
              "ListLast"
              "ListDropLast"
              "ListAppendItem"
              "ListItemAt"
              "MapRemove"
              "SetInsert"
              "SetRemove"
              "MapContainsKey"
              "SetContains"
              "ListIndexOf"
              "ListLastIndexOf"
              "ListContains"
              "MapGet"
              "MapGetOrDefault"
              "ListSlice"
              "ListSetAt"
              "ListInsertAt"
              "ListRemoveAt"
              "MapInsert"
              "ListConcat"
              "SetUnion"
              "SetIntersection"
              "SetDifference"
              "SetIsSubset"
              "MapUnion"
              "ListFold"
              "MapFold"
              "ParallelAll"
              "CancellationRequested"
              "ExceptionCatch"
              "ExceptionIsCancellation"
              "ExceptionMessage"
              "ExceptionRethrow"
              "CancellationShield"
              "TaskScope"
              "TaskStart"
              "TaskWait"
              "TaskCancel"
              "OneShotCreate"
              "OneShotComplete"
              "OneShotWait"
              "UseResource"
              "ListEffectFold"
              "ListMap"
              "ListZipWith"
              "ListFlatMap"
              "MapFromList"
              "ListFilter"
              "MapFilter"
              "MapMapValues"
              "ListSortBy"
              "ListMinBy"
              "ListAny"
              "ListFindIndex"
              "ListAll"
              "ProductConstruct"
              "FieldAccess"
              "ProductUpdate"
              "UnionConstruct"
              "OptionNone"
              "OptionSome"
              "OptionGetOrDefault"
              "ResultGetOrDefault"
              "OptionOkOr"
              "ResultOkValue"
              "ResultErrValue"
              "OptionIsSome"
              "ResultIsOk"
              "OptionMap"
              "OptionAndThen"
              "ResultMap"
              "ResultMapErr"
              "ResultAndThen"
              "ResultOk"
              "Abort"
              "ResultErr"
              "CapabilityConstruct"
              "FunctionReference"
              "FunctionInvoke"
              "DirectCall"
              "SelfCall"
              "CapabilityInvoke"
              "BoolNot"
              "BoolAnd"
              "BoolOr"
              "Conditional"
              "MatchUnion"
              "Bind"
              "MatchOption"
              "MatchResult" ]

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
