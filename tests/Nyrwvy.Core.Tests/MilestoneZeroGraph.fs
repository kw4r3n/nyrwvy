namespace Nyrwvy.Core.Tests

open Nyrwvy.Core

type MilestoneZeroFixture =
    { Snapshot: Snapshot
      PolicyTypeId: TypeId
      ValidateFunctionId: FunctionId
      ValidateInputParameterId: ParameterId
      ReportFunctionId: FunctionId
      ReportResultParameterId: ParameterId
      ReportConsoleParameterId: ParameterId
      MainFunctionId: FunctionId
      MainValidateCallNodeId: GraphNodeId
      MainPolicyNodeId: GraphNodeId
      ConsoleEffectId: EffectId
      ConsoleCapabilityId: CapabilityId
      ConsoleWriteLineOperationId: CapabilityOperationId }

[<RequireQualifiedAccess>]
module MilestoneZeroGraph =
    let create (): MilestoneZeroFixture =
        let stringType = TypeRef.Primitive PrimitiveType.String
        let unitType = TypeRef.Primitive PrimitiveType.Unit
        let optionStringType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])

        let rawUserInputTypeId = StableId.createForTesting<TypeTag> "type-raw-user-input"
        let rawNameFieldId = StableId.createForTesting<FieldTag> "field-raw-name"
        let rawEmailFieldId = StableId.createForTesting<FieldTag> "field-raw-email"
        let rawDisplayNameFieldId = StableId.createForTesting<FieldTag> "field-raw-display-name"

        let validUserTypeId = StableId.createForTesting<TypeTag> "type-valid-user"
        let validNameFieldId = StableId.createForTesting<FieldTag> "field-valid-name"
        let validEmailFieldId = StableId.createForTesting<FieldTag> "field-valid-email"
        let validDisplayNameFieldId = StableId.createForTesting<FieldTag> "field-valid-display-name"

        let validationErrorTypeId = StableId.createForTesting<TypeTag> "type-validation-error"
        let emptyNameVariantId = StableId.createForTesting<VariantTag> "variant-empty-name"
        let invalidEmailVariantId = StableId.createForTesting<VariantTag> "variant-invalid-email"

        let policyTypeId = StableId.createForTesting<TypeTag> "type-validation-policy"

        let rawUserInputType: SemanticType =
            { Exported = true; TypeParameters = []; Id = rawUserInputTypeId
              DisplayName = "RawUserInput"
              Kind =
                SemanticTypeKind.Product
                    [ { Id = rawNameFieldId
                        DisplayName = "name"
                        Type = stringType }
                      { Id = rawEmailFieldId
                        DisplayName = "email"
                        Type = stringType }
                      { Id = rawDisplayNameFieldId
                        DisplayName = "display_name"
                        Type = optionStringType } ] }

        let validUserType: SemanticType =
            { Exported = true; TypeParameters = []; Id = validUserTypeId
              DisplayName = "ValidUser"
              Kind =
                SemanticTypeKind.Product
                    [ { Id = validNameFieldId
                        DisplayName = "name"
                        Type = stringType }
                      { Id = validEmailFieldId
                        DisplayName = "email"
                        Type = stringType }
                      { Id = validDisplayNameFieldId
                        DisplayName = "display_name"
                        Type = optionStringType } ] }

        let validationErrorType: SemanticType =
            { Exported = true; TypeParameters = []; Id = validationErrorTypeId
              DisplayName = "ValidationError"
              Kind =
                SemanticTypeKind.Union
                    [ { Id = emptyNameVariantId
                        DisplayName = "EmptyName"
                        Fields = [] }
                      { Id = invalidEmailVariantId
                        DisplayName = "InvalidEmail"
                        Fields = [] } ] }

        let policyType: SemanticType =
            { Exported = true; TypeParameters = []; Id = policyTypeId
              DisplayName = "ValidationPolicy"
              Kind = SemanticTypeKind.Product [] }

        let validUserRef = TypeRef.Named(validUserTypeId, [])
        let validationErrorRef = TypeRef.Named(validationErrorTypeId, [])
        let validationResultType =
            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ validUserRef; validationErrorRef ])

        let consoleEffectId = StableId.createForTesting<EffectTag> "effect-console-output"
        let consoleCapabilityId = StableId.createForTesting<CapabilityTag> "capability-console"
        let consoleWriteLineOperationId =
            StableId.createForTesting<CapabilityOperationTag> "capability-operation-write-line"
        let consoleTextParameterId =
            StableId.createForTesting<CapabilityParameterTag> "capability-parameter-console-text"

        let consoleEffect: SemanticEffect =
            { Exported = true; Id = consoleEffectId
              DisplayName = "ConsoleOutput" }

        let consoleTextParameter: SemanticCapabilityParameter =
            { Id = consoleTextParameterId
              DisplayName = "text"
              Type = stringType }

        let consoleWriteLine: SemanticCapabilityOperation =
            { Id = consoleWriteLineOperationId
              DisplayName = "write_line"
              Parameters = [ consoleTextParameter ]
              ResultType = unitType
              FailureType = None
              EffectId = consoleEffectId }

        let consoleCapability: SemanticCapability =
            { Exported = true; Id = consoleCapabilityId
              DisplayName = "Console"
              Operations = [ consoleWriteLine ] }

        let validateFunctionId = StableId.createForTesting<FunctionTag> "function-validate-user"
        let validateInputParameterId = StableId.createForTesting<ParameterTag> "parameter-validate-input"
        let validateInputNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-input"
        let validateNameNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-name"
        let validateNameEmptyNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-name-empty"
        let validateEmailNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-email"
        let atLiteralNodeId = StableId.createForTesting<GraphNodeTag> "node-at-literal"
        let emailContainsAtNodeId = StableId.createForTesting<GraphNodeTag> "node-email-contains-at"
        let validateDisplayNameNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-display-name"
        let emptyNameErrorNodeId = StableId.createForTesting<GraphNodeTag> "node-empty-name-error"
        let emptyNameResultNodeId = StableId.createForTesting<GraphNodeTag> "node-empty-name-result"
        let validUserNodeId = StableId.createForTesting<GraphNodeTag> "node-valid-user"
        let validResultNodeId = StableId.createForTesting<GraphNodeTag> "node-valid-result"
        let invalidEmailErrorNodeId = StableId.createForTesting<GraphNodeTag> "node-invalid-email-error"
        let invalidEmailResultNodeId = StableId.createForTesting<GraphNodeTag> "node-invalid-email-result"
        let emailConditionalNodeId = StableId.createForTesting<GraphNodeTag> "node-email-conditional"
        let validationConditionalNodeId = StableId.createForTesting<GraphNodeTag> "node-validation-conditional"

        let validateInputParameter: SemanticParameter =
            { Id = validateInputParameterId
              DisplayName = "input"
              Type = TypeRef.Named(rawUserInputTypeId, []) }

        let validateInputNode: SemanticNode =
            { Id = validateInputNodeId
              Kind = SemanticNodeKind.ParameterReference validateInputParameterId }

        let validateNameNode: SemanticNode =
            { Id = validateNameNodeId
              Kind = SemanticNodeKind.FieldAccess(validateInputNodeId, rawNameFieldId) }

        let validateNameEmptyNode: SemanticNode =
            { Id = validateNameEmptyNodeId
              Kind = SemanticNodeKind.StringIsEmpty validateNameNodeId }

        let validateEmailNode: SemanticNode =
            { Id = validateEmailNodeId
              Kind = SemanticNodeKind.FieldAccess(validateInputNodeId, rawEmailFieldId) }

        let atLiteralNode: SemanticNode =
            { Id = atLiteralNodeId
              Kind = SemanticNodeKind.StringLiteral "@" }

        let emailContainsAtNode: SemanticNode =
            { Id = emailContainsAtNodeId
              Kind = SemanticNodeKind.StringContains(validateEmailNodeId, atLiteralNodeId) }

        let validateDisplayNameNode: SemanticNode =
            { Id = validateDisplayNameNodeId
              Kind = SemanticNodeKind.FieldAccess(validateInputNodeId, rawDisplayNameFieldId) }

        let emptyNameErrorNode: SemanticNode =
            { Id = emptyNameErrorNodeId
              Kind = SemanticNodeKind.UnionConstruct(emptyNameVariantId, [], []) }

        let emptyNameResultNode: SemanticNode =
            { Id = emptyNameResultNodeId
              Kind = SemanticNodeKind.ResultErr(validUserRef, emptyNameErrorNodeId) }

        let emptyNameGraph: ValueGraph =
            { Nodes = [ emptyNameErrorNode; emptyNameResultNode ]
              ResultNodeId = emptyNameResultNodeId }

        let validUserNode: SemanticNode =
            { Id = validUserNodeId
              Kind =
                SemanticNodeKind.ProductConstruct(
                    validUserTypeId, [],
                    [ { TargetFieldId = validNameFieldId
                        ValueNodeId = validateNameNodeId }
                      { TargetFieldId = validEmailFieldId
                        ValueNodeId = validateEmailNodeId }
                      { TargetFieldId = validDisplayNameFieldId
                        ValueNodeId = validateDisplayNameNodeId } ]
                ) }

        let validResultNode: SemanticNode =
            { Id = validResultNodeId
              Kind = SemanticNodeKind.ResultOk(validationErrorRef, validUserNodeId) }

        let validGraph: ValueGraph =
            { Nodes = [ validUserNode; validResultNode ]
              ResultNodeId = validResultNodeId }

        let invalidEmailErrorNode: SemanticNode =
            { Id = invalidEmailErrorNodeId
              Kind = SemanticNodeKind.UnionConstruct(invalidEmailVariantId, [], []) }

        let invalidEmailResultNode: SemanticNode =
            { Id = invalidEmailResultNodeId
              Kind = SemanticNodeKind.ResultErr(validUserRef, invalidEmailErrorNodeId) }

        let invalidEmailGraph: ValueGraph =
            { Nodes = [ invalidEmailErrorNode; invalidEmailResultNode ]
              ResultNodeId = invalidEmailResultNodeId }

        let emailConditionalNode: SemanticNode =
            { Id = emailConditionalNodeId
              Kind =
                SemanticNodeKind.Conditional(
                    emailContainsAtNodeId,
                    validGraph,
                    invalidEmailGraph
                ) }

        let nonEmptyNameGraph: ValueGraph =
            { Nodes = [ emailConditionalNode ]
              ResultNodeId = emailConditionalNodeId }

        let validationConditionalNode: SemanticNode =
            { Id = validationConditionalNodeId
              Kind =
                SemanticNodeKind.Conditional(
                    validateNameEmptyNodeId,
                    emptyNameGraph,
                    nonEmptyNameGraph
                ) }

        let validateFunction: SemanticFunction =
            { Exported = true; Id = validateFunctionId
              TypeParameters = []
              DisplayName = "validate_user"
              Parameters = [ validateInputParameter ]
              ResultType = validationResultType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ validateInputNode
                      validateNameNode
                      validateNameEmptyNode
                      validateEmailNode
                      atLiteralNode
                      emailContainsAtNode
                      validateDisplayNameNode
                      validationConditionalNode ]
                  ResultNodeId = validationConditionalNodeId } }

        let reportFunctionId = StableId.createForTesting<FunctionTag> "function-report-validation"
        let reportResultParameterId = StableId.createForTesting<ParameterTag> "parameter-report-result"
        let reportConsoleParameterId = StableId.createForTesting<ParameterTag> "parameter-report-console"
        let reportResultNodeId = StableId.createForTesting<GraphNodeTag> "node-report-result"
        let reportConsoleNodeId = StableId.createForTesting<GraphNodeTag> "node-report-console"
        let okBindingId = StableId.createForTesting<PatternBindingTag> "binding-report-ok-user"
        let errBindingId = StableId.createForTesting<PatternBindingTag> "binding-report-error"
        let okMessageNodeId = StableId.createForTesting<GraphNodeTag> "node-report-ok-message"
        let okWriteNodeId = StableId.createForTesting<GraphNodeTag> "node-report-ok-write"
        let errorReferenceNodeId = StableId.createForTesting<GraphNodeTag> "node-report-error-reference"
        let emptyNameMessageNodeId = StableId.createForTesting<GraphNodeTag> "node-report-empty-name-message"
        let emptyNameWriteNodeId = StableId.createForTesting<GraphNodeTag> "node-report-empty-name-write"
        let invalidEmailMessageNodeId = StableId.createForTesting<GraphNodeTag> "node-report-invalid-email-message"
        let invalidEmailWriteNodeId = StableId.createForTesting<GraphNodeTag> "node-report-invalid-email-write"
        let errorMatchNodeId = StableId.createForTesting<GraphNodeTag> "node-report-error-match"
        let resultMatchNodeId = StableId.createForTesting<GraphNodeTag> "node-report-result-match"

        let reportResultParameter: SemanticParameter =
            { Id = reportResultParameterId
              DisplayName = "result"
              Type = validationResultType }

        let reportConsoleParameter: SemanticParameter =
            { Id = reportConsoleParameterId
              DisplayName = "console"
              Type = TypeRef.Capability consoleCapabilityId }

        let reportResultNode: SemanticNode =
            { Id = reportResultNodeId
              Kind = SemanticNodeKind.ParameterReference reportResultParameterId }

        let reportConsoleNode: SemanticNode =
            { Id = reportConsoleNodeId
              Kind = SemanticNodeKind.ParameterReference reportConsoleParameterId }

        let okBinding: PatternBinding =
            { Id = okBindingId
              DisplayName = Some "user"
              Type = validUserRef }

        let okMessageNode: SemanticNode =
            { Id = okMessageNodeId
              Kind = SemanticNodeKind.StringLiteral "valid user" }

        let okWriteNode: SemanticNode =
            { Id = okWriteNodeId
              Kind =
                SemanticNodeKind.CapabilityInvoke(
                    reportConsoleNodeId,
                    consoleWriteLineOperationId,
                    [ { TargetCapabilityParameterId = consoleTextParameterId
                        ValueNodeId = okMessageNodeId } ]
                ) }

        let okArm: ValueMatchArm =
            { ValueBinding = okBinding
              Body =
                { Nodes = [ okMessageNode; okWriteNode ]
                  ResultNodeId = okWriteNodeId } }

        let errBinding: PatternBinding =
            { Id = errBindingId
              DisplayName = Some "error"
              Type = validationErrorRef }

        let errorReferenceNode: SemanticNode =
            { Id = errorReferenceNodeId
              Kind = SemanticNodeKind.PatternReference errBindingId }

        let emptyNameMessageNode: SemanticNode =
            { Id = emptyNameMessageNodeId
              Kind = SemanticNodeKind.StringLiteral "empty name" }

        let emptyNameWriteNode: SemanticNode =
            { Id = emptyNameWriteNodeId
              Kind =
                SemanticNodeKind.CapabilityInvoke(
                    reportConsoleNodeId,
                    consoleWriteLineOperationId,
                    [ { TargetCapabilityParameterId = consoleTextParameterId
                        ValueNodeId = emptyNameMessageNodeId } ]
                ) }

        let emptyNameReportArm: UnionMatchArm =
            { VariantIds = [ emptyNameVariantId ]
              FieldBindings = []
              Body =
                { Nodes = [ emptyNameMessageNode; emptyNameWriteNode ]
                  ResultNodeId = emptyNameWriteNodeId } }

        let invalidEmailMessageNode: SemanticNode =
            { Id = invalidEmailMessageNodeId
              Kind = SemanticNodeKind.StringLiteral "invalid email" }

        let invalidEmailWriteNode: SemanticNode =
            { Id = invalidEmailWriteNodeId
              Kind =
                SemanticNodeKind.CapabilityInvoke(
                    reportConsoleNodeId,
                    consoleWriteLineOperationId,
                    [ { TargetCapabilityParameterId = consoleTextParameterId
                        ValueNodeId = invalidEmailMessageNodeId } ]
                ) }

        let invalidEmailReportArm: UnionMatchArm =
            { VariantIds = [ invalidEmailVariantId ]
              FieldBindings = []
              Body =
                { Nodes = [ invalidEmailMessageNode; invalidEmailWriteNode ]
                  ResultNodeId = invalidEmailWriteNodeId } }

        let errorMatchNode: SemanticNode =
            { Id = errorMatchNodeId
              Kind =
                SemanticNodeKind.MatchUnion(
                    errorReferenceNodeId,
                    [ emptyNameReportArm; invalidEmailReportArm ]
                ) }

        let errArm: ErrorMatchArm =
            { ErrorBinding = errBinding
              Body =
                { Nodes = [ errorReferenceNode; errorMatchNode ]
                  ResultNodeId = errorMatchNodeId } }

        let resultMatchNode: SemanticNode =
            { Id = resultMatchNodeId
              Kind = SemanticNodeKind.MatchResult(reportResultNodeId, okArm, errArm) }

        let reportFunction: SemanticFunction =
            { Exported = true; Id = reportFunctionId
              TypeParameters = []
              DisplayName = "report_validation"
              Parameters = [ reportResultParameter; reportConsoleParameter ]
              ResultType = unitType
              RequiredEffects = [ consoleEffectId ]
              Body =
                { Nodes = [ reportResultNode; reportConsoleNode; resultMatchNode ]
                  ResultNodeId = resultMatchNodeId } }

        let mainFunctionId = StableId.createForTesting<FunctionTag> "function-main"
        let mainInputParameterId = StableId.createForTesting<ParameterTag> "parameter-main-input"
        let mainPolicyParameterId = StableId.createForTesting<ParameterTag> "parameter-main-policy"
        let mainConsoleParameterId = StableId.createForTesting<ParameterTag> "parameter-main-console"
        let mainInputNodeId = StableId.createForTesting<GraphNodeTag> "node-main-input"
        let mainPolicyNodeId = StableId.createForTesting<GraphNodeTag> "node-main-policy"
        let mainConsoleNodeId = StableId.createForTesting<GraphNodeTag> "node-main-console"
        let mainValidateCallNodeId = StableId.createForTesting<GraphNodeTag> "node-main-validate-call"
        let mainReportCallNodeId = StableId.createForTesting<GraphNodeTag> "node-main-report-call"

        let mainInputParameter: SemanticParameter =
            { Id = mainInputParameterId
              DisplayName = "input"
              Type = TypeRef.Named(rawUserInputTypeId, []) }

        let mainPolicyParameter: SemanticParameter =
            { Id = mainPolicyParameterId
              DisplayName = "policy"
              Type = TypeRef.Named(policyTypeId, []) }

        let mainConsoleParameter: SemanticParameter =
            { Id = mainConsoleParameterId
              DisplayName = "console"
              Type = TypeRef.Capability consoleCapabilityId }

        let mainInputNode: SemanticNode =
            { Id = mainInputNodeId
              Kind = SemanticNodeKind.ParameterReference mainInputParameterId }

        let mainPolicyNode: SemanticNode =
            { Id = mainPolicyNodeId
              Kind = SemanticNodeKind.ParameterReference mainPolicyParameterId }

        let mainConsoleNode: SemanticNode =
            { Id = mainConsoleNodeId
              Kind = SemanticNodeKind.ParameterReference mainConsoleParameterId }

        let mainValidateCallNode: SemanticNode =
            { Id = mainValidateCallNodeId
              Kind =
                SemanticNodeKind.DirectCall(
                    validateFunctionId,
                    [ { TargetParameterId = validateInputParameterId
                        ValueNodeId = mainInputNodeId } ]
                ) }

        let mainReportCallNode: SemanticNode =
            { Id = mainReportCallNodeId
              Kind =
                SemanticNodeKind.DirectCall(
                    reportFunctionId,
                    [ { TargetParameterId = reportResultParameterId
                        ValueNodeId = mainValidateCallNodeId }
                      { TargetParameterId = reportConsoleParameterId
                        ValueNodeId = mainConsoleNodeId } ]
                ) }

        let mainFunction: SemanticFunction =
            { Exported = true; Id = mainFunctionId
              TypeParameters = []
              DisplayName = "main"
              Parameters = [ mainInputParameter; mainPolicyParameter; mainConsoleParameter ]
              ResultType = unitType
              RequiredEffects = [ consoleEffectId ]
              Body =
                { Nodes =
                    [ mainInputNode
                      mainPolicyNode
                      mainConsoleNode
                      mainValidateCallNode
                      mainReportCallNode ]
                  ResultNodeId = mainReportCallNodeId } }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-m0-initial"
              ParentId = None
              Types = [ rawUserInputType; validUserType; validationErrorType; policyType ]
              Effects = [ consoleEffect ]
              Capabilities = [ consoleCapability ]
              Functions = [ validateFunction; reportFunction; mainFunction ]
              Obligations = [] }

        { Snapshot = snapshot
          PolicyTypeId = policyTypeId
          ValidateFunctionId = validateFunctionId
          ValidateInputParameterId = validateInputParameterId
          ReportFunctionId = reportFunctionId
          ReportResultParameterId = reportResultParameterId
          ReportConsoleParameterId = reportConsoleParameterId
          MainFunctionId = mainFunctionId
          MainValidateCallNodeId = mainValidateCallNodeId
          MainPolicyNodeId = mainPolicyNodeId
          ConsoleEffectId = consoleEffectId
          ConsoleCapabilityId = consoleCapabilityId
          ConsoleWriteLineOperationId = consoleWriteLineOperationId }
