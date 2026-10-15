namespace Nyrwvy.Tools.AuthoringSession

open Nyrwvy.Core

module internal WireEncode =
    open WireJson
    open WireEncodeCommon

    let private operationSignature (op: SemanticCapabilityOperation) =
        objectValue [ "id", box (StableId.value op.Id); "displayName", box op.DisplayName
                      "parameters", op.Parameters |> List.map (fun p -> objectValue [ "id", box (StableId.value p.Id); "displayName", box p.DisplayName; "type", typeRef p.Type ]) |> arrayValue
                      "resultType", typeRef op.ResultType ]

    let private rename (value: SymbolRename) =
        let kind, id, oldName, newName =
            match value with
            | SymbolRename.TypeRenamed(typeId, oldName, newName) -> "type", StableId.value typeId, oldName, newName
            | SymbolRename.FunctionRenamed(functionId, oldName, newName) -> "function", StableId.value functionId, oldName, newName
        objectValue [ "kind", box kind; "id", box id; "oldDisplayName", box oldName; "newDisplayName", box newName ]

    let private requiredEffectChange (value: FunctionRequiredEffectChange) =
        let kind, functionId, effectId =
            match value with
            | FunctionRequiredEffectChange.Added(functionId, effectId) -> "added", functionId, effectId
            | FunctionRequiredEffectChange.Removed(functionId, effectId) -> "removed", functionId, effectId
        objectValue [ "kind", box kind; "functionId", box (StableId.value functionId); "effectId", box (StableId.value effectId) ]

    let private obligationRecord (value: MissingArgumentObligation) =
        let status =
            match value.Status with
            | ObligationStatus.Open -> objectValue [ "kind", box "open" ]
            | ObligationStatus.Resolved resolution ->
                objectValue [ "kind", box "resolved"; "transactionId", box (StableId.value resolution.TransactionId)
                              "valueNodeId", box (StableId.value resolution.ValueNodeId) ]
        objectValue [ "obligationId", box (StableId.value value.Id); "originTransactionId", box (StableId.value value.OriginTransactionId)
                      "callNodeId", box (StableId.value value.CallNodeId); "parameterId", box (StableId.value value.ParameterId)
                      "status", status ]

    let private affectedUse (value: AgentExpressionStructuralWorkflow.Targeted.Use) =
        match value with
        | AgentExpressionStructuralWorkflow.Targeted.Use.NodeDependency(consumerNodeId, role) ->
            objectValue [ "kind", box "nodeDependency"; "consumerNodeId", box (StableId.value consumerNodeId); "role", box (dependencyRole role) ]
        | AgentExpressionStructuralWorkflow.Targeted.Use.GraphResult ownerNodeId ->
            objectValue [ "kind", box "graphResult"; "ownerNodeId", ownerNodeId |> Option.map (StableId.value >> box) |> Option.defaultValue null ]

    let private semanticDiff (value: SemanticDiff) =
        objectValue
            [ "renames", value.Renames |> List.map rename |> arrayValue
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
                    objectValue (
                        [ "functionId", box (StableId.value item.FunctionId)
                          "displayName", box item.DisplayName
                          "removedNodeIds", item.RemovedNodeIds |> List.map (StableId.value >> box) |> arrayValue ]
                        @ (if List.isEmpty item.RetiredObligations then [] else
                               [ "retiredObligations", item.RetiredObligations |> List.map obligationRecord |> arrayValue ])))
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
                value.FunctionRequiredEffectsChanged |> List.map requiredEffectChange |> arrayValue ]

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
              "affectedUses", value.AffectedUses |> List.map affectedUse |> arrayValue ]

    let private parameterTypeMigrationDiff (value: AgentParameterTypeMigrationDiff) =
        objectValue
            [ "parameterChange",
                objectValue
                    [ "functionId", box (StableId.value value.ParameterChange.FunctionId)
                      "parameterId", box (StableId.value value.ParameterChange.ParameterId)
                      "oldType", typeRef value.ParameterChange.OldType
                      "newType", typeRef value.ParameterChange.NewType ]
              "affectedCallSites",
                value.AffectedCallSites |> List.map callSite |> arrayValue
              "bodyRewrites", value.BodyRewrites |> List.map bodyDiff |> arrayValue ]

    let private resultTypeMigrationDiff (value: AgentResultTypeMigrationDiff) =
        objectValue
            [ "resultChange",
                objectValue
                    [ "functionId", box (StableId.value value.ResultChange.FunctionId)
                      "oldType", typeRef value.ResultChange.OldType
                      "newType", typeRef value.ResultChange.NewType ]
              "affectedCallSites",
                value.AffectedCallSites |> List.map callSite |> arrayValue
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
            | AuthoringProposalReview.DependencyUpgrade item ->
                let changes (items: AuthoringDependencyChange list) = items |> List.map (fun c -> objectValue [ "kind", box c.Kind; "id", box c.Id ]) |> arrayValue
                let members (items: AuthoringDependencyMember list) = items |> List.map (fun m -> objectValue [ "snapshotId", box m.SnapshotId; "fingerprint", box m.Fingerprint ]) |> arrayValue
                withRewritten
                    [ "kind", box "dependencyUpgrade"
                      "receipt", receipt item.Repair.Receipt
                      "readiness", readiness item.Repair.Readiness
                      "dependencies", objectValue [ "replaced", members item.Upgrade.Replaced; "selected", members item.Upgrade.Selected;
                                                     "added", changes item.Upgrade.Added; "removed", changes item.Upgrade.Removed; "changed", changes item.Upgrade.Changed ]
                      "changedFunctions", item.Repair.ChangedFunctions |> List.map functionEntity |> arrayValue
                      "changedTypes", item.Repair.ChangedTypes |> List.map semanticType |> arrayValue
                      "changedCapabilities", item.Repair.ChangedCapabilities |> List.map capabilityEntity |> arrayValue ]
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
            | AuthoringProposalReview.CapabilityOperationSignatureMigration item ->
                withRewritten
                    [ "kind", box "capabilityOperationSignatureMigration"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", objectValue
                          [ "capabilityId", box (StableId.value item.ResultingChange.CapabilityId)
                            "oldOperation", operationSignature item.ResultingChange.OldOperation
                            "newOperation", operationSignature item.ResultingChange.NewOperation
                            "signatureChanges", item.ResultingChange.SignatureChanges |> List.map (fun (before, after) -> objectValue [ "before", functionSummary before; "after", functionSummary after ]) |> arrayValue
                            "bodyRewrites", item.ResultingChange.BodyRewrites |> List.map bodyDiff |> arrayValue ]
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
    let compactProposal value = proposalWith "compact" WireEncodeCommon.functionSummary WireEncodeCommon.compactFocus value |> WireProposalProjection.compact

    let private csharpSymbols (items: CSharpSymbol<StableId<'Kind>> list) =
        items
        |> List.map (fun item -> objectValue [ "id", box (StableId.value item.Id); "name", box item.Name ])
        |> arrayValue

    let csharpArtifact (value: CSharpArtifact) =
        let symbols = value.Symbols
        objectValue
            [ "source", box value.Source
              "symbols",
                objectValue
                    [ "namespace", box symbols.Namespace
                      "functionContainer", box symbols.FunctionContainer
                      "types", csharpSymbols symbols.Types
                      "fields", csharpSymbols symbols.Fields
                      "variants", csharpSymbols symbols.Variants
                      "functions", csharpSymbols symbols.Functions
                      "parameters", csharpSymbols symbols.Parameters
                      "capabilities", csharpSymbols symbols.Capabilities
                      "capabilityOperations", csharpSymbols symbols.CapabilityOperations
                      "capabilityParameters", csharpSymbols symbols.CapabilityParameters
                      "capabilityAdapters", csharpSymbols symbols.CapabilityAdapters ] ]

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

    let private issues (values: ValidationIssue list) =
        values
        |> List.map (fun item -> objectValue [ "code", box item.Code; "message", box item.Message ])
        |> arrayValue

    /// One sentence for a validation issue list, with the first issue spelled out; details carry them all.
    let private issuesMessage (subject: string) (values: ValidationIssue list) =
        match values with
        | [] -> $"{subject} is invalid."
        | [ issue ] -> $"{subject} is invalid: {issue.Code}: {issue.Message}"
        | issue :: rest -> $"{subject} is invalid: {issue.Code}: {issue.Message} ({List.length rest + 1} issues; see details)"

    /// Worded codec failure for the wire and for startup diagnostics; details list validation issues when there are any.
    let describeCodecError (error: SnapshotCodecError) =
        match error with
        | SnapshotCodecError.InvalidJson message -> $"The snapshot is not valid JSON: {message}", None
        | SnapshotCodecError.InvalidShape message -> $"The snapshot does not have the expected shape: {message}", None
        | SnapshotCodecError.UnsupportedSchemaVersion version -> $"Snapshot schema version {version} is not supported.", None
        | SnapshotCodecError.InvalidSnapshot values -> issuesMessage "The snapshot" values, Some(objectValue [ "issues", issues values ])
        | SnapshotCodecError.MaximumDepthExceeded depth -> $"The graph would nest deeper than the codec's {depth} JSON levels (MaximumDepthExceeded).", None

    /// Code, message and optional details for every session error. Messages are worded for a
    /// reader without F# knowledge; the startup ones carry the recovery hint a first-time user needs.
    let private describe (value: AuthoringSessionError) =
        match value with
        | AuthoringSessionError.SnapshotFileNotFound path ->
            "SnapshotFileNotFound", $"No snapshot file at {path}. Use create (MCP nyrwvy_open mode \"create\") to start a new one.", None
        | AuthoringSessionError.SnapshotFileAlreadyExists path ->
            "SnapshotFileAlreadyExists", $"A snapshot already exists at {path}. Use open to resume it; create never overwrites.", None
        | AuthoringSessionError.LockUnavailable path -> "LockUnavailable", $"{path} is open in another authoring session. Close that session first.", None
        | AuthoringSessionError.IoFailure message -> "IoFailure", message, None
        | AuthoringSessionError.SnapshotCodecFailure error ->
            let message, details = describeCodecError error
            "SnapshotCodecFailure", message, details
        | AuthoringSessionError.WorkspaceValidationFailure values ->
            "WorkspaceValidationFailure", issuesMessage "The workspace graph" values, Some(objectValue [ "issues", issues values ])
        | AuthoringSessionError.ProposalNotFound proposalId ->
            "ProposalNotFound", $"No pending proposal has the ID '{proposalId}'; it was committed, rejected, or discarded when its session closed.", None
        | AuthoringSessionError.SessionClosed -> "SessionClosed", "The session is closed.", None
        | AuthoringSessionError.ExternalModification -> "ExternalModification", "The snapshot file changed outside this session.", None
        | AuthoringSessionError.SessionInvalidated -> "SessionInvalidated", "The session was invalidated by external modification.", None
        | AuthoringSessionError.WorkflowSimulationFailure(AgentWorkflowError.SimulationRejected values) ->
            "SimulationRejected", "The requested operations were rejected.", Some(objectValue [ "diagnostics", diagnostics values ])
        | AuthoringSessionError.WorkflowSimulationFailure other -> "WorkflowSimulationFailure", sprintf "%A" other, None
        | AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected values)
        | AuthoringSessionError.FieldTypeMigrationRepairMismatch(values, _, _) ->
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

    /// The one-line stderr text for a session that could not start.
    let startupMessage (value: AuthoringSessionError) =
        let code, message, _ = describe value
        $"{code}: {message}"

    let error (value: AuthoringSessionError) =
        let code, message, details = describe value
        match details with
        | None -> objectValue [ "code", box code; "message", box message ]
        | Some detail -> objectValue [ "code", box code; "message", box message; "details", detail ]

    /// Retain the established lowering error code and exact IDs, adding actionable context.
    let errorWithDeclarations (declarations: AuthoringDeclarations) value =
        match value with
        | AuthoringSessionError.FieldTypeMigrationRepairMismatch(values, missing, unrelated) ->
            let references ids =
                let shown = ids |> List.truncate 5 |> List.map (fun id ->
                    let exact = StableId.value id
                    let fn = declarations.Functions |> List.tryFind (fun f -> f.Id = id)
                    let provenance = declarations.Provenance |> List.tryFind (fun p -> p.Kind = "function" && p.Id = exact)
                    let name = fn |> Option.map (fun f -> f.DisplayName) |> Option.defaultValue "<unknown>"
                    let mutable cut = min 128 name.Length
                    if cut > 0 && cut < name.Length && System.Char.IsHighSurrogate name[cut - 1] then cut <- cut - 1
                    let pins = provenance |> Option.map (fun p -> p.Sources |> List.sortBy (fun s -> s.SnapshotId)) |> Option.defaultValue []
                    objectValue [ "id", box exact; "displayName", box (name.Substring(0, cut)); "displayNameTruncated", box (cut < name.Length)
                                  "ownership", box (provenance |> Option.map (fun p -> if p.IsLocal then "local" else "imported") |> Option.defaultValue "unknown")
                                  "sources", pins |> List.truncate 5 |> List.map (fun s -> objectValue [ "snapshotId", box s.SnapshotId; "fingerprint", box s.Fingerprint ]) |> arrayValue
                                  "sourceCount", box pins.Length; "sourcesTruncated", box (pins.Length > 5) ])
                objectValue [ "items", arrayValue shown; "totalCount", box ids.Length; "truncated", box (ids.Length > shown.Length) ]
            objectValue [ "code", box "SimulationRejected"; "message", box "The structural request was rejected."
                          "details", objectValue [ "diagnostics", diagnostics values
                                                   "repair", objectValue [ "missing", references missing; "unrelated", references unrelated
                                                                           "inspectOperation", box "declarations" ] ] ]
        | AuthoringSessionError.ExecutionLoweringFailure(ExecutionIrError.AmbiguousEffectOrder(functionId, first, second)) ->
            let fn = declarations.Functions |> List.tryFind (fun f -> f.Id = functionId)
            let displayName = fn |> Option.map (fun f -> f.DisplayName) |> Option.defaultValue (StableId.value functionId)
            let nodes = fn |> Option.map (fun f -> ValueGraph.collectNodes f.Body) |> Option.defaultValue []
            let operand id =
                let node = nodes |> List.tryFind (fun n -> n.Id = id)
                let kind, name =
                    match node with
                    | Some n ->
                        let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(n.Kind, typeof<SemanticNodeKind>)
                        let name =
                            match n.Kind with
                            | SemanticNodeKind.DirectCall(callee, _) -> declarations.Functions |> List.tryFind (fun f -> f.Id = callee) |> Option.map (fun f -> "Call to " + f.DisplayName)
                            | SemanticNodeKind.CapabilityInvoke(_, operation, _) -> declarations.Capabilities |> List.tryPick (fun c -> c.Operations |> List.tryFind (fun o -> o.Id = operation) |> Option.map (fun o -> c.DisplayName + "." + o.DisplayName))
                            | _ -> None
                        case.Name, Option.defaultValue case.Name name
                    | None -> "unknown", StableId.value id
                objectValue [ "nodeId", box (StableId.value id); "kind", box kind; "displayName", box name ]
            objectValue
                [ "code", box "ExecutionLoweringFailure"
                  "message", box $"Function '{displayName}' has unordered observable operations. Sequence them with Bind in the intended order, then simulate and review the edit."
                  "details", objectValue
                      [ "cause", box "AmbiguousEffectOrder"
                        "functionId", box (StableId.value functionId); "function", box displayName
                        "operands", [ operand first; operand second ] |> arrayValue
                        "repair", objectValue [ "kind", box "Bind"; "requiresExplicitOrder", box true ] ] ]
        | _ -> error value
