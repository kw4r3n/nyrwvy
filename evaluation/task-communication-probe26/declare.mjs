// Shared declarations from SPEC.md. Both versions call declare(s) first.
import { string, int, bool, unit, option, result, named } from './lib.mjs'
const fields = o => Object.entries(o).map(([displayName, type]) => ({ displayName, type }))
export async function declare(s) {
  await s.call('open', { mode: 'create', snapshot: s.snapshot })
  await s.publish(await s.call('request', { op: 'simulateCreateEffect', arguments: { displayName: 'Io' } }))
  await s.publish(await s.call('create_type', { displayName: 'Out', typeKind: { kind: 'union', variants: [{ displayName: 'Call', fields: fields({ id: int, method: string, param: string }) }, { displayName: 'Reply', fields: fields({ id: int, ok: bool, text: string }) }] } }))
  await s.publish(await s.call('create_type', { displayName: 'In', typeKind: { kind: 'union', variants: [{ displayName: 'Response', fields: fields({ id: int, isError: bool, text: string }) }, { displayName: 'ServerCall', fields: fields({ id: int, tool: string, args: string }) }, { displayName: 'Notice', fields: fields({ method: string, text: string }) }] } }))
  const op = (displayName, parameters, resultType) => ({ displayName, parameters: fields(parameters), resultType, effect: 'Io' })
  await s.publish(await s.call('request', { op: 'simulateCreateCapabilityByName', arguments: { displayName: 'Pipe', operations: [
    op('send', { message: named('Out') }, unit), op('read', {}, option(named('In'))), op('tool', { name: string, args: string }, result(string)), op('event', { method: string, text: string }, unit), op('sleep', { ms: int }, unit)] } }))
}
