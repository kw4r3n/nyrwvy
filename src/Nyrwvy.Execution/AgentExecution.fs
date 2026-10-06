namespace Nyrwvy.Core

/// Trusted downstream projection from the opaque agent workspace into the
/// backend-neutral execution pipeline. The raw authoritative Snapshot remains
/// internal to the Semantics -> Execution assembly boundary.
[<RequireQualifiedAccess>]
module AgentExecution =
    let projectTypedSemanticIr
        (workspace: AgentWorkspace)
        : Result<TypedSemanticProgram, TypedSemanticIrError> =
        AgentWorkspace.snapshotForExecution workspace
        |> TypedSemanticIr.project

    /// Project one entry without exposing the workspace's authoritative snapshot.
    /// Selection precedes readiness and lowering; all declarations remain retained.
    let projectEntryTypedSemanticIr (workspace: AgentWorkspace) entry =
        AgentWorkspace.snapshotForExecution workspace
        |> fun snapshot -> ExecutionCallGraph.forSnapshotEntry snapshot entry
        |> TypedSemanticIr.project
