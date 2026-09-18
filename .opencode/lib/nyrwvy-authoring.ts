import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { createInterface } from "node:readline"
import { resolve } from "node:path"

export type AuthoringContext = { sessionID: string; worktree: string; abort?: AbortSignal }
export type Request = { op: string; [key: string]: unknown }
type Reply = { id: string; ok: boolean; result?: unknown; error?: unknown }
type Pending = { id: string; resolve: (line: string) => void; reject: (error: Error) => void }

/** Transport only. All semantic decisions and publication remain in AuthoringSession. */
export class AuthoringClient {
  private pending?: Pending
  private failure?: Error
  private tail: Promise<unknown> = Promise.resolve()
  private closing = false
  private stderr = ""
  private exited: Promise<number | null>

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
        pending.resolve(line)
      } catch (error) {
        this.fail(error instanceof Error ? error : new Error(String(error)))
      }
    })
    lines.on("close", () => {
      if (this.pending) this.fail(new Error(`AuthoringSession closed before replying. ${this.stderr} Reopen and inspect committed state.`))
    })
    child.on("error", (error) => this.fail(error))
    child.stdin.on("error", (error) => this.fail(error))
    this.exited = new Promise((finish) => {
      child.once("close", (code) => {
        this.fail(new Error(`AuthoringSession exited (${code ?? "signal"}). ${this.stderr}`), false)
        finish(code)
      })
    })
  }

  private fail(error: Error, kill = true) {
    this.failure ??= error
    this.pending?.reject(this.failure)
    this.pending = undefined
    if (kill) this.child.kill()
  }

  request(request: Request, signal?: AbortSignal): Promise<string> {
    if (this.closing) return Promise.reject(new Error("AuthoringSession is closing."))
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
      const abort = () => this.fail(new Error("Authoring request cancelled; reopen and inspect committed state before retrying."))
      try {
        return await new Promise<string>((accept, reject) => {
          this.pending = { id, resolve: accept, reject }
          signal?.addEventListener("abort", abort, { once: true })
          this.child.stdin.write(`${line}\n`, (error) => { if (error) this.fail(error) })
        })
      } finally {
        signal?.removeEventListener("abort", abort)
      }
    })
    this.tail = operation.catch(() => undefined)
    return operation
  }

  async close(): Promise<void> {
    this.closing = true
    await this.tail
    this.child.stdin.end()
    const code = await this.exited
    if (code !== 0) throw this.failure ?? new Error(`AuthoringSession exited (${code}).`)
  }
}

const sessions = new Map<string, AuthoringClient>()
function key(context: AuthoringContext) { return `${resolve(context.worktree)}\0${context.sessionID}` }

export async function openAuthoring(
  mode: "create" | "open", snapshot: string, context: AuthoringContext,
): Promise<string> {
  const sessionKey = key(context)
  if (sessions.has(sessionKey)) throw new Error("An authoring session is already open. Close it before opening another.")
  const workspace = resolve(context.worktree, snapshot)
  const assembly = resolve(context.worktree, "tools/Nyrwvy.AuthoringSession/bin/Release/net10.0/Nyrwvy.AuthoringSession.dll")
  const client = new AuthoringClient(spawn("dotnet", [assembly, mode, "--snapshot", workspace], {
    cwd: context.worktree, stdio: ["pipe", "pipe", "pipe"],
  }))
  sessions.set(sessionKey, client)
  try {
    const status = JSON.parse(await client.request({ op: "status" }, context.abort)) as Reply
    if (!status.ok) throw new Error(JSON.stringify(status.error))
    return JSON.stringify({ snapshot: workspace, status,
      guide: "Use nyrwvy_inspect, nyrwvy_create_type, nyrwvy_create_function and nyrwvy_replace. Inspect every returned review before nyrwvy_commit or nyrwvy_reject. nyrwvy_request exposes the full maintained protocol. No raw snapshot or generated-source edits. Close and open the same snapshot to resume; uncommitted proposal handles do not survive." })
  } catch (error) {
    sessions.delete(sessionKey)
    await client.close().catch(() => undefined)
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
    sessions.delete(sessionKey)
  }
}
