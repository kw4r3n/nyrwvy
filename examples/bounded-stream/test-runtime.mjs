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

const graph = JSON.parse(await readFile(join(input, 'declarations.json'), 'utf8'))
const manifest = JSON.parse(await readFile(join(input, 'symbols.json'), 'utf8'))
const exactly = (items, predicate, label) => {
  const matches = items.filter(predicate)
  if (matches.length !== 1) throw new Error(`Expected one ${label}, found ${matches.length}`)
  return matches[0]
}
const symbol = (kind, id) => exactly(manifest[kind] ?? [], s => s.id === id, `${kind} symbol for ${id}`).name
const host = exactly(graph.capabilities, c => c.displayName === 'Host', 'Host capability')
const variables = { HOST: symbol('capabilities', host.id), LINK: symbol('types', exactly(graph.types, t => t.displayName === 'Link', 'Link type').id) }
for (const fn of graph.functions) {
  variables[fn.displayName.toUpperCase()] = symbol('functions', fn.id)
  for (const p of fn.parameters) variables[`${fn.displayName}_${p.displayName}`.toUpperCase()] = symbol('parameters',p.id)
}
for (const op of host.operations) variables[op.displayName.toUpperCase()] = symbol('capabilityOperations', op.id)
const template = await readFile(new URL('./retention-runtime.template.cs', import.meta.url), 'utf8')
const program = template.replace(/@@([A-Z_]+)@@/g, (_, key) => {
  if (!variables[key]) throw new Error(`Missing template symbol ${key}`)
  return variables[key]
})
if (program.includes('@@')) throw new Error('Unresolved runtime template placeholder')

const scratch = await mkdtemp(join(tmpdir(), 'nyrwvy-cursor-retention-runtime-'))
try {
  await copyFile(join(input, 'Generated.cs'), join(scratch, 'Generated.cs'))
  await writeFile(join(scratch, 'Program.cs'), program)
  const options = { cwd: scratch, env: { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' }, encoding: 'utf8', timeout: 90000 }
  const {assembly,evidence:compilation}=await compileRuntime(scratch,options)
  const stdout = execFileSync('dotnet', [assembly], options).trim()
  const checks = stdout.split(/\r?\n/).filter(line => line.startsWith('PASS:'))
  for (const count of [4,256]) if (!checks.some(x => x.includes(`${count} completed cells per worker`))) throw new Error(`Missing retention evidence for count ${count}`)
  if (!checks.some(x => x.startsWith('PASS: cursor-retention regression;'))) throw new Error('Missing regression summary')
  const sourceUnchanged = hash(await readFile(join(input, 'Generated.cs'))) === generatedSha256 &&
    hash(await readFile(join(scratch, 'Generated.cs'))) === generatedSha256
  if (!sourceUnchanged) throw new Error('Generated.cs changed during runtime checks')
  await writeFile(join(input, 'runtime-evidence.json'), JSON.stringify({ generatedSha256, sourceUnchanged, compilation, checks, stdout }, null, 2) + '\n')
  console.log(stdout)
} finally {
  await rm(scratch, { recursive: true, force: true })
}
