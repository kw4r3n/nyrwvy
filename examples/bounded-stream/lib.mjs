// Local authoring helpers; deliberately the ordinary graph-tool API, not a second runtime.
import assert from 'node:assert/strict'
import { mkdtemp, appendFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
export const repo = fileURLToPath(new URL('../..', import.meta.url))
const z = await import('zod/v4')
const { createGraphTools } = await import(`${repo}/tools/nyrwvy-agent/graph-tools.mjs`)
export async function session(label, output) {
  const dir = output ?? await mkdtemp(join(tmpdir(), `dynamic-${label}-`)), snapshot = join(dir, `${label}.nyrec`)
  const tools = createGraphTools(z), context = { sessionID: `${label}-${process.pid}`, worktree: repo }
  const nodes = {}
  async function exchange(name, args = {}) { const raw = await tools[name].execute(args, context); await appendFile(join(dir, 'transcript.jsonl'), JSON.stringify({ name, args, raw }) + '\n'); return name === 'close' ? raw : JSON.parse(raw) }
  async function call(name, args = {}) { const r = await exchange(name, args); assert.notEqual(r.ok, false, `${name}: ${JSON.stringify(r).slice(0, 2000)}`); return r.result ?? r }
  async function publish(p) { assert.equal(p.review.readiness.kind, 'semanticallyReady', JSON.stringify(p.review.readiness)); await call('commit', { proposalId: p.proposalId }) }
  async function create(displayName, parameters, resultType, body, requiredEffects = ['Io']) {
    const proposal = await call('create_function', { displayName, parameters: Object.entries(parameters).map(([displayName, type]) => ({ displayName, type })), resultType, body, requiredEffects })
    nodes[displayName] = proposal.review.createdFunction.body.nodeCount
    await publish(proposal)
  }
  return { dir, snapshot, exchange, call, publish, create, nodes }
}
// Type references
export const prim = k => ({ kind: 'primitive', primitive: k })
export const string = prim('string'), unit = prim('unit'), int = prim('int64'), bool = prim('bool')
export const gen = (generic, ...a) => ({ kind: 'builtinGeneric', generic, arguments: a })
export const named = name => ({ kind: 'named', name }), cap = name => ({ kind: 'capability', name })
export const scope = gen('taskScope'), task = t => gen('task', t), oneShot = t => gen('oneShot', t), list = t => gen('list', t), option = t => gen('option', t), result = (o, e = string) => gen('result', o, e)
// Recipes
export const p = name => ({ kind: 'parameter', name })
export const text = value => ({ kind: 'stringLiteral', value }), integer = n => ({ kind: 'int64Literal', value: String(n) }), boolean = value => ({ kind: 'boolLiteral', value }), unitValue = { kind: 'unitLiteral' }
export const pat = (slot = 'value', scopeDepth = 0) => ({ kind: 'patternReference', slot, scopeDepth })
export const bind = (value, body) => ({ kind: 'bind', value, body })
export const field = (source, type, field) => ({ kind: 'fieldAccess', source, type, field })
export const construct = (type, fields) => ({ kind: 'productConstruct', type, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
export const ok = (value, errorType = string) => ({ kind: 'resultOk', value, errorType }), err = (error, okType = string) => ({ kind: 'resultErr', error, okType })
export const direct = (fn, values) => ({ kind: 'directCall', function: fn, arguments: Object.entries(values).map(([parameter, value]) => ({ parameter, value })) })
export const cond = (condition, whenTrue, whenFalse) => ({ kind: 'conditional', condition, whenTrue, whenFalse })
export const matchOption = (input, someBody, noneBody) => ({ kind: 'matchOption', input, someBody, noneBody })
export const concat = (...values) => values.reduce((left, right) => ({ kind: 'stringConcat', left, right }))
// String and Int64 are equatable: both comparisons are valueEqual (docs/decisions/retired-authoring-kinds.md).
export const eqStr = (left, right) => ({ kind: 'valueEqual', left, right }), eqInt = (left, right) => ({ kind: 'valueEqual', left, right })
export const invoke = (capabilityValue, capability, operation, values = {}) => ({ kind: 'capabilityInvoke', capabilityValue, capability, operation, arguments: Object.entries(values).map(([parameter, value]) => ({ parameter, value })) })
export const cancellation = { kind: 'cancellationRequested' }
// Run values
export const sv = value => ({ kind: 'string', value }), iv = n => ({ kind: 'int64', value: String(n) })
export const returned = (value, delay) => ({ kind: 'return', value, ...(delay ? { delayMillis: String(delay) } : {}) })
export const flat = (value, isOk = true) => ({ kind: 'result', okKind: 'string', errorKind: 'string', isOk, value })

