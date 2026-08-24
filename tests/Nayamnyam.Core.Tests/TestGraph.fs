namespace Nayamnyam.Core.Tests

open Nayamnyam.Core

type GraphFixture =
    { Snapshot: Snapshot
      StringType: TypeRef
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
        let stringType = TypeRef.Primitive PrimitiveType.String
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

        let policyType: SemanticType =
            { Id = policyTypeId
              DisplayName = "ValidationPolicy"
              Kind = SemanticTypeKind.Product [] }

        let validateInput: SemanticParameter =
            { Id = validateInputParameterId
              DisplayName = "input"
              Type = stringType }

        let validateInputNode: SemanticNode =
            { Id = validateInputNodeId
              Kind = SemanticNodeKind.ParameterReference validateInputParameterId }

        let validateFunction: SemanticFunction =
            { Id = validateFunctionId
              DisplayName = "validate_user"
              Parameters = [ validateInput ]
              ResultType = stringType
              Body =
                { Nodes = [ validateInputNode ]
                  ResultNodeId = validateInputNodeId } }

        let runInput: SemanticParameter =
            { Id = runInputParameterId
              DisplayName = "input"
              Type = stringType }

        let runPolicy: SemanticParameter =
            { Id = runPolicyParameterId
              DisplayName = "policy"
              Type = TypeRef.Named policyTypeId }

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
              ResultType = stringType
              Body =
                { Nodes = [ runInputNode; runPolicyNode; runCallNode ]
                  ResultNodeId = runCallNodeId } }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-base"
              ParentId = Some(StableId.createForTesting<SnapshotTag> "snapshot-ancestor")
              Types = [ policyType ]
              Functions = [ validateFunction; runFunction ]
              Obligations = [] }

        { Snapshot = snapshot
          StringType = stringType
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
