// Runs each agent task N times with codex and reports how reliably the tools are used.
// usage: node evaluation/agent-eval/run.mjs [--runs 3] [--model gpt-6.1-sol] [--label name] [--out dir] [task.json ...]
//        node evaluation/agent-eval/run.mjs --summarize <out dir>
//        node evaluation/agent-eval/run.mjs --check <workspace> task.json   (verify one finished workspace)
// Success is judged here, not by the agent: the snapshot it committed is reopened and each check run with runPureByName.
import { spawn } from "node:child_process"
import { mkdir, mkdtemp, readFile, readdir, writeFile, appendFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { basename, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { parseArgs } from "node:util"
import { Client, StdioClientTransport } from "../../tools/nyrwvy-mcp/client-sdk.mjs"

const here = fileURLToPath(new URL(".", import.meta.url))
const server = resolve(here, "../../tools/nyrwvy-mcp/server.mjs")
const { values, positionals } = parseArgs({ allowPositionals: true, options: {
  runs: { type: "string", default: "3" }, model: { type: "string", default: "gpt-6.1-sol" },
  label: { type: "string", default: "run" }, out: { type: "string" }, summarize: { type: "string" }, check: { type: "string" } } })

// Summarizes one codex --json event log: tool calls, rejected calls by error code, token usage.
function summarizeEvents(lines) {
  const events = lines.filter(Boolean).map(line => JSON.parse(line))
  const calls = events.filter(e => e.type === "item.completed" && e.item.type === "mcp_tool_call").map(e => e.item)
  const rejections = calls.filter(c => c.status !== "completed").map(c => {
    const text = c.result?.content?.[0]?.text ?? c.error?.message ?? ""
    try { return JSON.parse(text).error?.code ?? "unknown" } catch { return text.slice(0, 60) || "unknown" }
  })
  const usage = events.filter(e => e.type === "turn.completed").map(e => e.usage)
  const sum = key => usage.reduce((total, u) => total + (u[key] ?? 0), 0)
  return { toolCalls: calls.length, rejections, input: sum("input_tokens"), cached: sum("cached_input_tokens"), output: sum("output_tokens") }
}

async function verify(workspace, checks) {
  const client = new Client({ name: "nyrwvy-agent-eval", version: "1.0.0" })
  await client.connect(new StdioClientTransport({ command: process.execPath, args: ["--experimental-strip-types", server],
    cwd: workspace, stderr: "ignore", env: process.env }))
  try {
    const opened = await client.callTool({ name: "nyrwvy_open", arguments: { mode: "read", snapshot: "workspace.json" } })
    if (opened.isError) return checks.map(() => false)
    const results = []
    for (const check of checks) {
      const reply = await client.callTool({ name: "nyrwvy_request", arguments: { op: "runPureByName", arguments: check } })
      results.push(reply.isError !== true && reply.structuredContent?.result?.matches === true)
    }
    return results
  } finally { await client.close() }
}

function codex(task, workspace, dir) {
  const args = ["exec", "--ignore-user-config", "--ephemeral", "--skip-git-repo-check", "-C", workspace, "-m", values.model, "-s", "workspace-write",
    "-c", `mcp_servers.nyrwvy.command=${JSON.stringify(process.execPath)}`,
    "-c", `mcp_servers.nyrwvy.args=${JSON.stringify(["--experimental-strip-types", server])}`,
    "-c", 'mcp_servers.nyrwvy.default_tools_approval_mode="approve"', "--json", "-o", join(dir, "final.txt"), "-"]
  return new Promise((done, fail) => {
    const child = spawn("codex", args, { stdio: ["pipe", "pipe", "pipe"] })
    const out = [], err = []
    child.stdout.on("data", chunk => out.push(chunk))
    child.stderr.on("data", chunk => err.push(chunk))
    child.on("error", fail)
    child.on("close", async code => {
      await writeFile(join(dir, "events.jsonl"), Buffer.concat(out))
      await writeFile(join(dir, "stderr.txt"), Buffer.concat(err))
      done(code)
    })
    child.stdin.end(task.prompt)
  })
}

function report(rows) {
  const median = list => { const s = [...list].sort((a, b) => a - b); return s.length ? s[Math.floor((s.length - 1) / 2)] : 0 }
  const mean = list => list.length ? (list.reduce((a, b) => a + b, 0) / list.length).toFixed(1) : "0"
  const groups = Map.groupBy(rows, row => `${row.label} ${row.task}`)
  console.log("| label task | passed | tool calls (mean) | rejected (mean) | rejection codes | input tokens (median) |")
  console.log("| --- | ---: | ---: | ---: | --- | ---: |")
  for (const [key, list] of groups) {
    const codes = Object.entries(Object.groupBy(list.flatMap(r => r.rejections), code => code)).map(([code, all]) => `${code}×${all.length}`).join(", ")
    console.log(`| ${key} | ${list.filter(r => r.passed).length}/${list.length} | ${mean(list.map(r => r.toolCalls))} | ${mean(list.map(r => r.rejections.length))} | ${codes || "-"} | ${median(list.map(r => r.input))} |`)
  }
}

if (values.check) {
  const task = JSON.parse(await readFile(positionals[0], "utf8"))
  console.log(JSON.stringify(await verify(resolve(values.check), task.checks)))
} else if (values.summarize) {
  const text = await readFile(join(values.summarize, "results.jsonl"), "utf8")
  report(text.split("\n").filter(Boolean).map(line => JSON.parse(line)))
} else {
  const tasks = positionals.length ? positionals : (await readdir(join(here, "tasks"))).filter(name => name.endsWith(".json")).map(name => join(here, "tasks", name))
  const out = values.out ? resolve(values.out) : await mkdtemp(join(tmpdir(), "nyrwvy-agent-eval-"))
  const rows = []
  for (const path of tasks) {
    const task = JSON.parse(await readFile(path, "utf8"))
    for (let i = 1; i <= Number(values.runs); i++) {
      const dir = join(out, `${values.label}-${basename(path, ".json")}-${i}`)
      const workspace = join(dir, "ws")
      await mkdir(workspace, { recursive: true })
      const started = Date.now()
      const exit = await codex(task, workspace, dir)
      const events = summarizeEvents((await readFile(join(dir, "events.jsonl"), "utf8")).split("\n"))
      const checks = await verify(workspace, task.checks).catch(() => task.checks.map(() => false))
      const row = { label: values.label, task: basename(path, ".json"), run: i, exit, passed: checks.every(Boolean), checks, seconds: Math.round((Date.now() - started) / 1000), ...events }
      rows.push(row)
      await appendFile(join(out, "results.jsonl"), JSON.stringify(row) + "\n")
      console.error(`${row.task} #${i}: ${row.passed ? "pass" : "FAIL"}, ${row.toolCalls} calls, ${row.rejections.length} rejected`)
    }
  }
  console.error(`results: ${join(out, "results.jsonl")}`)
  report(rows)
}
