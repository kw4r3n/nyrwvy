namespace Nyrwvy.Core

/// Complete type maps retain the parameter identities and declaration order.
type AgentCapabilityParameterType = { ParameterId: CapabilityParameterId; Type: TypeRef }
type AgentFunctionParameterType = { ParameterId: ParameterId; Type: TypeRef }
type AgentCapabilityFunctionRepair =
    { FunctionId: FunctionId
      ParameterTypes: AgentFunctionParameterType list
      ResultType: TypeRef
      Replacement: ExpressionRecipe }
type AgentCapabilityOperationSignatureMigrationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      OperationId: CapabilityOperationId
      ParameterTypes: AgentCapabilityParameterType list
      ResultType: TypeRef
      FunctionRepairs: AgentCapabilityFunctionRepair list }
type AgentCapabilityOperationSignatureMigrationDiff =
    { CapabilityId: CapabilityId
      OldOperation: SemanticCapabilityOperation
      NewOperation: SemanticCapabilityOperation
      SignatureChanges: (SemanticFunction * SemanticFunction) list
      BodyRewrites: FunctionBodyReplacementDiff list }
type AgentCapabilityOperationSignatureMigrationReview =
    { RequestedChange: AgentCapabilityOperationSignatureMigrationRequest
      FunctionEffects: FunctionEffectSummary list
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: AgentCapabilityOperationSignatureMigrationDiff }
type AgentCapabilityOperationSignatureMigrationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: AgentCapabilityOperationSignatureMigrationDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }
type private CapabilityOperationSignatureMigrationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentCapabilityOperationSignatureMigrationReview }
type AgentCapabilityOperationSignatureMigrationProposal =
    private
    | AgentCapabilityOperationSignatureMigrationProposal of CapabilityOperationSignatureMigrationProposalState
