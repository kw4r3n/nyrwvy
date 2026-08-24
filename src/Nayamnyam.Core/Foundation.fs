namespace Nayamnyam.Core

/// The rebuild keeps each semantic fact authoritative in exactly one model location.
/// Direct calls are graph nodes; there is intentionally no parallel function-level
/// call list that could drift away from the body graph.
type SemanticType =
    { Id: TypeId
      DisplayName: string }

type SemanticParameter =
    { Id: ParameterId
      DisplayName: string
      TypeId: TypeId }

type ArgumentBinding =
    { TargetParameterId: ParameterId
      ValueNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
type SemanticNodeKind =
    | ParameterReference of ParameterId
    | DirectCall of FunctionId * ArgumentBinding list

type SemanticNode =
    { Id: GraphNodeId
      Kind: SemanticNodeKind }

type SemanticFunction =
    { Id: FunctionId
      DisplayName: string
      Parameters: SemanticParameter list
      ResultTypeId: TypeId
      Nodes: SemanticNode list
      ResultNodeId: GraphNodeId }

type ObligationResolution =
    { TransactionId: TransactionId
      ValueNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
type ObligationStatus =
    | Open
    | Resolved of ObligationResolution

type MissingArgumentObligation =
    { Id: ObligationId
      OriginTransactionId: TransactionId
      CallNodeId: GraphNodeId
      ParameterId: ParameterId
      Status: ObligationStatus }

type Snapshot =
    { Id: SnapshotId
      ParentId: SnapshotId option
      Types: SemanticType list
      Functions: SemanticFunction list
      Obligations: MissingArgumentObligation list }

[<RequireQualifiedAccess>]
module Snapshot =
    let empty id =
        { Id = id
          ParentId = None
          Types = []
          Functions = []
          Obligations = [] }
