import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process"
import {
  appendResponseChunk,
  validateJsonObjectLine,
  type ResponseLineState,
} from "./evaluation-jsonl-runtime.ts"

const evaluationAgent = "nyrwvy-pure-control-eval"
const protocolVersion = "pure-expression-control-flow-v1"
const requestTimeoutMs = 120_000
const shutdownTimeoutMs = 5_000

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
}

const sessions = new Map<string, Session>()
const finalized = new Set<string>()

function requireAgent(agent: string) {
  if (agent !== evaluationAgent) {
    throw new Error("Nyrwvy pure-control evaluation tools are available only to the nyrwvy-pure-control-eval agent.")
  }
}

export function validateRequestLine(request: string): string {
  return validateJsonObjectLine(request, "The pure-control evaluation request")
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

function createSession(child: ChildProcessWithoutNullStreams): Session {
  const session: Session = {
    child,
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
    const failure = new Error("Nyrwvy pure-control evaluation runner failed.")
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
      `Nyrwvy pure-control evaluation runner exited with code ${code ?? "signal"}. ${session.stderr}`,
    )
    session.failure ??= failure
    rejectWaiters(session, failure)
  })
  return session
}

async function withLock<T>(session: Session, operation: () => Promise<T>): Promise<T> {
  const previous = session.operationTail
  let release: () => void = () => undefined
  session.operationTail = new Promise<void>((resolve) => (release = resolve))
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

  return await new Promise<string>((resolve, reject) => {
    const waiter: ResponseWaiter = { resolve, reject }
    waiter.timeout = setTimeout(() => {
      const index = session.waiters.indexOf(waiter)
      if (index >= 0) session.waiters.splice(index, 1)
      reject(new Error("Nyrwvy pure-control evaluation request timed out."))
      try { session.child.kill() } catch {}
    }, requestTimeoutMs)
    session.waiters.push(waiter)
    flushLines(session)
  })
}

async function waitForExit(child: ChildProcessWithoutNullStreams): Promise<number | null> {
  if (child.exitCode !== null || child.signalCode !== null) return child.exitCode
  return await new Promise<number | null>((resolve) => {
    let settled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const finish = (code: number | null) => {
      if (settled) return
      settled = true
      if (timer) clearTimeout(timer)
      child.removeListener("exit", onExit)
      resolve(code)
    }
    const onExit = (code: number | null) => finish(code)
    child.once("exit", onExit)
    timer = setTimeout(() => finish(child.exitCode), shutdownTimeoutMs)
    timer.unref?.()
  })
}

export async function startPureControlEvaluation(context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  if (finalized.has(context.sessionID)) {
    throw new Error("This pure-control evaluation agent session already finalized its run.")
  }
  if (sessions.has(context.sessionID)) {
    throw new Error("A pure-control evaluation session is already active.")
  }

  const child = spawn(
    "dotnet",
    [
      "run",
      "--project",
      "tools/Nyrwvy.AgentEval.PureExpression/Nyrwvy.AgentEval.PureExpression.fsproj",
      "--configuration",
      "Release",
      "--no-build",
      "--",
      "--protocol",
      protocolVersion,
    ],
    { cwd: context.worktree, stdio: ["pipe", "pipe", "pipe"] },
  )
  sessions.set(context.sessionID, createSession(child))
  return "Nyrwvy pure-control evaluation session started. Send describeV1 first."
}

export async function requestPureControlEvaluation(
  requestText: string,
  context: ToolContext,
): Promise<string> {
  requireAgent(context.agent)
  const session = sessions.get(context.sessionID)
  if (!session) throw new Error("No active pure-control evaluation session.")

  return await withLock(session, async () => {
    if (session.closed || session.failure) {
      throw session.failure ?? new Error("Nyrwvy pure-control evaluation session is no longer available.")
    }
    const request = validateRequestLine(requestText)
    await new Promise<void>((resolve, reject) => {
      session.child.stdin.write(`${request}\n`, (error) => (error ? reject(error) : resolve()))
    })
    return await readResponse(session)
  })
}

export async function finalizePureControlEvaluation(context: ToolContext): Promise<string> {
  requireAgent(context.agent)
  const session = sessions.get(context.sessionID)
  if (!session) return "No active pure-control evaluation session."

  return await withLock(session, async () => {
    sessions.delete(context.sessionID)
    finalized.add(context.sessionID)
    try { session.child.stdin.end() } catch {}
    let code = await waitForExit(session.child)
    if (code === null && session.child.exitCode === null) {
      try { session.child.kill() } catch {}
      code = await waitForExit(session.child)
    }
    if (code !== 0) {
      throw session.failure ?? new Error(`Nyrwvy pure-control evaluation runner did not finalize cleanly. ${session.stderr}`)
    }
    return "Nyrwvy pure-control evaluation session finalized."
  })
}
