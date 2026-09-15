namespace NyamNyam.Tools.AuthoringSession

open NyamNyam.Core

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

    let private targetDiff (value: AgentExpressionStructuralWorkflow.Targeted.ReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "targetNodeId", box (StableId.value value.TargetNodeId)
              "previousTarget", nodeSummary value.PreviousTarget
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue
              "addedPatternBindingIds",
                value.AddedPatternBindings
                |> List.map (fun binding -> box (StableId.value binding.Id))
                |> arrayValue
              "retainedSharedDependencies", value.RetainedSharedDependencies |> List.map nodeSummary |> arrayValue
              "affectedUses", value.AffectedUses |> List.map (sprintf "%A" >> box) |> arrayValue ]

    let proposal (value: AuthoringProposal) =
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
                      "diff", bodyDiff item.ResultingChange
                      "baseFocus", focus item.BaseFocus
                      "proposedFocus", focus item.ProposedFocus ]
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
                      "materializedBody", item.MaterializedBody |> List.map nodeSummary |> arrayValue
                      "proposedFocus", focus item.ProposedFocus ]
            | AuthoringProposalReview.CreateType item ->
                objectValue
                    [ "kind", box "createType"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdType", semanticType item.CreatedType ]
            | AuthoringProposalReview.CreateEffect item ->
                objectValue
                    [ "kind", box "createEffect"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdEffect",
                        objectValue
                            [ "id", box (StableId.value item.CreatedEffect.Id)
                              "displayName", box item.CreatedEffect.DisplayName ] ]
        objectValue [ "proposalId", box value.ProposalId; "review", review ]

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
            | AuthoringSessionError.CommitRejected values ->
                "CommitRejected", "The reviewed proposal could not be committed.", Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.SemanticProjectionFailure other -> "SemanticProjectionFailure", sprintf "%A" other, None
            | AuthoringSessionError.ExecutionLoweringFailure other -> "ExecutionLoweringFailure", sprintf "%A" other, None
            | AuthoringSessionError.BackendFailure other -> "BackendFailure", sprintf "%A" other, None
        match details with
        | None -> objectValue [ "code", box code; "message", box message ]
        | Some detail -> objectValue [ "code", box code; "message", box message; "details", detail ]
