import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { chmod, mkdir, rm } from "node:fs/promises"
import { isAbsolute, join, relative, resolve, sep } from "node:path"

const requestTimeoutMs = 120_000
const childShutdownTimeoutMs = 5_000

export type EvaluationRuntimeContext = {
  sessionKey: string
  worktree: string
}

type ResponseWaiter = {
  resolve: (line: string) => void
  reject: (error: Error) => void
  timeout?: ReturnType<typeof setTimeout>
}

export type LiveSession = {
  sessionKey: string
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

export function validateJsonObjectLine(request: string, label: string): string {
  if (!request.trim()) throw new Error(`${label} must not be blank.`)
  if (request.includes("\n") || request.includes("\r")) {
    throw new Error(`${label} must contain exactly one JSON object on one line.`)
  }

  let value: unknown
  try {
    value = JSON.parse(request)
  } catch {
    throw new Error(`${label} must contain exactly one valid JSON object.`)
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must contain exactly one JSON object.`)
  }
  return request
}

export type ArtifactRootMessages = {
  rootMustBeAbsolute: string
  worktreeMustBeAbsolute: string
  rootMustBeOutsideWorktree: string
}

export function validateExternalArtifactRoot(
  root: string,
  worktree: string,
  messages: ArtifactRootMessages,
): string {
  if (!isAbsolute(root)) throw new Error(messages.rootMustBeAbsolute)
  if (!isAbsolute(worktree)) throw new Error(messages.worktreeMustBeAbsolute)

  const absoluteRoot = resolve(root)
  const absoluteWorktree = resolve(worktree)
  const relativeRoot = relative(absoluteWorktree, absoluteRoot)
  const isContained =
    relativeRoot === "" ||
    (relativeRoot !== ".." && !relativeRoot.startsWith(`..${sep}`) && !isAbsolute(relativeRoot))
  if (isContained) throw new Error(messages.rootMustBeOutsideWorktree)
  return absoluteRoot
}

export async function ensurePrivateDirectory(path: string) {
  await mkdir(path, { recursive: true, mode: 0o700 })
  if (process.platform !== "win32") await chmod(path, 0o700)
}

export async function ensurePrivateFile(path: string) {
  if (process.platform !== "win32") await chmod(path, 0o600)
}

export function compactArguments(entries: readonly (readonly [string, string | undefined])[]): string[] {
  return entries.flatMap(([option, value]) => (value ? [option, value] : []))
}

export function frozenRevisionDiffArguments(revision: string, paths: readonly string[]): string[] {
  return ["diff", "--exit-code", revision, "--", ...paths]
}

function errorDetails(error: unknown): string {
  if (error instanceof Error) return error.stack ?? error.message
  return String(error)
}

function runDirectory(sessionKey: string, artifactRoot: string) {
  const safeSessionKey = sessionKey.replace(/[^A-Za-z0-9_-]/g, "_")
  return join(artifactRoot, `${safeSessionKey}-${Date.now()}-${randomUUID()}`)
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

export type EvaluationRuntimeMessages = {
  alreadyFinalized: string
  alreadyActiveOrStarting: string
  requestNoActive: string
  finalizeNoActive: string
  stillStarting: string
  unavailable: string
  startFailure: string
  requestTimeout: string
  runnerFailure: string
  finalizationFailed: string
  finalized: string
  started: (expectedCorpusRevision: string) => string
}

export type EvaluationRuntimeConfig = {
  component: string
  protocolVersion: string
  expectedCorpusRevision: string
  frozenInputDiffArguments: () => string[]
  resolveArtifactRoot: (worktree: string) => string
  configuredFixture: () => string
  configuredTaskID: () => string
  fixtureExporterProject?: string
  runnerProject: string
  stalePerturbationArguments: (taskID: string) => string[]
  metadataArguments: () => string[]
  validateRequestLine: (request: string) => string
  messages: EvaluationRuntimeMessages
}

export function createPersistentEvaluationRuntime(config: EvaluationRuntimeConfig) {
  const sessions = new Map<string, LiveSession>()
  const startingSessions = new Set<string>()
  const finalizedSessions = new Set<string>()
  const finalizationFailures = new Set<string>()

  function reportHarnessDiagnostic(event: string, details: Record<string, unknown> = {}) {
    console.error(`[${config.component}] ${JSON.stringify({ component: config.component, event, ...details })}`)
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

  function createLiveSession(
    child: ChildProcessWithoutNullStreams,
    artifactDirectory: string,
    sessionKey: string,
  ): LiveSession {
    const session: LiveSession = {
      sessionKey,
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
      const next = appendResponseChunk(
        { buffered: session.stdoutBuffer, completedLines: session.completedLines },
        chunk,
      )
      session.stdoutBuffer = next.buffered
      session.completedLines = next.completedLines
      flushLines(session)
    })
    child.stderr.on("data", (chunk: string) => (session.stderr += chunk))
    child.on("error", (error) => {
      session.closed = true
      const safe = new Error(config.messages.runnerFailure)
      session.bridgeError ??= safe
      rejectWaiters(session, safe)
      reportHarnessDiagnostic("runner-error", {
        sessionID: sessionKey,
        artifactDirectory,
        error: errorDetails(error),
        stderr: session.stderr,
      })
    })
    child.on("exit", (code) => {
      session.closed = true
      if (code !== 0) {
        const safe = new Error(config.messages.runnerFailure)
        session.bridgeError ??= safe
        rejectWaiters(session, safe)
      }
      reportHarnessDiagnostic("runner-exit", {
        sessionID: sessionKey,
        artifactDirectory,
        code,
        stderr: session.stderr,
      })
    })
    return session
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
        reject(new Error(config.messages.requestTimeout))
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

  async function start(context: EvaluationRuntimeContext): Promise<string> {
    if (finalizedSessions.has(context.sessionKey)) throw new Error(config.messages.alreadyFinalized)
    if (startingSessions.has(context.sessionKey) || sessions.has(context.sessionKey)) {
      throw new Error(config.messages.alreadyActiveOrStarting)
    }

    startingSessions.add(context.sessionKey)
    let child: ChildProcessWithoutNullStreams | undefined
    let artifactDirectory: string | undefined
    try {
      const fixture = config.configuredFixture()
      const taskID = config.configuredTaskID()
      const artifactRoot = config.resolveArtifactRoot(context.worktree)
      await runCommand("git", config.frozenInputDiffArguments(), context.worktree)
      await ensurePrivateDirectory(artifactRoot)
      artifactDirectory = runDirectory(context.sessionKey, artifactRoot)
      await ensurePrivateDirectory(artifactDirectory)
      const inputPath = join(artifactDirectory, "fixture.json")

      const fixtureExporterProject =
        config.fixtureExporterProject ?? "tools/NyamNyam.EvaluationFixtureExport/NyamNyam.EvaluationFixtureExport.fsproj"
      await runCommand(
        "dotnet",
        ["run", "--project", fixtureExporterProject, "--no-restore", "--", "--fixture", fixture, "--output", inputPath],
        context.worktree,
      )
      await ensurePrivateFile(inputPath)
      const repositoryRevision = await runCommand("git", ["rev-parse", "HEAD"], context.worktree)

      const args = [
        "run", "--project", config.runnerProject, "--no-restore", "--",
        "--protocol", config.protocolVersion,
        "--input", inputPath,
        "--artifact-dir", artifactDirectory,
        "--fixture", fixture,
        "--task-id", taskID,
        "--repository-revision", repositoryRevision,
        ...config.stalePerturbationArguments(taskID),
        ...config.metadataArguments(),
      ]
      child = spawn("dotnet", args, { cwd: context.worktree, stdio: ["pipe", "pipe", "pipe"] })
      sessions.set(context.sessionKey, createLiveSession(child, artifactDirectory, context.sessionKey))
      return config.messages.started(config.expectedCorpusRevision)
    } catch (error) {
      reportHarnessDiagnostic("startup-failed", {
        sessionID: context.sessionKey,
        worktree: context.worktree,
        artifactDirectory,
        error: errorDetails(error),
      })
      sessions.delete(context.sessionKey)
      if (child) await stopChild(child).catch(() => undefined)
      if (artifactDirectory) await rm(artifactDirectory, { recursive: true, force: true }).catch(() => undefined)
      throw new Error(config.messages.startFailure)
    } finally {
      startingSessions.delete(context.sessionKey)
    }
  }

  async function request(requestText: string, context: EvaluationRuntimeContext): Promise<string> {
    const session = sessions.get(context.sessionKey)
    if (!session) throw new Error(config.messages.requestNoActive)

    return await withSessionLock(session, async () => {
      if (session.closed || session.bridgeError) {
        throw session.bridgeError ?? new Error(config.messages.unavailable)
      }
      const request = config.validateRequestLine(requestText)
      await new Promise<void>((resolveWrite, reject) => {
        session.child.stdin.write(`${request}\n`, (error) => (error ? reject(error) : resolveWrite()))
      })
      return await readResponse(session)
    })
  }

  async function finalize(context: EvaluationRuntimeContext): Promise<string> {
    const session = sessions.get(context.sessionKey)
    if (!session) {
      if (startingSessions.has(context.sessionKey)) throw new Error(config.messages.stillStarting)
      if (finalizationFailures.has(context.sessionKey)) throw new Error(config.messages.finalizationFailed)
      return config.messages.finalizeNoActive
    }

    return await withSessionLock(session, async () => {
      sessions.delete(context.sessionKey)
      finalizedSessions.add(context.sessionKey)
      try {
        session.child.stdin.end()
        let code = await waitForExit(session.child)
        if (code === null) {
          await stopChild(session.child)
          code = session.child.exitCode
        }
        if (code !== 0) {
          finalizationFailures.add(context.sessionKey)
          reportHarnessDiagnostic("finalization-failed", {
            sessionID: context.sessionKey,
            artifactDirectory: session.artifactDirectory,
            code,
            stderr: session.stderr,
          })
          throw new Error(config.messages.finalizationFailed)
        }
        return config.messages.finalized
      } catch (error) {
        finalizationFailures.add(context.sessionKey)
        if (error instanceof Error && error.message === config.messages.finalizationFailed) throw error
        reportHarnessDiagnostic("finalization-error", {
          sessionID: context.sessionKey,
          artifactDirectory: session.artifactDirectory,
          error: errorDetails(error),
          stderr: session.stderr,
        })
        throw new Error(config.messages.finalizationFailed)
      }
    })
  }

  return { start, request, finalize, reportHarnessDiagnostic }
}
