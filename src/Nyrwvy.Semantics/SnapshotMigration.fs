namespace Nyrwvy.Core

open System.Collections.Generic

/// Why a snapshot cannot be migrated to the current schema.
[<RequireQualifiedAccess>]
type SnapshotMigrationError =
    /// A capability operation sets the retired `FailureType`, which has no meaning to preserve
    /// (docs/decisions/capability-failure-type-retired.md); each entry is the capability and
    /// operation display name. The snapshot is left as it is.
    | RetiredFailureType of operations: (string * string) list

/// What one migration did to one decoded snapshot. Every stable ID of the source is kept; the
/// only new identities are the inner nodes of two-node replacements, derived deterministically
/// from the node they replace (`SnapshotMigration.innerNodeId`).
type SnapshotMigrationOutcome =
    { Snapshot: Snapshot
      /// The schema version the source document declared.
      SourceSchema: SnapshotSchemaVersion
      /// `SnapshotSchema.current`.
      TargetSchema: SnapshotSchemaVersion
      /// Retired node kinds rewritten, by recipe kind name in `SnapshotMigration.retiredKinds`
      /// order, with the number of nodes; kinds the snapshot does not use are left out.
      Rewrites: (string * int) list
      /// Declarations whose visibility becomes an explicit `exported` marker: every declaration of
      /// a document below schema 21, which read as exported. Zero for a document at 21 or later,
      /// whose visibility is already explicit and is kept.
      DeclarationsMarkedExported: int
      /// Generic functions whose derived type parameter list is written as a declared list: every
      /// generic function of a document below schema 21. Zero at 21 or later.
      FunctionTypeParameterListsDeclared: int }

