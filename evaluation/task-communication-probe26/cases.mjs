// Shared scenarios for probe 26. Both versions run exactly these, through runPureByName on `session`.
import { sv, iv, returned, flat, unit } from './lib.mjs'
const b = value => ({ kind: 'bool', value })
const inbound = (variant, fields) => ({ kind: 'union', type: 'In', variant, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
const some = (value, delay) => returned({ kind: 'option', itemType: { kind: 'named', name: 'In' }, hasValue: true, value }, delay)
export const R = (id, text, delay = 5) => some(inbound('Response', { id: iv(id), isError: b(false), text: sv(text) }), delay)
export const E = (id, text, delay = 5) => some(inbound('Response', { id: iv(id), isError: b(true), text: sv(text) }), delay)
export const S = (id, tool, args, delay = 5) => some(inbound('ServerCall', { id: iv(id), tool: sv(tool), args: sv(args) }), delay)
export const N = (method, text, delay = 5) => some(inbound('Notice', { method: sv(method), text: sv(text) }), delay)
export const EOF = (delay = 5) => returned({ kind: 'option', itemType: { kind: 'named', name: 'In' }, hasValue: false }, delay)
export const PENDING = { kind: 'awaitCancellation', value: { kind: 'option', itemType: { kind: 'named', name: 'In' }, hasValue: false } }
const toolOk = (text, delay) => returned({ kind: 'result', okKind: 'string', errorKind: 'string', isOk: true, value: text }, delay)
const toolErr = (text, delay) => returned({ kind: 'result', okKind: 'string', errorKind: 'string', isOk: false, value: text }, delay)
const toolCancel = { kind: 'awaitCancellation', value: { kind: 'result', okKind: 'string', errorKind: 'string', isOk: false, value: 'cancelled' } }
// tools: [[name, args, response]]; replies: the Reply messages `send` must receive, as `${id}:${ok}:${text}`, in any order.
export const scenarios = [
  { id: 'happy', reads: [R(1, 'I'), R(2, 'T'), R(3, 'U'), N('item', 'x'), N('turn/completed', 'R'), PENDING], tools: [], replies: [],
    output: 'info=I;thread=T;turn=U;result=R;close=ok' },
  { id: 'ignored-ids', reads: [R(9, '?', 1), R(1, 'I', 4), R(1, 'dup', 1), R(2, 'T', 4), R(3, 'U'), N('turn/completed', 'R'), PENDING], tools: [], replies: [],
    output: 'info=I;thread=T;turn=U;result=R;close=ok' },
  { id: 'tools', reads: [R(1, 'I'), R(2, 'T'), R(3, 'U'), S(7, 'lint', 'a'), S(8, 'commit', 'b'), N('turn/completed', 'R', 20), PENDING],
    tools: [['lint', 'a', toolOk('fine')], ['commit', 'b', toolErr('denied')]], replies: ['7:true:fine', '8:false:denied'],
    output: 'info=I;thread=T;turn=U;result=R;close=ok' },
  { id: 'request-timeout', reads: [R(1, 'I'), PENDING], tools: [], replies: [],
    output: 'info=I;error=timeout thread/start;close=ok' },
  { id: 'rpc-error', reads: [R(1, 'I'), E(2, 'bad'), PENDING], tools: [], replies: [],
    output: 'info=I;error=rpc bad;close=ok' },
  // turn/start is sent at t=10, so the deadline is t=110; the interrupt (id 4) is answered at t=111 and the turn completes at t=112.
  { id: 'deadline-interrupt', reads: [R(1, 'I'), R(2, 'T'), R(3, 'U'), R(4, 'stopping', 96), N('turn/completed', 'late', 1), PENDING], tools: [], replies: [],
    output: 'info=I;thread=T;turn=U;error=deadline;close=interrupted' },
  { id: 'deadline-interrupt-unanswered', reads: [R(1, 'I'), R(2, 'T'), R(3, 'U'), PENDING], tools: [], replies: [],
    output: 'info=I;thread=T;turn=U;error=deadline;close=interrupt-failed' },
  { id: 'eof', reads: [R(1, 'I'), R(2, 'T'), R(3, 'U'), EOF()], tools: [], replies: [],
    output: 'info=I;thread=T;turn=U;error=closed;close=ok' },
  // The turn completes at t=25 while `commit` (until t=220) and `lint` (until cancelled) still run; a server call at t=60 arrives during close.
  { id: 'close-with-handlers', reads: [R(1, 'I'), R(2, 'T'), R(3, 'U'), S(7, 'commit', 'c'), S(8, 'lint', 'l', 1), N('turn/completed', 'R', 4), S(9, 'lint', 'x', 35), PENDING],
    tools: [['commit', 'c', toolOk('done', 200)], ['lint', 'l', toolCancel]], replies: ['7:true:done', '9:false:unavailable'],
    output: 'info=I;thread=T;turn=U;result=R;close=ok' },
]
export const requestLimit = 60, turnLimit = 100
// The Pipe fake. `sleep` answers after its argument in milliseconds for the limits the spec uses (2, requestLimit, turnLimit).
export function pipe(s) {
  const many = (n, r) => Array.from({ length: n }, () => r)
  return { kind: 'fakeCapability', capability: 'Pipe', operations: [
    { operation: 'read', responses: s.reads },
    { operation: 'send', responses: many(30, returned({ kind: 'unit' })) },
    { operation: 'event', responses: many(30, returned({ kind: 'unit' })) },
    { operation: 'sleep', cases: [2, requestLimit, turnLimit].map(ms => ({ arguments: [{ parameter: 'ms', value: iv(ms) }], responses: many(30, returned({ kind: 'unit' }, ms)) })) },
    { operation: 'tool', cases: s.tools.length ? s.tools.map(([name, args, response]) => ({ arguments: [{ parameter: 'name', value: sv(name) }, { parameter: 'args', value: sv(args) }], responses: [response] })) : [{ arguments: [{ parameter: 'name', value: sv('none') }, { parameter: 'args', value: sv('none') }], responses: [] }] },
  ] }
}
export const entryArguments = s => [pipe(s), sv('hello'), iv(requestLimit), iv(turnLimit)]
export const expected = s => flat(s.output)
// Reply messages seen by `send`, from a run result's `calls`.
export function replies(calls) {
  return calls.filter(c => c.operation === 'send').map(c => c.arguments?.[0]?.value ?? c.arguments?.[0]).filter(v => JSON.stringify(v).includes('Reply'))
}
