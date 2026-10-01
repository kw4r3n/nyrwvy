import { join } from "node:path"
import {
  appendResponseChunk,
  compactArguments,
  createPersistentEvaluationRuntime,
  ensurePrivateDirectory,
  ensurePrivateFile,
  frozenRevisionDiffArguments,
  validateExternalArtifactRoot,
  validateJsonObjectLine,
  type ResponseLineState,
} from "./evaluation-jsonl-runtime"
import type { LiveSession as RuntimeLiveSession } from "./evaluation-jsonl-runtime"

const protocolVersion = "m2-listmap-structural-v1"
const expectedCorpusRevision = "a2ffb5615d37d44bbd910c4f47c36136c31cdce4"
const evaluationAgent = "nyrwvy-listmap-eval"

// Replay only from the freeze tag eval/m2-listmap-orchestrator-v1-20260831 (f7d6d07), see
// docs/operations/evaluation-freeze-tags.md: on main these guarded paths no longer exist, so this copy refuses to start.
export const frozenListMapInputPaths = [
  "evaluation/m2-listmap-agent-corpus-v1",
  "tests/Nyrwvy.Core.Tests/M2ListMapStructuralEvaluationCorpus.fs",
  "tests/Nyrwvy.Core.Tests/M2ListMapStructuralEvaluationCorpusTests.fs",
  "tools/Nyrwvy.EvaluationFixtureExport/Program.fs",
  "tools/Nyrwvy.EvaluationFixtureExport/Nyrwvy.EvaluationFixtureExport.fsproj",
  "src/Nyrwvy.Core/AgentListMapStructuralWorkflow.fs",
  "tools/Nyrwvy.AgentEval.ListMapStructural/Protocol.fs",
] as const

type Fixture = "m2-listmap-ready" | "m2-listmap-open-protected" | "m2-listmap-resolved-protected"

type ListMapToolContext = {
  agent: string
  sessionID: string
  worktree: string
}

export type LiveSession = Omit<RuntimeLiveSession, "sessionKey"> & { sessionID: string }
export type { ResponseLineState }
export { appendResponseChunk, ensurePrivateDirectory, ensurePrivateFile }

export function validateRequestLine(request: string): string {
  return validateJsonObjectLine(request, "The ListMap evaluation request")
}

function requireEvaluationAgent(agent: string) {
  if (agent !== evaluationAgent) {
    throw new Error("Nyrwvy ListMap evaluation tools are available only to the nyrwvy-listmap-eval agent.")
  }
}

function runtimeContext(context: ListMapToolContext) {
  requireEvaluationAgent(context.agent)
  return { sessionKey: context.sessionID, worktree: context.worktree }
}

function configuredFixture(): Fixture {
  const fixture = process.env.NYRWVY_LISTMAP_EVAL_FIXTURE
  if (
    fixture === "m2-listmap-ready" ||
    fixture === "m2-listmap-open-protected" ||
    fixture === "m2-listmap-resolved-protected"
  ) return fixture
  throw new Error("The harness must set NYRWVY_LISTMAP_EVAL_FIXTURE to a frozen ListMap fixture name.")
}

function configuredTaskID(): string {
  const taskID = process.env.NYRWVY_LISTMAP_EVAL_TASK_ID
  if (taskID && /^M2-L(?:10|[1-9])$/.test(taskID)) return taskID
  throw new Error("The harness must set NYRWVY_LISTMAP_EVAL_TASK_ID to M2-L1 through M2-L10.")
}

export function artifactRootDirectory() {
  return process.env.NYRWVY_LISTMAP_EVAL_ARTIFACT_ROOT ??
    join(process.env.HOME ?? "/tmp", ".local", "share", "nyrwvy", "m2-listmap-evaluation-runs")
}

export function validateArtifactRoot(root: string, worktree: string): string {
  return validateExternalArtifactRoot(root, worktree, {
    rootMustBeAbsolute: "The ListMap evaluation artifact root must be an absolute path.",
    worktreeMustBeAbsolute: "The ListMap evaluation worktree must be an absolute path.",
    rootMustBeOutsideWorktree: "The ListMap evaluation artifact root must be outside the evaluation worktree.",
  })
}

export function resolveArtifactRoot(worktree: string): string {
  return validateArtifactRoot(artifactRootDirectory(), worktree)
}

export function metadataArguments() {
  return compactArguments([
    ["--model", process.env.NYRWVY_LISTMAP_EVAL_MODEL],
    ["--provider-runtime", process.env.NYRWVY_LISTMAP_EVAL_PROVIDER_RUNTIME],
    ["--reasoning-config", process.env.NYRWVY_LISTMAP_EVAL_REASONING_CONFIG],
    ["--opencode-version", process.env.NYRWVY_LISTMAP_EVAL_OPENCODE_VERSION],
    ["--prompt-revision", process.env.NYRWVY_LISTMAP_EVAL_PROMPT_REVISION],
    ["--tool-revision", process.env.NYRWVY_LISTMAP_EVAL_TOOL_REVISION],
  ])
}

export function stalePerturbationArguments(taskID: string) {
  return taskID === "M2-L10" ? ["--stale-perturbation", "m2-l10"] : []
}

export function frozenInputDiffArguments() {
  return frozenRevisionDiffArguments(expectedCorpusRevision, frozenListMapInputPaths)
}

const runtime = createPersistentEvaluationRuntime({
  component: "nyrwvy-listmap-eval",
  protocolVersion,
  expectedCorpusRevision,
  frozenInputDiffArguments,
  resolveArtifactRoot,
  configuredFixture,
  configuredTaskID,
  runnerProject: "tools/Nyrwvy.AgentEval.ListMapStructural/Nyrwvy.AgentEval.ListMapStructural.fsproj",
  stalePerturbationArguments,
  metadataArguments,
  validateRequestLine,
  messages: {
    alreadyFinalized: "This ListMap evaluation agent session already finalized its one permitted run.",
    alreadyActiveOrStarting: "A ListMap evaluation session is already active or starting.",
    requestNoActive: "No active ListMap evaluation session. Start one first.",
    finalizeNoActive: "No active Nyrwvy ListMap evaluation session.",
    stillStarting: "Nyrwvy ListMap evaluation session is still starting.",
    unavailable: "Nyrwvy ListMap evaluation session is no longer available.",
    startFailure: "Nyrwvy ListMap evaluation session could not be started.",
    requestTimeout: "Nyrwvy ListMap evaluation request timed out.",
    runnerFailure: "Nyrwvy ListMap evaluation runner failed before responding.",
    finalizationFailed: "Nyrwvy ListMap evaluation session finalization failed.",
    finalized: "Nyrwvy ListMap evaluation session finalized.",
    started: (revision) =>
      `Nyrwvy ListMap evaluation session started for frozen corpus ${revision}. Send describeV1 first.`,
  },
})

export const reportHarnessDiagnostic = runtime.reportHarnessDiagnostic

export async function startListMapEvaluation(context: ListMapToolContext): Promise<string> {
  return await runtime.start(runtimeContext(context))
}

export async function requestListMapEvaluation(
  requestText: string,
  context: ListMapToolContext,
): Promise<string> {
  return await runtime.request(requestText, runtimeContext(context))
}

export async function finalizeListMapEvaluation(context: ListMapToolContext): Promise<string> {
  return await runtime.finalize(runtimeContext(context))
}
