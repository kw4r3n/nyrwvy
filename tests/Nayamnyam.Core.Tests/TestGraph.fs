namespace Nayamnyam.Core.Tests

open Nayamnyam.Core

type GraphFixture =
    { Snapshot: Snapshot
      StringTypeId: TypeId
      PolicyTypeId: TypeId
      ValidateFunctionId: FunctionId
      ValidateInputParameterId: ParameterId
      ValidateInputNodeId: GraphNodeId
      RunFunctionId: FunctionId
      RunInputParameterId: ParameterId
      RunPolicyParameterId: ParameterId
      RunInputNodeId: GraphNodeId
      RunPolicyNodeId: GraphNodeId
      RunCallNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module TestGraph =
    let create (): GraphFixture =
        let stringTypeId = StableId.createForTesting<TypeTag> "type-string"
        let policyTypeId = StableId.createForTesting<TypeTag> "type-policy"
        let validateFunctionId = StableId.createForTesting<FunctionTag> "function-validate"
        let validateInputParameterId = StableId.createForTesting<ParameterTag> "parameter-validate-input"
        let validateInputNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-input"
        let runFunctionId = StableId.createForTesting<FunctionTag> "function-run"
        let runInputParameterId = StableId.createForTesting<ParameterTag> "parameter-run-input"
        let runPolicyParameterId = StableId.createForTesting<ParameterTag> "parameter-run-policy"
        let runInputNodeId = StableId.createForTesting<GraphNodeTag> "node-run-input"
        let runPolicyNodeId = StableId.createForTesting<GraphNodeTag> "node-run-policy"
        let runCallNodeId = StableId.createForTesting<GraphNodeTag> "node-run-call"

        let stringType: SemanticType = { Id = stringTypeId; DisplayName = "String" }
        let policyType: SemanticType = { Id = policyTypeId; DisplayName = "ValidationPolicy" }

        let validateInput: SemanticParameter =
            { Id = validateInputParameterId
              DisplayName = "input"
              TypeId = stringTypeId }

        let validateInputNode: SemanticNode =
            { Id = validateInputNodeId
              Kind = SemanticNodeKind.ParameterReference validateInputParameterId }

        let validateFunction: SemanticFunction =
            { Id = validateFunctionId
              DisplayName = "validate_user"
              Parameters = [ validateInput ]
              ResultTypeId = stringTypeId
              Nodes = [ validateInputNode ]
              ResultNodeId = validateInputNodeId }

        let runInput: SemanticParameter =
            { Id = runInputParameterId
              DisplayName = "input"
              TypeId = stringTypeId }

        let runPolicy: SemanticParameter =
            { Id = runPolicyParameterId
              DisplayName = "policy"
              TypeId = policyTypeId }

        let runInputNode: SemanticNode =
            { Id = runInputNodeId
              Kind = SemanticNodeKind.ParameterReference runInputParameterId }

        let runPolicyNode: SemanticNode =
            { Id = runPolicyNodeId
              Kind = SemanticNodeKind.ParameterReference runPolicyParameterId }

        let initialBinding: ArgumentBinding =
            { TargetParameterId = validateInputParameterId
              ValueNodeId = runInputNodeId }

        let runCallNode: SemanticNode =
            { Id = runCallNodeId
              Kind = SemanticNodeKind.DirectCall(validateFunctionId, [ initialBinding ]) }

        let runFunction: SemanticFunction =
            { Id = runFunctionId
              DisplayName = "run"
              Parameters = [ runInput; runPolicy ]
              ResultTypeId = stringTypeId
              Nodes = [ runInputNode; runPolicyNode; runCallNode ]
              ResultNodeId = runCallNodeId }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-base"
              ParentId = Some(StableId.createForTesting<SnapshotTag> "snapshot-ancestor")
              Types = [ stringType; policyType ]
              Functions = [ validateFunction; runFunction ]
              Obligations = [] }

        { Snapshot = snapshot
          StringTypeId = stringTypeId
          PolicyTypeId = policyTypeId
          ValidateFunctionId = validateFunctionId
          ValidateInputParameterId = validateInputParameterId
          ValidateInputNodeId = validateInputNodeId
          RunFunctionId = runFunctionId
          RunInputParameterId = runInputParameterId
          RunPolicyParameterId = runPolicyParameterId
          RunInputNodeId = runInputNodeId
          RunPolicyNodeId = runPolicyNodeId
          RunCallNodeId = runCallNodeId }
