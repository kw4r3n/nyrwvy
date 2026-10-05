import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { createInterface } from "node:readline"
import { stat } from "node:fs/promises"
import { resolve } from "node:path"

/**
 * allowHostSources is operator configuration, never a per-request argument: when true, the session
 * starts with --allow-host-sources and runPure hostSources compile and run arbitrary C# with the
 * user's permissions. NYRWVY_ALLOW_HOST_SOURCES=1 in the adapter's environment enables it too.
 */
export type AuthoringContext = { sessionID: string; worktree: string; repositoryRoot?: string; abort?: AbortSignal; allowHostSources?: boolean }

export function hostSourcesAllowed(context: AuthoringContext): boolean {
  return context.allowHostSources === true || process.env.NYRWVY_ALLOW_HOST_SOURCES === "1"
}
export type Request = { op: string; [key: string]: unknown }
type Reply = { id: string; ok: boolean; result?: unknown; error?: unknown }
type Pending = { id: string; resolve: (line: string) => void; reject: (error: Error) => void }

const shutdownTimeoutMs = 5_000
const recoveryAdvice = "A sent request's outcome is unknown if its response was lost. Reopen and inspect committed state before retrying; requests are never replayed."

async function settlesWithin(promise: Promise<unknown>): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    return await Promise.race([
      promise.then(() => true),
      new Promise<false>(resolveTimeout => { timer = setTimeout(() => resolveTimeout(false), shutdownTimeoutMs) }),
    ])
  } finally {
    clearTimeout(timer)
  }
}

/** Transport only. All semantic decisions and publication remain in AuthoringSession. */
export class AuthoringClient {
  private pending?: Pending
  private failure?: Error
  private tail: Promise<unknown> = Promise.resolve()
  private closing = false
  private closePromise?: Promise<void>
  private termination?: Promise<void>
  private reaped = false
  private stderr = ""
  private replies = 0
  private exitCode: number | string | null = null
  private spawnError?: NodeJS.ErrnoException
  private exited: Promise<number | null>

  get isReaped(): boolean { return this.reaped }

  /**
   * Why a child that never answered has gone, worded for the person who launched it: startup
   * errors (the session's own stderr line, or a missing dotnet) are configuration problems, not
   * lost responses, so they carry no replay advice.
   */
  get startupFailure(): string | undefined {
    if (!this.reaped || this.replies > 0) return undefined
    if (this.spawnError?.code === "ENOENT") {
      return `dotnet was not found on the PATH of this process (${this.spawnError.message}). Install the .NET 10 SDK or make dotnet available to the host that launches the server.`
    }
    // A child killed by this client (a signal, not an exit code) hung rather than failed to start.
    if (typeof this.exitCode !== "number") return undefined
    const detail = this.stderr.trim()
    return detail === "" ? `AuthoringSession exited with code ${this.exitCode} before answering its first request.` : `AuthoringSession could not start: ${detail}`
  }

  private child: ChildProcessWithoutNullStreams

  constructor(child: ChildProcessWithoutNullStreams) {
    this.child = child
    const lines = createInterface({ input: child.stdout })
    child.stderr.setEncoding("utf8")
    child.stderr.on("data", (chunk: string) => { this.stderr += chunk })
    lines.on("line", (line) => {
      try {
        const reply = JSON.parse(line) as Reply
        if (!this.pending || reply.id !== this.pending.id || typeof reply.ok !== "boolean") {
          throw new Error("Unexpected AuthoringSession response; reopen and inspect committed state.")
        }
        const pending = this.pending
        this.pending = undefined
        this.replies += 1
        pending.resolve(line)
      } catch (error) {
        this.fail(error instanceof Error ? error : new Error(String(error)))
      }
    })
    lines.on("close", () => {
      if (this.pending || !this.closing) {
        this.fail(new Error(`AuthoringSession closed its response stream. ${this.stderr}`))
      }
    })
    child.on("error", (error) => { this.spawnError ??= error; this.fail(error) })
    child.stdin.on("error", (error) => this.fail(error))
    this.exited = new Promise((finish) => {
      child.once("close", (code) => {
        this.reaped = true
        this.exitCode = code ?? child.signalCode
        if (code !== 0 || this.pending || !this.closing) {
          this.fail(new Error(`AuthoringSession exited (${code ?? child.signalCode ?? "signal"}). ${this.stderr}`), false)
        }
        finish(code)
      })
    })
  }

