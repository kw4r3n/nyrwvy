import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import { once } from "node:events"
import { appendFile, mkdir } from "node:fs/promises"
import { tmpdir } from "node:os"
import { isAbsolute, join, resolve } from "node:path"
import {
  appendResponseChunk,
  validateJsonObjectLine,
  type ResponseLineState,
} from "./evaluation-jsonl-runtime.ts"

const evaluationAgent = "nyrwvy-m4-authoring-eval"
const requestTimeoutMs = 120_000
const shutdownTimeoutMs = 5_000
const project = "tools/Nyrwvy.AuthoringSession/Nyrwvy.AuthoringSession.fsproj"
const transcriptName = "protocol-events.jsonl"

type ToolContext = {
  agent: string
  sessionID: string
  worktree: string
}

type ResponseWaiter = {
  resolve: (line: string) => void
  reject: (error: Error) => void
  timeout?: ReturnType<typeof setTimeout>
}

type Session = {
  child: ChildProcessWithoutNullStreams
  stdoutBuffer: string
  completedLines: string[]
  stderr: string
  waiters: ResponseWaiter[]
  closed: boolean
  reaped: boolean
  exited: Promise<number | null>
  closePromise?: Promise<void>
  failure?: Error
  workspacePath: string
  resultRoot: string
  worktree: string
}

// Keep the queue and finalizer attached to the owner, including across restart.
type SessionOwner = {
  current: Session
  operationTail: Promise<void>
  finalization?: Promise<string>
}

const sessions = new Map<string, SessionOwner>()
const startingSessions = new Set<string>()
const finalized = new Set<string>()

