import { randomUUID } from "node:crypto"
import { execFileSync, spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { createInterface } from "node:readline"
import { readFile, stat } from "node:fs/promises"
import { resolve } from "node:path"
import { fileURLToPath } from "node:url"

/**
 * The commit of the tool code this process loaded, read once at startup; undefined outside a git checkout.
 * shortcut: uncommitted edits are not seen, so a dirty checkout at the session's commit gives no warning; compare
 * file hashes if tool code is ever served from a dirty tree outside development.
 */
const toolCodeCommit = (() => {
  try {
    return execFileSync("git", ["-C", fileURLToPath(new URL(".", import.meta.url)), "rev-parse", "HEAD"],
      { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 2000 }).trim() || undefined
  } catch { return undefined }
})()

/**
 * Tool descriptions and the recipe reference are loaded when the MCP server starts, while each open starts the
 * session from the current build, so a rebuilt session under a long-running server is newer than what the agent
 * reads. Warns when the session build (`version+commit`) names a different commit than the loaded tool code.
 */
export function staleToolWarning(loadedCommit: string | undefined, sessionBuild: unknown): string | undefined {
  const sessionCommit = typeof sessionBuild === "string" ? sessionBuild.split("+")[1]?.slice(0, 40) : undefined
  if (loadedCommit === undefined || sessionCommit === undefined || !/^[0-9a-f]{40}$/.test(sessionCommit) || sessionCommit === loadedCommit) return undefined
  const mismatch = `The session runs build ${sessionCommit.slice(0, 12)}, but this server loaded its tool descriptions and recipe reference from ${loadedCommit.slice(0, 12)}.`
  // The Zig host's commit comes from the build:host stamp, which a commit leaving host sources unchanged does not refresh.
  if (typeof sessionBuild === "string" && sessionBuild.startsWith("nyrwvy-zig-host+")) return `${mismatch} Run \`npm run build:host\` if host sources changed since ${sessionCommit.slice(0, 12)} (it also refreshes the stamp when they did not); otherwise reconnect the MCP server to read the current tool descriptions.`
  return `${mismatch} Reconnect the MCP server to read the current ones; requests already use the session build.`
}

/**
 * allowHostSources is operator configuration, never a per-request argument: when true, the session
 * starts with --allow-host-sources and runPure hostSources compile and run arbitrary C# with the
 * user's permissions. NYRWVY_ALLOW_HOST_SOURCES=1 in the adapter's environment enables it too.
 */
export type AuthoringContext = { sessionID: string; worktree: string; repositoryRoot?: string; abort?: AbortSignal; allowHostSources?: boolean }

export function hostSourcesAllowed(context: AuthoringContext): boolean {
  return context.allowHostSources === true || process.env.NYRWVY_ALLOW_HOST_SOURCES === "1"
}
export type DependencyUpgrade = { source: string; replaces: string }
export type DependencySelectionUpgrade = { sources: string[]; replaces: string[] }
type UpgradeOptions = { expectedFingerprint: string; replaces?: string } | { upgrades: DependencyUpgrade[]; reviewFingerprint: string } | { selection: DependencySelectionUpgrade; reviewFingerprint: string }
export type Request = { op: string; [key: string]: unknown }
type Reply = { id: string | null; ok: boolean; result?: unknown; error?: unknown }
type Pending = { id: string; resolve: (line: string) => void; reject: (error: Error) => void }

const shutdownTimeoutMs = 5_000
/** Allows a cold Zig runPure to finish its tool build, emitter, runner build and run limits. */
const requestTimeoutMs = 900_000
const stderrLimit = 8_192
const replyLimit = 64 * 1024 * 1024
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
   * errors (the session's own stderr line, or a missing executable) are configuration problems, not
   * lost responses, so they carry no replay advice.
   */
  get startupFailure(): string | undefined {
    if (!this.reaped || this.replies > 0) return undefined
    if (this.spawnError?.code === "ENOENT") {
      return this.defaultHost
        ? `Native host executable was not found: ${this.executable}. Run npm run build:host from the repository root.`
        : `Host executable could not be started: ${this.executable} (${this.spawnError.message}).${this.executable === "dotnet" ? " Install .NET or make dotnet available on PATH." : " Check NYRWVY_HOST_EXECUTABLE."}`
    }
    if (this.spawnError) return `Host executable could not be started: ${this.executable} (${this.spawnError.message}). Check executable permissions and NYRWVY_HOST_EXECUTABLE.`
    // A child killed by this client (a signal, not an exit code) hung rather than failed to start.
    if (typeof this.exitCode !== "number") return undefined
    const detail = this.stderr.trim()
    return detail === "" ? `AuthoringSession exited with code ${this.exitCode} before answering its first request.` : `AuthoringSession could not start: ${detail}`
  }

  private child: ChildProcessWithoutNullStreams

  private executable: string
  private defaultHost: boolean

  constructor(child: ChildProcessWithoutNullStreams, executable = "dotnet", defaultHost = false) {
    this.child = child
    this.executable = executable
    this.defaultHost = defaultHost
    // Registered before readline, so an oversized reply fails before it is parsed.
    let lineLength = 0
    child.stdout.on("data", (chunk: Buffer) => {
      const first = chunk.indexOf(10)
      const longest = first < 0 ? lineLength + chunk.length : lineLength + first
      lineLength = first < 0 ? longest : chunk.length - chunk.lastIndexOf(10) - 1
      if (longest > replyLimit || lineLength > replyLimit) this.fail(new Error(`AuthoringSession sent a reply line longer than ${replyLimit} bytes.`))
    })
    const lines = createInterface({ input: child.stdout })
    child.stderr.setEncoding("utf8")
    // Only the tail is kept: it is quoted in failure messages, and a chatty child must not grow memory.
    child.stderr.on("data", (chunk: string) => { this.stderr = (this.stderr + chunk).slice(-stderrLimit) })
    lines.on("line", (line) => {
      try {
        const reply = JSON.parse(line) as Reply
        if (!this.pending || (reply.id !== this.pending.id && !(reply.id === null && reply.ok === false)) || typeof reply.ok !== "boolean") {
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
        try {
          // A POSIX host leads its own group (detached), so its whole group gets the signal.
          if (this.child.pid === undefined || process.platform === "win32") this.child.kill(signal)
          else process.kill(-this.child.pid, signal)
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code !== "ESRCH") {
            this.fail(error instanceof Error ? error : new Error(String(error)), false)
          }
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
      const line = JSON.stringify(payload, function (key, value) {
        // A Float64 run-value number already denotes an IEEE binary64 value, including
        // exactly representable integral values outside JavaScript's safe Int64 range.
        // Inspect the typed leaf, not its surrounding container or the request operation:
        // arguments, expectations and fake responses all use this same value shape.
        if (key === "value" && this?.kind === "float64" && typeof value === "number") {
          if (!Number.isFinite(value)) {
            throw new Error('Non-finite Float64 values must use "NaN", "Infinity" or "-Infinity" strings.')
          }
          return Object.is(value, -0) ? "-0" : value
        }
        // shortcut: an untyped -0 is written as 0 because the parameter type is unknown here (Int64 has no -0); a Float64 -0 needs {kind:"float64",value:-0}. Reject it once the client knows parameter types.
        if (typeof value === "number" && (!Number.isFinite(value) || (Number.isInteger(value) && !Number.isSafeInteger(value)))) {
          throw new Error("Numeric argument would lose precision; use an exact decimal string for Int64/Bits32. Float64 run values use {kind:\"float64\",value}, which accepts finite JSON numbers or special strings.")
        }
        return value
      })
      const abort = () => this.fail(new Error("Authoring request cancelled after sending."))
      // Replies arrive in order on one pipe, so a request that never answers can only be ended by
      // stopping the child, exactly as a cancel does; uncommitted proposals are lost either way.
      const timer = setTimeout(() => this.fail(new Error(`AuthoringSession did not answer within ${requestTimeoutMs / 1000} s.`)), requestTimeoutMs)
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
        clearTimeout(timer)
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
/** Reserved open slots; their promise resolves once a child is owned or startup fails. */
const opening = new Map<string, Promise<void>>()
function key(context: AuthoringContext) { return `${resolve(context.worktree)}\0${context.sessionID}` }

export async function openAuthoring(
  mode: "create" | "open" | "read" | "pin" | "upgrade", snapshot: string, context: AuthoringContext, workspaceOverride?: string, dependency?: string,
  dependencies?: string[], upgrade?: UpgradeOptions,
): Promise<string> {
  if (dependency !== undefined && (mode === "open" || mode === "read" || dependency.trim() === "")) {
    throw new Error("dependency requires mode=create, pin or upgrade and a non-blank source snapshot path.")
  }
  if ((mode === "upgrade") !== (upgrade !== undefined)) throw new Error("Upgrade options require mode=upgrade.")
  if (upgrade !== undefined) {
    if ("upgrades" in upgrade) {
      if (dependency !== undefined || upgrade.upgrades.length === 0 || upgrade.reviewFingerprint.trim() === "" ||
          upgrade.upgrades.some(item => item.source.trim() === "" || item.replaces.trim() === "") ||
          new Set(upgrade.upgrades.map(item => item.replaces)).size !== upgrade.upgrades.length) {
        throw new Error("Batch upgrade requires distinct non-blank source/replaces entries and reviewFingerprint from previewDependencyUpgrades; dependency cannot be combined with upgrades.")
      }
    } else if ("selection" in upgrade) {
      const { sources, replaces } = upgrade.selection
      if (dependency !== undefined || sources.length === 0 || upgrade.reviewFingerprint.trim() === "" ||
          [...sources, ...replaces].some(item => item.trim() === "") || new Set(replaces).size !== replaces.length) {
        throw new Error("Selection upgrade requires non-blank sources, distinct current replaces IDs and reviewFingerprint; dependency cannot be combined with selection.")
      }
    } else if (dependency === undefined || upgrade.expectedFingerprint.trim() === "") {
      throw new Error("mode=upgrade requires dependency (the new version) and the expectedFingerprint from previewDependencyUpgrade.")
    }
  }
  if (dependencies !== undefined && (mode !== "create" || dependencies.length === 0 || dependencies.some((item) => item.trim() === ""))) {
    throw new Error("dependencies requires mode=create and one or more non-blank source snapshot paths.")
  }
  if (context.abort?.aborted) throw new Error("Authoring open cancelled before starting.")
  const sessionKey = key(context)
  // The slot is reserved before the first await, so a concurrent open of the same session fails
  // instead of spawning a second child whose ownership the map would silently drop.
  if (sessions.get(sessionKey)?.isReaped) sessions.delete(sessionKey)
  if (sessions.has(sessionKey) || opening.has(sessionKey)) throw new Error("An authoring session is already open. Close it before opening another.")
  let resolveStarted!: () => void
  const started = new Promise<void>(resolve => { resolveStarted = resolve })
  const markStarted = () => {
    // The owned client now protects the slot. Never clear a later open's reservation.
    if (opening.get(sessionKey) === started) opening.delete(sessionKey)
    resolveStarted()
  }
  opening.set(sessionKey, started)
  try {
    return await launchAuthoring(sessionKey, mode, snapshot, context, workspaceOverride, dependency, dependencies, upgrade, markStarted)
  } finally {
    markStarted() // A failure before spawn also releases a waiting close.
  }
}

async function launchAuthoring(
  sessionKey: string, mode: "create" | "open" | "read" | "pin" | "upgrade", snapshot: string, context: AuthoringContext,
  workspaceOverride: string | undefined, dependency: string | undefined, dependencies: string[] | undefined,
  upgrade: UpgradeOptions | undefined,
  markStarted: () => void,
): Promise<string> {
  const worktree = resolve(context.worktree, workspaceOverride ?? ".")
  // Node reports a missing cwd as ENOENT on the executable; name the real problem instead.
  if (!(await stat(worktree).catch(() => undefined))?.isDirectory()) throw new Error(`workspace directory does not exist: ${worktree}`)
  const workspace = resolve(worktree, snapshot)
  const root = resolve(context.repositoryRoot ?? context.worktree)
  // The native host; an explicit NYRWVY_HOST_EXECUTABLE (relative to the repository root; a `.dll`
  // launches through dotnet) overrides it. The F# host was removed (tag fsharp-host-final).
  const selection = process.env.NYRWVY_HOST || "zig"
  if (selection !== "zig") throw new Error(`NYRWVY_HOST must be zig, not ${selection}; the F# host was removed (tag fsharp-host-final).`)
  const selected = process.env.NYRWVY_HOST_EXECUTABLE
  const host = resolve(root, selected || `tools/zig/host/bin/nyrwvy-host${process.platform === "win32" ? ".exe" : ""}`)
  const managed = /\.dll$/i.test(host)
  const executable = managed ? "dotnet" : host
  // Mode read is open without the workspace lock; the session cannot commit.
  const args = mode === "read" ? ["open", "--snapshot", workspace, "--read-only"] : [mode, "--snapshot", workspace]
  for (const source of [...(dependency === undefined ? [] : [dependency]), ...(dependencies ?? [])]) {
    args.push("--dependency", resolve(worktree, source))
  }
  if (upgrade !== undefined) {
    if ("upgrades" in upgrade) {
      args.push("--upgrades", JSON.stringify(upgrade.upgrades.map(item => ({ source: resolve(worktree, item.source), replaces: item.replaces }))),
        "--review", upgrade.reviewFingerprint)
    } else if ("selection" in upgrade) {
      args.push("--selection", JSON.stringify({ sources: upgrade.selection.sources.map(source => resolve(worktree, source)), replaces: upgrade.selection.replaces }),
        "--review", upgrade.reviewFingerprint)
    } else {
      args.push("--expect", upgrade.expectedFingerprint)
      if (upgrade.replaces !== undefined) args.push("--replaces", upgrade.replaces)
    }
  }
  // These values are raw argv tokens; paths and structured upgrades above are already encoded.
  const rawValues = upgrade === undefined ? [] : "expectedFingerprint" in upgrade
    ? [upgrade.expectedFingerprint, upgrade.replaces]
    : [upgrade.reviewFingerprint]
  if (rawValues.some(value => value?.startsWith("-"))) {
    throw new Error("expectedFingerprint, replaces and reviewFingerprint must not start with '-' (command-line option).")
  }
  const allowHostSources = hostSourcesAllowed(context)
  if (allowHostSources) args.push("--allow-host-sources")
  if (context.abort?.aborted) throw new Error("Authoring open cancelled before starting.")
  const client = new AuthoringClient(spawn(executable, managed ? [host, ...args] : args, {
    cwd: worktree, detached: process.platform !== "win32", stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env, NYRWVY_REPOSITORY_ROOT: root },
  }), executable, !selected && selection === "zig")
  sessions.set(sessionKey, client)
  markStarted()
  try {
    const status = JSON.parse(await client.request({ op: "status" }, context.abort)) as Reply
    if (!status.ok) throw new Error(JSON.stringify(status.error))
    const result = (status as { result?: { build?: unknown } }).result
    // `npm run build:host` records its commit in a stamp beside the host instead of in the binary.
    const stamped = result?.build === "nyrwvy-zig-host"
      ? await readFile(`${host}.json`, "utf8").then(text => JSON.parse(text).commit).catch(() => undefined) : undefined
    if (result && typeof stamped === "string") result.build = `${result.build}+${stamped}`
    const warning = staleToolWarning(toolCodeCommit, result?.build)
    return JSON.stringify({ worktree, snapshot: workspace, status, hostSourcesAllowed: allowHostSources, toolCodeCommit,
      ...(warning === undefined ? {} : { warning }),
      guide: "Use nyrwvy_inspect, nyrwvy_create_type, nyrwvy_create_function and nyrwvy_replace. Inspect every returned review before nyrwvy_commit or nyrwvy_reject; compact result views are bounded to 16 KiB UTF-8. If projection.requiresDetailedReview is true, read the omitted impact/graph paths with nyrwvy_request op readProposal (proposalId,path,offset,limit; pages at most 8 KiB). Small reviews keep signature, node count, impact facts and fingerprints; detail \"full\" adds the proposed graph without a byte bound. nyrwvy_request exposes the full maintained protocol. No raw snapshot or generated-source edits. Close and open the same snapshot to resume; uncommitted proposal handles do not survive." })
  } catch (error) {
    try { await client.close() } catch (cleanupError) {
      if (!client.isReaped) throw new AggregateError([error, cleanupError], `Authoring open failed; unreaped child ownership is retained. Close the session before reopening. Open: ${error instanceof Error ? error.message : String(error)} Cleanup: ${cleanupError instanceof Error ? cleanupError.message : String(cleanupError)}`)
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
  // Wait for ownership, not the first reply: close itself bounds a silent request queue.
  await opening.get(sessionKey)
  const client = sessions.get(sessionKey)
  if (!client) return "No active Nyrwvy authoring session."
  try {
    await client.close()
    return "Authoring session closed. Only committed graph changes persist; reopen the snapshot to resume."
  } finally {
    if (client.isReaped && sessions.get(sessionKey) === client) sessions.delete(sessionKey)
  }
}
