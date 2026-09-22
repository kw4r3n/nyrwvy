import { randomUUID } from "node:crypto"
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
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
  operationTail: Promise<void>
  closed: boolean
  failure?: Error
  workspacePath: string
  resultRoot: string
  worktree: string
}

const sessions = new Map<string, Session>()
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
  simulateReplaceBody requires id, op, functionId, expectedCurrentResultNodeId, and replacement. The caller must obtain the current result node from focusFunction first. Its wire review is intentionally compact: receipt/readiness, a diff with old/new result IDs plus added/removed node IDs, and the complete proposedFunction. Duplicate full baseFocus/proposedFocus projections are omitted.

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
    operationTail: Promise.resolve(),
    closed: false,
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
  const onError = () => {
    const failure = new Error("Nyrwvy maintained authoring session failed.")
    session.closed = true
    session.failure ??= failure
    rejectWaiters(session, failure)
  }
  child.on("error", onError)
  child.stdin.on("error", () => {
    onError()
    child.kill()
  })
  child.on("close", (code) => {
    session.closed = true
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

async function withLock<T>(session: Session, operation: () => Promise<T>): Promise<T> {
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
      reject(new Error("Nyrwvy maintained authoring request timed out."))
      try { session.child.kill() } catch {}
    }, requestTimeoutMs)
    session.waiters.push(waiter)
    flushLines(session)
  })
}

async function waitForExit(child: ChildProcessWithoutNullStreams): Promise<number | null> {
  if (child.exitCode !== null || child.signalCode !== null) return child.exitCode
  return await new Promise<number | null>((resolveExit) => {
    let settled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const finish = (code: number | null) => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      child.removeListener("exit", onExit)
      resolveExit(code)
    }
    const onExit = (code: number | null) => finish(code)
    child.once("exit", onExit)
    timer = setTimeout(() => finish(child.exitCode), shutdownTimeoutMs)
    timer.unref?.()
  })
}

async function closeSession(session: Session) {
  try { session.child.stdin.end() } catch {}
  let code = await waitForExit(session.child)
  if (code === null && session.child.exitCode === null) {
    try { session.child.kill() } catch {}
    code = await waitForExit(session.child)
  }
  if (code !== 0) {
    throw session.failure ?? new Error(`Nyrwvy maintained authoring session did not close cleanly. ${session.stderr}`)
  }
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
  if (sessions.has(context.sessionID)) throw new Error("An M4 authoring evaluation session is already active.")

  const resultRoot = configuredResultRoot(context)
  await mkdir(resultRoot, { recursive: true, mode: 0o700 })
  const workspacePath = join(resultRoot, "workspace.json")
  const session = spawnAuthoring("create", workspacePath, resultRoot, context.worktree)
  sessions.set(context.sessionID, session)
  await record(session, { kind: "start" })
  return protocolGuide
}

export async function requestM4AuthoringEvaluation(requestText: string, context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  const session = sessions.get(context.sessionID)
  if (!session) throw new Error("No active M4 authoring evaluation session.")

  return await withLock(session, async () => {
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
  const session = sessions.get(context.sessionID)
  if (!session) throw new Error("No active M4 authoring evaluation session.")

  return await withLock(session, async () => {
    await record(session, { kind: "restart" })
    await closeSession(session)
    const reopened = spawnAuthoring("open", session.workspacePath, session.resultRoot, session.worktree)
    sessions.set(context.sessionID, reopened)
    await record(reopened, { kind: "reopened" })
    return "Nyrwvy maintained authoring session reopened from the persisted snapshot. Rediscover status and declarations; pre-restart proposal handles are not valid after restart."
  })
}

export async function finalizeM4AuthoringEvaluation(context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  const session = sessions.get(context.sessionID)
  if (!session) return "No active M4 authoring evaluation session."

  return await withLock(session, async () => {
    await record(session, { kind: "finalize" })
    sessions.delete(context.sessionID)
    finalized.add(context.sessionID)
    await closeSession(session)
    return "Nyrwvy M4 authoring evaluation session finalized. The persisted workspace remains in the evaluation result directory."
  })
}
