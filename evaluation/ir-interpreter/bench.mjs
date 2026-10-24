// Per-request runPureByName latency, compiled C# vs Execution IR interpreter, each mode in a fresh session process.
import { spawn } from "node:child_process"
import { createInterface } from "node:readline"

const [dll, snapshot, rounds = "10"] = process.argv.slice(2)
const int = value => ({ kind: "int64", value: String(value) })
const words = n => Array.from({ length: n }, (_, i) => "w" + i).join("  ")
const cases = [
  { name: "add", args: i => [int(i), int(2)] },
  { name: "wordCount", args: i => [{ kind: "string", value: words(50 + i) }] },
  { name: "sumTo", args: i => [int(100000 + i)] },
  { name: "fib", args: i => [int(20 + (i % 3))] },
]

async function session(mode) {
  const env = { ...process.env }
  if (mode === "interpreter") env.NYRWVY_RUNPURE_EXECUTOR = "interpreter"
  else delete env.NYRWVY_RUNPURE_EXECUTOR
  const child = spawn("dotnet", [dll, "open", "--snapshot", snapshot, "--read-only"], { env, stdio: ["pipe", "pipe", "inherit"] })
  const lines = createInterface({ input: child.stdout })
  const pending = []
  lines.on("line", line => pending.shift()?.(JSON.parse(line)))
  let next = 0
  const send = request => new Promise(resolve => {
    pending.push(resolve)
    child.stdin.write(JSON.stringify({ id: String(next++), ...request }) + "\n")
  })
  return { send, close: () => child.kill() }
}

const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)]
for (const mode of ["compiled", "interpreter"]) {
  const started = performance.now()
  const { send, close } = await session(mode)
  await send({ op: "status" })
  const ready = performance.now() - started
  const rows = []
  for (const c of cases) {
    const times = []
    let result
    for (let i = 0; i <= Number(rounds); i++) {
      const t = performance.now()
      result = await send({ op: "runPureByName", function: c.name, arguments: c.args(i) })
      times.push(performance.now() - t)
      if (!result.ok) throw new Error(JSON.stringify(result))
    }
    rows.push({ case: c.name, firstMs: +times[0].toFixed(1), medianMs: +median(times.slice(1)).toFixed(1), min: +Math.min(...times.slice(1)).toFixed(1), max: +Math.max(...times.slice(1)).toFixed(1), actual: result.result.actual?.value })
  }
  close()
  console.log(JSON.stringify({ mode, sessionReadyMs: +ready.toFixed(0), rows }))
}
