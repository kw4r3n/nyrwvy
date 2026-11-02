import { execFileSync, spawn } from "node:child_process"
import { createHash } from "node:crypto"
import { access, mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile } from "node:fs/promises"
import { dirname, join, resolve, sep } from "node:path"
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
let commit
try {
  const head = execFileSync("git", ["-C", root, "rev-parse", "HEAD"], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 2000 }).trim()
  if (/^[0-9a-f]{40}$/.test(head)) commit = head
} catch {}
// The commit stays out of the binary, so a commit that leaves the sources unchanged reuses the build;
// the adapter reads it from the stamp beside the host (staleToolWarning).
const info = `pub const build = "nyrwvy-zig-host";\npub const repository_root: ?[]const u8 = ${JSON.stringify(root)};\n`
const stamp = join(output, `${name}.json`)
const writeStamp = async sources => {
  await writeFile(`${stamp}.partial`, JSON.stringify({ sources, commit }))
  await rename(`${stamp}.partial`, stamp)
}
// Every file in the host's and its modules' directories, as ZigTools keys the test host; mise.toml pins Zig.
const hash = createHash("sha256").update(JSON.stringify([info, modules, process.env.NYRWVY_ZIG ?? "", process.platform, process.arch]))
for (const directory of ["tools/zig/host", ...modules.map(([, path]) => dirname(path))]) {
  const files = (await readdir(join(root, directory), { recursive: true, withFileTypes: true }).catch(() => []))
    .filter(entry => entry.isFile()).map(entry => join(entry.parentPath, entry.name))
    .filter(file => !file.slice(root.length).split(sep).some(part => part === "bin" || part === "obj" || part === ".zig-cache")).sort()
  for (const file of files) hash.update(`\0${file.slice(root.length)}\0`).update(await readFile(file))
}
hash.update(await readFile(join(root, "mise.toml")).catch(() => ""))
const sources = hash.digest("hex")
const previous = await readFile(stamp, "utf8").then(JSON.parse).catch(() => undefined)
if (previous?.sources === sources && await access(join(output, name)).then(() => true, () => false)) {
  await writeStamp(sources)
  console.log(`${join(output, name)} is current with its sources.`)
  process.exit(0)
}
await mkdir(output, { recursive: true, mode: 0o700 })
const temporary = await mkdtemp(join(output, ".build-"))
try {
  const built = join(temporary, name)
  const buildInfo = join(temporary, "build_info.zig")
  await writeFile(buildInfo, info)
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
  await writeStamp(sources)
  console.log(`Built ${join(output, name)}`)
} finally {
  await rm(temporary, { recursive: true, force: true })
}
