namespace Nyrwvy.Core

/// Existing field identities and declaration order are retained.
type AgentDependencyFieldRepair = { TypeId: TypeId; FieldId: FieldId; NewType: TypeRef }
type AgentDependencyOperationRepair =
    { OperationId: CapabilityOperationId
      ParameterTypes: AgentCapabilityParameterType list
      ResultType: TypeRef }
type AgentDependencyLocalRepairs =
    { Fields: AgentDependencyFieldRepair list
      Functions: AgentCapabilityFunctionRepair list
      Operations: AgentDependencyOperationRepair list }
type AgentDependencyUpgradeReview =
    { RequestedRepairs: AgentDependencyLocalRepairs
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ChangedFunctions: SemanticFunction list
      ChangedTypes: SemanticType list
      ChangedCapabilities: SemanticCapability list }
type AgentDependencyUpgradeProposal =
    private
        { Candidate: Snapshot
          Review: AgentDependencyUpgradeReview }

[<RequireQualifiedAccess>]
module AgentDependencyUpgradeProposal =
    let review proposal = proposal.Review
    let proposedSnapshot proposal = proposal.Candidate
    let transactionId proposal = proposal.Review.Receipt.TransactionId

/// A dependency change uses the same structural simulation, recipe materialization,
/// readiness, fingerprints and publication guards as local signature migrations.
[<RequireQualifiedAccess>]
module AgentDependencyUpgradeWorkflow =
    open StructuralTransactionInternals

    let private exact expected supplied =
        List.length supplied = List.length (List.distinct supplied) && Set.ofList expected = Set.ofList supplied

    let simulate workspace (previous: Snapshot) (selected: Snapshot) (repairs: AgentDependencyLocalRepairs) =
        let prepare (snapshot: Snapshot) =
            let local imported id items =
                let ids = imported |> List.map id |> Set.ofList
                items |> List.filter (fun item -> not (Set.contains (id item) ids))
            let owned =
                { snapshot with
                    Types = local previous.Types (fun t -> t.Id) snapshot.Types
                    Functions = local previous.Functions (fun f -> f.Id) snapshot.Functions
                    Capabilities = local previous.Capabilities (fun c -> c.Id) snapshot.Capabilities
                    Effects = local previous.Effects (fun e -> e.Id) snapshot.Effects
                    Obligations = local previous.Obligations (fun o -> o.Id) snapshot.Obligations }
            let reject message = rejected "DependencyRepairInvalid" message
            let unique ids = List.length ids = List.length (List.distinct ids)
            let fields = repairs.Fields |> List.map (fun r -> r.TypeId, r.FieldId)
            let functions = repairs.Functions |> List.map (fun r -> r.FunctionId)
            let operations = repairs.Operations |> List.map (fun r -> r.OperationId)
            if not (unique fields && unique functions && unique operations) then reject "Each local field, function or operation may be repaired once."
            else
                let badField =
                    repairs.Fields |> List.exists (fun r ->
                        owned.Types |> List.exists (fun t ->
                            if t.Id <> r.TypeId then false
                            else match t.Kind with
                                 | SemanticTypeKind.Product fields -> fields |> List.exists (fun f -> f.Id = r.FieldId)
                                 | _ -> false) |> not)
                let badFunction =
                    repairs.Functions |> List.exists (fun r ->
                        owned.Functions |> List.exists (fun f ->
                            f.Id = r.FunctionId && exact (f.Parameters |> List.map (fun p -> p.Id)) (r.ParameterTypes |> List.map (fun p -> p.ParameterId))) |> not)
                let badOperation =
                    repairs.Operations |> List.exists (fun r ->
                        owned.Capabilities |> List.exists (fun c ->
                            c.Operations |> List.exists (fun o ->
                                o.Id = r.OperationId && exact (o.Parameters |> List.map (fun p -> p.Id)) (r.ParameterTypes |> List.map (fun p -> p.ParameterId)))) |> not)
                if badField || badFunction || badOperation then reject "Repair only existing locally owned declarations; supply every existing signature parameter ID exactly once. Imported declarations are read-only."
                else
                    let updateType (t: SemanticType) =
                        match t.Kind with
                        | SemanticTypeKind.Product fields ->
                            let updated = fields |> List.map (fun f ->
                                repairs.Fields |> List.tryFind (fun r -> r.TypeId = t.Id && r.FieldId = f.Id)
                                |> Option.map (fun r -> { f with Type = r.NewType }) |> Option.defaultValue f)
                            { t with Kind = SemanticTypeKind.Product updated }
                        | _ -> t
                    let updateFunction (f: SemanticFunction) =
                        match repairs.Functions |> List.tryFind (fun r -> r.FunctionId = f.Id) with
                        | None -> f
                        | Some r ->
                            let types = r.ParameterTypes |> List.map (fun p -> p.ParameterId, p.Type) |> Map.ofList
                            { f with Parameters = f.Parameters |> List.map (fun p -> { p with Type = Map.find p.Id types }); ResultType = r.ResultType }
                    let updateCapability (c: SemanticCapability) =
                        let updated = c.Operations |> List.map (fun o ->
                            match repairs.Operations |> List.tryFind (fun r -> r.OperationId = o.Id) with
                            | None -> o
                            | Some r ->
                                let types = r.ParameterTypes |> List.map (fun p -> p.ParameterId, p.Type) |> Map.ofList
                                { o with Parameters = o.Parameters |> List.map (fun p -> { p with Type = Map.find p.Id types }); ResultType = r.ResultType })
                        { c with Operations = updated }
                    let updated =
                        { owned with Types = List.map updateType owned.Types
                                     Functions = List.map updateFunction owned.Functions
                                     Capabilities = List.map updateCapability owned.Capabilities }
                    let candidate = SnapshotCodec.resolveDependency selected updated
                    repairs.Functions
                    |> List.fold (fun state repair ->
                        state |> Result.bind (fun bodies ->
                            let before = owned.Functions |> List.find (fun f -> f.Id = repair.FunctionId)
                            let target = updated.Functions |> List.find (fun f -> f.Id = repair.FunctionId)
                            match bodyObligationError before snapshot with
                            | Some error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
                            | None ->
                                AgentExpressionStructuralWorkflow.materializeReplacementBody candidate target repair.Replacement
                                |> Result.mapError (fun error -> AgentStructuralWorkflowError.SimulationRejected [ error ])
                                |> Result.map (fun body -> Map.add target.Id body bodies))) (Ok Map.empty)
                    |> Result.map (fun bodies ->
                        let functions = candidate.Functions |> List.map (fun f ->
                            match Map.tryFind f.Id bodies with
                            | Some body -> { f with Body = body }
                            | None -> f)
                        { candidate with Functions = functions })
        simulateStructural workspace prepare (fun repaired identity ->
            { repaired with Id = identity.Id; ParentId = identity.ParentId })
        |> Result.map (fun candidate ->
            { Candidate = candidate.ProposedSnapshot
              Review =
                { RequestedRepairs = repairs; Receipt = candidate.Receipt; Readiness = candidate.Readiness
                  ChangedFunctions = candidate.ProposedSnapshot.Functions |> List.filter (fun f -> repairs.Functions |> List.exists (fun r -> r.FunctionId = f.Id))
                  ChangedTypes = candidate.ProposedSnapshot.Types |> List.filter (fun t -> repairs.Fields |> List.exists (fun r -> r.TypeId = t.Id))
                  ChangedCapabilities = candidate.ProposedSnapshot.Capabilities |> List.filter (fun c -> c.Operations |> List.exists (fun o -> repairs.Operations |> List.exists (fun r -> r.OperationId = o.Id))) } })

    let commitWithPolicy workspace proposal policy =
        publishCandidateWithPolicy workspace proposal.Candidate proposal.Review.Readiness proposal.Review.Receipt policy
