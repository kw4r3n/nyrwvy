namespace Nyrwvy.AgentEval.Structural

open Nyrwvy.Core

type StructuralHarnessFixtureBinding =
    { Name: string
      SnapshotId: SnapshotId
      TargetFunctionId: FunctionId
      TargetResultNodeId: GraphNodeId
      CallerFunctionId: FunctionId
      ForeignFunctionId: FunctionId
      UnrelatedFunctionId: FunctionId
      UnrelatedResultNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module FrozenM2StructuralCorpus =
    let corpusId = "m2-agent-corpus-v1"
    let corpusRevision = "efce0cac15565941c11c8af8d1a52e7ecafaa5c6"

    let private stableId<'Kind> text: StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> failwith $"Invalid frozen M2 stable ID '{text}': {error}."

    let private common name snapshotId targetResultNodeId =
        { Name = name
          SnapshotId = stableId<SnapshotTag> snapshotId
          TargetFunctionId = stableId<FunctionTag> "function-m2-eval-target"
          TargetResultNodeId = stableId<GraphNodeTag> targetResultNodeId
          CallerFunctionId = stableId<FunctionTag> "function-m2-eval-caller"
          ForeignFunctionId = stableId<FunctionTag> "function-m2-eval-foreign"
          UnrelatedFunctionId = stableId<FunctionTag> "function-m2-eval-unrelated"
          UnrelatedResultNodeId = stableId<GraphNodeTag> "node-m2-eval-unrelated-result" }

    let ready = common "m2-ready" "snapshot-m2-eval-ready" "node-m2-eval-target-result"
    let openProtected = common "m2-open-protected" "snapshot-m2-eval-open-protected" "node-m2-eval-open-protected-call"
    let resolvedProtected = common "m2-resolved-protected" "snapshot-m2-eval-resolved-protected" "node-m2-eval-resolved-protected-call"

    let fixtureByName = function
        | "m2-ready" -> Ok ready
        | "m2-open-protected" -> Ok openProtected
        | "m2-resolved-protected" -> Ok resolvedProtected
        | value -> Error $"Unknown M2 structural evaluation fixture '{value}'."

    let expectedFixtureForTask = function
        | "M2-E1"
        | "M2-E2"
        | "M2-E3"
        | "M2-E4"
        | "M2-E5"
        | "M2-E6"
        | "M2-E7"
        | "M2-E10" -> Ok "m2-ready"
        | "M2-E8" -> Ok "m2-open-protected"
        | "M2-E9" -> Ok "m2-resolved-protected"
        | value -> Error $"Unknown M2 structural corpus task '{value}'."