  private fail(error: Error, terminate = true) {
    this.failure ??= new Error(`${error.message} ${recoveryAdvice}`, { cause: error })
    this.pending?.reject(this.failure)
    this.pending = undefined
    if (terminate) void this.terminate().catch(() => undefined)
  }

  private terminate(): Promise<void> {
    // Store ownership before signalling: kill can itself emit an error synchronously.
    this.termination ??= Promise.resolve().then(async () => {
      if (this.reaped) return
      this.child.stdin.destroy()
      for (const signal of ["SIGTERM", "SIGKILL"] as const) {
        try { this.child.kill(signal) } catch (error) {
          this.fail(error instanceof Error ? error : new Error(String(error)), false)
        }
        if (await settlesWithin(this.exited)) return
      }
      throw new Error(`AuthoringSession did not close within ${shutdownTimeoutMs}ms after SIGKILL; child ownership is retained until reaped. ${recoveryAdvice}`)
    })
    return this.termination
  }

  request(request: Request, signal?: AbortSignal): Promise<string> {
    if (this.closing) return Promise.reject(new Error("AuthoringSession is closing; this request was not sent."))
    const operation = this.tail.then(async () => {
      if (this.failure) throw this.failure
      if (signal?.aborted) throw new Error("Authoring request cancelled before sending.")
      const id = randomUUID()
      // The caller passes structured arguments, never a program that constructs JSONL.
      const payload = { ...request, id }
      const line = JSON.stringify(payload, (_key, value) => {
        if (typeof value === "number" && (!Number.isFinite(value) || (Number.isInteger(value) && !Number.isSafeInteger(value)))) {
          throw new Error("Numeric argument would lose precision; use an exact decimal string for Int64 or bits for Float64.")
        }
        return value
      })
      const abort = () => this.fail(new Error("Authoring request cancelled after sending."))
      try {
        return await new Promise<string>((accept, reject) => {
          this.pending = { id, resolve: accept, reject }
          signal?.addEventListener("abort", abort, { once: true })
          try {
            this.child.stdin.write(`${line}\n`, (error) => { if (error) this.fail(error) })
          } catch (error) {
            this.fail(error instanceof Error ? error : new Error(String(error)))
          }
        })
      } finally {
        signal?.removeEventListener("abort", abort)
      }
    })
    this.tail = operation.catch(() => undefined)
    return operation
  }

  close(): Promise<void> {
    this.closing = true
    this.closePromise ??= this.finishClose()
    return this.closePromise
  }

  private async finishClose(): Promise<void> {
    if (!await settlesWithin(this.tail)) {
      this.fail(new Error(`AuthoringSession request queue did not drain within ${shutdownTimeoutMs}ms; unsent requests were discarded.`))
    }
    if (!this.failure) {
      this.child.stdin.end()
      if (!await settlesWithin(this.exited)) {
        this.fail(new Error(`AuthoringSession did not close within ${shutdownTimeoutMs}ms after stdin EOF.`))
      }
    }
    if (this.failure) await this.terminate()
    // A signal being sent is not evidence of exit; only close releases ownership.
    await this.exited
    if (this.failure) throw this.failure
  }
}

const sessions = new Map<string, AuthoringClient>()
function key(context: AuthoringContext) { return `${resolve(context.worktree)}\0${context.sessionID}` }

