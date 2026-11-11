namespace Nyrwvy.Core.Tests

open Nyrwvy.Core

/// Literal snapshot edits that tests used to perform through the transaction engine:
/// adding a parameter whose call site stays missing (an open obligation), and resolving
/// an open obligation by binding a value at its call site.
module TestSnapshotEdits =
    let addParameterWithOpenObligation
        (snapshot: Snapshot) (newSnapshotId: SnapshotId) (transactionId: TransactionId)
        (targetFunctionId: FunctionId) (parameterId: ParameterId) (displayName: string)
        (parameterType: TypeRef) (callNodeId: GraphNodeId) (obligationId: ObligationId) : Snapshot =
        { snapshot with
            Id = newSnapshotId
            ParentId = Some snapshot.Id
            Functions =
                snapshot.Functions |> List.map (fun fn ->
                    if fn.Id = targetFunctionId then
                        { fn with Parameters = fn.Parameters @ [ { Id = parameterId; DisplayName = displayName; Type = parameterType } ] }
                    else fn)
            Obligations =
                snapshot.Obligations
                @ [ { Id = obligationId
                      OriginTransactionId = transactionId
                      CallNodeId = callNodeId
                      ParameterId = parameterId
                      Status = ObligationStatus.Open } ] }

    let resolveOpenObligation
        (snapshot: Snapshot) (newSnapshotId: SnapshotId) (transactionId: TransactionId)
        (obligationId: ObligationId) (valueNodeId: GraphNodeId) : Snapshot =
        let obligation = snapshot.Obligations |> List.find (fun item -> item.Id = obligationId)
        let bind (node: SemanticNode) =
            match node.Kind with
            | SemanticNodeKind.DirectCall(targetId, arguments) when node.Id = obligation.CallNodeId ->
                { node with
                    Kind =
                        SemanticNodeKind.DirectCall(
                            targetId,
                            arguments @ [ { TargetParameterId = obligation.ParameterId; ValueNodeId = valueNodeId } ]) }
            | _ -> node
        { snapshot with
            Id = newSnapshotId
            ParentId = Some snapshot.Id
            Functions = snapshot.Functions |> List.map (fun fn -> { fn with Body = ValueGraph.mapNodes bind fn.Body })
            Obligations =
                snapshot.Obligations |> List.map (fun item ->
                    if item.Id = obligationId then
                        { item with Status = ObligationStatus.Resolved { TransactionId = transactionId; ValueNodeId = valueNodeId } }
                    else item) }
