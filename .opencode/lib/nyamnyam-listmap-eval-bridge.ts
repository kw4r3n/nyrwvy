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
const evaluationAgent = "nyamnyam-listmap-eval"

export const frozenListMapInputPaths = [
  "evaluation/m2-listmap-agent-corpus-v1",
  "tests/NyamNyam.Core.Tests/M2ListMapStructuralEvaluationCorpus.fs",
  "tests/NyamNyam.Core.Tests/M2ListMapStructuralEvaluationCorpusTests.fs",
  "tools/NyamNyam.EvaluationFixtureExport/Program.fs",
  "tools/NyamNyam.EvaluationFixtureExport/NyamNyam.EvaluationFixtureExport.fsproj",
  "src/NyamNyam.Core/AgentListMapStructuralWorkflow.fs",
  "tools/NyamNyam.AgentEval.ListMapStructural/Protocol.fs",
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
    throw new Error("NyamNyam ListMap evaluation tools are available only to the nyamnyam-listmap-eval agent.")
  }
}

function runtimeContext(context: ListMapToolContext) {
  requireEvaluationAgent(context.agent)
  return { sessionKey: context.sessionID, worktree: context.worktree }
}

function configuredFixture(): Fixture {
  const fixture = process.env.NYAMNYAM_LISTMAP_EVAL_FIXTURE
  if (
    fixture === "m2-listmap-ready" ||
    fixture === "m2-listmap-open-protected" ||
    fixture === "m2-listmap-resolved-protected"
  ) return fixture
  throw new Error("The harness must set NYAMNYAM_LISTMAP_EVAL_FIXTURE to a frozen ListMap fixture name.")
}

function configuredTaskID(): string {
  const taskID = process.env.NYAMNYAM_LISTMAP_EVAL_TASK_ID
  if (taskID && /^M2-L(?:10|[1-9])$/.test(taskID)) return taskID
  throw new Error("The harness must set NYAMNYAM_LISTMAP_EVAL_TASK_ID to M2-L1 through M2-L10.")
}

export function artifactRootDirectory() {
  return process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT ??
    join(process.env.HOME ?? "/tmp", ".local", "share", "nyamnyam", "m2-listmap-evaluation-runs")
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
    ["--model", process.env.NYAMNYAM_LISTMAP_EVAL_MODEL],
    ["--provider-runtime", process.env.NYAMNYAM_LISTMAP_EVAL_PROVIDER_RUNTIME],
    ["--reasoning-config", process.env.NYAMNYAM_LISTMAP_EVAL_REASONING_CONFIG],
    ["--opencode-version", process.env.NYAMNYAM_LISTMAP_EVAL_OPENCODE_VERSION],
    ["--prompt-revision", process.env.NYAMNYAM_LISTMAP_EVAL_PROMPT_REVISION],
    ["--tool-revision", process.env.NYAMNYAM_LISTMAP_EVAL_TOOL_REVISION],
  ])
}

export function stalePerturbationArguments(taskID: string) {
  return taskID === "M2-L10" ? ["--stale-perturbation", "m2-l10"] : []
}

export function frozenInputDiffArguments() {
  return frozenRevisionDiffArguments(expectedCorpusRevision, frozenListMapInputPaths)
}

const runtime = createPersistentEvaluationRuntime({
  component: "nyamnyam-listmap-eval",
  protocolVersion,
  expectedCorpusRevision,
  frozenInputDiffArguments,
  resolveArtifactRoot,
  configuredFixture,
  configuredTaskID,
  runnerProject: "tools/NyamNyam.AgentEval.ListMapStructural/NyamNyam.AgentEval.ListMapStructural.fsproj",
  stalePerturbationArguments,
  metadataArguments,
  validateRequestLine,
  messages: {
    alreadyFinalized: "This ListMap evaluation agent session already finalized its one permitted run.",
    alreadyActiveOrStarting: "A ListMap evaluation session is already active or starting.",
    requestNoActive: "No active ListMap evaluation session. Start one first.",
    finalizeNoActive: "No active NyamNyam ListMap evaluation session.",
    stillStarting: "NyamNyam ListMap evaluation session is still starting.",
    unavailable: "NyamNyam ListMap evaluation session is no longer available.",
    startFailure: "NyamNyam ListMap evaluation session could not be started.",
    requestTimeout: "NyamNyam ListMap evaluation request timed out.",
    runnerFailure: "NyamNyam ListMap evaluation runner failed before responding.",
    finalizationFailed: "NyamNyam ListMap evaluation session finalization failed.",
    finalized: "NyamNyam ListMap evaluation session finalized.",
    started: (revision) =>
      `NyamNyam ListMap evaluation session started for frozen corpus ${revision}. Send describeV1 first.`,
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
