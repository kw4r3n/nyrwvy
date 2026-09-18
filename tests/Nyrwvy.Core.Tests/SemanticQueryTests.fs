namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module SemanticQueryTests =
    let private unwrap result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Unexpected semantic query error: %A" error

    [<Fact>]
    let ``focus view derives callers node types and referenced declarations`` () =
        let fixture = MilestoneZeroGraph.create ()
        let focus = SemanticQuery.focusFunction fixture.Snapshot fixture.ValidateFunctionId |> unwrap

        if focus.SnapshotId <> fixture.Snapshot.Id then
            failwith "Focus view lost snapshot identity."

        if focus.Function.Id <> fixture.ValidateFunctionId then
            failwith "Focus view returned the wrong function."

        let callers = focus.Callers |> List.map (fun site -> site.CallerFunctionId)
        if callers <> [ fixture.MainFunctionId ] then
            failwithf "Unexpected validate_user callers: %A" callers

        if not (List.isEmpty focus.Callees) then
            failwithf "validate_user unexpectedly has direct callees: %A" focus.Callees

        if
            focus.RelatedFunctionSignatures
            |> List.exists (fun signature -> signature.FunctionId = fixture.MainFunctionId)
            |> not
        then
            failwith "Focus view omitted the direct caller signature."

        let nodeIds =
            ValueGraph.collectNodes focus.Function.Body
            |> List.map (fun node -> node.Id)
            |> Set.ofList
        let typedNodeIds = focus.NodeTypes |> List.map (fun item -> item.NodeId) |> Set.ofList
        if nodeIds <> typedNodeIds then
            failwith "Focus node-type projection does not cover the focused graph exactly."

        let typeNames = focus.ReferencedTypes |> List.map (fun item -> item.DisplayName) |> Set.ofList
        for expected in [ "RawUserInput"; "ValidUser"; "ValidationError" ] do
            if not (Set.contains expected typeNames) then
                failwithf "Focused declarations are missing type '%s'." expected

    [<Fact>]
    let ``report focus carries explicit Console effect and capability declarations`` () =
        let fixture = MilestoneZeroGraph.create ()
        let focus = SemanticQuery.focusFunction fixture.Snapshot fixture.ReportFunctionId |> unwrap

        let effectIds = focus.ReferencedEffects |> List.map (fun effect -> effect.Id)
        if effectIds <> [ fixture.ConsoleEffectId ] then
            failwithf "Unexpected report effects: %A" effectIds

        let capabilityIds = focus.ReferencedCapabilities |> List.map (fun capability -> capability.Id)
        if capabilityIds <> [ fixture.ConsoleCapabilityId ] then
            failwithf "Unexpected report capabilities: %A" capabilityIds

        let callers = focus.Callers |> List.map (fun site -> site.CallerFunctionId)
        if callers <> [ fixture.MainFunctionId ] then
            failwithf "Unexpected report callers: %A" callers

    [<Fact>]
    let ``dependency and use queries are inverse for direct value references`` () =
        let fixture = MilestoneZeroGraph.create ()
        let main = fixture.Snapshot.Functions |> List.find (fun item -> item.Id = fixture.MainFunctionId)
        let reportCall =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node ->
                match node.Kind with
                | SemanticNodeKind.DirectCall(functionId, _) -> functionId = fixture.ReportFunctionId
                | _ -> false)

        let dependencies =
            SemanticQuery.getDependencies fixture.Snapshot fixture.MainFunctionId reportCall.Id
            |> unwrap

        if not (List.contains fixture.MainValidateCallNodeId dependencies) then
            failwith "Report call dependency query omitted the validate call result."

        let uses =
            SemanticQuery.getUses fixture.Snapshot fixture.MainFunctionId fixture.MainValidateCallNodeId
            |> unwrap

        if not (List.contains reportCall.Id uses) then
            failwith "Use query omitted the report call that consumes the validation result."

    [<Fact>]
    let ``focus view remains available while an explicit obligation is open`` () =
        let fixture = MilestoneZeroGraph.create ()
        let newParameterId = StableId.createForTesting<ParameterTag> "parameter-query-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-query-policy"
        let transactionId = StableId.createForTesting<TransactionTag> "transaction-query-policy"
        let proposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-query-policy"

        let request: TransactionRequest =
            { Id = transactionId
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = proposedSnapshotId
              Operations =
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = newParameterId
                      NewParameterDisplayName = "policy_for_query"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans =
                        [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let proposed =
            match Transaction.simulate fixture.Snapshot request with
            | Proposed simulation -> simulation.ProposedSnapshot
            | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

        let mainFocus = SemanticQuery.focusFunction proposed fixture.MainFunctionId |> unwrap
        match mainFocus.OpenObligations with
        | [ obligation ] when obligation.ObligationId = obligationId -> ()
        | obligations -> failwithf "Open obligation was not projected into main focus: %A" obligations

        match TypedSemanticIr.project proposed with
        | Error(TypedSemanticIrError.IncompleteSnapshot ids) when List.contains obligationId ids -> ()
        | result -> failwithf "Expected lowering projection to remain blocked, got %A" result

    [<Fact>]
    let ``missing query targets return explicit typed errors`` () =
        let fixture = MilestoneZeroGraph.create ()
        let missingFunctionId = StableId.createForTesting<FunctionTag> "function-query-missing"
        let missingNodeId = StableId.createForTesting<GraphNodeTag> "node-query-missing"

        match SemanticQuery.getFunction fixture.Snapshot missingFunctionId with
        | Error(SemanticQueryError.FunctionNotFound id) when id = missingFunctionId -> ()
        | result -> failwithf "Expected FunctionNotFound, got %A" result

        match SemanticQuery.getDependencies fixture.Snapshot fixture.MainFunctionId missingNodeId with
        | Error(SemanticQueryError.NodeNotFound(functionId, nodeId))
            when functionId = fixture.MainFunctionId && nodeId = missingNodeId -> ()
        | result -> failwithf "Expected NodeNotFound, got %A" result

    [<Fact>]
    let ``focus declaration closure follows transitive field type references`` () =
        let fixture = MilestoneZeroGraph.create ()
        let innerTypeId = StableId.createForTesting<TypeTag> "type-query-inner"
        let outerTypeId = StableId.createForTesting<TypeTag> "type-query-outer"
        let innerFieldId = StableId.createForTesting<FieldTag> "field-query-inner"
        let outerParameterId = StableId.createForTesting<ParameterTag> "parameter-query-outer"

        let innerType: SemanticType =
            { Id = innerTypeId
              DisplayName = "QueryInner"
              Kind = SemanticTypeKind.Product [] }

        let outerType: SemanticType =
            { Id = outerTypeId
              DisplayName = "QueryOuter"
              Kind =
                SemanticTypeKind.Product
                    [ { Id = innerFieldId
                        DisplayName = "inner"
                        Type = TypeRef.Named innerTypeId } ] }

        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.MainFunctionId then
                    functionEntity
                else
                    { functionEntity with
                        Parameters =
                            functionEntity.Parameters
                            @ [ { Id = outerParameterId
                                  DisplayName = "outer"
                                  Type = TypeRef.Named outerTypeId } ] })

        let snapshot =
            { fixture.Snapshot with
                Types = fixture.Snapshot.Types @ [ innerType; outerType ]
                Functions = functions }

        let focus = SemanticQuery.focusFunction snapshot fixture.MainFunctionId |> unwrap
        let typeIds = focus.ReferencedTypes |> List.map (fun item -> item.Id) |> Set.ofList

        if not (Set.contains outerTypeId typeIds && Set.contains innerTypeId typeIds) then
            failwithf "Transitive type closure is incomplete: %A" focus.ReferencedTypes

        if not (List.isEmpty focus.MissingTypeIds) then
            failwithf "Valid declaration closure reported missing types: %A" focus.MissingTypeIds

    [<Fact>]
    let ``focus view exposes unresolved declaration references without guessing`` () =
        let fixture = MilestoneZeroGraph.create ()
        let missingTypeId = StableId.createForTesting<TypeTag> "type-query-unresolved"
        let missingCapabilityId = StableId.createForTesting<CapabilityTag> "capability-query-unresolved"
        let missingEffectId = StableId.createForTesting<EffectTag> "effect-query-unresolved"
        let typeParameterId = StableId.createForTesting<ParameterTag> "parameter-query-unresolved-type"
        let capabilityParameterId = StableId.createForTesting<ParameterTag> "parameter-query-unresolved-capability"

        let functions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.MainFunctionId then
                    functionEntity
                else
                    { functionEntity with
                        Parameters =
                            functionEntity.Parameters
                            @ [ { Id = typeParameterId
                                  DisplayName = "unresolved_type"
                                  Type = TypeRef.Named missingTypeId }
                                { Id = capabilityParameterId
                                  DisplayName = "unresolved_capability"
                                  Type = TypeRef.Capability missingCapabilityId } ]
                        RequiredEffects = functionEntity.RequiredEffects @ [ missingEffectId ] })

        let snapshot = { fixture.Snapshot with Functions = functions }
        let focus = SemanticQuery.focusFunction snapshot fixture.MainFunctionId |> unwrap

        if focus.MissingTypeIds <> [ missingTypeId ] then
            failwithf "Missing type reference was not explicit: %A" focus.MissingTypeIds
        if focus.MissingCapabilityIds <> [ missingCapabilityId ] then
            failwithf "Missing capability reference was not explicit: %A" focus.MissingCapabilityIds
        if focus.MissingEffectIds <> [ missingEffectId ] then
            failwithf "Missing effect reference was not explicit: %A" focus.MissingEffectIds
