namespace Nayamnyam.Core

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
