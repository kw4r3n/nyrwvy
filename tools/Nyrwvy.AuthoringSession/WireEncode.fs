namespace Nyrwvy.Tools.AuthoringSession

open Nyrwvy.Core

module internal WireEncode =
    open WireJson
    open WireEncodeCommon

    let private semanticDiff (value: SemanticDiff) =
        objectValue
            [ "renames", value.Renames |> List.map (sprintf "%A" >> box) |> arrayValue
              "parametersAdded",
                value.ParametersAdded
                |> List.map (fun item ->
                    objectValue
                        [ "ownerFunctionId", box (StableId.value item.OwnerFunctionId)
                          "parameterId", box (StableId.value item.AddedParameterId)
                          "displayName", box item.AddedDisplayName
                          "type", typeRef item.AddedType ])
                |> arrayValue
              "productFieldsAdded",
                value.ProductFieldsAdded
                |> List.map (fun item ->
                    objectValue
                        [ "ownerTypeId", box (StableId.value item.OwnerTypeId)
                          "fieldId", box (StableId.value item.AddedFieldId)
                          "displayName", box item.AddedDisplayName
                          "type", typeRef item.AddedType ])
                |> arrayValue
              "productFieldsBound",
                value.ProductFieldsBound
                |> List.map (fun item ->
                    objectValue
                        [ "ownerFunctionId", box (StableId.value item.OwnerFunctionId)
                          "constructNodeId", box (StableId.value item.ConstructNodeId)
                          "fieldId", box (StableId.value item.TargetFieldId)
                          "valueNodeId", box (StableId.value item.ValueNodeId) ])
                |> arrayValue
              "productFieldsRemoved",
                value.ProductFieldsRemoved
                |> List.map (fun item ->
                    objectValue
                        [ "ownerTypeId", box (StableId.value item.OwnerTypeId)
                          "fieldId", box (StableId.value item.RemovedFieldId)
                          "displayName", box item.DisplayName
                          "affectedConstructNodeIds", item.AffectedConstructNodeIds |> List.map (StableId.value >> box) |> arrayValue ])
                |> arrayValue
              "effectsRemoved",
                value.EffectsRemoved
                |> List.map (fun item ->
                    objectValue
                        [ "effectId", box (StableId.value item.EffectId)
                          "displayName", box item.DisplayName ])
                |> arrayValue
              "capabilitiesRemoved",
                value.CapabilitiesRemoved
                |> List.map (fun item ->
                    objectValue
                        [ "capabilityId", box (StableId.value item.CapabilityId)
                          "displayName", box item.DisplayName
                          "removedOperationIds", item.RemovedOperationIds |> List.map (StableId.value >> box) |> arrayValue
                          "removedParameterIds", item.RemovedParameterIds |> List.map (StableId.value >> box) |> arrayValue ])
                |> arrayValue
              "capabilityOperationsRemoved",
                value.CapabilityOperationsRemoved
                |> List.map (fun item ->
                    objectValue
                        [ "ownerCapabilityId", box (StableId.value item.OwnerCapabilityId)
                          "operationId", box (StableId.value item.OperationId)
                          "displayName", box item.DisplayName
                          "removedParameterIds", item.RemovedParameterIds |> List.map (StableId.value >> box) |> arrayValue ])
                |> arrayValue
              "typesRemoved",
                value.TypesRemoved
                |> List.map (fun item ->
                    objectValue
                        [ "typeId", box (StableId.value item.TypeId)
                          "displayName", box item.DisplayName
                          "removedVariantIds", item.RemovedVariantIds |> List.map (StableId.value >> box) |> arrayValue
                          "removedFieldIds", item.RemovedFieldIds |> List.map (StableId.value >> box) |> arrayValue ])
                |> arrayValue
              "functionsRemoved",
                value.FunctionsRemoved
                |> List.map (fun item ->
                    objectValue
                        [ "functionId", box (StableId.value item.FunctionId)
                          "displayName", box item.DisplayName
                          "removedNodeIds", item.RemovedNodeIds |> List.map (StableId.value >> box) |> arrayValue ])
                |> arrayValue
              "argumentsAttached",
                value.ArgumentsAttached
                |> List.map (fun item ->
                    objectValue
                        [ "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.TargetParameterId)
                          "valueNodeId", box (StableId.value item.ValueNodeId) ])
                |> arrayValue
              "obligationsCreated",
                value.ObligationsCreated
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", box (StableId.value item.ObligationId)
                          "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.ParameterId) ])
                |> arrayValue
              "obligationsResolved",
                value.ObligationsResolved
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", box (StableId.value item.ObligationId)
                          "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.ParameterId)
                          "valueNodeId", box (StableId.value item.ValueNodeId) ])
                |> arrayValue
              "requiredEffectsChanged",
                value.FunctionRequiredEffectsChanged |> List.map (sprintf "%A" >> box) |> arrayValue ]

    let private bodyDiff (value: FunctionBodyReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "oldResultNodeId", box (StableId.value value.OldResultNodeId)
              "newResultNodeId", box (StableId.value value.NewResultNodeId)
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue ]

    let private compactBodyDiff (value: FunctionBodyReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "oldResultNodeId", box (StableId.value value.OldResultNodeId)
              "newResultNodeId", box (StableId.value value.NewResultNodeId)
              "removedNodeIds",
                value.RemovedNodes
                |> List.map (fun item -> box (StableId.value item.NodeId))
                |> arrayValue
              "addedNodeIds",
                value.AddedNodes
                |> List.map (fun item -> box (StableId.value item.NodeId))
                |> arrayValue ]

    let private targetDiff (value: AgentExpressionStructuralWorkflow.Targeted.ReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "targetNodeId", box (StableId.value value.TargetNodeId)
              "previousTarget", nodeSummary value.PreviousTarget
              "proposedTarget", value.ProposedTarget |> Option.map nodeSummary |> Option.defaultValue null
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue
              "addedPatternBindingIds",
                value.AddedPatternBindings
                |> List.map (fun binding -> box (StableId.value binding.Id))
                |> arrayValue
              "retainedSharedDependencies", value.RetainedSharedDependencies |> List.map nodeSummary |> arrayValue
              "affectedUses", value.AffectedUses |> List.map (sprintf "%A" >> box) |> arrayValue ]

    let private parameterTypeMigrationDiff (value: AgentParameterTypeMigrationDiff) =
        objectValue
            [ "parameterChange",
                objectValue
                    [ "functionId", box (StableId.value value.ParameterChange.FunctionId)
                      "parameterId", box (StableId.value value.ParameterChange.ParameterId)
                      "oldType", typeRef value.ParameterChange.OldType
                      "newType", typeRef value.ParameterChange.NewType ]
              "affectedCallSites",
                value.AffectedCallSites
                |> List.map (fun site ->
                    objectValue
                        [ "callerFunctionId", box (StableId.value site.CallerFunctionId)
                          "callNodeId", box (StableId.value site.CallNodeId)
                          "calleeFunctionId", box (StableId.value site.CalleeFunctionId) ])
                |> arrayValue
              "bodyRewrites", value.BodyRewrites |> List.map bodyDiff |> arrayValue ]

    let private resultTypeMigrationDiff (value: AgentResultTypeMigrationDiff) =
        objectValue
            [ "resultChange",
                objectValue
                    [ "functionId", box (StableId.value value.ResultChange.FunctionId)
                      "oldType", typeRef value.ResultChange.OldType
                      "newType", typeRef value.ResultChange.NewType ]
              "affectedCallSites",
                value.AffectedCallSites
                |> List.map (fun site ->
                    objectValue
                        [ "callerFunctionId", box (StableId.value site.CallerFunctionId)
                          "callNodeId", box (StableId.value site.CallNodeId)
                          "calleeFunctionId", box (StableId.value site.CalleeFunctionId) ])
                |> arrayValue
              "bodyRewrites", value.BodyRewrites |> List.map bodyDiff |> arrayValue ]

    let private productFieldTypeMigrationDiff (value: AgentProductFieldTypeMigrationDiff) =
        objectValue
            [ "fieldChange",
                objectValue
                    [ "typeId", box (StableId.value value.FieldChange.TypeId)
                      "fieldId", box (StableId.value value.FieldChange.FieldId)
                      "oldType", typeRef value.FieldChange.OldType
                      "newType", typeRef value.FieldChange.NewType ]
              "bodyRewrites", value.BodyRewrites |> List.map bodyDiff |> arrayValue ]

    let private capabilityEntity (value: SemanticCapability) =
        let operations =
            value.Operations
            |> List.map (fun operation ->
                let parameters =
                    operation.Parameters
                    |> List.map (fun parameter ->
                        objectValue
                            [ "id", box (StableId.value parameter.Id)
                              "displayName", box parameter.DisplayName
                              "type", typeRef parameter.Type ])
                let failureType =
                    match operation.FailureType with
                    | Some item -> typeRef item
                    | None -> null
                objectValue
                    [ "id", box (StableId.value operation.Id)
                      "displayName", box operation.DisplayName
                      "parameters", arrayValue parameters
                      "resultType", typeRef operation.ResultType
                      "failureType", failureType
                      "effectId", box (StableId.value operation.EffectId) ])
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "operations", arrayValue operations ]

    let private functionEffects (items: FunctionEffectSummary list) =
        items |> List.map (fun item ->
            objectValue
                [ "functionId", box (StableId.value item.FunctionId)
                  "declaredEffectIds", item.DeclaredEffectIds |> List.map (StableId.value >> box) |> arrayValue
                  "bodyEffectRequirements", WireEncodeCommon.bodyEffectRequirements item.Body ]) |> arrayValue

    let proposalWith (detail: string) functionEntity focus (value: AuthoringProposal) =
        // A full review of a migration or union-variant evolution adds each rewritten function's
        // proposed body; the compact review keeps only the diff's node summaries.
        let withRewritten fields =
            if detail = "full" then
                objectValue (fields @ [ "rewrittenFunctions", value.RewrittenFunctions |> List.map functionEntity |> arrayValue ])
            else objectValue fields
        let review =
            match value.Review with
            | AuthoringProposalReview.Operations item ->
                objectValue
                    [ "kind", box "operations"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", semanticDiff item.ResultingChange.Diff
                      "affectedFunctionIds", item.ResultingChange.AffectedFunctionIds |> List.map (StableId.value >> box) |> arrayValue
                      "affectedTypeIds", item.ResultingChange.AffectedTypeIds |> List.map (StableId.value >> box) |> arrayValue
                      "functionFocus", item.ResultingChange.FunctionFocus |> List.map focus |> arrayValue ]
            | AuthoringProposalReview.ReplaceBody item ->
                objectValue
                    [ "kind", box "replaceBody"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", compactBodyDiff item.ResultingChange
                      "proposedFunction", functionEntity item.ProposedFocus.Function
                      "bodyEffectRequirements", WireEncodeCommon.bodyEffectRequirements item.ProposedFocus.BodyEffectRequirements
                      "aborts", WireEncodeCommon.aborts item.ProposedFocus.Aborts ]
            | AuthoringProposalReview.ReplaceTarget item ->
                objectValue
                    [ "kind", box "replaceTarget"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", targetDiff item.ResultingChange
                      "baseFocus", focus item.BaseFocus
                      "proposedFocus", focus item.ProposedFocus ]
            | AuthoringProposalReview.CreateFunction item ->
                objectValue
                    [ "kind", box "createFunction"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdFunction", functionEntity item.CreatedFunction
                      "bodyEffectRequirements", WireEncodeCommon.bodyEffectRequirements item.ProposedFocus.BodyEffectRequirements
                      "aborts", WireEncodeCommon.aborts item.ProposedFocus.Aborts ]
            | AuthoringProposalReview.ParameterTypeMigration item ->
                withRewritten
                    [ "kind", box "parameterTypeMigration"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", parameterTypeMigrationDiff item.ResultingChange
                      "functionEffects", functionEffects item.FunctionEffects ]
            | AuthoringProposalReview.ResultTypeMigration item ->
                withRewritten
                    [ "kind", box "resultTypeMigration"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", resultTypeMigrationDiff item.ResultingChange
                      "functionEffects", functionEffects item.FunctionEffects ]
            | AuthoringProposalReview.ProductFieldTypeMigration item ->
                withRewritten
                    [ "kind", box "productFieldTypeMigration"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", productFieldTypeMigrationDiff item.ResultingChange
                      "functionEffects", functionEffects item.FunctionEffects ]
            | AuthoringProposalReview.CreateType item ->
                objectValue
                    [ "kind", box "createType"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdType", semanticType item.CreatedType ]
            | AuthoringProposalReview.CreateTypeGroup item ->
                objectValue
                    [ "kind", box "createTypes"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdTypes", item.CreatedTypes |> List.map semanticType |> arrayValue ]
            | AuthoringProposalReview.FieldlessUnionVariantEvolution item ->
                withRewritten
                    [ "kind", box "fieldlessUnionVariantEvolution"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "updatedType", semanticType item.UpdatedType
                      "addedVariantId", box (StableId.value item.AddedVariant.Id)
                      "addedVariantDisplayName", box item.AddedVariant.DisplayName
                      "repairedFunctionIds",
                        item.RepairedFunctionIds
                        |> List.map (StableId.value >> box)
                        |> arrayValue
                      "repairedMatchCount", box item.RepairedMatchCount ]
            | AuthoringProposalReview.CreateEffect item ->
                objectValue
                    [ "kind", box "createEffect"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdEffect",
                        objectValue
                            [ "id", box (StableId.value item.CreatedEffect.Id)
                              "displayName", box item.CreatedEffect.DisplayName ] ]
            | AuthoringProposalReview.CreateCapability item ->
                objectValue
                    [ "kind", box "createCapability"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdCapability", capabilityEntity item.CreatedCapability ]
        objectValue [ "proposalId", box value.ProposalId; "detail", box detail; "review", review ]

    let proposal value = proposalWith "full" WireEncodeCommon.functionEntity WireEncodeCommon.focus value

    /// Default simulate reply: the same review with function bodies reduced to a summary.
    let compactProposal value = proposalWith "compact" WireEncodeCommon.functionSummary WireEncodeCommon.compactFocus value

    let private csharpSymbol id name =
        objectValue [ "id", box id; "name", box name ]

    let csharpArtifact (value: CSharpArtifact) =
        let symbols = value.Symbols
        objectValue
            [ "source", box value.Source
              "symbols",
                objectValue
                    [ "namespace", box symbols.Namespace
                      "functionContainer", box symbols.FunctionContainer
                      "types",
                        symbols.Types
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "fields",
                        symbols.Fields
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "variants",
                        symbols.Variants
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "functions",
                        symbols.Functions
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "parameters",
                        symbols.Parameters
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "capabilities",
                        symbols.Capabilities
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "capabilityOperations",
                        symbols.CapabilityOperations
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "capabilityParameters",
                        symbols.CapabilityParameters
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue
                      "capabilityAdapters",
                        symbols.CapabilityAdapters
                        |> List.map (fun item -> csharpSymbol (StableId.value item.Id) item.Name)
                        |> arrayValue ] ]

    let declarations = WireEncodeCommon.declarations
    let focus = WireEncodeCommon.focus

    let status (value: AuthoringSessionStatus) =
        objectValue
            [ "snapshotId", box (StableId.value value.SnapshotId)
              "snapshotFingerprint", box (SnapshotFingerprint.value value.SnapshotFingerprint)
              "semanticFingerprint", box (SemanticFingerprint.value value.SemanticFingerprint) ]

    let commitReceipt (value: AuthoringCommitReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId)
              "readiness", readiness value.Readiness ]

    let rejectionReceipt (value: AuthoringRejectionReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "retainedSnapshotId", box (StableId.value value.RetainedSnapshotId) ]

    let private diagnostics (values: TransactionDiagnostic list) =
        values
        |> List.map (fun item -> objectValue [ "code", box item.Code; "message", box item.Message ])
        |> arrayValue

    let error (value: AuthoringSessionError) =
        let code, message, details =
            match value with
            | AuthoringSessionError.SnapshotFileNotFound path -> "SnapshotFileNotFound", path, None
            | AuthoringSessionError.SnapshotFileAlreadyExists path -> "SnapshotFileAlreadyExists", path, None
            | AuthoringSessionError.LockUnavailable path -> "LockUnavailable", path, None
            | AuthoringSessionError.IoFailure message -> "IoFailure", message, None
            | AuthoringSessionError.SnapshotCodecFailure error -> "SnapshotCodecFailure", sprintf "%A" error, None
            | AuthoringSessionError.WorkspaceValidationFailure issues -> "WorkspaceValidationFailure", sprintf "%A" issues, None
            | AuthoringSessionError.ProposalNotFound proposalId -> "ProposalNotFound", proposalId, None
            | AuthoringSessionError.SessionClosed -> "SessionClosed", "The session is closed.", None
            | AuthoringSessionError.ExternalModification -> "ExternalModification", "The snapshot file changed outside this session.", None
            | AuthoringSessionError.SessionInvalidated -> "SessionInvalidated", "The session was invalidated by external modification.", None
            | AuthoringSessionError.WorkflowSimulationFailure(AgentWorkflowError.SimulationRejected values) ->
                "SimulationRejected", "The requested operations were rejected.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.WorkflowSimulationFailure other -> "WorkflowSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected values) ->
                "SimulationRejected", "The structural request was rejected.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.StructuralSimulationFailure other -> "StructuralSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.TypeDeclarationSimulationFailure(AgentTypeDeclarationWorkflowError.SimulationRejected values) ->
                "SimulationRejected", "The type declaration was rejected.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.TypeDeclarationSimulationFailure other ->
                "TypeDeclarationSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.EffectDeclarationSimulationFailure(AgentEffectDeclarationWorkflowError.SimulationRejected values) ->
                "SimulationRejected", "The effect declaration was rejected.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.EffectDeclarationSimulationFailure other ->
                "EffectDeclarationSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.CapabilityDeclarationSimulationFailure(AgentCapabilityDeclarationWorkflowError.SimulationRejected values) ->
                "SimulationRejected", "The capability declaration was rejected.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.CapabilityDeclarationSimulationFailure other ->
                "CapabilityDeclarationSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.CommitRejected values ->
                "CommitRejected", "The reviewed proposal could not be committed.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.SemanticProjectionFailure other -> "SemanticProjectionFailure", sprintf "%A" other, None
            | AuthoringSessionError.ExecutionLoweringFailure other -> "ExecutionLoweringFailure", sprintf "%A" other, None
            | AuthoringSessionError.BackendFailure(CSharpBackendError.ConcurrentCapabilityBindingRequired(functionId, nodeId, capabilityId, operationId)) ->
                "ConcurrentCapabilityBindingRequired",
                "An operation reachable from a ParallelAll or TaskStart worker requires a capability binding that supports concurrent calls.",
                Some(objectValue
                    [ "functionId", box (StableId.value functionId)
                      "nodeId", box (StableId.value nodeId)
                      "capabilityId", box (StableId.value capabilityId)
                      "operationId", box (StableId.value operationId) ])
            | AuthoringSessionError.BackendFailure other -> "BackendFailure", sprintf "%A" other, None
            | AuthoringSessionError.DependencyFailure message -> "DependencyFailure", message, None
        match details with
        | None -> objectValue [ "code", box code; "message", box message ]
        | Some detail -> objectValue [ "code", box code; "message", box message; "details", detail ]
