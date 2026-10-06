// Spike: a reader task publishes items into a chain of OneShot cells that a driver consumes.
import assert from 'node:assert/strict'
import { mkdtemp, appendFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
const repo = fileURLToPath(new URL('../..', import.meta.url))
const z = await import('zod/v4')
const { createGraphTools } = await import(`${repo}/tools/nyrwvy-agent/graph-tools.mjs`)
const dir = await mkdtemp(join(tmpdir(), 'chain-')), snapshot = join(dir, 'chain.json')
const tools = createGraphTools(z), context = { sessionID: `chain-${process.pid}`, worktree: repo }
async function exchange(name, args = {}) { const raw = await tools[name].execute(args, context); await appendFile(join(dir, 't.jsonl'), JSON.stringify({ name, args, raw }) + '\n'); return name === 'close' ? raw : JSON.parse(raw) }
async function call(name, args = {}) { const r = await exchange(name, args); assert.notEqual(r.ok, false, `${name}: ${JSON.stringify(r).slice(0, 1500)}`); return r.result ?? r }
async function publish(p) { assert.equal(p.review.readiness.kind, 'semanticallyReady', JSON.stringify(p.review.readiness)); await call('commit', { proposalId: p.proposalId }) }
const prim = k => ({ kind: 'primitive', primitive: k }), string = prim('string'), unit = prim('unit'), int = prim('int64')
const gen = (generic, ...a) => ({ kind: 'builtinGeneric', generic, arguments: a }), named = name => ({ kind: 'named', name }), cap = name => ({ kind: 'capability', name })
const outcome = gen('result', string, string), link = named('Link'), cell = gen('oneShot', link), scope = gen('taskScope')
const p = name => ({ kind: 'parameter', name }), text = value => ({ kind: 'stringLiteral', value }), integer = n => ({ kind: 'int64Literal', value: String(n) })
const pat = (slot = 'value', scopeDepth = 0) => ({ kind: 'patternReference', slot, scopeDepth }), bind = (value, body) => ({ kind: 'bind', value, body })
const field = (source, field) => ({ kind: 'fieldAccess', source, type: 'Link', field })
const ok = value => ({ kind: 'resultOk', value, errorType: string }), err = error => ({ kind: 'resultErr', error, okType: string })
const direct = (fn, values) => ({ kind: 'directCall', function: fn, arguments: Object.entries(values).map(([parameter, value]) => ({ parameter, value })) })
const matchOption = (input, someBody, noneBody) => ({ kind: 'matchOption', input, someBody, noneBody })
const read = { kind: 'capabilityInvoke', capabilityValue: p('wire'), capability: 'Wire', operation: 'read', arguments: [] }
async function create(displayName, parameters, resultType, body, requiredEffects = ['Transport']) {
  await publish(await call('create_function', { displayName, parameters: Object.entries(parameters).map(([displayName, type]) => ({ displayName, type })), resultType, body, requiredEffects }))
}
await call('open', { mode: 'create', snapshot })
await publish(await call('request', { op: 'simulateCreateTypesByName', arguments: { types: [{ displayName: 'Link', typeKind: { kind: 'product', fields: [{ displayName: 'item', type: string }, { displayName: 'next', type: cell }] } }] } }))
await publish(await call('request', { op: 'simulateCreateEffect', arguments: { displayName: 'Transport' } }))
await publish(await call('request', { op: 'simulateCreateCapabilityByName', arguments: { displayName: 'Wire', operations: [{ displayName: 'read', parameters: [], resultType: gen('option', string), effect: 'Transport' }] } }))
// Reader: read an item, create the next cell, complete the tail with both, continue from the new cell.
await create('readerLoop', { wire: cap('Wire'), scope, tail: cell, item: unit }, outcome,
  matchOption(read,
    bind({ kind: 'oneShotCreate', scope: p('scope'), valueType: link },
      bind({ kind: 'oneShotComplete', cell: p('tail'), value: { kind: 'productConstruct', type: 'Link', fields: [{ field: 'item', value: pat('value', 1) }, { field: 'next', value: pat('value', 0) }] } },
        direct('readerLoop', { wire: p('wire'), scope: p('scope'), tail: pat('value', 1), item: { kind: 'unitLiteral' } }))),
    ok(text('eof'))))
// Driver: wait on the current cell, append its item, continue from its next cell.
await create('consume', { tail: cell, count: int, acc: string, limit: int }, outcome,
  { kind: 'conditional', condition: { kind: 'int64Equal', left: p('count'), right: integer(0) }, whenTrue: ok(p('acc')),
    whenFalse: matchOption({ kind: 'oneShotWait', cell: p('tail'), limit: { kind: 'optionSome', value: p('limit') } },
      direct('consume', { tail: field(pat(), 'next'), count: { kind: 'int64WrappingSubtract', left: p('count'), right: integer(1) }, acc: { kind: 'stringConcat', left: p('acc'), right: field(pat(), 'item') }, limit: p('limit') }),
      err({ kind: 'stringConcat', left: text('timeout after '), right: p('acc') })) }, [])
await create('body', { wire: cap('Wire'), count: int, scope }, outcome,
  bind({ kind: 'oneShotCreate', scope: p('scope'), valueType: link },
    bind({ kind: 'taskStart', scope: p('scope'), worker: 'readerLoop', item: { kind: 'unitLiteral' }, context: [{ parameter: 'wire', value: p('wire') }, { parameter: 'scope', value: p('scope') }, { parameter: 'tail', value: pat('value', 0) }] },
      direct('consume', { tail: pat('value', 1), count: p('count'), acc: text(''), limit: integer(50) }))))
await create('run', { wire: cap('Wire'), count: int }, outcome, { kind: 'taskScope', body: 'body', context: [{ parameter: 'wire', value: p('wire') }, { parameter: 'count', value: p('count') }] })
await call('close'); await call('open', { mode: 'open', snapshot })
const some = (v, delay) => ({ kind: 'return', value: { kind: 'option', itemKind: 'string', hasValue: true, value: v }, ...(delay ? { delayMillis: String(delay) } : {}) })
const awaiting = { kind: 'awaitCancellation', value: { kind: 'option', itemKind: 'string', hasValue: false } }
const flat = (value, isOk = true) => ({ kind: 'result', okKind: 'string', errorKind: 'string', isOk, value })
const cases = [
  ['immediate', [some('a'), some('b'), some('c'), awaiting], 3, flat('abc')],
  ['delayed', [some('a', 10), some('b', 20), some('c', 5), awaiting], 3, flat('abc')],
  ['timeout', [some('a', 10), some('b', 100), awaiting], 3, flat('timeout after a', false)],
]
for (const [id, reads, count, expected] of cases) {
  const r = await exchange('request', { op: 'runPureByName', arguments: { function: 'run', arguments: [{ kind: 'fakeCapability', capability: 'Wire', operations: [{ operation: 'read', responses: reads }] }, { kind: 'int64', value: String(count) }], expected } })
  assert.equal(r.ok, true, JSON.stringify(r).slice(0, 1500)); assert.equal(r.result.matches, true, JSON.stringify(r.result).slice(0, 1500))
  console.log(`PASS ${id}`)
}
const fns = (await call('inspect', { view: 'declarations' })).functions
console.log(JSON.stringify(fns.map(f => [f.displayName, f.nodeCount ?? f.nodes])))
await call('close'); console.log(dir)
