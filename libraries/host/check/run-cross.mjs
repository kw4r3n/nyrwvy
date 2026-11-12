// Checks that the two backends' locks exclude each other on the same lock file: emits tryLock and
// holdLock both ways, holds the lock through one backend's program for a few seconds, and requires
// the other backend's acquire to fail meanwhile (and to succeed after the release), in both directions.
// Usage: node libraries/host/check/run-cross.mjs [<host executable>]
import { spawn, spawnSync } from "node:child_process"
import { hostExchange, hostSend } from "../../host-client.mjs"
import { copyFileSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-host-cross-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
for (const file of ["host.json", "host.zig", "host.zig.json"]) copyFileSync(join(here, "..", file), join(work, file))
const snapshot = join(work, "check.json")
writeFileSync(snapshot + ".backends.json", JSON.stringify({ targets: ["csharp"], zigTables: ["host.zig.json"], exports: ["tryLock", "holdLock"] }))

const cli = spawn(executable, ["create", "--snapshot", snapshot, "--dependency", join(work, "host.json")],
  { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const send = hostSend(hostExchange(cli))
const str = { kind: "primitive", primitive: "string" }, int = { kind: "primitive", primitive: "int64" }
const cap = name => ({ kind: "capability", name })
const P = name => ({ kind: "parameter", name }), R = name => ({ kind: "patternReference", name })
const invoke = (capabilityValue, capability, operation, args = {}) => ({ kind: "capabilityInvoke", capabilityValue, capability, operation,
  arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const strResult = { kind: "builtinGeneric", generic: "result", arguments: [str, str] }
const define = async (displayName, parameters, resultType, body, exported = false) => {
  const created = await send({ op: "simulateCreateFunctionByName", displayName, exported, resultType, requiredEffects: ["System"], body,
    parameters: parameters.map(([displayName, type]) => ({ displayName, type })) })
  await send({ op: "commit", proposalId: created.proposalId })
}
// UseResource bodies get the Lock handle as their one unbound parameter (`cap`); the rest is context.
await define("lockPathBody", [["cap", cap("Lock")]], strResult,
  { kind: "resultOk", errorType: str, value: invoke(P("cap"), "Lock", "path") })
await define("holdBody", [["cap", cap("Lock")], ["clock", cap("Clock")], ["ms", int]], strResult,
  { kind: "bind", value: invoke(P("clock"), "Clock", "sleep", { milliseconds: P("ms") }),
    body: { kind: "resultOk", errorType: str, value: invoke(P("cap"), "Lock", "path") } })
const asText = (use, okText) => ({ kind: "matchResult", okName: "heldPath", errName: "failure", input: use,
  okBody: { kind: "stringLiteral", value: okText },
  errBody: { kind: "stringConcat", left: { kind: "stringLiteral", value: "error: " }, right: R("failure") } })
const use = (body, context = []) => ({ kind: "useResource", acquire: invoke(P("locks"), "Locks", "acquire", { path: P("path") }),
  releaseCapability: P("locks"), capability: "Locks", releaseOperation: "release", body, context })
await define("tryLock", [["locks", cap("Locks")], ["path", str]], str, asText(use("lockPathBody"), "ok"), true)
await define("holdLock", [["locks", cap("Locks")], ["clock", cap("Clock")], ["path", str], ["ms", int]], str,
  asText(use("holdBody", [{ parameter: "clock", value: P("clock") }, { parameter: "ms", value: P("ms") }]), "held"), true)

const emitted = await send({ op: "emitZigLibrary", output: "check.zig" })
const host = JSON.parse(readFileSync(join(work, "host.json"), "utf8"))
const operationIds = host.capabilities.flatMap(capability => capability.operations.map(operation => operation.id))
await send({ op: "emitCSharp", profile: { kind: "externalImplementations", operationIds }, output: join(work, "Generated.cs"), facade: join(work, "Facade.cs") })
cli.stdin.end()

const main = `
pub fn main(init: std.process.Init) !void {
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    var buffer: [4096]u8 = undefined;
    var n: usize = 0;
    const status = if (std.mem.eql(u8, args[1], "hold"))
        ny_holdLock(args[2].ptr, args[2].len, try std.fmt.parseInt(i64, args[3], 10), &buffer, buffer.len, &n)
    else
        ny_tryLock(args[2].ptr, args[2].len, &buffer, buffer.len, &n);
    if (status != 0) {
        std.debug.print("export failed with status {d}\\n", .{status});
        return error.ExportFailed;
    }
    // The result goes to stdout (fd 1); std.debug.print would land on stderr.
    _ = std.c.write(1, buffer[0..n].ptr, n);
    _ = std.c.write(1, "\\n".ptr, 1);
}
`
writeFileSync(join(work, "cross.zig"), readFileSync(emitted.path, "utf8") + main)
const zig = process.env.NYRWVY_ZIG || "zig"
const zigExe = join(work, "cross-zig")
const build = spawnSync(zig, ["build-exe", "-lc", "-O", "ReleaseSafe", join(work, "cross.zig"), `-femit-bin=${zigExe}`], { stdio: "inherit" })
if (build.status !== 0) process.exit(build.status ?? 1)
for (const file of ["CrossProgram.cs", "check.csproj"]) copyFileSync(join(here, file), join(work, file))
copyFileSync(join(here, "../NyHost.cs"), join(work, "NyHost.cs"))
const dotnetBuild = spawnSync("dotnet", ["build", "-c", "Release", join(work, "check.csproj")], { stdio: "inherit" })
if (dotnetBuild.status !== 0) process.exit(dotnetBuild.status ?? 1)
const dll = join(work, "bin/Release/net10.0/check.dll")

const sleep = ms => new Promise(done => setTimeout(done, ms))
const runSync = (cmd, args) => { const r = spawnSync(cmd, args, { encoding: "utf8" }); return { status: r.status, out: (r.stdout ?? "").trim(), err: r.stderr ?? "" } }
const holdAsync = (cmd, args) => new Promise((resolveHold, reject) => {
  const proc = spawn(cmd, args, { stdio: ["ignore", "pipe", "pipe"] })
  let out = "", err = ""
  proc.stdout.on("data", d => { out += d })
  proc.stderr.on("data", d => { err += d })
  proc.on("error", reject)
  proc.on("exit", code => resolveHold({ code, out: out.trim(), err }))
})
let failures = 0
const expect = (condition, label, detail) => {
  if (condition) console.log(`ok ${label}`)
  else { failures++; console.error(`FAIL ${label}: ${detail}`) }
}
const lockPath = join(work, "shared.lock")
const zigHold = holdAsync(zigExe, ["hold", lockPath, "6000"])
await sleep(1500)
let attempt = runSync("dotnet", [dll, "try", lockPath])
expect(attempt.out.startsWith("error:"), "C# acquire fails while Zig holds the lock", attempt.out + attempt.err)
const zigHeld = await zigHold
expect(zigHeld.out === "held", "the Zig hold completes", zigHeld.out + zigHeld.err)
attempt = runSync("dotnet", [dll, "try", lockPath])
expect(attempt.out === "ok", "C# acquire succeeds after Zig releases", attempt.out + attempt.err)
const csHold = holdAsync("dotnet", [dll, "hold", lockPath, "6000"])
await sleep(2500)
attempt = runSync(zigExe, ["try", lockPath])
expect(attempt.out === "error: LockHeld", "Zig acquire fails while C# holds the lock", attempt.out + attempt.err)
const csHeld = await csHold
expect(csHeld.out === "held", "the C# hold completes", csHeld.out + csHeld.err)
attempt = runSync(zigExe, ["try", lockPath])
expect(attempt.out === "ok", "Zig acquire succeeds after C# releases", attempt.out + attempt.err)
if (failures) { console.error(`${failures} cross-backend lock checks failed`); process.exit(1) }
console.log("cross-backend lock checks passed")
