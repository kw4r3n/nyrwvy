// Checks NyHost.cs against the real OS: builds a program that imports host.json, emits its C#, and runs
// Program.cs, which calls every host class and a Nyrwvy function driving a pty. Linux, needs dotnet and cat.
// Usage: node libraries/host/check/run.mjs [<host executable>] (default: the Zig host, `npm run build:host`)
import { spawn, spawnSync } from "node:child_process"
import { hostExchange, hostSend } from "../../host-client.mjs"
import { copyFileSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, "../../..")
const executable = process.argv[2] ?? join(repo, "tools/zig/host/bin/nyrwvy-host")
const work = mkdtempSync(join(tmpdir(), "nyr-host-check-"))
process.on("exit", () => rmSync(work, { recursive: true, force: true }))
copyFileSync(join(here, "../host.json"), join(work, "host.json"))

const cli = spawn(executable, ["create", "--snapshot", join(work, "check.json"), "--dependency", join(work, "host.json")], { stdio: ["pipe", "pipe", "inherit"] })
const exchange = hostExchange(cli)
const send = hostSend(exchange)
const str = { kind: "primitive", primitive: "string" }
const P = name => ({ kind: "parameter", name }), R = name => ({ kind: "patternReference", name })
const invoke = (capabilityValue, capability, operation, args = {}) => ({ kind: "capabilityInvoke", capabilityValue, capability, operation,
  arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const int = v => ({ kind: "int64Literal", value: String(v) })

// ptyEcho(ptys, text): run cat on a pty (TERM=dumb), write text, return the first chunk read back.
const created = await send({ op: "simulateCreateFunctionByName", displayName: "ptyEcho", exported: true,
  parameters: [{ displayName: "ptys", type: { kind: "capability", name: "Ptys" } }, { displayName: "text", type: str }],
  resultType: str, requiredEffects: ["System"],
  body: { kind: "matchResult", okName: "pty", errName: "failure",
    input: invoke(P("ptys"), "Ptys", "open", { argv: { kind: "listConstruct", itemType: str, items: [{ kind: "stringLiteral", value: "cat" }] }, environment: { kind: "mapInsert", map: { kind: "mapEmpty", keyType: str, valueType: str },
      key: { kind: "stringLiteral", value: "TERM" }, value: { kind: "stringLiteral", value: "dumb" } }, cols: int(80), rows: int(24) }),
    okBody: { kind: "bind", value: invoke(R("pty"), "Pty", "write", { data: { kind: "bytesFromLatin1", value: P("text") } }),
      body: { kind: "bind", name: "chunk", value: invoke(R("pty"), "Pty", "read"),
        body: { kind: "bind", value: invoke(P("ptys"), "Ptys", "close", { pty: R("pty") }),
          body: { kind: "bytesToLatin1", value: { kind: "optionGetOrDefault", option: R("chunk"), default: { kind: "bytesEmpty" } } } } } },
    errBody: { kind: "stringConcat", left: { kind: "stringLiteral", value: "open failed: " }, right: R("failure") } } })
await send({ op: "commit", proposalId: created.proposalId })
const host = JSON.parse(readFileSync(join(work, "host.json"), "utf8"))
const operationIds = host.capabilities.flatMap(capability => capability.operations.map(operation => operation.id))
await send({ op: "emitCSharp", profile: { kind: "externalImplementations", operationIds }, output: join(work, "Generated.cs"), facade: join(work, "Facade.cs") })
cli.stdin.end()

for (const file of ["Program.cs", "check.csproj"]) copyFileSync(join(here, file), join(work, file))
copyFileSync(join(here, "../NyHost.cs"), join(work, "NyHost.cs"))
const run = spawnSync("dotnet", ["run", "-c", "Release", "--project", join(work, "check.csproj")], { stdio: "inherit" })
process.exit(run.status ?? 1)