const protocolGuide = `Nyrwvy bounded authoring evaluation session started with a new maintained snapshot.
Use nyrwvy_m4_authoring_eval_request to send exactly one maintained AuthoringSession JSON object per call. Every request needs an id and op. Inspect every response; simulations return an exact proposalId that must be reviewed and committed explicitly.

Core requests:
  {"id":"s1","op":"status"}
  {"id":"d1","op":"declarations"}
  {"id":"f1","op":"focusFunction","functionId":"<published FunctionId>"}
  {"id":"c1","op":"commit","proposalId":"<proposalId>"}
  {"id":"x1","op":"reject","proposalId":"<proposalId>"}
  {"id":"r1","op":"executionReadiness"}
  {"id":"e1","op":"emitCSharp","profile":{"kind":"empty"}}
  {"id":"e2","op":"emitCSharp","profile":{"kind":"systemConsoleWriteLine","operationId":"<published operationId>"}}

Creation requests:
  simulateCreateType: id, op, displayName, typeKind. A product typeKind is {"kind":"product","fields":[{"displayName":"...","type":<TypeRef>}]}; a union typeKind is {"kind":"union","variants":[{"displayName":"...","fields":[{"displayName":"...","type":<TypeRef>}]}]}.
  simulateCreateEffect: id, op, displayName.
  simulateCreateCapability: id, op, displayName, operations. Each operation is {"displayName":"...","parameters":[{"displayName":"...","type":<TypeRef>}],"resultType":<TypeRef>,"failureType":null,"effectId":"<published EffectId>"}.
  simulateCreateFunction: id, op, displayName, parameters, resultType, requiredEffects, body. Function parameters are {"displayName":"...","type":<TypeRef>} and requiredEffects is an array of published EffectId strings. Its wire review is intentionally compact: receipt/readiness plus the complete createdFunction are authoritative for pre-commit review; duplicate materializedBody/proposedFocus projections are omitted.

Whole-body replacement:
  simulateReplaceBody requires id, op, functionId, expectedCurrentResultNodeId, and replacement. The caller must obtain the current result node from focusFunction first. Its wire review is intentionally compact: receipt/readiness, a diff with old/new result IDs plus added/removed node IDs, and proposedFunction. Duplicate full baseFocus/proposedFocus projections are omitted. Every simulation review is compact by default (functions as signature, resultNodeId and nodeCount); add "detail":"full" to a simulate request to include the proposed body graph.

Bounded M5 parameter-type migration:
  simulateParameterTypeMigration requires id, op, targetFunctionId, targetParameterId, newParameterType, targetReplacement, and callerRewrites.
  callerRewrites must contain exactly one complete replacement body for every affected direct caller: [{"callerFunctionId":"<published FunctionId>","replacement":<recipe>}].
  The maintained workflow discovers the actual caller set, preserves the target FunctionId and ParameterId, validates the target and caller bodies together, and proposes one final atomic snapshot. Missing, duplicate, unrelated, incompatible, recursive, or unsupported callback uses are rejected with diagnostics.
  Migration replacement recipes refer to parameters of existing functions by stable ID using {"kind":"parameter","parameterId":"<published ParameterId>"}; do not use parameterSlot in migration replacement bodies.

TypeRef forms needed here:
  {"kind":"primitive","primitive":"string"}
  {"kind":"primitive","primitive":"unit"}
  {"kind":"named","typeId":"<published TypeId>"}
  {"kind":"capability","capabilityId":"<published CapabilityId>"}

ExpressionRecipe forms needed by these workloads:
  {"kind":"parameterSlot","index":0}
  {"kind":"parameter","parameterId":"<published ParameterId>"}
  {"kind":"stringLiteral","value":"text"}
  {"kind":"stringContains","value":<recipe>,"substring":<recipe>}
  {"kind":"conditional","condition":<recipe>,"whenTrue":<recipe>,"whenFalse":<recipe>}
  {"kind":"productConstruct","typeId":"<published TypeId>","fields":[{"fieldId":"<published FieldId>","value":<recipe>}]}
  {"kind":"fieldAccess","source":<recipe>,"fieldId":"<published FieldId>"}
  {"kind":"unionConstruct","variantId":"<published VariantId>","fields":[{"fieldId":"<published FieldId>","value":<recipe>}]}
  {"kind":"directCall","functionId":"<published FunctionId>","arguments":[{"parameterId":"<published callee ParameterId>","value":<recipe>}]}
  {"kind":"matchUnion","input":<recipe>,"arms":[{"variantId":"<published VariantId>","body":<recipe>}]}
  {"kind":"patternReference","scopeDepth":0,"slot":"field","fieldId":"<field bound by the current union arm>"}
  {"kind":"capabilityInvoke","capabilityValue":<recipe>,"operationId":"<published operationId>","arguments":[{"capabilityParameterId":"<published capability parameterId>","value":<recipe>}]}

After each successful creation commit, query declarations and reuse the published IDs. Do not invent production IDs. Review every proposal before commit. For migration, verify the review's parameterChange, complete affectedCallSites, and bodyRewrites before committing. The restart tool closes the maintained process and reopens the same persisted snapshot; proposal handles do not survive it, so rediscover state afterward. Empty-profile C# emission is expected to fail specifically for a missing capability binding once the capability-backed program exists. Explicit System.Console.WriteLine emission should then succeed. Emitted backend source is an output artifact, not semantic authoring authority.`

function requireAgent(agent: string) {
  if (agent !== evaluationAgent) {
    throw new Error("Nyrwvy M4 authoring evaluation tools are available only to the nyrwvy-m4-authoring-eval agent.")
  }
}

async function record(session: Session, event: Record<string, unknown>) {
  await appendFile(join(session.resultRoot, transcriptName), `${JSON.stringify(event)}\n`, "utf8")
}

function flushLines(session: Session) {
  while (session.completedLines.length > 0 && session.waiters.length > 0) {
    const line = session.completedLines.shift()!
    const waiter = session.waiters.shift()!
    clearTimeout(waiter.timeout)
    waiter.resolve(line)
  }
}

function rejectWaiters(session: Session, error: Error) {
  for (const waiter of session.waiters.splice(0)) {
    clearTimeout(waiter.timeout)
    waiter.reject(error)
  }
}

