import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises"
import { join } from "node:path"
import { tmpdir } from "node:os"
import { closeAuthoring, openAuthoring } from "../../tools/nyrwvy-agent/authoring.ts"

test("host selection resolves against the repository and preserves launch arguments", async t => {
  if (process.platform === "win32") return t.skip("fixture host uses a Unix executable script")
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-host-selection-"))
  const previous = { host: process.env.NYRWVY_HOST_EXECUTABLE, path: process.env.PATH }
  t.after(async () => {
    if (previous.host === undefined) delete process.env.NYRWVY_HOST_EXECUTABLE; else process.env.NYRWVY_HOST_EXECUTABLE = previous.host
    process.env.PATH = previous.path
    await rm(root, { recursive: true, force: true })
  })
  const repository = join(root, "tool repository")
  const project = join(root, "project worktree")
  await mkdir(repository)
  await mkdir(join(project, "workspace"), { recursive: true })
  const log = join(root, "args")
  const script = `#!${process.execPath}\nconst fs = require('node:fs');\nfs.writeFileSync(${JSON.stringify(log)}, JSON.stringify({cwd:process.cwd(),args:process.argv.slice(2)}));\nrequire('node:readline').createInterface({input:process.stdin}).on('line', line => {const r=JSON.parse(line); process.stdout.write(JSON.stringify({id:r.id,ok:true,result:{}})+'\\n')});\n`
  await writeFile(join(repository, "native"), script, { mode: 0o700 })
  await writeFile(join(root, "dotnet"), script, { mode: 0o700 })
  const context = { sessionID: "selection", worktree: project, repositoryRoot: repository, allowHostSources: true }
  process.env.NYRWVY_HOST_EXECUTABLE = "native"
  await openAuthoring("read", "snapshot.json", context, "workspace")
  await closeAuthoring(context)
  let call = JSON.parse(await readFile(log, "utf8"))
  assert.equal(call.cwd, join(project, "workspace"))
  assert.deepEqual(call.args, ["open", "--snapshot", join(project, "workspace/snapshot.json"), "--read-only", "--allow-host-sources"])
  process.env.PATH = `${root}:${previous.path}`
  process.env.NYRWVY_HOST_EXECUTABLE = "managed.DLL"
  await openAuthoring("create", "snapshot.json", context, "workspace")
  await closeAuthoring(context)
  call = JSON.parse(await readFile(log, "utf8"))
  assert.deepEqual(call.args, [join(repository, "managed.DLL"), "create", "--snapshot", join(project, "workspace/snapshot.json"), "--allow-host-sources"])
  await writeFile(join(repository, "denied"), script, { mode: 0o600 })
  process.env.NYRWVY_HOST_EXECUTABLE = "denied"
  await assert.rejects(openAuthoring("open", "snapshot.json", context), error => /denied/.test(error.message) && /EACCES/.test(error.message) && !/outcome is unknown/.test(error.message))
  delete process.env.NYRWVY_HOST_EXECUTABLE
  await assert.rejects(openAuthoring("open", "snapshot.json", context), /npm run build:host/)
})
