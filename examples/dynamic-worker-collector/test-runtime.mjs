// Compile the unedited emitted projection with an external scheduling-only host.
import { mkdtemp, copyFile, writeFile, readFile, readdir, stat, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { compileRuntime } from './compile-runtime.mjs'

if (!process.argv[2]) throw new Error('Usage: test-runtime.mjs <emitted-bundle-directory>')
const input = resolve(process.argv[2])
if (!(await stat(input)).isDirectory()) throw new Error('A bundle directory containing Generated.cs, symbols.json and the graph snapshot is required')
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const authoring = JSON.parse(await readFile(join(input, 'authoring.json'), 'utf8'))
const generatedSha256 = hash(await readFile(join(input, 'Generated.cs')))
if (generatedSha256 !== authoring.generatedSha256) throw new Error('Generated.cs differs from the authoring.json generatedSha256 before runtime checks')

const snapshots = []
for (const name of await readdir(input)) {
  if (!name.endsWith('.json') || name === 'symbols.json') continue
  const value = JSON.parse(await readFile(join(input, name), 'utf8'))
  if (Array.isArray(value.functions) && Array.isArray(value.capabilities)) snapshots.push({ name, value })
}
if (snapshots.length !== 1) throw new Error(`Expected one graph snapshot, found: ${snapshots.map(s => s.name).join(', ') || 'none'}`)
const graph = snapshots[0].value
const manifest = JSON.parse(await readFile(join(input, 'symbols.json'), 'utf8'))
const exactly = (items, predicate, label) => {
  const matches = items.filter(predicate)
  if (matches.length !== 1) throw new Error(`Expected one ${label}, found ${matches.length}`)
  return matches[0]
}
const symbol = (kind, id) => exactly(manifest[kind] ?? [], s => s.id === id, `${kind} symbol for ${id}`).name
const host = exactly(graph.capabilities, c => c.displayName === 'Host', 'Host capability')
const run = exactly(graph.functions, f => f.displayName === 'run', 'run function')
const parameter = name => symbol('parameters', exactly(run.parameters, p => p.displayName === name, `run parameter ${name}`).id)
const variables = { HOST: symbol('capabilities', host.id), RUN: symbol('functions', run.id), RUN_HOST: parameter('host'), RUN_COUNT: parameter('count'), RUN_MODE: parameter('mode') }
for (const op of host.operations) variables[op.displayName.toUpperCase()] = symbol('capabilityOperations', op.id)
const template = await readFile(new URL('./collector-runtime.template.cs', import.meta.url), 'utf8')
const program = template.replace(/@@([A-Z_]+)@@/g, (_, key) => {
  if (!variables[key]) throw new Error(`Missing template symbol ${key}`)
  return variables[key]
})
if (program.includes('@@')) throw new Error('Unresolved runtime template placeholder')

const scratch = await mkdtemp(join(tmpdir(), 'nyrwvy-dynamic-collector-runtime-'))
try {
  await copyFile(join(input, 'Generated.cs'), join(scratch, 'Generated.cs'))
  await writeFile(join(scratch, 'Program.cs'), program)
  const options = { cwd: scratch, env: { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' }, encoding: 'utf8', timeout: 90000 }
  const {assembly,evidence:compilation}=await compileRuntime(scratch,options)
  const stdout = execFileSync('dotnet', [assembly], options).trim()
  const checks = stdout.split(/\r?\n/).filter(line => line.startsWith('PASS:'))
  for (const label of ['normal, 2 producers;', 'normal, 3 producers;', 'normal, 4 producers;', 'worker-error, 4 producers;', 'cancel-one, 4 producers;', 'parent-exit, 4 producers;', 'host-exception;']) {
    if (checks.filter(line => line.startsWith(`PASS: ${label}`)).length !== 1) throw new Error(`Missing or duplicate runtime check: ${label}`)
  }
  if (checks.filter(line => line.startsWith('PASS: stop-race, 4 producers;')).length !== 20) throw new Error('Expected 20 successful real Stop race checks')
  if (!checks.some(line => line.startsWith('PASS: emitted graph/runtime collector,'))) throw new Error('Missing final runtime success summary')
  const sourceUnchanged = hash(await readFile(join(input, 'Generated.cs'))) === generatedSha256 &&
    hash(await readFile(join(scratch, 'Generated.cs'))) === generatedSha256
  if (!sourceUnchanged) throw new Error('Generated.cs changed during runtime checks')
  await writeFile(join(input, 'runtime-evidence.json'), JSON.stringify({ generatedSha256, sourceUnchanged, compilation, checks, stdout }, null, 2) + '\n')
  console.log(stdout)
} finally {
  await rm(scratch, { recursive: true, force: true })
}
