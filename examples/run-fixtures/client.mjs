// Saved run values go directly to the ordinary MCP tool, never through a model transcript.
import { readFile, appendFile, mkdtemp } from "node:fs/promises"
import { tmpdir } from "node:os"
import { basename, dirname, join, resolve } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

export async function loadFixtures(paths) {
  return Promise.all(paths.map(async path => {
    const request = JSON.parse(await readFile(path, "utf8"))
    if (!request || !["runPure", "runPureByName"].includes(request.op)
      || Object.keys(request).some(key => !["op", "arguments"].includes(key))
      || !request.arguments || Array.isArray(request.arguments) || typeof request.arguments !== "object"
      || !Object.hasOwn(request.arguments, "expected")
      || ["hostSources", "id", "op"].some(key => Object.hasOwn(request.arguments, key))) {
      throw new Error(`Invalid fixture ${basename(path)}: use {op: runPure or runPureByName, arguments: {..., expected}} without hostSources or request ids.`)
    }
    return { label: basename(path), request }
  }))
}

function payload(response) {
  if (response.structuredContent) return response.structuredContent
  const text = response.content?.find(block => block.type === "text")?.text
  try { return JSON.parse(text) } catch { return undefined }
}

export async function runFixtures(client, fixtures, transcript, report = console.log) {
  const counts = { passed: 0, failed: 0, unknown: 0 }
  for (const { label, request } of fixtures) {
    let response
    try {
      response = await client.callTool({ name: "nyrwvy_request", arguments: request })
    } catch (error) {
      // The request may have executed. Keep the evidence and stop; never replay it.
      await appendFile(transcript, JSON.stringify({ label, request, transportError: String(error) }) + "\n")
      counts.unknown += 1
      report(`${label}: UNKNOWN (transport interrupted; remaining fixtures were not sent)`)
      return counts
    }
    await appendFile(transcript, JSON.stringify({ label, request, response }) + "\n")
    const value = payload(response)
    const result = value?.result ?? value
    const outcome = response.isError || value?.ok === false ? "failed"
      : result?.matches === true ? "passed" : result?.matches === false ? "failed" : "unknown"
    counts[outcome] += 1
    report(`${label}: ${outcome.toUpperCase()}`)
  }
  return counts
}

async function main() {
  const [snapshotArg, ...paths] = process.argv.slice(2)
  if (!snapshotArg || paths.length === 0) throw new Error("Usage: node examples/run-fixtures/client.mjs SNAPSHOT REQUEST.json [REQUEST.json ...]")
  // Parse every input before launching the server. Relative paths use the caller's cwd.
  const fixtures = await loadFixtures(paths)
  const snapshot = resolve(snapshotArg)
  const output = await mkdtemp(join(tmpdir(), "nyrwvy-run-fixtures-"))
  const transcript = join(output, "transcript.jsonl")
  console.log(`Full requests and responses: ${transcript}`)
  // The locked SDK belongs to the MCP workspace and need not be hoisted to root.
  const { Client, StdioClientTransport } = await import("../../tools/nyrwvy-mcp/client-sdk.mjs")
  const client = new Client({ name: "nyrwvy-run-fixtures", version: "1.0.0" })
  const server = fileURLToPath(new URL("../../tools/nyrwvy-mcp/server.mjs", import.meta.url))
  try {
    await client.connect(new StdioClientTransport({ command: process.execPath,
      args: ["--experimental-strip-types", server, "--workspace", dirname(snapshot)],
      cwd: dirname(snapshot), env: { NYRWVY_ALLOW_HOST_SOURCES: "0" }, stderr: "inherit" }))
    const opened = await client.callTool({ name: "nyrwvy_open", arguments: { mode: "open", snapshot } })
    await appendFile(transcript, JSON.stringify({ phase: "open", response: opened }) + "\n")
    if (opened.isError || payload(opened)?.ok === false) throw new Error("Could not open snapshot; see transcript.")
    const counts = await runFixtures(client, fixtures, transcript)
    console.log(`${counts.passed} passed, ${counts.failed} failed, ${counts.unknown} unknown`)
    if (counts.failed || counts.unknown) process.exitCode = 1
  } finally {
    await client.close()
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => { console.error(error.message); process.exitCode = 1 })
}
