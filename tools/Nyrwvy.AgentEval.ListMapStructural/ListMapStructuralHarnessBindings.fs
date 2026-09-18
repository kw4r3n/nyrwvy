namespace Nyrwvy.AgentEval.ListMapStructural

open Nyrwvy.Core

type ListMapStructuralHarnessFixtureBinding =
    { Name: string
      SnapshotId: SnapshotId
      TargetFunctionId: FunctionId
      TargetResultNodeId: GraphNodeId
      SourceListParameterId: ParameterId
      NonListParameterId: ParameterId
      CompatibleMapperFunctionId: FunctionId
      WrongInputMapperFunctionId: FunctionId
      WrongResultMapperFunctionId: FunctionId
      EffectfulMapperFunctionId: FunctionId
      MissingMapperFunctionId: FunctionId
      CallerFunctionId: FunctionId
      ForeignFunctionId: FunctionId
      ForeignListParameterId: ParameterId
      UnrelatedFunctionId: FunctionId
      UnrelatedResultNodeId: GraphNodeId }

type ListMapStructuralHarnessTaskBinding =
    { Id: string
      FixtureName: string
      ExpectedDiagnostic: string option
      ExpectedCommit: bool
      EnableStalePerturbation: bool }

[<RequireQualifiedAccess>]
module FrozenM2ListMapCorpus =
    let corpusId = "m2-listmap-agent-corpus-v1"
    let corpusRevision = "a2ffb5615d37d44bbd910c4f47c36136c31cdce4"
    let promptRevision = "m2-listmap-corpus-v1-seed-v1"

    let private stableId<'Kind> (text: string): StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> failwith $"Invalid frozen M2 ListMap stable ID '{text}': {error}."

    let private common (name: string) (snapshotId: string) (targetResultNodeId: string) =
        { Name = name
          SnapshotId = stableId<SnapshotTag> snapshotId
          TargetFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-target"
          TargetResultNodeId = stableId<GraphNodeTag> targetResultNodeId
          SourceListParameterId = stableId<ParameterTag> "parameter-m2-listmap-eval-target-values"
          NonListParameterId = stableId<ParameterTag> "parameter-m2-listmap-eval-target-count"
          CompatibleMapperFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-positive"
          WrongInputMapperFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-string-to-bool"
          WrongResultMapperFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-int-identity"
          EffectfulMapperFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-effectful-positive"
          MissingMapperFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-missing-mapper"
          CallerFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-caller"
          ForeignFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-foreign"
          ForeignListParameterId = stableId<ParameterTag> "parameter-m2-listmap-eval-foreign-values"
          UnrelatedFunctionId = stableId<FunctionTag> "function-m2-listmap-eval-unrelated"
          UnrelatedResultNodeId = stableId<GraphNodeTag> "node-m2-listmap-eval-unrelated-result" }

    let ready =
        common
            "m2-listmap-ready"
            "snapshot-m2-listmap-eval-ready"
            "node-m2-listmap-eval-target-result"

    let openProtected =
        common
            "m2-listmap-open-protected"
            "snapshot-m2-listmap-eval-open-protected"
            "node-m2-listmap-eval-open-protected-call"

    let resolvedProtected =
        common
            "m2-listmap-resolved-protected"
            "snapshot-m2-listmap-eval-resolved-protected"
            "node-m2-listmap-eval-resolved-protected-call"

    let fixtureByName = function
        | "m2-listmap-ready" -> Ok ready
        | "m2-listmap-open-protected" -> Ok openProtected
        | "m2-listmap-resolved-protected" -> Ok resolvedProtected
        | value -> Error $"Unknown M2 ListMap evaluation fixture '{value}'."

    let private task id fixture expectedDiagnostic expectedCommit stale =
        { Id = id
          FixtureName = fixture
          ExpectedDiagnostic = expectedDiagnostic
          ExpectedCommit = expectedCommit
          EnableStalePerturbation = stale }

    let tasks =
        [ task "M2-L1" ready.Name None true false
          task "M2-L2" ready.Name (Some "StructuralListSourceParameterOutOfScope") false false
          task "M2-L3" ready.Name (Some "StructuralListSourceTypeMismatch") false false
          task "M2-L4" ready.Name (Some "StructuralListMapperNotFound") false false
          task "M2-L5" ready.Name (Some "StructuralListMapperInputTypeMismatch") false false
          task "M2-L6" ready.Name (Some "StructuralListMapperResultTypeMismatch") false false
          task "M2-L7" ready.Name (Some "StructuralListMapperEffectsNotAllowed") false false
          task "M2-L8" openProtected.Name (Some "StructuralBodyReferencedByObligation") false false
          task "M2-L9" resolvedProtected.Name (Some "StructuralBodyReferencedByObligation") false false
          task "M2-L10" ready.Name (Some "StaleBaseSnapshot") true true ]

    let taskById (taskId: string): Result<ListMapStructuralHarnessTaskBinding, string> =
        tasks
        |> List.tryFind (fun task -> task.Id = taskId)
        |> function
            | Some value -> Ok value
            | None -> Error $"Unknown M2 ListMap corpus task '{taskId}'."