export async function openAuthoring(
  mode: "create" | "open" | "pin" | "upgrade", snapshot: string, context: AuthoringContext, workspaceOverride?: string, dependency?: string,
  dependencies?: string[], upgrade?: { expectedFingerprint: string; replaces?: string },
): Promise<string> {
  if (dependency !== undefined && (mode === "open" || dependency.trim() === "")) {
    throw new Error("dependency requires mode=create, pin or upgrade and a non-blank source snapshot path.")
  }
  if ((mode === "upgrade") !== (upgrade !== undefined) || (mode === "upgrade" && (dependency === undefined || upgrade?.expectedFingerprint.trim() === ""))) {
    throw new Error("mode=upgrade requires dependency (the new version) and the expectedFingerprint from previewDependencyUpgrade.")
  }
  if (dependencies !== undefined && (mode !== "create" || dependencies.length === 0 || dependencies.some((item) => item.trim() === ""))) {
    throw new Error("dependencies requires mode=create and one or more non-blank source snapshot paths.")
  }
  if (context.abort?.aborted) throw new Error("Authoring open cancelled before starting.")
  const sessionKey = key(context)
  if (sessions.has(sessionKey)) throw new Error("An authoring session is already open. Close it before opening another.")
  const worktree = resolve(context.worktree, workspaceOverride ?? ".")
  // Node reports a missing cwd as ENOENT on the executable; name the real problem instead.
  if (!(await stat(worktree).catch(() => undefined))?.isDirectory()) throw new Error(`workspace directory does not exist: ${worktree}`)
  const workspace = resolve(worktree, snapshot)
  const assembly = resolve(context.repositoryRoot ?? context.worktree, "tools/Nyrwvy.AuthoringSession/bin/Release/net10.0/Nyrwvy.AuthoringSession.dll")
  const args = [assembly, mode, "--snapshot", workspace]
  for (const source of [...(dependency === undefined ? [] : [dependency]), ...(dependencies ?? [])]) {
    args.push("--dependency", resolve(worktree, source))
  }
  if (upgrade !== undefined) {
    args.push("--expect", upgrade.expectedFingerprint)
    if (upgrade.replaces !== undefined) args.push("--replaces", upgrade.replaces)
  }
  const allowHostSources = hostSourcesAllowed(context)
  if (allowHostSources) args.push("--allow-host-sources")
  const client = new AuthoringClient(spawn("dotnet", args, {
    cwd: worktree, stdio: ["pipe", "pipe", "pipe"],
  }))
  sessions.set(sessionKey, client)
  try {
    const status = JSON.parse(await client.request({ op: "status" }, context.abort)) as Reply
    if (!status.ok) throw new Error(JSON.stringify(status.error))
    return JSON.stringify({ worktree, snapshot: workspace, status, hostSourcesAllowed: allowHostSources,
      guide: "Use nyrwvy_inspect, nyrwvy_create_type, nyrwvy_create_function and nyrwvy_replace. Inspect every returned review before nyrwvy_commit or nyrwvy_reject; reviews are compact by default (signature, node count, impact facts, fingerprints), and detail \"full\" adds the proposed graph. nyrwvy_request exposes the full maintained protocol. No raw snapshot or generated-source edits. Close and open the same snapshot to resume; uncommitted proposal handles do not survive." })
  } catch (error) {
    try { await client.close() } catch (cleanupError) {
      if (!client.isReaped) throw new AggregateError([error, cleanupError], "Authoring open failed; unreaped child ownership is retained. Close the session before reopening.")
    } finally {
      if (client.isReaped && sessions.get(sessionKey) === client) sessions.delete(sessionKey)
    }
    const startupFailure = client.startupFailure
    if (startupFailure !== undefined) throw new Error(startupFailure, { cause: error })
    throw error
  }
}

export function requestAuthoring(request: Request, context: AuthoringContext): Promise<string> {
  const client = sessions.get(key(context))
  if (!client) throw new Error("Open a Nyrwvy authoring session first.")
  const payload = request.op.startsWith("simulate")
    ? { structuredReview: true, ...request }
    : request
  return client.request(payload, context.abort)
}

export async function closeAuthoring(context: AuthoringContext): Promise<string> {
  const sessionKey = key(context)
  const client = sessions.get(sessionKey)
  if (!client) return "No active Nyrwvy authoring session."
  try {
    await client.close()
    return "Authoring session closed. Only committed graph changes persist; reopen the snapshot to resume."
  } finally {
    if (client.isReaped && sessions.get(sessionKey) === client) sessions.delete(sessionKey)
  }
}
