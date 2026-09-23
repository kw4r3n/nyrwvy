namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type AgentRenameTarget =
    | Type of TypeId
    | Function of FunctionId

[<RequireQualifiedAccess>]
type AgentCallPlan =
    | BindExistingValue of callNodeId: GraphNodeId * valueNodeId: GraphNodeId
    | LeaveMissing of callNodeId: GraphNodeId

type AgentAddFunctionParameterIntent =
    { TargetFunctionId: FunctionId
      NewParameterDisplayName: string
      NewParameterType: TypeRef
      CallSitePlans: AgentCallPlan list }

type AgentResolveMissingArgumentIntent =
    { TargetObligationId: ObligationId
      ResolutionValueNodeId: GraphNodeId }

type AgentFunctionRequiredEffectIntent =
    { TargetFunctionId: FunctionId
      EffectId: EffectId }

[<RequireQualifiedAccess>]
type AgentOperationIntent =
    | RenameDisplayName of AgentRenameTarget * newDisplayName: string
    | AddFunctionParameter of AgentAddFunctionParameterIntent
    | ResolveMissingArgument of AgentResolveMissingArgumentIntent
    | AddFunctionRequiredEffect of AgentFunctionRequiredEffectIntent
    | RemoveFunctionRequiredEffect of AgentFunctionRequiredEffectIntent

type AgentWorkspace = private { Workspace: WorkspaceState }

type AgentChangeReview =
    { RequestedOperations: AgentOperationIntent list
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: ChangeReviewView }

type AgentChangeProposal = private { Request: TransactionRequest; Preview: WorkspacePreview; Review: AgentChangeReview }

[<RequireQualifiedAccess>]
type AgentWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | ReviewProjectionFailed of ChangeReviewQueryError

[<RequireQualifiedAccess>]
type AgentCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * PublishedTransaction

[<RequireQualifiedAccess>]
module AgentChangeProposal =
    let review (proposal: AgentChangeProposal): AgentChangeReview = proposal.Review
    let transactionId (proposal: AgentChangeProposal): TransactionId = proposal.Request.Id
    let proposedSnapshotId (proposal: AgentChangeProposal): SnapshotId = proposal.Request.ProposedSnapshotId

