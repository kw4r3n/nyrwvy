// Runs the model-cooldown port on the C# backend against the real OS: emits C# from a workspace
// importing host.json and json.json, and runs Program.cs, which drives the emitted coolModel through
// the real host classes (NyHost.cs). Usage: node examples/model-cooldown/run.mjs [<host executable>]
import { spawn, spawnSync } from "node:child_process"
import { hostExchange, hostSend } from "../../libraries/host-client.mjs"
import { copyFileSync, mkdtempSync, readFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"
import { author } from "./author.mjs"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-cooldown-cs-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
copyFileSync(join(repo, "libraries/host/host.json"), join(work, "host.json"))
copyFileSync(join(repo, "libraries/json/json.json"), join(work, "json.json"))
const cli = spawn(executable, ["create", "--snapshot", join(work, "check.json"), "--dependency", join(work, "host.json"), "--dependency", join(work, "json.json")],
  { stdio: ["pipe", "pipe", "inherit"], cwd: repo })
const send = hostSend(hostExchange(cli))
await author(send)
const host = JSON.parse(readFileSync(join(work, "host.json"), "utf8"))
const operationIds = host.capabilities.flatMap(capability => capability.operations.map(operation => operation.id))
await send({ op: "emitCSharp", profile: { kind: "externalImplementations", operationIds }, output: join(work, "Generated.cs"), facade: join(work, "Facade.cs") })
cli.stdin.end()

for (const file of ["Program.cs"]) copyFileSync(join(here, file), join(work, file))
copyFileSync(join(repo, "libraries/host/check/check.csproj"), join(work, "check.csproj"))
copyFileSync(join(repo, "libraries/host/NyHost.cs"), join(work, "NyHost.cs"))
const run = spawnSync("dotnet", ["run", "-c", "Release", "--project", join(work, "check.csproj")], { stdio: "inherit" })
process.exit(run.status ?? 1)
