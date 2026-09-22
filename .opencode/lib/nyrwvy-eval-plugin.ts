import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { chmod, mkdir, rm } from "node:fs/promises"
import { isAbsolute, join, relative, resolve, sep } from "node:path"
import { tool, type Plugin } from "@opencode-ai/plugin"

const requestTimeoutMs = 120_000
const childShutdownTimeoutMs = 5_000
const startFailureMessage = "Nyrwvy evaluation session could not be started."
const requestFailureMessage = "Nyrwvy evaluation request could not be completed."
const runnerFailureMessage = "Nyrwvy evaluation runner failed before responding."
const finalizationFailureMessage = "Nyrwvy evaluation session finalization failed."
const sessions = new Map<string, LiveSession>()
const startingSessions = new Set<string>()
const startingTasks = new Map<string, Promise<void>>()
const finalizedSessions = new Set<string>()
const finalizationFailures = new Set<string>()
const runMetadata = new Map<string, { model?: string; providerRuntime?: string; reasoningConfig?: string }>()

type Fixture = "m0" | "m1"

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
  console.error(`[nyrwvy-eval] ${JSON.stringify({ component: "nyrwvy-eval", event, ...details })}`)
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
  if (!request.trim()) throw new Error("The evaluation request must not be blank.")
  if (request.includes("\n") || request.includes("\r")) {
    throw new Error("The evaluation request must contain exactly one JSON object on one line.")
  }

  let value: unknown
  try {
    value = JSON.parse(request)
  } catch {
    throw new Error("The evaluation request must contain exactly one valid JSON object.")
  }

  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("The evaluation request must contain exactly one JSON object.")
  }

  return request
}

function requireEvaluationAgent(agent: string) {
  if (agent !== "nyrwvy-eval") {
    throw new Error("Nyrwvy evaluation tools are available only to the nyrwvy-eval agent.")
  }
}

function runDirectory(sessionID: string, artifactRoot: string) {
  const safeSessionID = sessionID.replace(/[^A-Za-z0-9_-]/g, "_")
  return join(artifactRoot, `${safeSessionID}-${Date.now()}-${randomUUID()}`)
}

export function artifactRootDirectory() {
  return process.env.NYRWVY_EVAL_ARTIFACT_ROOT ?? join(process.env.HOME ?? "/tmp", ".local", "share", "nyrwvy", "evaluation-runs")
}

export function validateArtifactRoot(root: string, worktree: string): string {
  if (!isAbsolute(root)) throw new Error("The evaluation artifact root must be an absolute path.")
  if (!isAbsolute(worktree)) throw new Error("The evaluation worktree must be an absolute path.")

  const absoluteRoot = resolve(root)
  const absoluteWorktree = resolve(worktree)
  const relativeRoot = relative(absoluteWorktree, absoluteRoot)
  const isContained =
    relativeRoot === "" ||
    (relativeRoot !== ".." && !relativeRoot.startsWith(`..${sep}`) && !isAbsolute(relativeRoot))

  if (isContained) throw new Error("The evaluation artifact root must be outside the evaluation worktree.")
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

export function metadataArguments(sessionID: string) {
  const observed = runMetadata.get(sessionID)
  const metadata = [
    ["--model", observed?.model],
    ["--provider-runtime", observed?.providerRuntime],
    ["--reasoning-config", observed?.reasoningConfig],
    ["--opencode-version", process.env.NYRWVY_EVAL_OPENCODE_VERSION],
    ["--prompt-revision", process.env.NYRWVY_EVAL_PROMPT_REVISION],
    ["--tool-revision", process.env.NYRWVY_EVAL_TOOL_REVISION],
  ] as const

  return metadata.flatMap(([option, value]) => (value ? [option, value] : []))
}

function configuredFixture(): Fixture {
  const fixture = process.env.NYRWVY_EVAL_FIXTURE
  if (fixture === "m0" || fixture === "m1") return fixture
  throw new Error("The harness must set NYRWVY_EVAL_FIXTURE to m0 or m1 before starting an evaluation agent.")
}

function stalePerturbationArguments(fixture: Fixture) {
  const mode = process.env.NYRWVY_EVAL_STALE_PERTURBATION
  if (!mode) return []
  if (mode !== "m0-raw-user-input" || fixture !== "m0") {
    throw new Error(`Unsupported stale perturbation configuration '${mode}' for fixture '${fixture}'.`)
  }

  return [
    "--stale-perturbation-trigger-function-id",
    "function-validate-user",
    "--stale-perturbation-trigger-name",
    "validate_profile",
    "--stale-perturbation-type-id",
    "type-raw-user-input",
    "--stale-perturbation-name",
    "RawAccountInput",
  ]
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

  return await new Promise<number | null>((resolve) => {
    let timer: ReturnType<typeof setTimeout> | undefined
    let settled = false
    const finish = (code: number | null) => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      child.removeListener("exit", onExit)
      resolve(code)
    }
    const onExit = (code: number | null) => finish(code)

    child.once("exit", onExit)
    if (child.exitCode !== null) {
      finish(child.exitCode)
      return
    }

    timer = setTimeout(() => finish(child.exitCode), childShutdownTimeoutMs)
    timer.unref?.()
  })
}

