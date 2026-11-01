#!/usr/bin/env node
// Builds the native session host (tools/zig/host) the way ZigTools.fs `tool "host"` does, into a
// per-user cache keyed by its sources and build options, and prints the binary's path. Linux only.
// `--optional` exits 0 without building when no zig runs (setup on machines without Zig).
import { createHash, randomUUID } from "node:crypto"
import { execFile } from "node:child_process"
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync } from "node:fs"
import { homedir } from "node:os"
import { join, relative } from "node:path"
import { fileURLToPath } from "node:url"

const buildTimeoutMs = 600_000
// The modules the host imports (`@import("model_reader")`), as in ZigTools.fs `imports`.
const imports = [
  ["model_reader", "tools/zig/model-reader", "main.zig"],
  ["snapshot_reader", "tools/zig/snapshot-reader", "main.zig"],
  ["zig_emitter", "tools/zig/emitter", "main.zig"],
  ["csharp_emitter", "tools/zig/csharp-emitter", "root.zig"],
  ["viewer_asset", "src/Nyrwvy.Viewer", "asset.zig"],
]
const excluded = new Set(["bin", "obj", ".zig-cache"])

/** @param {string} folder @param {ReturnType<typeof createHash>} hash */
function hashFolder(folder, hash) {
  /** @param {string} directory @returns {string[]} */
  const files = directory => readdirSync(directory, { withFileTypes: true })
    .filter(entry => !excluded.has(entry.name))
    .flatMap(entry => entry.isDirectory() ? files(join(directory, entry.name)) : [join(directory, entry.name)])
  for (const file of files(folder).sort()) hash.update(`${relative(folder, file)}\0`).update(readFileSync(file)).update("\0")
}

/** @param {string} command @param {string[]} args @param {string} cwd @param {AbortSignal | undefined} signal */
function run(command, args, cwd, signal) {
  return new Promise((resolveRun, rejectRun) => execFile(command, args,
    { cwd, timeout: buildTimeoutMs, maxBuffer: 16 * 1024 * 1024, killSignal: "SIGKILL", signal },
    (error, stdout, stderr) => error ? rejectRun(Object.assign(error, { stderr })) : resolveRun(stdout)))
}

/** @type {Map<string, Promise<string>>} */
const built = new Map()

/**
 * The host binary for the repository at `root`, built on first use. `zig` is `NYRWVY_ZIG` or the PATH's,
 * started in `root` so a version manager shim resolves the Zig that mise.toml pins.
 * @param {string} root @param {AbortSignal} [signal] @returns {Promise<string>}
 */
export function zigHost(root, signal) {
  let path = built.get(root)
  if (path === undefined) {
    path = buildZigHost(root, signal)
    built.set(root, path)
    path.catch(() => built.delete(root)) // A failed or cancelled build is retried by the next open.
    // shortcut: the first caller's signal cancels the shared build for concurrent callers too; give each its own wait if that matters.
  }
  return path
}

/** @param {string} root @param {AbortSignal | undefined} signal */
async function buildZigHost(root, signal) {
  const zig = process.env.NYRWVY_ZIG || "zig"
  let version
  try {
    version = (await run(zig, ["version"], root, signal)).trim()
  } catch (error) {
    throw new Error(`zig could not run from ${root} (${/** @type {Error} */ (error).message.trim()}). Install the Zig that mise.toml pins (mise install) or set NYRWVY_ZIG.`)
  }
  const host = join(root, "tools/zig/host")
  // `-O` is a per-module option: it applies to the module defined after it.
  const roots = ["-O", "ReleaseSafe", ...imports.flatMap(([name]) => ["--dep", name]), `-Mroot=${join(host, "main.zig")}`,
    ...imports.flatMap(([name, folder, file]) => ["-O", "ReleaseSafe", `-M${name}=${join(root, folder, file)}`])]
  const hash = createHash("sha256")
  for (const folder of [host, ...imports.map(([, folder]) => join(root, folder))]) hashFolder(folder, hash)
  // The options and compiler are part of the key, so a build made otherwise is not reused.
  hash.update(`\0${version}\0${roots.join(" ").replaceAll(join(root, "/"), "")}`)
  // The key is predictable, so the cache is per user and owner-only: another user must not plant a host.
  const cache = join(process.env.XDG_CACHE_HOME || join(homedir(), ".cache"), "nyrwvy", "zig-host")
  const target = join(cache, hash.digest("hex").slice(0, 32))
  const binary = join(target, "host")
  if (existsSync(binary)) return binary
  mkdirSync(target, { recursive: true, mode: 0o700 })
  // Built under a fresh name and moved, so another process never runs a partial file.
  const partial = `${binary}.${randomUUID()}`
  try {
    await run(zig, ["build-exe", ...roots, "--cache-dir", join(cache, "cache"), `-femit-bin=${partial}`], root, signal)
  } catch (error) {
    const detail = /** @type {{ stderr?: string, message: string }} */ (error)
    throw new Error(`Zig host build failed: ${(detail.stderr?.trim() || detail.message).slice(-4096)}`)
  }
  renameSync(partial, binary)
  return binary
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const root = fileURLToPath(new URL("../", import.meta.url))
  if (process.argv.includes("--optional")) {
    try { await run(process.env.NYRWVY_ZIG || "zig", ["version"], root, undefined) } catch {
      console.error("zig is not available; skipping the Zig session host build.")
      process.exit(0)
    }
  }
  try {
    console.log(await zigHost(root))
  } catch (error) {
    console.error(/** @type {Error} */ (error).message)
    process.exit(1)
  }
}
