// Probe 26, ONESHOT style: the reader is a long-lived task from the start of the scope;
// it talks to the session driver only through scope-owned OneShot cells.
import { session, string, int, bool, unit, cap, scope, task, oneShot, list, option, result, named, p, text, integer, boolean, unitValue, pat, bind, field, construct, ok, err, direct, cond, matchOption, concat, eqStr, eqInt, invoke, cancellation } from './lib.mjs'
import { declare } from './declare.mjs'
import { scenarios, entryArguments, expected, replies } from './cases.mjs'

const s = await session('oneshot'); await declare(s)
const R = result(string), pending = named('Pending'), started = named('Started'), handlerResult = result(unit, string)
const fields = o => Object.entries(o).map(([displayName, type]) => ({ displayName, type }))
await s.publish(await s.call('request', { op: 'simulateCreateTypesByName', arguments: { types: [
  // One sent request, linked to the next: the driver appends, the reader answers.
  { displayName: 'Pending', typeKind: { kind: 'product', fields: fields({ id: int, answer: oneShot(R), next: oneShot(pending) }) } },
  // One started handler, linked to the next: the reader appends, close walks it.
  { displayName: 'Started', typeKind: { kind: 'product', fields: fields({ tool: string, task: task(handlerResult), next: oneShot(started) }) } },
] } }))

// Recipe helpers local to this program.
const pipe = p('pipe'), call = (operation, values) => invoke(pipe, 'Pipe', operation, values)
const loop = (fn, values = {}) => ({ ...direct(fn, values), keepUnlisted: true })
const create = (valueType) => ({ kind: 'oneShotCreate', scope: p('scope'), valueType })
const complete = (cell, value) => ({ kind: 'oneShotComplete', cell, value })
const waitCell = (cell, ms) => ({ kind: 'oneShotWait', cell, ...(ms === undefined ? {} : { limit: { kind: 'optionSome', value: ms } }) })
const start = (worker, context) => ({ kind: 'taskStart', scope: p('scope'), worker, item: unitValue, context: Object.entries(context).map(([parameter, value]) => ({ parameter, value })) })
const cancel = t => ({ kind: 'taskCancel', task: t }), tasks = (...items) => ({ kind: 'listConstruct', itemType: task(R), items })
const matchResult = (input, okBody, errBody) => ({ kind: 'matchResult', input, okBody, errBody })
const reply = (id, isOk, value) => ({ kind: 'unionConstruct', type: 'Out', variant: 'Reply', fields: [{ field: 'id', value: id }, { field: 'ok', value: boolean(isOk) }, { field: 'text', value }] })
const P = (source, name) => field(source, 'Pending', name), H = (source, name) => field(source, 'Started', name)
const unitOk = ok(unitValue)
const inField = (variant, name, scopeDepth = 0) => ({ kind: 'patternReference', scopeDepth, slot: 'field', type: 'In', variant, field: name })

// ---- timing and racing ----
await s.create('timer', { pipe: cap('Pipe'), ms: int, reason: string, item: unit }, R,
  bind(call('sleep', { ms: p('ms') }), err(p('reason'))))
await s.create('awaitCell', { cell: oneShot(R), item: unit }, R,
  matchOption(waitCell(p('cell')), pat(), err(text('cancelled'))))
await s.create('firstDone', { results: list(option(R)), index: int }, R,
  matchOption({ kind: 'listItemAt', list: p('results'), index: p('index') },
    matchOption(pat(), pat(), loop('firstDone', { index: { kind: 'int64WrappingAdd', left: p('index'), right: integer(1) } })),
    err(text('nothing finished'))), [])
// The first finished task in list order wins, so earlier entries win a same-round tie.
await s.create('race', { racers: list(task(R)) }, R,
  bind({ kind: 'taskWait', tasks: p('racers'), mode: 'any' }, direct('firstDone', { results: pat(), index: integer(0) })))

// ---- request / wait path (driver side) ----
// Append a Pending link at the end of the request chain; its position is the request ID.
await s.create('enqueue', { scope, cell: oneShot(pending), id: int, answer: oneShot(R) }, int,
  matchOption(waitCell(p('cell'), integer(0)),
    loop('enqueue', { cell: P(pat(), 'next'), id: { kind: 'int64WrappingAdd', left: p('id'), right: integer(1) } }),
    bind(create(pending),
      bind(complete(p('cell'), construct('Pending', { id: p('id'), answer: p('answer'), next: pat() })), p('id')))))
await s.create('request', { pipe: cap('Pipe'), scope, requests: oneShot(pending), method: string, param: string, limit: int, deadline: list(task(R)) }, R,
  bind(create(R),                                                                    // answer cell
    bind(direct('enqueue', { scope: p('scope'), cell: p('requests'), id: integer(1), answer: pat() }),   // id
      bind(call('send', { message: { kind: 'unionConstruct', type: 'Out', variant: 'Call', fields: [{ field: 'id', value: pat() }, { field: 'method', value: p('method') }, { field: 'param', value: p('param') }] } }),
        bind(start('awaitCell', { cell: pat('value', 2) }),                          // waiter
          bind(start('timer', { pipe, ms: p('limit'), reason: concat(text('timeout '), p('method')) }),  // request timer
            // Answer first, then the request limit, then the turn deadline.
            bind(direct('race', { racers: { kind: 'listConcat', left: tasks(pat('value', 1), pat()), right: p('deadline') } }),
              bind(cancel(pat('value', 2)), bind(cancel(pat('value', 2)), pat('value', 2))))))))))