async function stopChild(child: ChildProcessWithoutNullStreams) {
  if (child.exitCode !== null || child.signalCode !== null) return

  try {
    child.stdin.destroy()
  } catch {
    // The child may have closed its stdin while the failure was being handled.
  }

  try {
    child.kill()
  } catch {
    // The child may have exited between the exit-code check and kill.
  }

  if ((await waitForExit(child)) === null) {
    try {
      child.kill(process.platform === "win32" ? undefined : "SIGKILL")
    } catch {
      // Best effort only; there is no stronger portable child-process API here.
    }

    await waitForExit(child)
  }
}

function invalidateSession(session: LiveSession, error: Error) {
  session.closed = true
  session.bridgeError ??= error
  rejectWaiters(session, error)
  void stopChild(session.child).catch(() => undefined)
}

export function createLiveSession(
  child: ChildProcessWithoutNullStreams,
  artifactDirectory: string,
  sessionID: string,
): LiveSession {
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
    const next = appendResponseChunk(
      { buffered: session.stdoutBuffer, completedLines: session.completedLines },
      chunk,
    )
    session.stdoutBuffer = next.buffered
    session.completedLines = next.completedLines
    flushLines(session)
  })
  child.stderr.on("data", (chunk: string) => {
    session.stderr += chunk
  })
  const onError = (error: Error) => {
    session.closed = true
    reportHarnessDiagnostic("runner-error", {
      sessionID: session.sessionID,
      artifactDirectory: session.artifactDirectory,
      error: errorDetails(error),
      stderr: session.stderr,
    })
    const safeError = new Error(runnerFailureMessage)
    session.bridgeError ??= safeError
    rejectWaiters(session, safeError)
  }
  child.on("error", onError)
  child.stdin.on("error", (error) => {
    onError(error)
    void stopChild(child).catch(() => undefined)
  })
  child.on("close", (code) => {
    session.closed = true
    reportHarnessDiagnostic("runner-exit", {
      sessionID: session.sessionID,
      artifactDirectory: session.artifactDirectory,
      code,
      stderr: session.stderr,
    })
    const safeError = new Error(runnerFailureMessage)
    session.bridgeError ??= safeError
    rejectWaiters(session, safeError)
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

  const code = await new Promise<number | null>((resolve, reject) => {
    child.once("error", reject)
    child.once("exit", resolve)
  })
  if (code !== 0) throw new Error(`${command} failed (code ${code ?? "signal"}): ${stderr}`)
  return stdout.trim()
}

export async function finalizeRunnerProcess(session: LiveSession): Promise<string> {
  try {
    try {
      session.child.stdin.end()
    } catch {
      // The child may already have closed its input while finalization started.
    }

    let code = await waitForExit(session.child)
    if (code === null) {
      await stopChild(session.child)
      code = session.child.exitCode
    }

    if (code !== 0) {
      reportHarnessDiagnostic("finalization-failed", {
        sessionID: session.sessionID,
        artifactDirectory: session.artifactDirectory,
        code,
        stderr: session.stderr,
      })
      throw new Error(finalizationFailureMessage)
    }

    return "Nyrwvy evaluation session finalized."
  } catch (error) {
    if (error instanceof Error && error.message === finalizationFailureMessage) throw error
    reportHarnessDiagnostic("finalization-error", {
      sessionID: session.sessionID,
      artifactDirectory: session.artifactDirectory,
      error: errorDetails(error),
      stderr: session.stderr,
    })
    throw new Error(finalizationFailureMessage)
  }
}

async function readResponse(session: LiveSession) {
  flushLines(session)
  const completedLine = session.completedLines.shift()
  if (completedLine !== undefined) return completedLine
  if (session.bridgeError) throw session.bridgeError

  return await new Promise<string>((resolve, reject) => {
    const waiter: ResponseWaiter = {
      resolve: (line) => {
        clearTimeout(waiter.timeout)
        resolve(line)
      },
      reject: (error) => {
        clearTimeout(waiter.timeout)
        reject(error)
      },
    }

    waiter.timeout = setTimeout(() => {
      const index = session.waiters.indexOf(waiter)
      if (index < 0) return
      session.waiters.splice(index, 1)
      const error = new Error(requestFailureMessage)
      reportHarnessDiagnostic("runner-timeout", {
        sessionID: session.sessionID,
        artifactDirectory: session.artifactDirectory,
      })
      invalidateSession(session, error)
      reject(error)
    }, requestTimeoutMs)

    session.waiters.push(waiter)
    flushLines(session)
  })
}

async function withSessionLock<T>(session: LiveSession, operation: () => Promise<T>): Promise<T> {
  const previous = session.operationTail
  let release: () => void = () => undefined
  session.operationTail = new Promise<void>((resolve) => {
    release = resolve
  })

  await previous
  try {
    return await operation()
  } finally {
    release()
  }
}

async function finalize(sessionID: string) {
  const session = sessions.get(sessionID)
  if (!session) {
    if (startingSessions.has(sessionID)) throw new Error("Nyrwvy evaluation session is still starting.")
    if (finalizationFailures.has(sessionID)) throw new Error(finalizationFailureMessage)
    return "No active Nyrwvy evaluation session."
  }

  return await withSessionLock(session, async () => {
    sessions.delete(sessionID)
    finalizedSessions.add(sessionID)
    runMetadata.delete(sessionID)
    session.closed = true

    try {
      return await finalizeRunnerProcess(session)
    } catch (error) {
      finalizationFailures.add(sessionID)
      throw error
    }
  })
}

export const NyrwvyEvalPlugin: Plugin = async () => ({
  tool: {
    nyrwvy_eval_start: tool({
      description: "Start the single harness-configured persistent Nyrwvy evaluation session. This does not expose a raw snapshot.",
      args: {},
      async execute(_args, context) {
        requireEvaluationAgent(context.agent)
        if (finalizedSessions.has(context.sessionID)) throw new Error("This evaluation agent session already finalized its one permitted run.")
        if (startingSessions.has(context.sessionID)) throw new Error("An evaluation session is already starting.")
        if (sessions.has(context.sessionID)) throw new Error("An evaluation session is already active. Use nyrwvy_eval_request or finalize it first.")

        startingSessions.add(context.sessionID)
        const startTask = (async () => {
          let child: ChildProcessWithoutNullStreams | undefined
          let artifactRoot: string | undefined
          let artifactDirectory: string | undefined

          try {
            const fixture = configuredFixture()
            artifactRoot = resolveArtifactRoot(context.worktree)
            await ensurePrivateDirectory(artifactRoot)
            artifactDirectory = runDirectory(context.sessionID, artifactRoot)
            const inputPath = join(artifactDirectory, "fixture.json")
            await ensurePrivateDirectory(artifactDirectory)

            await runCommand(
              "dotnet",
              ["run", "--project", "tools/Nyrwvy.EvaluationFixtureExport/Nyrwvy.EvaluationFixtureExport.fsproj", "--no-restore", "--", "--fixture", fixture, "--output", inputPath],
              context.worktree,
            )
            await ensurePrivateFile(inputPath)

            child = spawn(
              "dotnet",
              [
                "run",
                "--project",
                "tools/Nyrwvy.AgentEval/Nyrwvy.AgentEval.fsproj",
                "--no-restore",
                "--",
                "--input",
                inputPath,
                "--artifact-dir",
                artifactDirectory,
                "--verification-fixture",
                fixture,
                ...stalePerturbationArguments(fixture),
                ...metadataArguments(context.sessionID),
              ],
              { cwd: context.worktree, stdio: ["pipe", "pipe", "pipe"] },
            )

            sessions.set(context.sessionID, createLiveSession(child, artifactDirectory, context.sessionID))
            return "Nyrwvy evaluation session started. Send one bounded protocol request at a time with nyrwvy_eval_request."
          } catch (error) {
            reportHarnessDiagnostic("startup-failed", {
              sessionID: context.sessionID,
              worktree: context.worktree,
              artifactRoot,
              artifactDirectory,
              error: errorDetails(error),
            })
            sessions.delete(context.sessionID)
            if (child) {
              try {
                await stopChild(child)
              } catch {
                // Preserve the startup error while still attempting child cleanup.
              }
            }

            if (artifactDirectory) {
              try {
                await rm(artifactDirectory, { recursive: true, force: true })
              } catch {
                // A partially created artifact directory is harmless only after its owner has failed.
              }
            }

            throw new Error(startFailureMessage)
          }
        })()
        const completion = startTask.then(() => undefined, () => undefined)
        startingTasks.set(context.sessionID, completion)

        try {
          return await startTask
        } finally {
          startingSessions.delete(context.sessionID)
          if (startingTasks.get(context.sessionID) === completion) startingTasks.delete(context.sessionID)
        }
      },
    }),

    nyrwvy_eval_request: tool({
      description: "Send one JSON Lines request to the active persistent Nyrwvy evaluation session. Only the runner's bounded query, simulate, reviewed commit, reject, state, and metrics commands are accepted.",
      args: {
        request: tool.schema.string().describe("One JSON object conforming to the Nyrwvy.AgentEval protocol."),
      },
      async execute(args, context) {
        requireEvaluationAgent(context.agent)
        const session = sessions.get(context.sessionID)
        if (!session) throw new Error("No active evaluation session. Start one first.")

        return await withSessionLock(session, async () => {
          if (session.closed || session.bridgeError) {
            throw session.bridgeError ?? new Error("Nyrwvy.AgentEval session is no longer available.")
          }

          const request = validateRequestLine(args.request)
          if (session.child.exitCode !== null) {
            reportHarnessDiagnostic("request-after-runner-exit", {
              sessionID: session.sessionID,
              artifactDirectory: session.artifactDirectory,
              code: session.child.exitCode,
              stderr: session.stderr,
            })
            throw new Error(requestFailureMessage)
          }

          try {
            await new Promise<void>((resolve, reject) => {
              session.child.stdin.write(`${request}\n`, (error) => (error ? reject(error) : resolve()))
            })
          } catch (error) {
            reportHarnessDiagnostic("request-write-failed", {
              sessionID: session.sessionID,
              artifactDirectory: session.artifactDirectory,
              error: errorDetails(error),
            })
            throw new Error(requestFailureMessage)
          }
          return await readResponse(session)
        })
      },
    }),

    nyrwvy_eval_finalize: tool({
      description: "Finalize the active Nyrwvy evaluation session after its terminal state is observed. This durably records terminal run status and prevents later commands.",
      args: {},
      async execute(_args, context) {
        requireEvaluationAgent(context.agent)
        return await finalize(context.sessionID)
      },
    }),
  },
  "chat.message": async (input) => {
    if (!input.model) return
    runMetadata.set(input.sessionID, {
      model: `${input.model.providerID}/${input.model.modelID}`,
      providerRuntime: "opencode",
      reasoningConfig: input.variant,
    })
  },
  dispose: async () => {
    await Promise.all([...startingTasks.values()])
    for (const sessionID of [...sessions.keys()]) {
      try {
        await finalize(sessionID)
      } catch {
        const session = sessions.get(sessionID)
        if (session) await stopChild(session.child).catch(() => undefined)
        sessions.delete(sessionID)
      }
    }
  },
})
