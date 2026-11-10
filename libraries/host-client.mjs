// Shared by the library author and check scripts: sends one JSONL request to a spawned host and returns its parsed reply.
import { createInterface } from "node:readline"

export function hostExchange(child) {
  const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]()
  let n = 0, stderr = ""
  child.stderr?.on("data", chunk => { stderr = (stderr + chunk).slice(-2000) })
  child.stdin.on("error", () => {}) // a dead host is reported from its closed stdout below
  return async request => {
    child.stdin.write(JSON.stringify({ id: String(n++), ...request }) + "\n")
    const line = await lines.next()
    if (line.done) {
      if (child.exitCode === null && child.signalCode === null) await new Promise(done => child.once("close", done))
      throw new Error(`host exited (${child.exitCode ?? child.signalCode})${stderr.trim() === "" ? "" : `: ${stderr.trim()}`}`)
    }
    return JSON.parse(line.value)
  }
}

// Wraps an exchange: throws on a failed reply and returns its result. `label` names the request in the error.
export const hostSend = (exchange, label = request => request.op) => async request => {
  const reply = await exchange(request)
  if (!reply.ok) throw new Error(`${label(request)}: ${JSON.stringify(reply.error)}`)
  return reply.result
}
