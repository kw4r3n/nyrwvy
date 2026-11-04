// Runs each agent task N times with codex and reports how reliably the tools are used.
// usage: node evaluation/agent-eval/run.mjs [--runs 3] [--model gpt-6.1-sol] [--label name] [--out dir] [task.json ...]
//        node evaluation/agent-eval/run.mjs --summarize <out dir>
//        node evaluation/agent-eval/run.mjs --check <workspace> task.json   (verify one finished workspace)
// Success is judged here, not by the agent: the snapshot it committed is reopened and each check run with runPureByName.
import { spawn } from "node:child_process"
import { existsSync } from "node:fs"
import { mkdir, mkdtemp, readFile, readdir, writeFile, appendFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { basename, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { parseArgs } from "node:util"
import { summarizeEvents } from "./events.mjs"
import { Client, StdioClientTransport } from "../../tools/nyrwvy-mcp/client-sdk.mjs"

const here = fileURLToPath(new URL(".", import.meta.url))
const server = resolve(here, "../../tools/nyrwvy-mcp/server.mjs")
const { values, positionals } = parseArgs({ allowPositionals: true, options: {
  runs: { type: "string", default: "3" }, model: { type: "string", default: "gpt-6.1-sol" },
  label: { type: "string", default: "run" }, out: { type: "string" }, summarize: { type: "string" }, check: { type: "string" } } })

async function verify(workspace, checks) {
  const client = new Client({ name: "nyrwvy-agent-eval", version: "1.0.0" })
  await client.connect(new StdioClientTransport({ command: process.execPath, args: ["--experimental-strip-types", server],
    cwd: workspace, stderr: "ignore", env: process.env }))
  try {
    // The SDK cancels after 60 s by default; a cold C# tool build may take up to the adapter's 900 s.
    const options = { timeout: 900_000 }
    // A snapshot the agent never wrote is the agent's failure; any other open error is the verifier's.
    const opened = await client.callTool({ name: "nyrwvy_open", arguments: { mode: "read", snapshot: "workspace.json" } }, options)
    if (opened.isError) {
      if (!existsSync(join(workspace, "workspace.json"))) return checks.map(() => false)
      throw new Error(`verifier could not open the snapshot: ${opened.content?.[0]?.text ?? "unknown error"}`)
    }
    const results = []
    for (const check of checks) {
      const reply = await client.callTool({ name: "nyrwvy_request", arguments: { op: "runPureByName", arguments: check } }, options)
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
    // The evaluated agent auto-approves tools, so host sources stay off whatever the caller's environment says.
    const child = spawn("codex", args, { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, NYRWVY_ALLOW_HOST_SOURCES: "0" } })
    const out = [], err = []
    child.stdout.on("data", chunk => out.push(chunk))
    child.stderr.on("data", chunk => err.push(chunk))
    child.on("error", fail)
    child.on("close", code => Promise.all([
      writeFile(join(dir, "events.jsonl"), Buffer.concat(out)),
      writeFile(join(dir, "stderr.txt"), Buffer.concat(err)),
    ]).then(() => done(code), fail))
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
    const judged = list.filter(r => r.verifyError === undefined)
    console.log(`| ${key} | ${judged.filter(r => r.passed).length}/${judged.length}${judged.length < list.length ? ` (+${list.length - judged.length} verifier errors)` : ""} | ${mean(list.map(r => r.toolCalls))} | ${mean(list.map(r => r.rejections.length))} | ${codes || "-"} | ${median(list.map(r => r.input))} |`)
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
  const runs = Number(values.runs)
  if (!Number.isInteger(runs) || runs < 1) throw new Error(`--runs must be a positive integer, got ${values.runs}`)
  const out = values.out ? resolve(values.out) : await mkdtemp(join(tmpdir(), "nyrwvy-agent-eval-"))
  const rows = []
  for (const path of tasks) {
    const task = JSON.parse(await readFile(path, "utf8"))
    if (!task.checks?.length) throw new Error(`${path} has no checks; a run could never fail`)
    for (let i = 1; i <= runs; i++) {
      const dir = join(out, `${values.label}-${basename(path, ".json")}-${i}`)
      const workspace = join(dir, "ws")
      await mkdir(out, { recursive: true })
      await mkdir(dir) // fails on a reused --out/--label, so an old snapshot can never pass as a new run
      await mkdir(workspace)
      const started = Date.now()
      const exit = await codex(task, workspace, dir)
      const events = summarizeEvents((await readFile(join(dir, "events.jsonl"), "utf8")).split("\n"))
      let checks, verifyError
      try { checks = await verify(workspace, task.checks) } catch (error) { verifyError = String(error) }
      // A verifier failure is not the model's: such a run has no passed value and is left out of the pass rate.
      const row = { label: values.label, task: basename(path, ".json"), run: i, exit, ...(verifyError ? { verifyError } : { passed: checks.every(Boolean), checks }), seconds: Math.round((Date.now() - started) / 1000), ...events }
      rows.push(row)
      await appendFile(join(out, "results.jsonl"), JSON.stringify(row) + "\n")
      console.error(`${row.task} #${i}: ${verifyError ? `VERIFIER ERROR (${verifyError})` : row.passed ? "pass" : "FAIL"}, ${row.toolCalls} calls, ${row.rejections.length} rejected`)
    }
  }
  console.error(`results: ${join(out, "results.jsonl")}`)
  report(rows)
}