[<RequireQualifiedAccess>]
module AgentWorkspace =
    let create (initialSnapshot: Snapshot): Result<AgentWorkspace, ValidationIssue list> =
        SemanticWorkspace.create initialSnapshot
        |> Result.map (fun workspace -> { Workspace = workspace })

    /// Open a locally stored snapshot against a separately validated, pinned dependency.
    let createResolved (dependency: Snapshot) (local: Snapshot): Result<AgentWorkspace, ValidationIssue list> =
        create (SnapshotCodec.resolveDependency dependency local)

    let currentSnapshotId (workspace: AgentWorkspace): SnapshotId =
        SemanticWorkspace.currentSnapshot workspace.Workspace |> fun snapshot -> snapshot.Id

    let currentSnapshotFingerprint
        (workspace: AgentWorkspace)
        : Result<SnapshotFingerprint, SnapshotCodecError> =
        SemanticWorkspace.currentSnapshot workspace.Workspace |> SnapshotFingerprint.compute

    let currentSemanticFingerprint
        (workspace: AgentWorkspace)
        : Result<SemanticFingerprint, SnapshotCodecError> =
        SemanticWorkspace.currentSnapshot workspace.Workspace |> SemanticFingerprint.compute

    let private snapshot (workspace: AgentWorkspace): Snapshot =
        SemanticWorkspace.currentSnapshot workspace.Workspace

    /// Serialize the current authoritative workspace state through the strict
    /// snapshot codec without exposing raw snapshot publication authority.
    let serializeCurrentSnapshot
        (workspace: AgentWorkspace)
        : Result<string, SnapshotCodecError> =
        snapshot workspace |> SnapshotCodec.serialize

    /// Refuse publication if an imported declaration changed or disappeared. Only
    /// locally owned declarations are persisted; no imported graph is copied.
    let serializeWithDependency (dependency: Snapshot) (workspace: AgentWorkspace): Result<string, SnapshotCodecError> =
        let combined = snapshot workspace
        let importedIds items id = items |> List.map id |> Set.ofList
        let split imported id items = items |> List.partition (fun item -> Set.contains (id item) imported)
        let importedTypes, localTypes = split (importedIds dependency.Types (fun (item: SemanticType) -> item.Id)) (fun (item: SemanticType) -> item.Id) combined.Types
        let importedEffects, localEffects = split (importedIds dependency.Effects (fun (item: SemanticEffect) -> item.Id)) (fun (item: SemanticEffect) -> item.Id) combined.Effects
        let importedCapabilities, localCapabilities = split (importedIds dependency.Capabilities (fun (item: SemanticCapability) -> item.Id)) (fun (item: SemanticCapability) -> item.Id) combined.Capabilities
        let importedFunctions, localFunctions = split (importedIds dependency.Functions (fun (item: SemanticFunction) -> item.Id)) (fun (item: SemanticFunction) -> item.Id) combined.Functions
        let importedObligations, localObligations = split (importedIds dependency.Obligations (fun (item: MissingArgumentObligation) -> item.Id)) (fun (item: MissingArgumentObligation) -> item.Id) combined.Obligations
        if importedTypes <> dependency.Types || importedEffects <> dependency.Effects
           || importedCapabilities <> dependency.Capabilities || importedFunctions <> dependency.Functions
           || importedObligations <> dependency.Obligations then
            Error(SnapshotCodecError.InvalidSnapshot
                [ { Code = "ImportedDeclarationModified"; Message = "A dependency declaration was modified or removed." } ])
        else
            SnapshotCodec.serializeResolved dependency
                { combined with Types = localTypes; Effects = localEffects
                                Capabilities = localCapabilities; Functions = localFunctions
                                Obligations = localObligations }

    /// Read-only declaration discovery for the maintained authoring surface.
    let listFunctions (workspace: AgentWorkspace): SemanticFunction list =
        snapshot workspace
        |> fun current -> current.Functions
        |> List.sortBy (fun item -> StableId.value item.Id)

    let listTypes (workspace: AgentWorkspace): SemanticType list =
        snapshot workspace
        |> fun current -> current.Types
        |> List.sortBy (fun item -> StableId.value item.Id)

    let listEffects (workspace: AgentWorkspace): SemanticEffect list =
        snapshot workspace
        |> fun current -> current.Effects
        |> List.sortBy (fun item -> StableId.value item.Id)

    let listCapabilities (workspace: AgentWorkspace): SemanticCapability list =
        snapshot workspace
        |> fun current -> current.Capabilities
        |> List.sortBy (fun item -> StableId.value item.Id)

    /// Trusted bridge for the downstream Execution assembly. This remains
    /// internal so the ordinary agent-facing API never exposes the raw snapshot.
    let internal snapshotForExecution (workspace: AgentWorkspace): Snapshot =
        snapshot workspace

    /// Trusted bridge for the bounded structural-editing implementation.
    /// The raw snapshot remains assembly-internal and is never part of the
    /// ordinary agent request surface.
    let internal snapshotForStructuralEditing (workspace: AgentWorkspace): Snapshot =
        snapshot workspace

    /// Publish an already validated exact snapshot through the opaque agent
    /// workspace while preserving prior snapshot history. This remains internal
    /// semantic infrastructure rather than an ordinary agent mutation surface.
    let internal publishValidatedSnapshot
        (workspace: AgentWorkspace)
        (snapshot: Snapshot)
        : Result<AgentWorkspace, TransactionDiagnostic list> =
        SemanticWorkspace.publishValidatedSnapshot workspace.Workspace snapshot
        |> Result.map (fun published -> { Workspace = published })

    let getFunction workspace functionId = SemanticQuery.getFunction (snapshot workspace) functionId
    let getType workspace typeId = SemanticQuery.getType (snapshot workspace) typeId
    let getCallers workspace functionId = SemanticQuery.getCallers (snapshot workspace) functionId
    let getCallees workspace functionId = SemanticQuery.getCallees (snapshot workspace) functionId
    let getOpenObligations workspace functionId = SemanticQuery.getOpenObligations (snapshot workspace) functionId
    let getDependencies workspace functionId nodeId = SemanticQuery.getDependencies (snapshot workspace) functionId nodeId
    let getUses workspace functionId nodeId = SemanticQuery.getUses (snapshot workspace) functionId nodeId
    let focusFunction workspace functionId = SemanticQuery.focusFunction (snapshot workspace) functionId

