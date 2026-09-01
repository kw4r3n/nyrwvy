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

const protocolVersion = "m2-structural-v1"
const expectedCorpusRevision = "efce0cac15565941c11c8af8d1a52e7ecafaa5c6"
// The structural corpus/fixture source remained unchanged when the shared fixture
// exporter gained ListMap support. Pin the full guarded path set to that later
// compatible baseline without reinterpreting the historical structural corpus SHA.
const frozenInputCompatibilityRevision = "a2ffb5615d37d44bbd910c4f47c36136c31cdce4"
const evaluationAgent = "nyamnyam-structural-eval"

export const frozenStructuralInputPaths = [
  "evaluation/m2-agent-corpus-v1",
  "tests/NyamNyam.Core.Tests/M2StructuralEvaluationCorpus.fs",
  "tools/NyamNyam.EvaluationFixtureExport/Program.fs",
  "tools/NyamNyam.EvaluationFixtureExport/NyamNyam.EvaluationFixtureExport.fsproj",
] as const

type Fixture = "m2-ready" | "m2-open-protected" | "m2-resolved-protected"

type StructuralToolContext = {
  agent: string
  sessionID: string
  worktree: string
}

export type LiveSession = Omit<RuntimeLiveSession, "sessionKey"> & { sessionID: string }
export type { ResponseLineState }
export { appendResponseChunk, ensurePrivateDirectory, ensurePrivateFile }

export function validateRequestLine(request: string): string {
  return validateJsonObjectLine(request, "The structural evaluation request")
}

function requireEvaluationAgent(agent: string) {
  if (agent !== evaluationAgent) {
    throw new Error("NyamNyam structural evaluation tools are available only to the nyamnyam-structural-eval agent.")
  }
}

function runtimeContext(context: StructuralToolContext) {
  requireEvaluationAgent(context.agent)
  return { sessionKey: context.sessionID, worktree: context.worktree }
}

function configuredFixture(): Fixture {
  const fixture = process.env.NYAMNYAM_STRUCTURAL_EVAL_FIXTURE
  if (fixture === "m2-ready" || fixture === "m2-open-protected" || fixture === "m2-resolved-protected") return fixture
  throw new Error("The harness must set NYAMNYAM_STRUCTURAL_EVAL_FIXTURE to a frozen M2 fixture name.")
}

function configuredTaskID(): string {
  const taskID = process.env.NYAMNYAM_STRUCTURAL_EVAL_TASK_ID
  if (taskID && /^M2-E(?:10|[1-9])$/.test(taskID)) return taskID
  throw new Error("The harness must set NYAMNYAM_STRUCTURAL_EVAL_TASK_ID to M2-E1 through M2-E10.")
}

export function artifactRootDirectory() {
  return process.env.NYAMNYAM_STRUCTURAL_EVAL_ARTIFACT_ROOT ??
    join(process.env.HOME ?? "/tmp", ".local", "share", "nyamnyam", "m2-evaluation-runs")
}

export function validateArtifactRoot(root: string, worktree: string): string {
  return validateExternalArtifactRoot(root, worktree, {
    rootMustBeAbsolute: "The structural evaluation artifact root must be an absolute path.",
    worktreeMustBeAbsolute: "The structural evaluation worktree must be an absolute path.",
    rootMustBeOutsideWorktree: "The structural evaluation artifact root must be outside the evaluation worktree.",
  })
}

export function resolveArtifactRoot(worktree: string): string {
  return validateArtifactRoot(artifactRootDirectory(), worktree)
}

export function metadataArguments() {
  return compactArguments([
    ["--model", process.env.NYAMNYAM_STRUCTURAL_EVAL_MODEL],
    ["--provider-runtime", process.env.NYAMNYAM_STRUCTURAL_EVAL_PROVIDER_RUNTIME],
    ["--reasoning-config", process.env.NYAMNYAM_STRUCTURAL_EVAL_REASONING_CONFIG],
    ["--opencode-version", process.env.NYAMNYAM_STRUCTURAL_EVAL_OPENCODE_VERSION],
    ["--prompt-revision", process.env.NYAMNYAM_STRUCTURAL_EVAL_PROMPT_REVISION],
    ["--tool-revision", process.env.NYAMNYAM_STRUCTURAL_EVAL_TOOL_REVISION],
  ])
}

export function stalePerturbationArguments(taskID: string) {
  return taskID === "M2-E10" ? ["--stale-perturbation", "m2-e10"] : []
}

export function frozenInputDiffArguments() {
  return frozenRevisionDiffArguments(frozenInputCompatibilityRevision, frozenStructuralInputPaths)
}

const runtime = createPersistentEvaluationRuntime({
  component: "nyamnyam-structural-eval",
  protocolVersion,
  expectedCorpusRevision,
  frozenInputDiffArguments,
  resolveArtifactRoot,
  configuredFixture,
  configuredTaskID,
  runnerProject: "tools/NyamNyam.AgentEval.Structural/NyamNyam.AgentEval.Structural.fsproj",
  stalePerturbationArguments,
  metadataArguments,
  validateRequestLine,
  messages: {
    alreadyFinalized: "This structural evaluation agent session already finalized its one permitted run.",
    alreadyActiveOrStarting: "A structural evaluation session is already active or starting.",
    requestNoActive: "No active structural evaluation session. Start one first.",
    finalizeNoActive: "No active NyamNyam structural evaluation session.",
    stillStarting: "NyamNyam structural evaluation session is still starting.",
    unavailable: "NyamNyam structural evaluation session is no longer available.",
    startFailure: "NyamNyam structural evaluation session could not be started.",
    requestTimeout: "NyamNyam structural evaluation request timed out.",
    runnerFailure: "NyamNyam structural evaluation runner failed before responding.",
    finalizationFailed: "NyamNyam structural evaluation session finalization failed.",
    finalized: "NyamNyam structural evaluation session finalized.",
    started: (revision) =>
      `NyamNyam structural evaluation session started for the frozen ${revision} corpus. Send describeV1 first.`,
  },
})

export const reportHarnessDiagnostic = runtime.reportHarnessDiagnostic

export async function startStructuralEvaluation(context: StructuralToolContext): Promise<string> {
  return await runtime.start(runtimeContext(context))
}

export async function requestStructuralEvaluation(
  requestText: string,
  context: StructuralToolContext,
): Promise<string> {
  return await runtime.request(requestText, runtimeContext(context))
}

export async function finalizeStructuralEvaluation(context: StructuralToolContext): Promise<string> {
  return await runtime.finalize(runtimeContext(context))
}