function createSession(
  child: ChildProcessWithoutNullStreams,
  workspacePath: string,
  resultRoot: string,
  worktree: string,
): Session {
  const session: Session = {
    child,
    stdoutBuffer: "",
    completedLines: [],
    stderr: "",
    waiters: [],
    closed: false,
    reaped: false,
    exited: new Promise(resolveExit => child.once("close", resolveExit)),
    workspacePath,
    resultRoot,
    worktree,
  }

  child.stdout.setEncoding("utf8")
  child.stderr.setEncoding("utf8")
  child.stdout.on("data", (chunk: string) => {
    const next: ResponseLineState = appendResponseChunk(
      { buffered: session.stdoutBuffer, completedLines: session.completedLines },
      chunk,
    )
    session.stdoutBuffer = next.buffered
    session.completedLines = next.completedLines
    flushLines(session)
  })
  child.stderr.on("data", (chunk: string) => (session.stderr += chunk))
  const onError = (error: Error) => {
    const failure = new Error(`Nyrwvy maintained authoring session failed: ${error.message}`, { cause: error })
    session.closed = true
    session.failure ??= failure
    rejectWaiters(session, failure)
  }
  child.on("error", onError)
  child.stdin.on("error", (error) => {
    onError(error)
    void closeSession(session).catch(() => undefined)
  })
  child.on("close", (code) => {
    session.closed = true
    session.reaped = true
    const failure = new Error(
      `Nyrwvy maintained authoring session exited with code ${code ?? "signal"}. ${session.stderr}`,
    )
    session.failure ??= failure
    rejectWaiters(session, failure)
  })
  return session
}

function spawnAuthoring(mode: "create" | "open", workspacePath: string, resultRoot: string, worktree: string) {
  const child = spawn(
    "dotnet",
    [
      "run",
      "--project",
      project,
      "--configuration",
      "Release",
      "--no-build",
      "--",
      mode,
      "--snapshot",
      workspacePath,
    ],
    { cwd: worktree, stdio: ["pipe", "pipe", "pipe"] },
  )
  return createSession(child, workspacePath, resultRoot, worktree)
}

async function withLock<T>(owner: SessionOwner, operation: (session: Session) => Promise<T>): Promise<T> {
  const previous = owner.operationTail
  let release: () => void = () => undefined
  owner.operationTail = new Promise<void>((resolveLock) => (release = resolveLock))
  await previous
  try {
    return await operation(owner.current)
  } finally {
    release()
  }
}

async function readResponse(session: Session): Promise<string> {
  flushLines(session)
  const completed = session.completedLines.shift()
  if (completed !== undefined) return completed
  if (session.failure) throw session.failure

  return await new Promise<string>((resolveLine, reject) => {
    const waiter: ResponseWaiter = { resolve: resolveLine, reject }
    waiter.timeout = setTimeout(() => {
      const index = session.waiters.indexOf(waiter)
      if (index >= 0) session.waiters.splice(index, 1)
      session.failure ??= new Error("Nyrwvy maintained authoring request timed out; a sent mutation's outcome is unknown. Reopen and inspect committed state; requests are never replayed.")
      reject(session.failure)
      void closeSession(session).catch(() => undefined)
    }, requestTimeoutMs)
    session.waiters.push(waiter)
    flushLines(session)
  })
}

async function waitForExit(session: Session): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    return await Promise.race([
      session.exited.then(() => true),
      new Promise<false>(resolveTimeout => { timer = setTimeout(() => resolveTimeout(false), shutdownTimeoutMs) }),
    ])
  } finally {
    clearTimeout(timer)
  }
}

function closeSession(session: Session): Promise<void> {
  session.closed = true
  session.closePromise ??= Promise.resolve().then(async () => {
    try { session.child.stdin.end() } catch {}
    if (!await waitForExit(session)) {
      for (const signal of ["SIGTERM", "SIGKILL"] as const) {
        try { session.child.kill(signal) } catch (error) {
          session.failure ??= error instanceof Error ? error : new Error(String(error))
        }
        if (await waitForExit(session)) break
      }
    }
    if (!session.reaped) {
      throw new Error(`Nyrwvy maintained authoring session did not close within ${shutdownTimeoutMs}ms after SIGKILL; child ownership is retained until reaped.`)
    }
    const code = await session.exited
    if (code !== 0) {
      throw session.failure ?? new Error(`Nyrwvy maintained authoring session did not close cleanly. ${session.stderr}`)
    }
  })
  return session.closePromise
}

function configuredResultRoot(context: ToolContext) {
  const configured = process.env.NYRWVY_M4_AUTHORING_RESULT_ROOT
  if (configured) {
    if (!isAbsolute(configured)) throw new Error("NYRWVY_M4_AUTHORING_RESULT_ROOT must be absolute.")
    return resolve(configured)
  }
  return join(tmpdir(), `nyrwvy-m4-authoring-${context.sessionID.replace(/[^A-Za-z0-9_-]/g, "_")}-${randomUUID()}`)
}

