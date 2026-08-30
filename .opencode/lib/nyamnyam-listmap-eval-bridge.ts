import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { chmod, mkdir, rm } from "node:fs/promises"
import { isAbsolute, join, relative, resolve, sep } from "node:path"

const requestTimeoutMs = 120_000
const childShutdownTimeoutMs = 5_000
const protocolVersion = "m2-listmap-structural-v1"
const expectedCorpusRevision = "a2ffb5615d37d44bbd910c4f47c36136c31cdce4"
const evaluationAgent = "nyamnyam-listmap-eval"

const sessions = new Map<string, LiveSession>()
const startingSessions = new Set<string>()
const finalizedSessions = new Set<string>()
const finalizationFailures = new Set<string>()

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

type ResponseWaiter = {
  resolve: (line: string) => void
  reject: (error: Error) => void
  timeout?: ReturnType<typeof setTimeout>
}

export type LiveSession = {
  sessionID: string
  child: ChildProcessWithoutNullStreams
  artifactDirectory: string
  stdoutBuffer: string
  completedLines: string[]
  stderr: string
  waiters: ResponseWaiter[]
  operationTail: Promise<void>
  closed: boolean
  bridgeError?: Error
}

function errorDetails(error: unknown): string {
  if (error instanceof Error) return error.stack ?? error.message
  return String(error)
}

export function reportHarnessDiagnostic(event: string, details: Record<string, unknown> = {}) {
  console.error(`[nyamnyam-listmap-eval] ${JSON.stringify({ component: "nyamnyam-listmap-eval", event, ...details })}`)
}

export type ResponseLineState = {
  buffered: string
  completedLines: string[]
}

export function appendResponseChunk(state: ResponseLineState, chunk: string): ResponseLineState {
  let buffered = state.buffered + chunk
  const completedLines = [...state.completedLines]
  while (true) {
    const newline = buffered.indexOf("\n")
    if (newline < 0) break
    completedLines.push(buffered.slice(0, newline))
    buffered = buffered.slice(newline + 1)
  }
  return { buffered, completedLines }
}

export function validateRequestLine(request: string): string {
  if (!request.trim()) throw new Error("The ListMap evaluation request must not be blank.")
  if (request.includes("\n") || request.includes("\r")) {
    throw new Error("The ListMap evaluation request must contain exactly one JSON object on one line.")
  }

  let value: unknown
  try {
    value = JSON.parse(request)
  } catch {
    throw new Error("The ListMap evaluation request must contain exactly one valid JSON object.")
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("The ListMap evaluation request must contain exactly one JSON object.")
  }
  return request
}