[<RequireQualifiedAccess>]
module AgentCapabilityOperationSignatureMigrationProposal =
    let private state (AgentCapabilityOperationSignatureMigrationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot
    let proposedFunctions proposal = (state proposal).ProposedSnapshot.Functions
[<RequireQualifiedAccess>]
type AgentCapabilityOperationSignatureMigrationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentCapabilityOperationSignatureMigrationPublishedTransaction

[<RequireQualifiedAccess>]
module AgentCapabilityOperationSignatureMigrationWorkflow =
    open StructuralTransactionInternals
    let private reject suffix message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ("OperationSignatureMigration" + suffix) message ])
    type private Prepared =
        { Owner: SemanticCapability
          UpdatedOwner: SemanticCapability
          Operation: SemanticCapabilityOperation
          UpdatedOperation: SemanticCapabilityOperation
          Affected: SemanticFunction list
          Functions: Map<FunctionId, SemanticFunction>
          Bodies: Map<FunctionId, ValueGraph> }
    let private prepare (request: AgentCapabilityOperationSignatureMigrationRequest) (snapshot: Snapshot) =
        let owner = snapshot.Capabilities |> List.tryPick (fun cap ->
            cap.Operations |> List.tryFind (fun op -> op.Id = request.OperationId) |> Option.map (fun op -> cap, op))
        let exact expected actual = List.length actual = List.length (List.distinct actual) && Set.ofList actual = Set.ofList expected
        let signatureChanged (before: SemanticFunction) (after: SemanticFunction) = before.Parameters <> after.Parameters || before.ResultType <> after.ResultType
        if request.ExpectedBaseSnapshotId <> snapshot.Id then reject "ExpectedBaseMismatch" "The request targets a different snapshot."
        else
            match owner with
            | None -> reject "OperationNotFound" "The operation does not exist."
            | Some(cap, op) ->
                if not (exact (op.Parameters |> List.map (fun p -> p.Id)) (request.ParameterTypes |> List.map (fun p -> p.ParameterId))) then
                    reject "ParameterMismatch" "Supply each existing operation parameter ID exactly once."
                elif not (exact (request.FunctionRepairs |> List.map (fun r -> r.FunctionId)) (request.FunctionRepairs |> List.map (fun r -> r.FunctionId))) then
                    reject "DuplicateRepair" "Supply each repaired function once."
                else
                    let parameters = request.ParameterTypes |> List.map (fun p -> p.ParameterId, p.Type) |> Map.ofList
                    let updatedOp = { op with Parameters = op.Parameters |> List.map (fun p -> { p with Type = Map.find p.Id parameters }); ResultType = request.ResultType }
                    if updatedOp = op then reject "TypeUnchanged" "The operation signature is unchanged."
                    else
                        let invalidType = (request.ResultType :: (request.ParameterTypes |> List.map (fun p -> p.Type))) |> List.tryPick (fun t ->
                            match validateDeclaredTypeRef "OperationSignatureMigration" (snapshot.Types |> List.map (fun t -> t.Id) |> Set.ofList) (snapshot.Capabilities |> List.map (fun c -> c.Id) |> Set.ofList) "Operation signature" t with
                            | Error e -> Some e | Ok () -> None)
                        let invalidRepair = request.FunctionRepairs |> List.tryPick (fun r ->
                            match snapshot.Functions |> List.tryFind (fun f -> f.Id = r.FunctionId) with
                            | None -> Some "A repair references an unknown function."
                            | Some f when not (exact (f.Parameters |> List.map (fun p -> p.Id)) (r.ParameterTypes |> List.map (fun p -> p.ParameterId))) -> Some "Supply each existing function parameter ID exactly once."
                            | _ -> None)
                        match invalidType, invalidRepair with
                        | Some error, _ -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
                        | _, Some message -> reject "RepairSignatureMismatch" message
                        | None, None ->
                            let repairs = request.FunctionRepairs |> List.map (fun r -> r.FunctionId, r) |> Map.ofList
                            let functions = snapshot.Functions |> List.map (fun f ->
                                match Map.tryFind f.Id repairs with
                                | None -> f
                                | Some r ->
                                    let types = r.ParameterTypes |> List.map (fun p -> p.ParameterId, p.Type) |> Map.ofList
                                    { f with Parameters = f.Parameters |> List.map (fun p -> { p with Type = Map.find p.Id types }); ResultType = r.ResultType })
                            let byId = functions |> List.map (fun f -> f.Id, f) |> Map.ofList
                            let changed = snapshot.Functions |> List.filter (fun f -> signatureChanged f (Map.find f.Id byId)) |> List.map (fun f -> f.Id) |> Set.ofList
                            let implementations =
                                snapshot.Functions
                                |> List.collect (fun f ->
                                    ValueGraph.collectNodes f.Body
                                    |> List.collect (fun n ->
                                        match n.Kind with
                                        | SemanticNodeKind.CapabilityConstruct(_, bindings) ->
                                            bindings |> List.choose (fun (operation, implementation) -> if operation = op.Id then Some implementation else None)
                                        | _ -> []))
                                |> Set.ofList
                            let seeds =
                                snapshot.Functions
                                |> List.filter (fun f ->
                                    Set.contains f.Id implementations ||
                                    (ValueGraph.collectNodes f.Body |> List.exists (fun n ->
                                        match n.Kind with
                                        | SemanticNodeKind.CapabilityInvoke(_, operation, _) -> operation = op.Id
                                        | SemanticNodeKind.CapabilityConstruct(_, bindings) -> bindings |> List.exists (fun (operation, _) -> operation = op.Id)
                                        | _ -> false)))
                                |> List.map (fun f -> f.Id)
                                |> Set.ofList
                            let rec closure ids =
                                let callers =
                                    Set.intersect ids changed
                                    |> Set.toList
                                    |> List.collect (fun target ->
                                        match SemanticQuery.getCallers snapshot target with
                                        | Ok sites -> sites |> List.map (fun site -> site.CallerFunctionId)
                                        | Error _ -> [])
                                    |> Set.ofList
                                let next = Set.union ids callers
                                if next = ids then ids else closure next
                            let required = closure seeds
                            let affected = snapshot.Functions |> List.filter (fun f -> Set.contains f.Id required)
                            let supplied = request.FunctionRepairs |> List.map (fun r -> r.FunctionId) |> Set.ofList
                            if supplied <> required then
                                let render ids = ids |> Set.toList |> List.map StableId.value |> String.concat ", "
                                reject "RepairMismatch"
                                    $"Repair all invokers, constructed implementations/owners and users of changed signatures. Missing: [{render (Set.difference required supplied)}]. Unrelated: [{render (Set.difference supplied required)}]."
                            else
                                match affected |> List.tryPick (fun f -> bodyObligationError f snapshot) with
                                | Some e -> reject "BodyReferencedByObligation" e.Message
                                | None ->
                                    let updatedCap = { cap with Operations = cap.Operations |> List.map (fun o -> if o.Id = op.Id then updatedOp else o) }
                                    let candidate = { snapshot with Functions = functions; Capabilities = snapshot.Capabilities |> List.map (fun c -> if c.Id = cap.Id then updatedCap else c) }
                                    let materialized = affected |> List.map (fun f ->
                                        AgentExpressionStructuralWorkflow.materializeReplacementBody candidate (Map.find f.Id byId) (Map.find f.Id repairs).Replacement |> Result.map (fun body -> f.Id, body))
                                    match materialized |> List.tryPick (function Error e -> Some e | _ -> None) with
                                    | Some e -> reject "RepairInvalid" (e.Code + ": " + e.Message)
                                    | None -> Ok { Owner = cap; UpdatedOwner = updatedCap; Operation = op; UpdatedOperation = updatedOp; Affected = affected; Functions = byId; Bodies = materialized |> List.choose (function Ok x -> Some x | _ -> None) |> Map.ofList }
    let simulate
        (workspace: AgentWorkspace)
        (request: AgentCapabilityOperationSignatureMigrationRequest)
        : Result<AgentCapabilityOperationSignatureMigrationProposal, AgentStructuralWorkflowError> =
        simulateStructural
            workspace
            (prepare request)
            (fun prepared snapshot ->
                { snapshot with
                    Capabilities = snapshot.Capabilities |> List.map (fun item -> if item.Id = prepared.Owner.Id then prepared.UpdatedOwner else item)
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            match Map.tryFind fn.Id prepared.Bodies with
                            | Some body -> { (Map.find fn.Id prepared.Functions) with Body = body }
                            | None -> fn) })
        |> Result.map (fun candidate ->
            let prepared = candidate.Prepared
            let proposedSnapshot = candidate.ProposedSnapshot
            let proposed functionId = proposedSnapshot.Functions |> List.find (fun fn -> fn.Id = functionId)
            AgentCapabilityOperationSignatureMigrationProposal
                { TransactionId = candidate.TransactionId
                  ProposedSnapshot = proposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = candidate.Receipt
                      Readiness = candidate.Readiness
                      FunctionEffects = EffectRequirements.summarize proposedSnapshot (prepared.Affected |> List.map (fun fn -> fn.Id) |> Set.ofList)
                      ResultingChange =
                        { CapabilityId = prepared.Owner.Id
                          OldOperation = prepared.Operation
                          NewOperation = prepared.UpdatedOperation
                          SignatureChanges = prepared.Affected |> List.map (fun fn -> fn, proposed fn.Id)
                          BodyRewrites =
                            prepared.Affected
                            |> List.map (fun fn ->
                                SignatureMigrationCore.bodyDiff candidate.BaseSnapshot proposedSnapshot fn (proposed fn.Id)) } } })

    let commitWithPolicy workspace (AgentCapabilityOperationSignatureMigrationProposal proposal) policy =
        match publishCandidateWithPolicy workspace proposal.ProposedSnapshot proposal.Review.Readiness proposal.Review.Receipt policy with
        | Error diagnostics -> AgentCapabilityOperationSignatureMigrationCommitResult.Rejected diagnostics
        | Ok nextWorkspace ->
            AgentCapabilityOperationSignatureMigrationCommitResult.Committed(
                nextWorkspace,
                { TransactionId = proposal.TransactionId
                  BaseSnapshotId = proposal.Review.Receipt.BaseSnapshotId
                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                  Diff = proposal.Review.ResultingChange
                  Readiness = proposal.Review.Readiness
                  Receipt = proposal.Review.Receipt })
