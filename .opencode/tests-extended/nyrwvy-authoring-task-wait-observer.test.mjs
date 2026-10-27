import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, writeFile, copyFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { randomUUID, createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { openAuthoring, requestAuthoring, closeAuthoring } from '../../tools/nyrwvy-agent/authoring.ts'
import { defineRender, defineFixture, defineController } from '../../examples/task-wait-observer/fixture.mjs'
import { string } from '../../examples/task-wait-observer/author.mjs'
import { compileRuntime } from '../../examples/shared/compile-runtime.mjs'

const hash = value => createHash('sha256').update(value).digest('hex')
const unit = { kind: 'unit' }, sv = value => ({ kind: 'string', value })
const ret = (value, delayMillis) => ({ kind: 'return', value, ...(delayMillis ? { delayMillis: String(delayMillis) } : {}) })
const value = text => ({ kind: 'result', okKind: 'string', errorKind: 'string', isOk: true, value: text })
const cases = [
  { id: 'any', mode: 'any', expected: ['|running|a'] },
  { id: 'all', mode: 'all', expected: ['|b|a'] },
  { id: 'finite', mode: 'all', limit: 5, expected: ['|running|running'] },
  { id: 'zero', mode: 'any', limit: 0, expected: ['|running|running'] },
  { id: 'negative', mode: 'all', limit: -5, expected: ['|running|running'] },
  ...['any', 'all'].map(mode => ({ id: `empty-${mode}`, mode, policy: 'empty', expected: [''] })),
  ...['any', 'all'].map(mode => ({ id: `settled-${mode}`, mode, policy: 'settled', limit: 0, expected: ['|b|a'] })),
  ...['any', 'all'].map(mode => ({ id: `cancel-${mode}`, mode, policy: 'cancel', expected: ['unobserved', '|running|running', 'monitor-running'] })),
  { id: 'owner-exit', mode: 'all', policy: 'owner-exit', expected: [] },
  { id: 'fault-any', mode: 'any', fault: true },
  { id: 'fault-all', mode: 'all', fault: true },
]
const limitValue = n => n === undefined ? { kind: 'option', itemKind: 'int64', hasValue: false } : { kind: 'option', itemKind: 'int64', hasValue: true, value: String(n) }
const argsFor = c => [{ kind: 'fakeCapability', capability: 'Probe', operations: [
  { operation: 'open', responses: [ret({ kind: 'result', okType: { kind: 'primitive', primitive: 'unit' }, errorType: string, isOk: true, value: unit })] },
  { operation: 'close', responses: [ret(unit)] },
  { operation: 'gate', responses: [ret(unit, 1)] },
  { operation: 'report', responses: Array.from({ length: 3 }, () => ret(unit)) },
  { operation: 'work', cases: ['a', 'b'].map((name, i) => ({ arguments: [{ parameter: 'name', value: sv(name) }], responses: [
    c.fault && i === 0 ? { kind: 'throw', message: 'worker fault', delayMillis: '10' } :
    c.id === 'fault-all' && i === 1 ? ret(value(name), 20) :
    c.policy === 'cancel' || c.policy === 'owner-exit' || c.fault ? { kind: 'awaitCancellation', value: value('stopped') } : ret(value(name), i === 0 ? 10 : 20),
  ] })) },
] }, sv(c.mode), limitValue(c.limit), sv(c.policy ?? 'observe')]

test('generic task observers preserve TaskWait semantics, replay and unedited C# parity', { timeout: 180000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'nyrwvy-task-observer-'))
  const context = { sessionID: randomUUID(), worktree: resolve('.') }
  const snapshot = join(root, 'observer.json')
  const request = async payload => JSON.parse(await requestAuthoring(payload, context))
  const success = async payload => { const reply = await request(payload); assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.result }
  const publish = async payload => { const proposal = await success(payload); assert.equal(proposal.review.readiness.kind, 'semanticallyReady'); await success({ op: 'commit', proposalId: proposal.proposalId }) }
  const create = (displayName, parameters, resultType, body, requiredEffects = []) => publish({ op: 'simulateCreateFunctionByName', displayName, parameters: Object.entries(parameters).map(([displayName, type]) => ({ displayName, type })), resultType, body, requiredEffects })
  try {
    await openAuthoring('create', snapshot, context)
    await defineRender(create)
    await defineFixture(create, publish)
    await defineController(create)
    const declarations = await success({ op: 'declarations' })
    const probe = declarations.capabilities.find(c => c.displayName === 'Probe')
    const emit = () => success({ op: 'emitCSharp', profile: { kind: 'externalImplementations', operationIds: probe.operations.map(o => o.id), concurrentCapabilityIds: [probe.id] } })
    const artifact = await emit()
    await closeAuthoring(context)
    await openAuthoring('open', snapshot, context)
    assert.deepEqual(await emit(), artifact)
    const before = hash(await readFile(snapshot))
    async function virtual() {
      for (const c of cases) {
        const reply = await request({ op: 'runPureByName', function: 'run', arguments: argsFor(c) })
        assert.equal(reply.ok, !c.fault, `${c.id}: ${JSON.stringify(reply)}`)
        const calls = c.fault ? reply.error.calls : reply.result.calls
        assert.equal(calls.filter(call => call.operation === 'close').length, 1)
        if (c.fault) { assert.equal(reply.error.code, 'RunRuntimeFailure'); assert.match(reply.error.message, /worker fault/); continue }
        assert.deepEqual(calls.filter(call => call.operation === 'report').map(call => call.arguments[0].value.value), c.expected, c.id)
      }
    }
    await virtual()
    assert.equal(hash(await readFile(snapshot)), before)
    await closeAuthoring(context)
    // Replay from a moved directory retains IDs, source and symbols, and never edits the snapshot.
    const replay = join(root, 'replayed.json')
    await copyFile(snapshot, replay)
    // The workspace's backend declaration moves with it.
    await copyFile(snapshot + '.backends.json', replay + '.backends.json')
    await openAuthoring('open', replay, context)
    assert.deepEqual(await emit(), artifact)
    await virtual()
    assert.equal(hash(await readFile(replay)), before)
    await closeAuthoring(context)
    const symbol = (kind, id) => artifact.symbols[kind].find(s => s.id === id).name
    const functions = Object.fromEntries(declarations.functions.map(f => [f.displayName, symbol('functions', f.id)]))
    const csType = t => t.kind === 'primitive' ? ({ string: 'NyStr', unit: 'NyUnit', int64: 'long' })[t.primitive] : `NyResult<${t.arguments.map(csType).join(',')}>`
    const replacements = { HOST: symbol('capabilities', probe.id), METHODS: probe.operations.map(o => `public ${csType(o.resultType)} ${symbol('capabilityOperations', o.id)}(${o.parameters.map(p => `${csType(p.type)} ${p.displayName}`).join(',')}) => ${o.displayName}(${o.parameters.map(p => p.displayName).join(',')});`).join('\n') }
    for (const [name, fn] of Object.entries(functions)) replacements[name.toUpperCase()] = fn
    // Calls use positional arguments in declaration order; generic functions remain emitted verbatim.
    const program = (await readFile(new URL('../../examples/task-wait-observer/runtime.template.cs', import.meta.url), 'utf8')).replace(/@@([A-Z_]+)@@/g, (_, key) => { assert.ok(replacements[key], key); return replacements[key] })
    await writeFile(join(root, 'Generated.cs'), artifact.source)
    await writeFile(join(root, 'Program.cs'), program)
    await writeFile(join(root, 'cases.json'), JSON.stringify(cases))
    const options = { cwd: root, encoding: 'utf8', timeout: 60000, env: { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' } }
    const compiled = await compileRuntime(root, options)
    const rows = execFileSync('dotnet', [compiled.assembly, join(root, 'cases.json')], options).trim().split(/\r?\n/).map(row => JSON.parse(row))
    assert.equal(rows.length, cases.length + 11)
    assert.ok(rows.filter(row => row.passed).length === 11)
    for (const c of cases) {
      const row = rows.find(row => row.id === c.id)
      assert.equal(row.fault, !!c.fault, c.id)
      assert.equal(row.closed, 1, c.id)
      if (!c.fault) assert.deepEqual(row.reports, c.expected, c.id)
    }
    assert.equal(hash(await readFile(join(root, 'Generated.cs'))), hash(artifact.source))
    console.log(JSON.stringify({ virtualCases: cases.length, movedReplayCases: cases.length, realCases: rows.length, snapshotSha256: before, generatedSha256: hash(artifact.source), generatedSourceUnchanged: true, compilation: compiled.evidence, rows }))
  } finally {
    await closeAuthoring(context).catch(() => undefined)
    await rm(root, { recursive: true, force: true })
  }
})
