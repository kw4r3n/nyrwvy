namespace NyamNyam.Core.Tests

open NyamNyam.Core

type M2StructuralEvaluationFixture =
    { Name: string
      Snapshot: Snapshot
      TargetFunctionId: FunctionId
      TargetFloatParameterId: ParameterId
      TargetWrongTypeParameterId: ParameterId
      TargetResultNodeId: GraphNodeId
      CallerFunctionId: FunctionId
      CallerCallNodeId: GraphNodeId
      ForeignFunctionId: FunctionId
      ForeignParameterId: ParameterId
      UnrelatedFunctionId: FunctionId
      UnrelatedResultNodeId: GraphNodeId
      ProtectedCallNodeId: GraphNodeId option
      ProtectedObligationId: ObligationId option }

type M2StructuralCorpusTask =
    { Id: string
      FixtureName: string
      ExpectedDiagnostic: string option
      RecoveryRequired: bool
      ExpectedCommit: bool }

[<RequireQualifiedAccess>]
module M2StructuralEvaluationCorpus =
    let corpusId = "m2-agent-corpus-v1"
    let protocolVersion = "m2-structural-v1"

    let readyFixtureName = "m2-ready"
    let openProtectedFixtureName = "m2-open-protected"
    let resolvedProtectedFixtureName = "m2-resolved-protected"

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-m2-eval-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-m2-eval-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-m2-eval-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-m2-eval-" + name)
    let private obligationId name = StableId.createForTesting<ObligationTag> ("obligation-m2-eval-" + name)
    let private transactionId name = StableId.createForTesting<TransactionTag> ("transaction-m2-eval-" + name)

    let wrongBaseSnapshotId = snapshotId "wrong-base"
    let wrongResultNodeId = nodeId "wrong-result"

    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let ready (): M2StructuralEvaluationFixture =
        let floatParameter: SemanticParameter =
            { Id = parameterId "target-value"
              DisplayName = "value"
              Type = float64Type }

        let wrongTypeParameter: SemanticParameter =
            { Id = parameterId "target-count"
              DisplayName = "count"
              Type = int64Type }

        let valueReferenceId = nodeId "target-value-reference"
        let biasId = nodeId "target-bias"
        let targetResultId = nodeId "target-result"
        let target: SemanticFunction =
            { Id = functionId "target"
              DisplayName = "target_metric"
              Parameters = [ floatParameter; wrongTypeParameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = valueReferenceId
                        Kind = SemanticNodeKind.ParameterReference floatParameter.Id }
                      { Id = biasId
                        Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0) }
                      { Id = targetResultId
                        Kind = SemanticNodeKind.Float64Add(valueReferenceId, biasId) } ]
                  ResultNodeId = targetResultId } }

        let callerValueId = nodeId "caller-value"
        let callerCountId = nodeId "caller-count"
        let callerCallId = nodeId "caller-call"
        let callerArguments: ArgumentBinding list =
            [ { TargetParameterId = floatParameter.Id
                ValueNodeId = callerValueId }
              { TargetParameterId = wrongTypeParameter.Id
                ValueNodeId = callerCountId } ]
        let caller: SemanticFunction =
            { Id = functionId "caller"
              DisplayName = "evaluate_metric"
              Parameters = []
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = callerValueId
                        Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.5) }
                      { Id = callerCountId
                        Kind = SemanticNodeKind.Int64Literal 7L }
                      { Id = callerCallId
                        Kind = SemanticNodeKind.DirectCall(target.Id, callerArguments) } ]
                  ResultNodeId = callerCallId } }

        let foreignParameter: SemanticParameter =
            { Id = parameterId "foreign-value"
              DisplayName = "foreign_value"
              Type = float64Type }
        let foreignResultId = nodeId "foreign-result"
        let foreign: SemanticFunction =
            { Id = functionId "foreign"
              DisplayName = "foreign_source"
              Parameters = [ foreignParameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = foreignResultId
                        Kind = SemanticNodeKind.ParameterReference foreignParameter.Id } ]
                  ResultNodeId = foreignResultId } }

        let unrelatedResultId = nodeId "unrelated-result"
        let unrelated: SemanticFunction =
            { Id = functionId "unrelated"
              DisplayName = "unrelated_metric"
              Parameters = []
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = unrelatedResultId
                        Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 99.0) } ]
                  ResultNodeId = unrelatedResultId } }

        let snapshot: Snapshot =
            { Id = snapshotId "ready"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ target; caller; foreign; unrelated ]
              Obligations = [] }

        { Name = readyFixtureName
          Snapshot = snapshot
          TargetFunctionId = target.Id
          TargetFloatParameterId = floatParameter.Id
          TargetWrongTypeParameterId = wrongTypeParameter.Id
          TargetResultNodeId = targetResultId
          CallerFunctionId = caller.Id
          CallerCallNodeId = callerCallId
          ForeignFunctionId = foreign.Id
          ForeignParameterId = foreignParameter.Id
          UnrelatedFunctionId = unrelated.Id
          UnrelatedResultNodeId = unrelatedResultId
          ProtectedCallNodeId = None
          ProtectedObligationId = None }

    let private protectedFixture (resolved: bool): M2StructuralEvaluationFixture =
        let baseFixture = ready ()

        let helperParameter: SemanticParameter =
            { Id = parameterId "protected-helper-value"
              DisplayName = "value"
              Type = float64Type }
        let helperResultId = nodeId "protected-helper-result"
        let helper: SemanticFunction =
            { Id = functionId "protected-helper"
              DisplayName = "protected_helper"
              Parameters = [ helperParameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = helperResultId
                        Kind = SemanticNodeKind.ParameterReference helperParameter.Id } ]
                  ResultNodeId = helperResultId } }

        let protectedValueId = nodeId (if resolved then "resolved-protected-value" else "open-protected-value")
        let protectedCallId = nodeId (if resolved then "resolved-protected-call" else "open-protected-call")
        let arguments: ArgumentBinding list =
            if resolved then
                [ { TargetParameterId = helperParameter.Id
                    ValueNodeId = protectedValueId } ]
            else
                []

        let targetBodyNodes =
            (if resolved then
                 [ { Id = protectedValueId
                     Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 4.0) } ]
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

        let origin = transactionId (if resolved then "resolved-protected" else "open-protected")
        let status =
            if resolved then
                ObligationStatus.Resolved
                    { TransactionId = origin
                      ValueNodeId = protectedValueId }
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

    let fixtureByName name =
        match name with
        | value when value = readyFixtureName -> Ok(ready ())
        | value when value = openProtectedFixtureName -> Ok(openProtected ())
        | value when value = resolvedProtectedFixtureName -> Ok(resolvedProtected ())
        | value -> Error $"Unknown M2 structural evaluation fixture '{value}'."

    let tasks: M2StructuralCorpusTask list =
        [ { Id = "M2-E1"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = None
            RecoveryRequired = false
            ExpectedCommit = true }
          { Id = "M2-E2"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralExpectedBaseMismatch"
            RecoveryRequired = true
            ExpectedCommit = true }
          { Id = "M2-E3"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralExpectedResultMismatch"
            RecoveryRequired = true
            ExpectedCommit = true }
          { Id = "M2-E4"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralParameterOutOfScope"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-E5"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralParameterTypeMismatch"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-E6"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralRecipeDepthLimitExceeded"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-E7"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StructuralRecipeNodeLimitExceeded"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-E8"
            FixtureName = openProtectedFixtureName
            ExpectedDiagnostic = Some "StructuralBodyReferencedByObligation"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-E9"
            FixtureName = resolvedProtectedFixtureName
            ExpectedDiagnostic = Some "StructuralBodyReferencedByObligation"
            RecoveryRequired = false
            ExpectedCommit = false }
          { Id = "M2-E10"
            FixtureName = readyFixtureName
            ExpectedDiagnostic = Some "StaleBaseSnapshot"
            RecoveryRequired = true
            ExpectedCommit = true } ]
