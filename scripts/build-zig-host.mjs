import { execFileSync, spawn } from "node:child_process"
import { mkdir, mkdtemp, rename, rm, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..")
const output = join(root, "tools/zig/host/bin")
const name = `nyrwvy-host${process.platform === "win32" ? ".exe" : ""}`
const modules = [
  ["model_reader", "tools/zig/model-reader/main.zig"],
  ["snapshot_reader", "tools/zig/snapshot-reader/main.zig"],
  ["zig_emitter", "tools/zig/emitter/main.zig"],
  ["csharp_emitter", "tools/zig/csharp-emitter/root.zig"],
  ["viewer_asset", "src/Nyrwvy.Viewer/asset.zig"],
]

// `--optional` exits 0 without building when no zig runs (setup on machines without Zig).
if (process.argv.includes("--optional")) {
  try { execFileSync(process.env.NYRWVY_ZIG || "zig", ["version"], { cwd: root, stdio: "ignore", timeout: 30_000 }) } catch {
    console.error("zig is not available; skipping the Zig session host build.")
    process.exit(0)
  }
}
await mkdir(output, { recursive: true, mode: 0o700 })
const temporary = await mkdtemp(join(output, ".build-"))
try {
  const built = join(temporary, name)
  let commit
  try {
    const head = execFileSync("git", ["-C", root, "rev-parse", "HEAD"], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 2000 }).trim()
    if (/^[0-9a-f]{40}$/.test(head)) commit = head
  } catch {}
  const buildInfo = join(temporary, "build_info.zig")
  await writeFile(buildInfo, `pub const build = ${JSON.stringify(`nyrwvy-zig-host${commit ? `+${commit}` : ""}`)};\npub const repository_root: ?[]const u8 = ${JSON.stringify(root)};\n`)
  modules.push(["build_info", buildInfo])
  const args = ["build-exe", `-femit-bin=${built}`, ...(process.platform === "darwin" ? ["-lc"] : []), "-O", "ReleaseSafe",
    ...modules.flatMap(([name]) => ["--dep", name]), "-Mroot=tools/zig/host/main.zig",
    ...modules.flatMap(([name, path]) => ["-O", "ReleaseSafe", `-M${name}=${path}`])]
  await new Promise((accept, reject) => {
    const child = spawn(process.env.NYRWVY_ZIG || "zig", args, { cwd: root, stdio: "inherit", detached: process.platform !== "win32" })
    let interruption
    let spawnError
    const killTree = () => {
      if (!child.pid) return
      try {
        if (process.platform === "win32") {
          // taskkill waits until the tree has been terminated.
          execFileSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], { stdio: "ignore", timeout: 5000 })
        } else process.kill(-child.pid, "SIGKILL")
      } catch (error) {
        if (error.code !== "ESRCH") throw error
      }
    }
    const stop = reason => { interruption ??= reason; killTree() }
    const onTerm = () => stop("SIGTERM")
    const onInt = () => stop("SIGINT")
    process.on("SIGTERM", onTerm)
    process.on("SIGINT", onInt)
    const timer = setTimeout(() => stop("deadline exceeded"), 300_000)
    child.once("error", error => { spawnError = error })
    child.once("close", (code, signal) => {
      clearTimeout(timer)
      process.off("SIGTERM", onTerm)
      process.off("SIGINT", onInt)
      // A wrapper may exit while leaving compiler children behind.
      try { if (process.platform !== "win32") killTree() } catch (error) { reject(error); return }
      if (spawnError) reject(spawnError)
      else if (interruption) reject(new Error(`Zig host build interrupted: ${interruption}.`))
      else if (code !== 0) reject(new Error(`Zig host build failed (${signal ?? code}).`))
      else accept()
    })
  })
  await rename(built, join(output, name))
  console.log(`Built ${join(output, name)}`)
} finally {
  await rm(temporary, { recursive: true, force: true })
}