const noDeadline = { kind: 'listEmpty', itemType: task(R) }
const req = (method, param, deadline = noDeadline) => direct('request', { pipe, scope: p('scope'), requests: p('requests'), method: text(method), param, limit: p('requestLimit'), deadline })
// After turn/start answered (frames: turnText, deadline, output so far, ...): wait for completion or the deadline.
const awaitTurn =
  bind(complete(p('started'), pat()),
    bind(concat(pat('value', 3), text(';turn='), pat('value', 1)),
      bind(start('awaitCell', { cell: p('turn') }),
        bind(direct('race', { racers: tasks(pat(), pat('value', 4)) }),
          bind(cancel(pat('value', 1)),
            bind(cancel(pat('value', 6)),
              concat(pat('value', 4), text(';'), matchResult(pat('value', 2), concat(text('result='), pat()), concat(text('error='), pat('error'))))))))))
// After thread/start answered (frames: thread, output so far, ...): start the turn deadline and run the turn.
const runTurn =
  bind(concat(pat('value', 1), text(';thread='), pat()),
    bind(start('timer', { pipe, ms: p('turnLimit'), reason: text('deadline') }),
      matchResult(req('turn/start', p('prompt'), tasks(pat())),
        awaitTurn,
        bind(cancel(pat('value', 1)), concat(pat('value', 3), text(';error='), pat('error', 1))))))
await s.create('steps', { pipe: cap('Pipe'), scope, requests: oneShot(pending), turn: oneShot(R), started: oneShot(string), prompt: string, requestLimit: int, turnLimit: int }, string,
  matchResult(req('initialize', text('')),
    bind(concat(text('info='), pat()),
      matchResult(req('thread/start', text('')),
        runTurn,
        concat(pat('value', 1), text(';error='), pat('error')))),
    concat(text('error='), pat('error'))))

// ---- reader ----
// Complete the answer of the request with this ID and move the cursor past it; unknown IDs leave it.
await s.create('deliver', { start: oneShot(pending), cell: oneShot(pending), id: int, reply: R }, oneShot(pending),
  matchOption(waitCell(p('cell'), integer(0)),
    cond(eqInt(P(pat(), 'id'), p('id')),
      bind(complete(P(pat(), 'answer'), p('reply')), P(pat('value', 1), 'next')),
      loop('deliver', { cell: P(pat(), 'next') })),
    p('start')))
// After input ended, every request still waiting or sent later fails with `closed`.
await s.create('failRequests', { cell: oneShot(pending) }, handlerResult,
  matchOption(waitCell(p('cell')),
    bind(complete(P(pat(), 'answer'), err(text('closed'))), loop('failRequests', { cell: P(pat('value', 1), 'next') })),
    unitOk))
await s.create('handle', { pipe: cap('Pipe'), closing: oneShot(unit), id: int, tool: string, args: string, item: unit }, handlerResult,
  matchOption(waitCell(p('closing'), integer(0)),
    bind(call('send', { message: reply(p('id'), false, text('unavailable')) }), unitOk),
    bind(call('tool', { name: p('tool'), args: p('args') }),
      cond(cancellation, unitOk,
        bind(call('send', { message: matchResult(pat(), reply(p('id'), true, pat()), reply(p('id'), false, pat('error'))) }), unitOk)))))
await s.create('startHandler', { pipe: cap('Pipe'), scope, closing: oneShot(unit), handlers: oneShot(started), id: int, tool: string, args: string }, oneShot(started),
  bind(start('handle', { pipe, closing: p('closing'), id: p('id'), tool: p('tool'), args: p('args') }),
    bind(create(started),
      bind(complete(p('handlers'), construct('Started', { tool: p('tool'), task: pat('value', 1), next: pat() })), pat('value', 1)))))
await s.create('readLoop', { pipe: cap('Pipe'), scope, closing: oneShot(unit), turn: oneShot(R), requests: oneShot(pending), handlers: oneShot(started), item: unit }, handlerResult,
  matchOption(call('read'),
    { kind: 'matchUnion', type: 'In', input: pat(), arms: [
      { variant: 'Response', body: loop('readLoop', { requests: direct('deliver', { start: p('requests'), cell: p('requests'), id: inField('Response', 'id'),
        reply: cond(inField('Response', 'isError'), err(concat(text('rpc '), inField('Response', 'text'))), ok(inField('Response', 'text'))) }) }) },
      { variant: 'ServerCall', body: loop('readLoop', { handlers: direct('startHandler', { pipe, scope: p('scope'), closing: p('closing'), handlers: p('handlers'), id: inField('ServerCall', 'id'), tool: inField('ServerCall', 'tool'), args: inField('ServerCall', 'args') }) }) },
      { variant: 'Notice', body: bind(call('event', { method: inField('Notice', 'method'), text: inField('Notice', 'text') }),
        bind(cond(eqStr(inField('Notice', 'method', 1), text('turn/completed')), complete(p('turn'), ok(inField('Notice', 'text', 1))), boolean(false)), loop('readLoop'))) },
    ] },
    bind(complete(p('turn'), err(text('closed'))), direct('failRequests', { cell: p('requests') }))))