[<RequireQualifiedAccess>]
module AgentWorkflow =
    let private materializeCallPlan plan =
        match plan with
        | AgentCallPlan.BindExistingValue(callNodeId, valueNodeId) ->
            CallPlan.BindExistingValue(callNodeId, valueNodeId)
        | AgentCallPlan.LeaveMissing callNodeId ->
            CallPlan.LeaveMissing(callNodeId, StableIdAllocator.newObligationId ())

    let private requiredEffectRequest (intent: AgentFunctionRequiredEffectIntent): FunctionRequiredEffectRequest =
        { TargetFunctionId = intent.TargetFunctionId
          EffectId = intent.EffectId }

    let private materializeOperation intent =
        match intent with
        | AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Type typeId, newDisplayName) ->
            SemanticOperation.RenameDisplayName(RenameTarget.TypeTarget typeId, newDisplayName)
        | AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function functionId, newDisplayName) ->
            SemanticOperation.RenameDisplayName(RenameTarget.FunctionTarget functionId, newDisplayName)
        | AgentOperationIntent.AddFunctionParameter request ->
            SemanticOperation.AddFunctionParameter
                { TargetFunctionId = request.TargetFunctionId
                  NewParameterId = StableIdAllocator.newParameterId ()
                  NewParameterDisplayName = request.NewParameterDisplayName
                  NewParameterType = request.NewParameterType
                  CallSitePlans = request.CallSitePlans |> List.map materializeCallPlan }
        | AgentOperationIntent.ResolveMissingArgument request ->
            SemanticOperation.ResolveMissingArgument
                { TargetObligationId = request.TargetObligationId
                  ResolutionValueNodeId = request.ResolutionValueNodeId }
        | AgentOperationIntent.AddFunctionRequiredEffect request ->
            SemanticOperation.AddFunctionRequiredEffect(requiredEffectRequest request)
        | AgentOperationIntent.RemoveFunctionRequiredEffect request ->
            SemanticOperation.RemoveFunctionRequiredEffect(requiredEffectRequest request)

    let simulate
        (workspace: AgentWorkspace)
        (requestedOperations: AgentOperationIntent list)
        : Result<AgentChangeProposal, AgentWorkflowError> =
        let semanticWorkspace = workspace.Workspace
        let request: TransactionRequest =
            { Id = StableIdAllocator.newTransactionId ()
              BaseSnapshotId = SemanticWorkspace.currentSnapshot semanticWorkspace |> fun snapshot -> snapshot.Id
              ProposedSnapshotId = StableIdAllocator.newSnapshotId ()
              Operations = requestedOperations |> List.map materializeOperation }

        match SemanticWorkspace.simulate semanticWorkspace request with
        | WorkspaceSimulationResult.Rejected diagnostics ->
            Error(AgentWorkflowError.SimulationRejected diagnostics)
        | WorkspaceSimulationResult.Preview preview ->
            match ChangeReviewQuery.project preview.Simulation with
            | Error error -> Error(AgentWorkflowError.ReviewProjectionFailed error)
            | Ok resultingChange ->
                let review =
                    { RequestedOperations = requestedOperations
                      Receipt = preview.Receipt
                      Readiness = preview.Readiness
                      ResultingChange = resultingChange }

                Ok
                    { Request = request
                      Preview = preview
                      Review = review }

    let private mapCommitResult result =
        match result with
        | WorkspaceCommitResult.Rejected diagnostics -> AgentCommitResult.Rejected diagnostics
        | WorkspaceCommitResult.Committed(workspace, publication) ->
            AgentCommitResult.Committed({ Workspace = workspace }, publication)

    let commit
        (workspace: AgentWorkspace)
        (proposal: AgentChangeProposal)
        : AgentCommitResult =
        SemanticWorkspace.commitReviewed
            workspace.Workspace
            proposal.Request
            proposal.Preview.Receipt
        |> mapCommitResult

    let commitWithPolicy
        (workspace: AgentWorkspace)
        (proposal: AgentChangeProposal)
        (policy: CommitPolicy)
        : AgentCommitResult =
        SemanticWorkspace.commitReviewedWithPolicy
            workspace.Workspace
            proposal.Request
            proposal.Preview.Receipt
            policy
        |> mapCommitResult

    let reject
        (workspace: AgentWorkspace)
        (proposal: AgentChangeProposal)
        : AgentWorkspace * TransactionRejectionReceipt =
        let retained, receipt = SemanticWorkspace.reject workspace.Workspace proposal.Request
        { Workspace = retained }, receipt