/// The migration of a decoded snapshot to the current schema: the rewrite step of
/// `Nyrwvy.AuthoringSession migrate` ([legacy cutover](docs/plans/post-m2-roadmap.md)). It is a
/// privileged infrastructure boundary under docs/specs/schema-evolution-policy.md: it preserves
/// every existing stable ID, invents no semantic decision, and leaves validation and fingerprint
/// evidence to its caller, which also owns the dependency sidecar and the file replacement.
[<RequireQualifiedAccess>]
module SnapshotMigration =
    /// The retired recipe kinds in the order the report lists them, each with its replacement
    /// ([decision](docs/decisions/retired-authoring-kinds.md)).
    let retiredKinds =
        [ "stringEqual", "valueEqual"
          "int64Equal", "valueEqual"
          "mapGetOrDefault", "optionGetOrDefault of mapGet"
          "int64GreaterThan", "int64LessThan with swapped operands"
          "int64LessThanOrEqual", "boolNot of int64LessThan with swapped operands"
          "int64GreaterThanOrEqual", "boolNot of int64LessThan" ]

    /// The identity of the inner node a two-node replacement introduces (the `MapGet` under an
    /// `OptionGetOrDefault`, the `Int64LessThan` under a `BoolNot`): derived from the replaced
    /// node's identity, so that migrating the same document twice derives the same ID and the
    /// result is reproducible. The replaced node keeps its own ID on the outer replacement node,
    /// so every existing reference to it still names the same value.
    let innerNodeId (replaced: GraphNodeId): GraphNodeId =
        StableIdAllocator.derived<GraphNodeTag> ("nyrwvy.snapshot-migration/inner-node/" + StableId.value replaced)

    /// The retired `FailureType` has no faithful rewrite; a snapshot that sets it is rejected.
    let retiredFailureTypes (snapshot: Snapshot): (string * string) list =
        [ for capability in snapshot.Capabilities do
              for operation in capability.Operations do
                  if Option.isSome operation.FailureType then yield capability.DisplayName, operation.DisplayName ]

    let private rewriteNode (counts: Dictionary<string, int>) (node: SemanticNode): SemanticNode list =
        let count kind =
            counts[kind] <- (match counts.TryGetValue kind with | true, n -> n + 1 | _ -> 1)
        let twoNodes kind (inner: SemanticNodeKind) (outer: GraphNodeId -> SemanticNodeKind) =
            count kind
            let innerId = innerNodeId node.Id
            [ { Id = innerId; Kind = inner }; { node with Kind = outer innerId } ]
        match node.Kind with
        | SemanticNodeKind.StringEqual(left, right) ->
            count "stringEqual"
            [ { node with Kind = SemanticNodeKind.ValueEqual(left, right) } ]
        | SemanticNodeKind.Int64Equal(left, right) ->
            count "int64Equal"
            [ { node with Kind = SemanticNodeKind.ValueEqual(left, right) } ]
        | SemanticNodeKind.Int64GreaterThan(left, right) ->
            count "int64GreaterThan"
            [ { node with Kind = SemanticNodeKind.Int64LessThan(right, left) } ]
        | SemanticNodeKind.MapGetOrDefault(map, key, defaultValue) ->
            twoNodes "mapGetOrDefault" (SemanticNodeKind.MapGet(map, key)) (fun inner -> SemanticNodeKind.OptionGetOrDefault(inner, defaultValue))
        | SemanticNodeKind.Int64LessThanOrEqual(left, right) ->
            twoNodes "int64LessThanOrEqual" (SemanticNodeKind.Int64LessThan(right, left)) SemanticNodeKind.BoolNot
        | SemanticNodeKind.Int64GreaterThanOrEqual(left, right) ->
            twoNodes "int64GreaterThanOrEqual" (SemanticNodeKind.Int64LessThan(left, right)) SemanticNodeKind.BoolNot
        | _ -> [ node ]

    let rec private rewriteGraph (counts: Dictionary<string, int>) (graph: ValueGraph): ValueGraph =
        { graph with
            Nodes =
                graph.Nodes
                |> List.collect (fun node -> rewriteNode counts (SemanticNodeShape.mapNestedGraphs (rewriteGraph counts) node)) }

    /// Rewrites every retired node of every function body to its replacement, keeping the
    /// rewritten node's ID on the node that yields the same value. Returns the snapshot and the
    /// rewritten kinds with their counts.
    let rewriteRetiredNodes (snapshot: Snapshot): Snapshot * (string * int) list =
        let counts = Dictionary<string, int>()
        let functions =
            snapshot.Functions
            |> List.map (fun fn -> { fn with Body = rewriteGraph counts fn.Body })
        let rewrites =
            retiredKinds |> List.choose (fun (kind, _) ->
                match counts.TryGetValue kind with
                | true, n -> Some(kind, n)
                | _ -> None)
        { snapshot with Functions = functions }, rewrites

    /// Migrates a snapshot decoded from a document that declared `declared`. A document below
    /// schema 21 read every declaration as exported and derived every function's type parameter
    /// list, so marking them is the same semantic state made explicit; a document at 21 or later
    /// keeps the visibility and lists it declares. Retired nodes are rewritten in both cases.
    let migrate (declared: SnapshotSchemaVersion) (snapshot: Snapshot): Result<SnapshotMigrationOutcome, SnapshotMigrationError> =
        match retiredFailureTypes snapshot with
        | _ :: _ as operations -> Error(SnapshotMigrationError.RetiredFailureType operations)
        | [] ->
            let legacy = declared.Json < SnapshotSchema.declaredFunctionTypeParameters.Json
            let exported, declarationsMarked =
                if not legacy then snapshot, 0
                else
                    { snapshot with
                        Types = snapshot.Types |> List.map (fun item -> { item with Exported = true })
                        Effects = snapshot.Effects |> List.map (fun item -> { item with Exported = true })
                        Capabilities = snapshot.Capabilities |> List.map (fun item -> { item with Exported = true })
                        Functions = snapshot.Functions |> List.map (fun item -> { item with Exported = true }) },
                    snapshot.Types.Length + snapshot.Effects.Length + snapshot.Capabilities.Length + snapshot.Functions.Length
            let typeParameterLists =
                if not legacy then 0
                else snapshot.Functions |> List.filter (fun fn -> not (List.isEmpty fn.TypeParameters)) |> List.length
            let rewritten, rewrites = rewriteRetiredNodes exported
            Ok { Snapshot = rewritten
                 SourceSchema = declared
                 TargetSchema = SnapshotSchema.current
                 Rewrites = rewrites
                 DeclarationsMarkedExported = declarationsMarked
                 FunctionTypeParameterListsDeclared = typeParameterLists }

    /// Whether the migration changed nothing but representation, so that semantic fingerprint
    /// equality is an asserted invariant (docs/specs/schema-evolution-policy.md, migration
    /// fingerprints and evidence). A rewritten retired node changes the graph, and the semantic
    /// fingerprint with it.
    let representationOnly (outcome: SnapshotMigrationOutcome) = List.isEmpty outcome.Rewrites