// ---- handler / close path ----
// Cancel every started handler except `commit` and collect all of them to wait for.
await s.create('stopHandlers', { cell: oneShot(started), stopped: list(task(handlerResult)) }, list(task(handlerResult)),
  matchOption(waitCell(p('cell'), integer(0)),
    bind(cond(eqStr(H(pat(), 'tool'), text('commit')), unitValue, cancel(H(pat(), 'task'))),
      loop('stopHandlers', { cell: H(pat('value', 1), 'next'), stopped: { kind: 'listAppendItem', list: p('stopped'), item: H(pat('value', 1), 'task') } })),
    p('stopped')))
await s.create('interrupt', { pipe: cap('Pipe'), scope, requests: oneShot(pending), turn: oneShot(R), turnText: string }, string,
  matchResult(direct('request', { pipe, scope: p('scope'), requests: p('requests'), method: text('turn/interrupt'), param: p('turnText'), limit: integer(2), deadline: noDeadline }),
    matchOption(waitCell(p('turn'), integer(2)), matchResult(pat(), text('interrupted'), text('interrupt-failed')), text('interrupt-failed')),
    text('interrupt-failed')))
await s.create('close', { pipe: cap('Pipe'), scope, requests: oneShot(pending), handlers: oneShot(started), turn: oneShot(R), closing: oneShot(unit), started: oneShot(string) }, string,
  bind(complete(p('closing'), unitValue),
    bind(direct('stopHandlers', { cell: p('handlers'), stopped: { kind: 'listEmpty', itemType: task(handlerResult) } }),
      bind({ kind: 'taskWait', tasks: pat(), mode: 'all' },
        // Interrupt only a turn that started and has neither finished nor lost its input.
        matchOption(waitCell(p('started'), integer(0)),
          matchOption(waitCell(p('turn'), integer(0)), text('ok'),
            direct('interrupt', { pipe, scope: p('scope'), requests: p('requests'), turn: p('turn'), turnText: pat('value', 1) })),
          text('ok'))))))

// ---- entry ----
await s.create('body', { pipe: cap('Pipe'), prompt: string, requestLimit: int, turnLimit: int, scope }, R,
  bind(create(pending), bind(create(started), bind(create(R), bind(create(unit), bind(create(string),
    bind(start('readLoop', { pipe, scope: p('scope'), closing: pat('value', 1), turn: pat('value', 2), requests: pat('value', 4), handlers: pat('value', 3) }),
      bind(direct('steps', { pipe, scope: p('scope'), requests: pat('value', 5), turn: pat('value', 3), started: pat('value', 1), prompt: p('prompt'), requestLimit: p('requestLimit'), turnLimit: p('turnLimit') }),
        bind(direct('close', { pipe, scope: p('scope'), requests: pat('value', 6), handlers: pat('value', 5), turn: pat('value', 4), closing: pat('value', 3), started: pat('value', 2) }),
          ok(concat(pat('value', 1), text(';close='), pat())))))))))))
await s.create('session', { pipe: cap('Pipe'), prompt: string, requestLimit: int, turnLimit: int }, R,
  { kind: 'taskScope', body: 'body', context: ['pipe', 'prompt', 'requestLimit', 'turnLimit'].map(parameter => ({ parameter, value: p(parameter) })) })

// ---- scenarios ----
const replyText = v => { const f = Object.fromEntries((v.fields ?? []).map(x => [x.field, x.value?.value])); return `${f.id}:${f.ok}:${f.text}` }
let failures = 0
for (const sc of scenarios) {
  const r = await s.exchange('request', { op: 'runPureByName', arguments: { function: 'session', arguments: entryArguments(sc), expected: expected(sc) } })
  if (r.ok === false) { failures++; console.log(`FAIL ${sc.id}: ${JSON.stringify(r).slice(0, 1500)}`); continue }
  const got = replies(r.result.calls).map(replyText).sort(), want = [...sc.replies].sort()
  const pass = r.result.matches && JSON.stringify(got) === JSON.stringify(want)
  if (!pass) failures++
  console.log(`${pass ? 'PASS' : 'FAIL'} ${sc.id}${pass ? '' : ` actual=${JSON.stringify(r.result.actual)} replies=${JSON.stringify(got)}`}`)
}
const total = Object.values(s.nodes).reduce((a, b) => a + b, 0)
console.log(JSON.stringify(s.nodes), `functions=${Object.keys(s.nodes).length} nodes=${total} failures=${failures}`)
await s.call('close')