function requireEvaluationAgent(agent: string) {
  if (agent !== evaluationAgent) {
    throw new Error("NyamNyam ListMap evaluation tools are available only to the nyamnyam-listmap-eval agent.")
  }
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

function runDirectory(sessionID: string, artifactRoot: string) {
  const safeSessionID = sessionID.replace(/[^A-Za-z0-9_-]/g, "_")
  return join(artifactRoot, `${safeSessionID}-${Date.now()}-${randomUUID()}`)
}

export function artifactRootDirectory() {
  return process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT ??
    join(process.env.HOME ?? "/tmp", ".local", "share", "nyamnyam", "m2-listmap-evaluation-runs")
}

export function validateArtifactRoot(root: string, worktree: string): string {
  if (!isAbsolute(root)) throw new Error("The ListMap evaluation artifact root must be an absolute path.")
  if (!isAbsolute(worktree)) throw new Error("The ListMap evaluation worktree must be an absolute path.")

  const absoluteRoot = resolve(root)
  const absoluteWorktree = resolve(worktree)
  const relativeRoot = relative(absoluteWorktree, absoluteRoot)
  const isContained =
    relativeRoot === "" ||
    (relativeRoot !== ".." && !relativeRoot.startsWith(`..${sep}`) && !isAbsolute(relativeRoot))
  if (isContained) throw new Error("The ListMap evaluation artifact root must be outside the evaluation worktree.")
  return absoluteRoot
}

export function resolveArtifactRoot(worktree: string): string {
  return validateArtifactRoot(artifactRootDirectory(), worktree)
}

export async function ensurePrivateDirectory(path: string) {
  await mkdir(path, { recursive: true, mode: 0o700 })
  if (process.platform !== "win32") await chmod(path, 0o700)
}

export async function ensurePrivateFile(path: string) {
  if (process.platform !== "win32") await chmod(path, 0o600)
}

export function metadataArguments() {
  const metadata = [
    ["--model", process.env.NYAMNYAM_LISTMAP_EVAL_MODEL],
    ["--provider-runtime", process.env.NYAMNYAM_LISTMAP_EVAL_PROVIDER_RUNTIME],
    ["--reasoning-config", process.env.NYAMNYAM_LISTMAP_EVAL_REASONING_CONFIG],
    ["--opencode-version", process.env.NYAMNYAM_LISTMAP_EVAL_OPENCODE_VERSION],
    ["--prompt-revision", process.env.NYAMNYAM_LISTMAP_EVAL_PROMPT_REVISION],
    ["--tool-revision", process.env.NYAMNYAM_LISTMAP_EVAL_TOOL_REVISION],
  ] as const
  return metadata.flatMap(([option, value]) => (value ? [option, value] : []))
}

export function stalePerturbationArguments(taskID: string) {
  return taskID === "M2-L10" ? ["--stale-perturbation", "m2-l10"] : []
}

export function frozenInputDiffArguments() {
  return ["diff", "--exit-code", expectedCorpusRevision, "--", ...frozenListMapInputPaths]
}

function flushLines(session: LiveSession) {
  while (session.completedLines.length > 0 && session.waiters.length > 0) {
    const line = session.completedLines.shift()!
    const waiter = session.waiters.shift()!
    clearTimeout(waiter.timeout)
    waiter.resolve(line)
  }
}

function rejectWaiters(session: LiveSession, error: Error) {
  for (const waiter of session.waiters.splice(0)) {
    clearTimeout(waiter.timeout)
    waiter.reject(error)
  }
}

async function waitForExit(child: ChildProcessWithoutNullStreams): Promise<number | null> {
  if (child.exitCode !== null || child.signalCode !== null) return child.exitCode
  return await new Promise<number | null>((resolveExit) => {
    let timer: ReturnType<typeof setTimeout> | undefined
    let settled = false
    const finish = (code: number | null) => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      child.removeListener("exit", onExit)
      resolveExit(code)
    }
    const onExit = (code: number | null) => finish(code)
    child.once("exit", onExit)
    if (child.exitCode !== null) return finish(child.exitCode)
    timer = setTimeout(() => finish(child.exitCode), childShutdownTimeoutMs)
    timer.unref?.()
  })
}

async function stopChild(child: ChildProcessWithoutNullStreams) {
  if (child.exitCode !== null || child.signalCode !== null) return
  try { child.stdin.destroy() } catch {}
  try { child.kill() } catch {}
  if ((await waitForExit(child)) === null) {
    try { child.kill(process.platform === "win32" ? undefined : "SIGKILL") } catch {}
    await waitForExit(child)
  }
}

