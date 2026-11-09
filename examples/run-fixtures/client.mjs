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
      || ["hostSources", "id", "op", "backend"].some(key => Object.hasOwn(request.arguments, key))) {
      throw new Error(`Invalid fixture ${basename(path)}: use {op: runPure or runPureByName, arguments: {..., expected}} without hostSources, backend or request ids.`)
    }
    return { label: basename(path), request }
  }))
}

function payload(response) {
  if (response.structuredContent) return response.structuredContent
  const text = response.content?.find(block => block.type === "text")?.text
  try { return JSON.parse(text) } catch { return undefined }
}

// The SDK cancels after 60 s by default; a cold C# tool build may take up to the adapter's 900 s.
const callTimeoutMs = 900_000

export async function runFixtures(client, fixtures, transcript, report = console.log) {
  const counts = { passed: 0, failed: 0, unknown: 0 }
  for (const { label, request } of fixtures) {
    let response
    try {
      response = await client.callTool({ name: "nyrwvy_request", arguments: request }, { timeout: callTimeoutMs })
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

/// The workspace's declared targets (`<snapshot>.backends.json`), or null without the sidecar.
export async function readTargets(snapshot) {
  let text
  try { text = await readFile(snapshot + ".backends.json", "utf8") } catch (error) {
    if (error.code === "ENOENT") return null
    throw error
  }
  const targets = JSON.parse(text)?.targets
  if (!Array.isArray(targets) || targets.length === 0 || targets.some(name => typeof name !== "string")) {
    throw new Error(`${snapshot}.backends.json must be an object with a non-empty "targets" array of backend names.`)
  }
  return targets
}

/// What two backends must agree on: the value and calls of a run, or the failure and calls.
export function comparable(value) {
  const result = value?.result ?? value
  if (value?.ok === false) {
    const error = value.error ?? {}
    return JSON.stringify([error.code, error.message, error.failureKind, error.exceptionType, error.calls])
  }
  return JSON.stringify([result?.actual, result?.calls])
}

/// Sends every fixture once per backend and compares the backends: PASS when every backend that
/// supports the entry agrees and matches `expected`; DISAGREE when they differ; UNSUPPORTED is
/// reported per backend and never counts as agreement or as a pass by itself.
export async function runParity(client, fixtures, backends, transcript, report = console.log) {
  const counts = { passed: 0, failed: 0, unknown: 0, unsupported: 0 }
  for (const { label, request } of fixtures) {
    const outcomes = []
    for (const backend of backends) {
      const sent = { ...request, arguments: { ...request.arguments, backend } }
      let response
      try {
        response = await client.callTool({ name: "nyrwvy_request", arguments: sent }, { timeout: callTimeoutMs })
      } catch (error) {
        await appendFile(transcript, JSON.stringify({ label, backend, request: sent, transportError: String(error) }) + "\n")
        counts.unknown += 1
        report(`${label}: UNKNOWN on ${backend} (transport interrupted; remaining fixtures were not sent)`)
        return counts
      }
      await appendFile(transcript, JSON.stringify({ label, backend, request: sent, response }) + "\n")
      const value = payload(response)
      if (value?.ok === false && value.error?.code === "RunBackendUnsupported") outcomes.push({ backend, unsupported: true })
      else outcomes.push({ backend, value, transportFailed: response.isError && value === undefined })
    }
    const supported = outcomes.filter(outcome => !outcome.unsupported)
    const unsupported = outcomes.filter(outcome => outcome.unsupported).map(outcome => outcome.backend)
    const note = unsupported.length ? ` (unsupported on ${unsupported.join(", ")})` : ""
    if (unsupported.length > 0) { // a partial run compares nothing, so it must not pass
      counts.unsupported += 1
      report(`${label}: UNSUPPORTED${note}`)
    } else if (supported.some(outcome => outcome.transportFailed)) {
      counts.unknown += 1
      report(`${label}: UNKNOWN${note}`)
    } else if (new Set(supported.map(outcome => comparable(outcome.value))).size > 1) {
      counts.failed += 1
      report(`${label}: DISAGREE between ${supported.map(outcome => outcome.backend).join(", ")}${note}`)
    } else {
      const result = supported[0].value?.result ?? supported[0].value
      const outcome = supported[0].value?.ok === false ? "failed" : result?.matches === true ? "passed" : result?.matches === false ? "failed" : "unknown"
      counts[outcome] += 1
      report(`${label}: ${outcome.toUpperCase()} on ${supported.map(item => item.backend).join(", ")}${note}`)
    }
  }
  return counts
}

async function main() {
  const argv = process.argv.slice(2)
  const option = argv[0] === "--backends" ? argv.splice(0, 2)[1] : undefined
  const [snapshotArg, ...paths] = argv
  if (!snapshotArg || paths.length === 0) throw new Error("Usage: node examples/run-fixtures/client.mjs [--backends csharp,zig] SNAPSHOT REQUEST.json [REQUEST.json ...]")
  // Parse every input before launching the server. Relative paths use the caller's cwd.
  const fixtures = await loadFixtures(paths)
  const snapshot = resolve(snapshotArg)
  // Several backends, given or declared by the workspace, turn the run into a parity check.
  const backends = option ? option.split(",").filter(Boolean) : await readTargets(snapshot)
  const parity = backends && (option !== undefined || backends.length > 1)
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
      cwd: dirname(snapshot), env: { NYRWVY_ALLOW_HOST_SOURCES: "0",
        ...(process.env.NYRWVY_HOST_EXECUTABLE ? { NYRWVY_HOST_EXECUTABLE: process.env.NYRWVY_HOST_EXECUTABLE } : {}),
        ...(process.env.NYRWVY_ZIG ? { NYRWVY_ZIG: process.env.NYRWVY_ZIG } : {}) }, stderr: "inherit" }))
    const opened = await client.callTool({ name: "nyrwvy_open", arguments: { mode: "open", snapshot } }, { timeout: callTimeoutMs })
    await appendFile(transcript, JSON.stringify({ phase: "open", response: opened }) + "\n")
    if (opened.isError || payload(opened)?.ok === false) throw new Error("Could not open snapshot; see transcript.")
    const counts = parity ? await runParity(client, fixtures, backends, transcript) : await runFixtures(client, fixtures, transcript)
    console.log(`${counts.passed} passed, ${counts.failed} failed, ${counts.unknown} unknown` + (parity ? `, ${counts.unsupported} unsupported` : ""))
    if (counts.failed || counts.unknown || counts.unsupported) process.exitCode = 1
  } finally {
    await client.close()
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch(error => { console.error(error.message); process.exitCode = 1 })
}
