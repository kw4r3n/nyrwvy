namespace NyamNyam.AgentEval.PureExpression

open NyamNyam.Core

type PureExpressionEvalFixture =
    { Snapshot: Snapshot
      TargetFunctionId: FunctionId
      InitialResultNodeId: GraphNodeId
      OptionParameterId: ParameterId
      UnionParameterId: ParameterId
      FirstVariantId: VariantId
      SecondVariantId: VariantId
      FirstFieldId: FieldId }

[<RequireQualifiedAccess>]
module PureExpressionEvalFixture =
    let private typeId name = StableId.createForTesting<TypeTag> ("type-pure-control-" + name)
    let private fieldId name = StableId.createForTesting<FieldTag> ("field-pure-control-" + name)
    let private variantId name = StableId.createForTesting<VariantTag> ("variant-pure-control-" + name)
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-pure-control-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-pure-control-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-pure-control-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-pure-control-" + name)

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private optionType itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])

    let create () =
        let choiceTypeId = typeId "choice"
        let firstFieldId = fieldId "first-value"
        let firstVariantId = variantId "first"
        let secondVariantId = variantId "second"

        let choiceType: SemanticType =
            { Id = choiceTypeId
              DisplayName = "choice"
              Kind =
                SemanticTypeKind.Union
                    [ { Id = firstVariantId
                        DisplayName = "first"
                        Fields =
                            [ { Id = firstFieldId
                                DisplayName = "value"
                                Type = int64Type } ] }
                      { Id = secondVariantId
                        DisplayName = "second"
                        Fields = [] } ] }

        let optionParameter: SemanticParameter =
            { Id = parameterId "option"
              DisplayName = "option"
              Type = optionType int64Type }

        let unionParameter: SemanticParameter =
            { Id = parameterId "union"
              DisplayName = "union"
              Type = TypeRef.Named choiceTypeId }

        let targetFunctionId = functionId "target"
        let initialResultNodeId = nodeId "old-result"

        let target: SemanticFunction =
            { Id = targetFunctionId
              DisplayName = "target"
              Parameters = [ optionParameter; unionParameter ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = initialResultNodeId; Kind = SemanticNodeKind.Int64Literal 0L } ]
                  ResultNodeId = initialResultNodeId } }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = [ choiceType ]
              Effects = []
              Capabilities = []
              Functions = [ target ]
              Obligations = [] }

        match SnapshotValidator.validate snapshot with
        | [] ->
            { Snapshot = snapshot
              TargetFunctionId = targetFunctionId
              InitialResultNodeId = initialResultNodeId
              OptionParameterId = optionParameter.Id
              UnionParameterId = unionParameter.Id
              FirstVariantId = firstVariantId
              SecondVariantId = secondVariantId
              FirstFieldId = firstFieldId }
        | issues -> failwithf "Pure-expression evaluation fixture is invalid: %A" issues