function createLiveSession(child: ChildProcessWithoutNullStreams, artifactDirectory: string, sessionID: string): LiveSession {
  const session: LiveSession = {
    sessionID,
    child,
    artifactDirectory,
    stdoutBuffer: "",
    completedLines: [],
    stderr: "",
    waiters: [],
    operationTail: Promise.resolve(),
    closed: false,
  }
  child.stdout.setEncoding("utf8")
  child.stderr.setEncoding("utf8")
  child.stdout.on("data", (chunk: string) => {
    const next = appendResponseChunk({ buffered: session.stdoutBuffer, completedLines: session.completedLines }, chunk)
    session.stdoutBuffer = next.buffered
    session.completedLines = next.completedLines
    flushLines(session)
  })
  child.stderr.on("data", (chunk: string) => (session.stderr += chunk))
  child.on("error", (error) => {
    session.closed = true
    const safe = new Error("NyamNyam ListMap evaluation runner failed before responding.")
    session.bridgeError ??= safe
    rejectWaiters(session, safe)
    reportHarnessDiagnostic("runner-error", { sessionID, artifactDirectory, error: errorDetails(error), stderr: session.stderr })
  })
  child.on("exit", (code) => {
    session.closed = true
    if (code !== 0) {
      const safe = new Error("NyamNyam ListMap evaluation runner failed before responding.")
      session.bridgeError ??= safe
      rejectWaiters(session, safe)
    }
    reportHarnessDiagnostic("runner-exit", { sessionID, artifactDirectory, code, stderr: session.stderr })
  })
  return session
}

async function runCommand(command: string, args: string[], cwd: string) {
  const child = spawn(command, args, { cwd, stdio: ["ignore", "pipe", "pipe"] })
  let stdout = ""
  let stderr = ""
  child.stdout.setEncoding("utf8")
  child.stderr.setEncoding("utf8")
  child.stdout.on("data", (chunk: string) => (stdout += chunk))
  child.stderr.on("data", (chunk: string) => (stderr += chunk))
  const code = await new Promise<number | null>((resolveExit, reject) => {
    child.once("error", reject)
    child.once("exit", resolveExit)
  })
  if (code !== 0) throw new Error(`${command} failed (code ${code ?? "signal"}): ${stderr}`)
  return stdout.trim()
}

async function readResponse(session: LiveSession) {
  flushLines(session)
  const completed = session.completedLines.shift()
  if (completed !== undefined) return completed
  if (session.bridgeError) throw session.bridgeError

  return await new Promise<string>((resolveLine, reject) => {
    const waiter: ResponseWaiter = {
      resolve: (line) => {
        clearTimeout(waiter.timeout)
        resolveLine(line)
      },
      reject: (error) => {
        clearTimeout(waiter.timeout)
        reject(error)
      },
    }
    waiter.timeout = setTimeout(() => {
      const index = session.waiters.indexOf(waiter)
      if (index >= 0) session.waiters.splice(index, 1)
      reject(new Error("NyamNyam ListMap evaluation request timed out."))
      void stopChild(session.child)
    }, requestTimeoutMs)
    session.waiters.push(waiter)
    flushLines(session)
  })
}

async function withSessionLock<T>(session: LiveSession, operation: () => Promise<T>): Promise<T> {
  const previous = session.operationTail
  let release: () => void = () => undefined
  session.operationTail = new Promise<void>((resolveLock) => (release = resolveLock))
  await previous
  try {
    return await operation()
  } finally {
    release()
  }
}

