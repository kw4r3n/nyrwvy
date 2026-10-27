namespace Nyrwvy.Core.Tests

open Nyrwvy.Core

type MilestoneOneFixture =
    { Snapshot: Snapshot
      MilestoneZero: MilestoneZeroFixture
      RawUserInputTypeId: TypeId
      RawNameFieldId: FieldId
      RawEmailFieldId: FieldId
      RawDisplayNameFieldId: FieldId
      ValidateFunctionId: FunctionId
      ValidateUsersFunctionId: FunctionId
      ValidateUsersMapNodeId: GraphNodeId
      CountResultsFunctionId: FunctionId
      CountInvalidResultsFunctionId: FunctionId
      ReportResultsFunctionId: FunctionId
      MainFunctionId: FunctionId
      MainReportResultsCallNodeId: GraphNodeId
      MainUnitNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module MilestoneOneGraph =
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-m1-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-m1-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-m1-" + name)
    let private bindingId name = StableId.createForTesting<PatternBindingTag> ("binding-m1-" + name)

    let private findType displayName (snapshot: Snapshot) =
        snapshot.Types
        |> List.find (fun typeEntity -> typeEntity.DisplayName = displayName)

    let private findField displayName (typeEntity: SemanticType) =
        match typeEntity.Kind with
        | SemanticTypeKind.Product fields ->
            fields |> List.find (fun field -> field.DisplayName = displayName)
        | SemanticTypeKind.Union _ ->
            failwithf "Expected product type %s" typeEntity.DisplayName

    let create (): MilestoneOneFixture =
        let m0 = MilestoneZeroGraph.create ()
        let rawUserInput = findType "RawUserInput" m0.Snapshot
        let validUser = findType "ValidUser" m0.Snapshot
        let validationError = findType "ValidationError" m0.Snapshot
        let rawNameField = findField "name" rawUserInput
        let rawEmailField = findField "email" rawUserInput
        let rawDisplayNameField = findField "display_name" rawUserInput

        let rawUserRef = TypeRef.Named(rawUserInput.Id, [])
        let validUserRef = TypeRef.Named(validUser.Id, [])
        let validationErrorRef = TypeRef.Named(validationError.Id, [])
        let validationResultType =
            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ validUserRef; validationErrorRef ])
        let rawUserListType =
            TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ rawUserRef ])
        let validationResultListType =
            TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ validationResultType ])
        let boolType = TypeRef.Primitive PrimitiveType.Bool
        let int64Type = TypeRef.Primitive PrimitiveType.Int64
        let unitType = TypeRef.Primitive PrimitiveType.Unit
        let consoleType = TypeRef.Capability m0.ConsoleCapabilityId

        let consoleCapability =
            m0.Snapshot.Capabilities
            |> List.find (fun capability -> capability.Id = m0.ConsoleCapabilityId)
        let consoleWriteLine =
            consoleCapability.Operations
            |> List.find (fun operation -> operation.Id = m0.ConsoleWriteLineOperationId)
        let consoleTextParameter = List.exactlyOne consoleWriteLine.Parameters

        let isErrorParameter: SemanticParameter =
            { Id = parameterId "is-error-result"
              DisplayName = "result"
              Type = validationResultType }
        let isErrorParameterNodeId = nodeId "is-error-result"
        let isErrorParameterNode =
            { Id = isErrorParameterNodeId
              Kind = SemanticNodeKind.ParameterReference isErrorParameter.Id }
        let isErrorOkBinding: PatternBinding =
            { Id = bindingId "is-error-ok"
              DisplayName = Some "user"
              Type = validUserRef }
        let isErrorErrBinding: PatternBinding =
            { Id = bindingId "is-error-err"
              DisplayName = Some "error"
              Type = validationErrorRef }
        let isErrorFalseNodeId = nodeId "is-error-false"
        let isErrorTrueNodeId = nodeId "is-error-true"
        let isErrorMatchNodeId = nodeId "is-error-match"
        let isErrorFalseNode =
            { Id = isErrorFalseNodeId
              Kind = SemanticNodeKind.BoolLiteral false }
        let isErrorTrueNode =
            { Id = isErrorTrueNodeId
              Kind = SemanticNodeKind.BoolLiteral true }
        let isErrorMatchNode =
            { Id = isErrorMatchNodeId
              Kind =
                SemanticNodeKind.MatchResult(
                    isErrorParameterNodeId,
                    { ValueBinding = isErrorOkBinding
                      Body =
                        { Nodes = [ isErrorFalseNode ]
                          ResultNodeId = isErrorFalseNodeId } },
                    { ErrorBinding = isErrorErrBinding
                      Body =
                        { Nodes = [ isErrorTrueNode ]
                          ResultNodeId = isErrorTrueNodeId } }
                ) }
        let isValidationErrorFunction: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "is-validation-error"
              TypeParameters = []
              DisplayName = "is_validation_error"
              Parameters = [ isErrorParameter ]
              ResultType = boolType
              RequiredEffects = []
              Body =
                { Nodes = [ isErrorParameterNode; isErrorMatchNode ]
                  ResultNodeId = isErrorMatchNodeId } }

        let validateUsersInputParameter: SemanticParameter =
            { Id = parameterId "validate-users-inputs"
              DisplayName = "inputs"
              Type = rawUserListType }
        let validateUsersInputNodeId = nodeId "validate-users-inputs"
        let validateUsersMapNodeId = nodeId "validate-users-map"
        let validateUsersFunction: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "validate-users"
              TypeParameters = []
              DisplayName = "validate_users"
              Parameters = [ validateUsersInputParameter ]
              ResultType = validationResultListType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = validateUsersInputNodeId
                        Kind = SemanticNodeKind.ParameterReference validateUsersInputParameter.Id }
                      { Id = validateUsersMapNodeId
                        Kind = SemanticNodeKind.ListMap(validateUsersInputNodeId, m0.ValidateFunctionId, []) } ]
                  ResultNodeId = validateUsersMapNodeId } }

        let countResultsParameter: SemanticParameter =
            { Id = parameterId "count-results-values"
              DisplayName = "results"
              Type = validationResultListType }
        let countResultsParameterNodeId = nodeId "count-results-values"
        let countResultsLengthNodeId = nodeId "count-results-length"
        let countResultsFunction: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "count-results"
              TypeParameters = []
              DisplayName = "count_results"
              Parameters = [ countResultsParameter ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = countResultsParameterNodeId
                        Kind = SemanticNodeKind.ParameterReference countResultsParameter.Id }
                      { Id = countResultsLengthNodeId
                        Kind = SemanticNodeKind.ListLength countResultsParameterNodeId } ]
                  ResultNodeId = countResultsLengthNodeId } }

        let countInvalidParameter: SemanticParameter =
            { Id = parameterId "count-invalid-results-values"
              DisplayName = "results"
              Type = validationResultListType }
        let countInvalidParameterNodeId = nodeId "count-invalid-results-values"
        let countInvalidFilterNodeId = nodeId "count-invalid-results-filter"
        let countInvalidLengthNodeId = nodeId "count-invalid-results-length"
        let countInvalidResultsFunction: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "count-invalid-results"
              TypeParameters = []
              DisplayName = "count_invalid_results"
              Parameters = [ countInvalidParameter ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = countInvalidParameterNodeId
                        Kind = SemanticNodeKind.ParameterReference countInvalidParameter.Id }
                      { Id = countInvalidFilterNodeId
                        Kind =
                            SemanticNodeKind.ListFilter(
                                countInvalidParameterNodeId,
                                isValidationErrorFunction.Id,
                                []
                            ) }
                      { Id = countInvalidLengthNodeId
                        Kind = SemanticNodeKind.ListLength countInvalidFilterNodeId } ]
                  ResultNodeId = countInvalidLengthNodeId } }

        let reportResultsParameter: SemanticParameter =
            { Id = parameterId "report-results-values"
              DisplayName = "results"
              Type = validationResultListType }
        let reportConsoleParameter: SemanticParameter =
            { Id = parameterId "report-results-console"
              DisplayName = "console"
              Type = consoleType }
        let reportResultsNodeId = nodeId "report-results-values"
        let reportConsoleNodeId = nodeId "report-results-console"
        let reportCountNodeId = nodeId "report-results-count"
        let reportInvalidCountNodeId = nodeId "report-results-invalid-count"
        let reportZeroNodeId = nodeId "report-results-zero"
        let reportHasUsersNodeId = nodeId "report-results-has-users"
        let reportHasErrorsNodeId = nodeId "report-results-has-errors"
        let reportAnyErrorsNodeId = nodeId "report-results-any-errors"
        let reportAllValidNodeId = nodeId "report-results-all-valid"
        let reportNoUsersMessageNodeId = nodeId "report-results-no-users-message"
        let reportNoUsersWriteNodeId = nodeId "report-results-no-users-write"
        let reportErrorMessageNodeId = nodeId "report-results-error-message"
        let reportErrorWriteNodeId = nodeId "report-results-error-write"
        let reportValidMessageNodeId = nodeId "report-results-valid-message"
        let reportValidWriteNodeId = nodeId "report-results-valid-write"

        let reportNoUsersGraph: ValueGraph =
            { Nodes =
                [ { Id = reportNoUsersMessageNodeId
                    Kind = SemanticNodeKind.StringLiteral "no users" }
                  { Id = reportNoUsersWriteNodeId
                    Kind =
                        SemanticNodeKind.CapabilityInvoke(
                            reportConsoleNodeId,
                            m0.ConsoleWriteLineOperationId,
                            [ { TargetCapabilityParameterId = consoleTextParameter.Id
                                ValueNodeId = reportNoUsersMessageNodeId } ]
                        ) } ]
              ResultNodeId = reportNoUsersWriteNodeId }

        let reportErrorGraph: ValueGraph =
            { Nodes =
                [ { Id = reportErrorMessageNodeId
                    Kind = SemanticNodeKind.StringLiteral "validation errors present" }
                  { Id = reportErrorWriteNodeId
                    Kind =
                        SemanticNodeKind.CapabilityInvoke(
                            reportConsoleNodeId,
                            m0.ConsoleWriteLineOperationId,
                            [ { TargetCapabilityParameterId = consoleTextParameter.Id
                                ValueNodeId = reportErrorMessageNodeId } ]
                        ) } ]
              ResultNodeId = reportErrorWriteNodeId }

        let reportValidGraph: ValueGraph =
            { Nodes =
                [ { Id = reportValidMessageNodeId
                    Kind = SemanticNodeKind.StringLiteral "all users valid" }
                  { Id = reportValidWriteNodeId
                    Kind =
                        SemanticNodeKind.CapabilityInvoke(
                            reportConsoleNodeId,
                            m0.ConsoleWriteLineOperationId,
                            [ { TargetCapabilityParameterId = consoleTextParameter.Id
                                ValueNodeId = reportValidMessageNodeId } ]
                        ) } ]
              ResultNodeId = reportValidWriteNodeId }

        let reportUsersGraph: ValueGraph =
            { Nodes =
                [ { Id = reportHasErrorsNodeId
                    Kind = SemanticNodeKind.Int64GreaterThan(reportInvalidCountNodeId, reportZeroNodeId) }
                  { Id = reportAnyErrorsNodeId
                    Kind =
                        SemanticNodeKind.Conditional(
                            reportHasErrorsNodeId,
                            reportErrorGraph,
                            reportValidGraph
                        ) } ]
              ResultNodeId = reportAnyErrorsNodeId }

        let reportResultsFunction: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "report-results"
              TypeParameters = []
              DisplayName = "report_results"
              Parameters = [ reportResultsParameter; reportConsoleParameter ]
              ResultType = unitType
              RequiredEffects = [ m0.ConsoleEffectId ]
              Body =
                { Nodes =
                    [ { Id = reportResultsNodeId
                        Kind = SemanticNodeKind.ParameterReference reportResultsParameter.Id }
                      { Id = reportConsoleNodeId
                        Kind = SemanticNodeKind.ParameterReference reportConsoleParameter.Id }
                      { Id = reportCountNodeId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                countResultsFunction.Id,
                                [ { TargetParameterId = countResultsParameter.Id
                                    ValueNodeId = reportResultsNodeId } ]
                            ) }
                      { Id = reportInvalidCountNodeId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                countInvalidResultsFunction.Id,
                                [ { TargetParameterId = countInvalidParameter.Id
                                    ValueNodeId = reportResultsNodeId } ]
                            ) }
                      { Id = reportZeroNodeId
                        Kind = SemanticNodeKind.Int64Literal 0L }
                      { Id = reportHasUsersNodeId
                        Kind = SemanticNodeKind.Int64GreaterThan(reportCountNodeId, reportZeroNodeId) }
                      { Id = reportAllValidNodeId
                        Kind =
                            SemanticNodeKind.Conditional(
                                reportHasUsersNodeId,
                                reportUsersGraph,
                                reportNoUsersGraph
                            ) } ]
                  ResultNodeId = reportAllValidNodeId } }

        let mainInputsParameter: SemanticParameter =
            { Id = parameterId "main-inputs"
              DisplayName = "inputs"
              Type = rawUserListType }
        let mainConsoleParameter: SemanticParameter =
            { Id = parameterId "main-console"
              DisplayName = "console"
              Type = consoleType }
        let mainInputsNodeId = nodeId "main-inputs"
        let mainConsoleNodeId = nodeId "main-console"
        let mainUnitNodeId = nodeId "main-unit-context"
        let mainValidateUsersNodeId = nodeId "main-validate-users"
        let mainReportResultsNodeId = nodeId "main-report-results"
        let mainFunction: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = functionId "main"
              TypeParameters = []
              DisplayName = "m1_main"
              Parameters = [ mainInputsParameter; mainConsoleParameter ]
              ResultType = unitType
              RequiredEffects = [ m0.ConsoleEffectId ]
              Body =
                { Nodes =
                    [ { Id = mainInputsNodeId
                        Kind = SemanticNodeKind.ParameterReference mainInputsParameter.Id }
                      { Id = mainConsoleNodeId
                        Kind = SemanticNodeKind.ParameterReference mainConsoleParameter.Id }
                      { Id = mainUnitNodeId
                        Kind = SemanticNodeKind.UnitLiteral }
                      { Id = mainValidateUsersNodeId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                validateUsersFunction.Id,
                                [ { TargetParameterId = validateUsersInputParameter.Id
                                    ValueNodeId = mainInputsNodeId } ]
                            ) }
                      { Id = mainReportResultsNodeId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                reportResultsFunction.Id,
                                [ { TargetParameterId = reportResultsParameter.Id
                                    ValueNodeId = mainValidateUsersNodeId }
                                  { TargetParameterId = reportConsoleParameter.Id
                                    ValueNodeId = mainConsoleNodeId } ]
                            ) } ]
                  ResultNodeId = mainReportResultsNodeId } }

        let snapshot =
            { m0.Snapshot with
                Id = StableId.createForTesting<SnapshotTag> "snapshot-m1-expanded-program"
                ParentId = Some m0.Snapshot.Id
                Functions =
                    m0.Snapshot.Functions
                    @ [ isValidationErrorFunction
                        validateUsersFunction
                        countResultsFunction
                        countInvalidResultsFunction
                        reportResultsFunction
                        mainFunction ] }

        { Snapshot = snapshot
          MilestoneZero = m0
          RawUserInputTypeId = rawUserInput.Id
          RawNameFieldId = rawNameField.Id
          RawEmailFieldId = rawEmailField.Id
          RawDisplayNameFieldId = rawDisplayNameField.Id
          ValidateFunctionId = m0.ValidateFunctionId
          ValidateUsersFunctionId = validateUsersFunction.Id
          ValidateUsersMapNodeId = validateUsersMapNodeId
          CountResultsFunctionId = countResultsFunction.Id
          CountInvalidResultsFunctionId = countInvalidResultsFunction.Id
          ReportResultsFunctionId = reportResultsFunction.Id
          MainFunctionId = mainFunction.Id
          MainReportResultsCallNodeId = mainReportResultsNodeId
          MainUnitNodeId = mainUnitNodeId }
