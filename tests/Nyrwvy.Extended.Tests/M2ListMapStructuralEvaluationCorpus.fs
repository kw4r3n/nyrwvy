namespace Nyrwvy.Core.Tests

open Nyrwvy.Core

type M2ListMapEvaluationFixture =
    { Name: string
      Snapshot: Snapshot
      TargetFunctionId: FunctionId
      SourceListParameterId: ParameterId
      NonListParameterId: ParameterId
      TargetResultNodeId: GraphNodeId
      CompatibleMapperFunctionId: FunctionId
      WrongInputMapperFunctionId: FunctionId
      WrongResultMapperFunctionId: FunctionId
      EffectfulMapperFunctionId: FunctionId
      CallerFunctionId: FunctionId
      CallerCallNodeId: GraphNodeId
      ForeignFunctionId: FunctionId
      ForeignListParameterId: ParameterId
      UnrelatedFunctionId: FunctionId
      UnrelatedResultNodeId: GraphNodeId
      ProtectedCallNodeId: GraphNodeId option
      ProtectedObligationId: ObligationId option }

type M2ListMapCorpusTask =
    { Id: string
      FixtureName: string
      ExpectedDiagnostic: string option
      RecoveryRequired: bool
      ExpectedCommit: bool }

[<RequireQualifiedAccess>]
module M2ListMapStructuralEvaluationCorpus =
    let corpusId = "m2-listmap-agent-corpus-v1"
    let protocolVersion = "m2-listmap-structural-v1"

    let readyFixtureName = "m2-listmap-ready"
    let openProtectedFixtureName = "m2-listmap-open-protected"
    let resolvedProtectedFixtureName = "m2-listmap-resolved-protected"

    let private functionId name =
        StableId.createForTesting<FunctionTag> ("function-m2-listmap-eval-" + name)

    let private parameterId name =
        StableId.createForTesting<ParameterTag> ("parameter-m2-listmap-eval-" + name)

    let private nodeId name =
        StableId.createForTesting<GraphNodeTag> ("node-m2-listmap-eval-" + name)

    let private snapshotId name =
        StableId.createForTesting<SnapshotTag> ("snapshot-m2-listmap-eval-" + name)

    let private effectId name =
        StableId.createForTesting<EffectTag> ("effect-m2-listmap-eval-" + name)

    let private obligationId name =
        StableId.createForTesting<ObligationTag> ("obligation-m2-listmap-eval-" + name)

    let private transactionId name =
        StableId.createForTesting<TransactionTag> ("transaction-m2-listmap-eval-" + name)

    let missingMapperFunctionId = functionId "missing-mapper"

    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listOf itemType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])
    let private int64ListType = listOf int64Type
    let private boolListType = listOf boolType
    let private callbackEffectId = effectId "callback"

    let private unaryFunction
        (name: string)
        (parameterType: TypeRef)
        (resultType: TypeRef)
        (requiredEffects: EffectId list)
        (bodyBuilder: GraphNodeId -> SemanticNode list * GraphNodeId)
        : SemanticFunction =
        let parameter: SemanticParameter =
            { Id = parameterId (name + "-value")
              DisplayName = "value"
              Type = parameterType }
        let parameterNodeId = nodeId (name + "-parameter")
        let parameterNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameter.Id }
        let extraNodes, resultNodeId = bodyBuilder parameterNodeId
        { Id = functionId name
          DisplayName = name
          Parameters = [ parameter ]
          ResultType = resultType
          RequiredEffects = requiredEffects
          Body =
            { Nodes = parameterNode :: extraNodes
              ResultNodeId = resultNodeId } }

    let ready (): M2ListMapEvaluationFixture =
        let compatibleMapper =
            unaryFunction "positive" int64Type boolType [] (fun valueNodeId ->
                let zeroId = nodeId "positive-zero"
                let resultId = nodeId "positive-result"
                [ { Id = zeroId; Kind = SemanticNodeKind.Int64Literal 0L }
                  { Id = resultId; Kind = SemanticNodeKind.Int64GreaterThan(valueNodeId, zeroId) } ],
                resultId)

        let wrongInputMapper =
            unaryFunction "string-to-bool" stringType boolType [] (fun _ ->
                let resultId = nodeId "string-to-bool-result"
                [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

        let wrongResultMapper =
            unaryFunction "int-identity" int64Type int64Type [] (fun valueNodeId -> [], valueNodeId)

        let effectfulMapper =
            unaryFunction "effectful-positive" int64Type boolType [ callbackEffectId ] (fun _ ->
                let resultId = nodeId "effectful-positive-result"
                [ { Id = resultId; Kind = SemanticNodeKind.BoolLiteral true } ], resultId)

        let boolIdentity =
            unaryFunction "bool-identity" boolType boolType [] (fun valueNodeId -> [], valueNodeId)

        let sourceParameter: SemanticParameter =
            { Id = parameterId "target-values"
              DisplayName = "values"
              Type = int64ListType }

        let nonListParameter: SemanticParameter =
            { Id = parameterId "target-count"
              DisplayName = "count"
              Type = int64Type }

        let targetResultId = nodeId "target-result"
        let target: SemanticFunction =
            { Id = functionId "target"
              DisplayName = "map_signs"
              Parameters = [ sourceParameter; nonListParameter ]
              ResultType = boolListType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = targetResultId; Kind = SemanticNodeKind.ListEmpty boolType } ]
                  ResultNodeId = targetResultId } }

        let callerFirstId = nodeId "caller-first"
        let callerSecondId = nodeId "caller-second"
        let callerThirdId = nodeId "caller-third"
        let callerListId = nodeId "caller-list"
        let callerCountId = nodeId "caller-count"
        let callerCallId = nodeId "caller-call"
        let callerAnyId = nodeId "caller-any"
        let caller: SemanticFunction =
            { Id = functionId "caller"
              DisplayName = "evaluate_listmap"
              Parameters = []
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = callerFirstId; Kind = SemanticNodeKind.Int64Literal 1L }
                      { Id = callerSecondId; Kind = SemanticNodeKind.Int64Literal -2L }
                      { Id = callerThirdId; Kind = SemanticNodeKind.Int64Literal 3L }
                      { Id = callerListId
                        Kind =
                            SemanticNodeKind.ListConstruct(
                                int64Type,
                                [ callerFirstId; callerSecondId; callerThirdId ]
                            ) }
                      { Id = callerCountId; Kind = SemanticNodeKind.Int64Literal 0L }
                      { Id = callerCallId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                target.Id,
                                [ { TargetParameterId = sourceParameter.Id; ValueNodeId = callerListId }
                                  { TargetParameterId = nonListParameter.Id; ValueNodeId = callerCountId } ]
                            ) }
                      { Id = callerAnyId; Kind = SemanticNodeKind.ListAny(callerCallId, boolIdentity.Id) } ]
                  ResultNodeId = callerAnyId } }

        let foreignParameter: SemanticParameter =
            { Id = parameterId "foreign-values"
              DisplayName = "foreign_values"
              Type = int64ListType }
        let foreignResultId = nodeId "foreign-result"
        let foreign: SemanticFunction =
            { Id = functionId "foreign"
              DisplayName = "foreign_source"
              Parameters = [ foreignParameter ]
              ResultType = int64ListType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = foreignResultId
                        Kind = SemanticNodeKind.ParameterReference foreignParameter.Id } ]
                  ResultNodeId = foreignResultId } }

        let unrelatedResultId = nodeId "unrelated-result"
        let unrelated: SemanticFunction =
            { Id = functionId "unrelated"
              DisplayName = "unrelated_marker"
              Parameters = []
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = unrelatedResultId; Kind = SemanticNodeKind.Int64Literal 99L } ]
                  ResultNodeId = unrelatedResultId } }

        let snapshot: Snapshot =
            { Id = snapshotId "ready"
              ParentId = None
              Types = []
              Effects = [ { Id = callbackEffectId; DisplayName = "callback_effect" } ]
              Capabilities = []
              Functions =
                [ compatibleMapper
                  wrongInputMapper
                  wrongResultMapper
                  effectfulMapper
                  boolIdentity
                  target
                  caller
                  foreign
                  unrelated ]
              Obligations = [] }

        { Name = readyFixtureName
          Snapshot = snapshot
          TargetFunctionId = target.Id
          SourceListParameterId = sourceParameter.Id
          NonListParameterId = nonListParameter.Id
          TargetResultNodeId = targetResultId
          CompatibleMapperFunctionId = compatibleMapper.Id
          WrongInputMapperFunctionId = wrongInputMapper.Id
          WrongResultMapperFunctionId = wrongResultMapper.Id
          EffectfulMapperFunctionId = effectfulMapper.Id
          CallerFunctionId = caller.Id
          CallerCallNodeId = callerCallId
          ForeignFunctionId = foreign.Id
          ForeignListParameterId = foreignParameter.Id
          UnrelatedFunctionId = unrelated.Id
          UnrelatedResultNodeId = unrelatedResultId
          ProtectedCallNodeId = None
          ProtectedObligationId = None }

    let private protectedFixture (resolved: bool): M2ListMapEvaluationFixture =
        let baseFixture = ready ()
        let helperParameter: SemanticParameter =
            { Id = parameterId "protected-helper-values"
              DisplayName = "values"
              Type = int64ListType }
        let helperResultId = nodeId "protected-helper-result"
        let helper: SemanticFunction =
            { Id = functionId "protected-helper"
              DisplayName = "protected_helper"
              Parameters = [ helperParameter ]
              ResultType = boolListType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = helperResultId; Kind = SemanticNodeKind.ListEmpty boolType } ]
                  ResultNodeId = helperResultId } }

        let sourceReferenceId =
            nodeId (if resolved then "resolved-protected-source" else "open-protected-source")
        let protectedCallId =
            nodeId (if resolved then "resolved-protected-call" else "open-protected-call")
        let arguments: ArgumentBinding list =
            if resolved then
                [ { TargetParameterId = helperParameter.Id
                    ValueNodeId = sourceReferenceId } ]
            else
                []
        let targetBodyNodes =
            (if resolved then
                 [ { Id = sourceReferenceId
                     Kind = SemanticNodeKind.ParameterReference baseFixture.SourceListParameterId } ]
             else
                 [])
            @ [ { Id = protectedCallId
                  Kind = SemanticNodeKind.DirectCall(helper.Id, arguments) } ]

        let target =
            baseFixture.Snapshot.Functions
            |> List.find (fun fn -> fn.Id = baseFixture.TargetFunctionId)
            |> fun fn ->
                { fn with
                    Body =
                        { Nodes = targetBodyNodes
                          ResultNodeId = protectedCallId } }

        let origin =
            transactionId (if resolved then "resolved-protected" else "open-protected")
        let status =
            if resolved then
                ObligationStatus.Resolved
                    { TransactionId = origin
                      ValueNodeId = sourceReferenceId }
            else
                ObligationStatus.Open
        let obligation: MissingArgumentObligation =
            { Id = obligationId (if resolved then "resolved-protected" else "open-protected")
              OriginTransactionId = origin
              CallNodeId = protectedCallId
              ParameterId = helperParameter.Id
              Status = status }

        let snapshot: Snapshot =
            { baseFixture.Snapshot with
                Id = snapshotId (if resolved then "resolved-protected" else "open-protected")
                Functions =
                    target
                    :: helper
                    :: (baseFixture.Snapshot.Functions
                        |> List.filter (fun fn -> fn.Id <> target.Id))
                Obligations = [ obligation ] }

        { baseFixture with
            Name = if resolved then resolvedProtectedFixtureName else openProtectedFixtureName
            Snapshot = snapshot
            TargetResultNodeId = protectedCallId
            ProtectedCallNodeId = Some protectedCallId
            ProtectedObligationId = Some obligation.Id }

    let openProtected () = protectedFixture false
    let resolvedProtected () = protectedFixture true

    let fixtureByName (name: string): Result<M2ListMapEvaluationFixture, string> =
        match name with
        | value when value = readyFixtureName -> Ok(ready ())
        | value when value = openProtectedFixtureName -> Ok(openProtected ())
        | value when value = resolvedProtectedFixtureName -> Ok(resolvedProtected ())
        | value -> Error $"Unknown M2 ListMap evaluation fixture '{value}'."

    let tasks: M2ListMapCorpusTask list =
        [ { Id = "M2-L1"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = None
            RecoveryRequired = false
            ExpectedCommit = true }
          { Id = "M2-L2"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralListSourceParameterOutOfScope"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L3"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralListSourceTypeMismatch"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L4"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralListMapperNotFound"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L5"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralListMapperInputTypeMismatch"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L6"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralListMapperResultTypeMismatch"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L7"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralListMapperEffectsNotAllowed"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L8"
            FixtureName = openProtectedFixtureName
            ExpectedDiagnostic = Some "StructuralBodyReferencedByObligation"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L9"
            FixtureName = resolvedProtectedFixtureName
            ExpectedDiagnostic = Some "StructuralBodyReferencedByObligation"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-L10"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StaleBaseSnapshot"
            RecoveryRequired = true
            ExpectedCommit = true } ]
