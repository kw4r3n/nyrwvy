namespace Nayamnyam.Core

/// The first rebuild slice intentionally keeps the semantic model tiny.
/// New semantic facts should be added only after their single authoritative
/// representation and validation invariant are explicit.
type SemanticType =
    { Id: TypeId
      DisplayName: string }

type SemanticFunction =
    { Id: FunctionId
      DisplayName: string }

type Snapshot =
    { Id: SnapshotId
      ParentId: SnapshotId option
      Types: SemanticType list
      Functions: SemanticFunction list }

[<RequireQualifiedAccess>]
module Snapshot =
    let empty id =
        { Id = id
          ParentId = None
          Types = []
          Functions = [] }