export async function startListMapEvaluation(context: ListMapToolContext): Promise<string> {
  requireEvaluationAgent(context.agent)
  if (finalizedSessions.has(context.sessionID)) throw new Error("This ListMap evaluation agent session already finalized its one permitted run.")
  if (startingSessions.has(context.sessionID) || sessions.has(context.sessionID)) throw new Error("A ListMap evaluation session is already active or starting.")

  startingSessions.add(context.sessionID)
  let child: ChildProcessWithoutNullStreams | undefined
  let artifactDirectory: string | undefined
  try {
    const fixture = configuredFixture()
    const taskID = configuredTaskID()
    const artifactRoot = resolveArtifactRoot(context.worktree)
    await runCommand("git", frozenInputDiffArguments(), context.worktree)
    await ensurePrivateDirectory(artifactRoot)
    artifactDirectory = runDirectory(context.sessionID, artifactRoot)
    await ensurePrivateDirectory(artifactDirectory)
    const inputPath = join(artifactDirectory, "fixture.json")

    await runCommand(
      "dotnet",
      ["run", "--project", "tools/NyamNyam.EvaluationFixtureExport/NyamNyam.EvaluationFixtureExport.fsproj", "--no-restore", "--", "--fixture", fixture, "--output", inputPath],
      context.worktree,
    )
    await ensurePrivateFile(inputPath)
    const repositoryRevision = await runCommand("git", ["rev-parse", "HEAD"], context.worktree)

    const args = [
      "run", "--project", "tools/NyamNyam.AgentEval.ListMapStructural/NyamNyam.AgentEval.ListMapStructural.fsproj", "--no-restore", "--",
      "--protocol", protocolVersion,
      "--input", inputPath,
      "--artifact-dir", artifactDirectory,
      "--fixture", fixture,
      "--task-id", taskID,
      "--repository-revision", repositoryRevision,
      ...stalePerturbationArguments(taskID),
      ...metadataArguments(),
    ]
    child = spawn("dotnet", args, { cwd: context.worktree, stdio: ["pipe", "pipe", "pipe"] })
    sessions.set(context.sessionID, createLiveSession(child, artifactDirectory, context.sessionID))
    return `NyamNyam ListMap evaluation session started for frozen corpus ${expectedCorpusRevision}. Send describeV1 first.`
  } catch (error) {
    reportHarnessDiagnostic("startup-failed", { sessionID: context.sessionID, worktree: context.worktree, artifactDirectory, error: errorDetails(error) })
    sessions.delete(context.sessionID)
    if (child) await stopChild(child).catch(() => undefined)
    if (artifactDirectory) await rm(artifactDirectory, { recursive: true, force: true }).catch(() => undefined)
    throw new Error("NyamNyam ListMap evaluation session could not be started.")
  } finally {
    startingSessions.delete(context.sessionID)
  }
}

export async function requestListMapEvaluation(requestText: string, context: ListMapToolContext): Promise<string> {
  requireEvaluationAgent(context.agent)
  const session = sessions.get(context.sessionID)
  if (!session) throw new Error("No active ListMap evaluation session. Start one first.")

  return await withSessionLock(session, async () => {
    if (session.closed || session.bridgeError) throw session.bridgeError ?? new Error("NyamNyam ListMap evaluation session is no longer available.")
    const request = validateRequestLine(requestText)
    await new Promise<void>((resolveWrite, reject) => {
      session.child.stdin.write(`${request}\n`, (error) => (error ? reject(error) : resolveWrite()))
    })
    return await readResponse(session)
  })
}

export async function finalizeListMapEvaluation(context: ListMapToolContext): Promise<string> {
  requireEvaluationAgent(context.agent)
  const session = sessions.get(context.sessionID)
  if (!session) {
    if (startingSessions.has(context.sessionID)) throw new Error("NyamNyam ListMap evaluation session is still starting.")
    if (finalizationFailures.has(context.sessionID)) throw new Error("NyamNyam ListMap evaluation session finalization failed.")
    return "No active NyamNyam ListMap evaluation session."
  }

  return await withSessionLock(session, async () => {
    sessions.delete(context.sessionID)
    finalizedSessions.add(context.sessionID)
    try {
      session.child.stdin.end()
      let code = await waitForExit(session.child)
      if (code === null) {
        await stopChild(session.child)
        code = session.child.exitCode
      }
      if (code !== 0) {
        finalizationFailures.add(context.sessionID)
        reportHarnessDiagnostic("finalization-failed", { sessionID: context.sessionID, artifactDirectory: session.artifactDirectory, code, stderr: session.stderr })
        throw new Error("NyamNyam ListMap evaluation session finalization failed.")
      }
      return "NyamNyam ListMap evaluation session finalized."
    } catch (error) {
      finalizationFailures.add(context.sessionID)
      if (error instanceof Error && error.message.includes("finalization failed")) throw error
      reportHarnessDiagnostic("finalization-error", { sessionID: context.sessionID, artifactDirectory: session.artifactDirectory, error: errorDetails(error), stderr: session.stderr })
      throw new Error("NyamNyam ListMap evaluation session finalization failed.")
    }
  })
}