export async function startM4AuthoringEvaluation(context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  if (finalized.has(context.sessionID)) throw new Error("This M4 authoring evaluation session already finalized.")
  if (startingSessions.has(context.sessionID) || sessions.has(context.sessionID)) {
    throw new Error("An M4 authoring evaluation session is already active or starting.")
  }

  startingSessions.add(context.sessionID)
  let owner: SessionOwner | undefined
  try {
    const resultRoot = configuredResultRoot(context)
    await mkdir(resultRoot, { recursive: true, mode: 0o700 })
    const workspacePath = join(resultRoot, "workspace.json")
    const session = spawnAuthoring("create", workspacePath, resultRoot, context.worktree)
    owner = { current: session, operationTail: Promise.resolve() }
    sessions.set(context.sessionID, owner)
    await once(session.child, "spawn")
    await record(session, { kind: "start" })
    if (session.failure) throw session.failure
    return protocolGuide
  } catch (error) {
    if (owner) {
      try { await closeSession(owner.current) } catch (cleanupError) {
        if (!owner.current.reaped) throw new AggregateError([error, cleanupError], "M4 authoring startup failed; unreaped child ownership is retained. Finalize before retrying.")
      } finally {
        if (owner.current.reaped) sessions.delete(context.sessionID)
      }
    }
    throw error
  } finally {
    startingSessions.delete(context.sessionID)
  }
}

export async function requestM4AuthoringEvaluation(requestText: string, context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  if (startingSessions.has(context.sessionID)) throw new Error("The M4 authoring evaluation session is still starting.")
  const owner = sessions.get(context.sessionID)
  if (!owner) throw new Error("No active M4 authoring evaluation session.")
  if (owner.finalization) throw new Error("The M4 authoring evaluation session is finalizing.")

  return await withLock(owner, async (session) => {
    if (session.closed || session.failure) {
      throw session.failure ?? new Error("Nyrwvy maintained authoring session is no longer available.")
    }
    const request = validateJsonObjectLine(requestText, "The M4 authoring request")
    const requestValue = JSON.parse(request) as Record<string, unknown>
    await record(session, { kind: "request", request: requestValue })
    await new Promise<void>((resolveWrite, reject) => {
      session.child.stdin.write(`${request}\n`, (error) => (error ? reject(error) : resolveWrite()))
    })
    const response = await readResponse(session)
    let responseValue: unknown = response
    try { responseValue = JSON.parse(response) } catch {}
    await record(session, { kind: "response", response: responseValue })
    return response
  })
}

export async function restartM4AuthoringEvaluation(context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  if (startingSessions.has(context.sessionID)) throw new Error("The M4 authoring evaluation session is still starting.")
  const owner = sessions.get(context.sessionID)
  if (!owner) throw new Error("No active M4 authoring evaluation session.")
  if (owner.finalization) throw new Error("The M4 authoring evaluation session is finalizing.")

  return await withLock(owner, async (session) => {
    await record(session, { kind: "restart" })
    await closeSession(session)
    const reopened = spawnAuthoring("open", session.workspacePath, session.resultRoot, session.worktree)
    owner.current = reopened
    try {
      await once(reopened.child, "spawn")
      await record(reopened, { kind: "reopened" })
      if (reopened.failure) throw reopened.failure
    } catch (error) {
      await closeSession(reopened).catch(() => undefined)
      throw error
    }
    return "Nyrwvy maintained authoring session reopened from the persisted snapshot. Rediscover status and declarations; pre-restart proposal handles are not valid after restart."
  })
}

export async function finalizeM4AuthoringEvaluation(context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  if (startingSessions.has(context.sessionID)) throw new Error("The M4 authoring evaluation session is still starting.")
  const owner = sessions.get(context.sessionID)
  if (!owner) return "No active M4 authoring evaluation session."

  owner.finalization ??= withLock(owner, async (session) => {
    try { await record(session, { kind: "finalize" }) }
    finally { await closeSession(session) }
    return "Nyrwvy M4 authoring evaluation session finalized. The persisted workspace remains in the evaluation result directory."
  })
  try {
    return await owner.finalization
  } finally {
    if (owner.current.reaped) {
      sessions.delete(context.sessionID)
      finalized.add(context.sessionID)
    }
  }
}
