// Probe 26, event-loop version: the session's caller runs one loop (pump) that waits
// on the pending read task and timer tasks with TaskWait(any) and threads a state Product.
import { session as open, string, int, bool, unit, cap, named, scope, task, list, option, result,
  p, text, integer, boolean, unitValue, pat, bind, field, construct, ok, err, direct, cond, matchOption,
  concat, eqStr, eqInt, invoke, cancellation } from './lib.mjs'
import { declare } from './declare.mjs'
import { scenarios, entryArguments, expected, replies } from './cases.mjs'

const s = await open('eventloop'); await declare(s)
const fields = o => Object.entries(o).map(([displayName, type]) => ({ displayName, type }))
const pairs = (o, key) => Object.entries(o).map(([k, value]) => ({ [key]: k, value }))
const product = async (name, o) => s.publish(await s.call('create_type', { displayName: name, typeKind: { kind: 'product', fields: fields(o) } }))
const union = async (name, variants) => s.publish(await s.call('create_type', { displayName: name, typeKind: { kind: 'union', variants: Object.entries(variants).map(([displayName, f]) => ({ displayName, fields: fields(f) })) } }))

// Recipe helpers not in lib.mjs
const variant = (type, name, f = {}) => ({ kind: 'unionConstruct', type, variant: name, fields: pairs(f, 'field') })
const matchU = (type, input, arms, otherwise) => ({ kind: 'matchUnion', type, input, arms: Object.entries(arms).map(([v, body]) => ({ variant: v, body })), ...(otherwise ? { otherwise } : {}) })
const of = (type, v, f, depth = 0) => ({ kind: 'patternReference', slot: 'field', scopeDepth: depth, type, variant: v, field: f })
const update = (source, f) => ({ kind: 'productUpdate', source, type: 'St', fields: pairs(f, 'field') })
const some = value => ({ kind: 'optionSome', value }), none = itemType => ({ kind: 'optionNone', itemType })
const listOf = (itemType, ...items) => ({ kind: 'listConstruct', itemType, items })
const append = (l, item) => ({ kind: 'listAppendItem', list: l, item })
const call = (op, args = {}) => invoke(p('pipe'), 'Pipe', op, args)
const start = (worker, item, context = {}) => ({ kind: 'taskStart', scope: p('scope'), worker, item, context: pairs(context, 'parameter') })
const loop = values => ({ ...direct('pump', values), keepUnlisted: true })
const st = (name, source = p('st')) => field(source, 'St', name)
const PS = { pipe: p('pipe'), scope: p('scope') }

// Types
const Wake = named('Wake'), Waiting = named('Waiting'), Handler = named('Handler'), St = named('St')
const wakeTask = task(result(Wake)), handlerTask = task(result(unit)), outcome = result(string)
await union('Wake', { Arrived: { input: option(named('In')) }, Finished: { reason: string } })
await union('Waiting', { Idle: {}, ForResponse: { id: int }, ForTurn: {} })
await product('Handler', { tool: string, task: handlerTask })
await product('St', { reader: wakeTask, inputEnded: bool, closing: bool, nextId: int, waiting: Waiting, outcome: option(outcome),
  turnText: option(string), turnResult: option(string), handlers: list(Handler), parts: list(string), stopped: bool })
const pipeT = cap('Pipe'), scopeT = scope

// Tasks: reader, timers, handlers, join
await s.create('readNext', { pipe: pipeT, item: unit }, result(Wake), ok(variant('Wake', 'Arrived', { input: call('read') })))
await s.create('timer', { pipe: pipeT, ms: int, reason: string }, result(Wake),
  bind(call('sleep', { ms: p('ms') }), ok(variant('Wake', 'Finished', { reason: p('reason') }))))
const reply = (isOk, value) => call('send', { message: variant('Out', 'Reply', { id: p('id'), ok: boolean(isOk), text: value }) })
await s.create('handle', { pipe: pipeT, closing: bool, id: int, tool: string, args: string, item: unit }, result(unit),
  cond(p('closing'), bind(reply(false, text('unavailable')), ok(unitValue)),
    bind(call('tool', { name: p('tool'), args: p('args') }),
      cond(cancellation, ok(unitValue),
        bind({ kind: 'matchResult', input: pat(), okBody: reply(true, pat()), errBody: reply(false, pat('error')) }, ok(unitValue))))))
await s.create('joinHandlers', { tasks: list(handlerTask) }, result(Wake),
  bind({ kind: 'taskWait', tasks: p('tasks'), mode: 'all' }, ok(variant('Wake', 'Finished', { reason: text('handlers finished') }))))
await s.create('startReader', { pipe: pipeT, scope: scopeT }, wakeTask, start('readNext', unitValue, { pipe: p('pipe') }))
await s.create('startTimer', { pipe: pipeT, scope: scopeT, ms: int, reason: string }, wakeTask, start('timer', p('reason'), { pipe: p('pipe'), ms: p('ms') }))

// Pure state helpers
await s.create('settle', { st: St, result: outcome }, St, update(p('st'), { outcome: some(p('result')), waiting: variant('Waiting', 'Idle') }), [])
await s.create('expect', { st: St, waiting: Waiting }, St,
  cond(st('inputEnded'), direct('settle', { st: p('st'), result: err(text('closed')) }), update(p('st'), { waiting: p('waiting'), outcome: none(outcome) })), [])
const finishTurn = update(p('st'), { turnResult: some(p('text')) })
await s.create('completeTurn', { st: St, text: string }, St,
  matchU('Waiting', st('waiting'), { ForTurn: direct('settle', { st: finishTurn, result: ok(p('text')) }) }, finishTurn), [])
await s.create('record', { st: St, label: string }, St,
  matchOption(st('outcome'), { kind: 'matchResult', input: pat(),
    okBody: update(p('st'), { parts: append(st('parts'), concat(p('label'), text('='), pat())) }),
    errBody: update(p('st'), { parts: append(st('parts'), concat(text('error='), pat('error'))), stopped: boolean(true) }) }, p('st')), [])
await s.create('answerText', { st: St }, option(string),
  matchOption(st('outcome'), { kind: 'matchResult', input: pat(), okBody: some(pat()), errBody: none(string) }, none(string)), [])
await s.create('succeeded', { st: St }, bool,
  matchOption(st('outcome'), { kind: 'matchResult', input: pat(), okBody: boolean(true), errBody: boolean(false) }, boolean(false)), [])
await s.create('handlerTask', { handler: Handler }, handlerTask, field(p('handler'), 'Handler', 'task'), [])

// Reader path: one finished task result folded into the state
const R = (f, d = 0) => of('In', 'Response', f, d), C = f => of('In', 'ServerCall', f), N = (f, d = 0) => of('In', 'Notice', f, d)
await s.create('onMessage', { pipe: pipeT, scope: scopeT, st: St, message: named('In') }, St, matchU('In', p('message'), {
  Response: matchU('Waiting', st('waiting'), {
    ForResponse: cond(eqInt(of('Waiting', 'ForResponse', 'id'), R('id', 1)),
      direct('settle', { st: p('st'), result: cond(R('isError', 1), err(concat(text('rpc '), R('text', 1))), ok(R('text', 1))) }), p('st')) }, p('st')),
  ServerCall: update(p('st'), { handlers: append(st('handlers'), construct('Handler', { tool: C('tool'),
    task: start('handle', unitValue, { pipe: p('pipe'), closing: st('closing'), id: C('id'), tool: C('tool'), args: C('args') }) })) }),
  Notice: bind(call('event', { method: N('method'), text: N('text') }),
    cond(eqStr(N('method', 1), text('turn/completed')), direct('completeTurn', { st: p('st'), text: N('text', 1) }), p('st'))),
}))
const ended = update(p('st'), { inputEnded: boolean(true) })
await s.create('absorb', { pipe: pipeT, scope: scopeT, st: St, input: option(named('In')) }, St,
  matchOption(p('input'),
    bind(direct('onMessage', { ...PS, st: p('st'), message: pat() }), bind(direct('startReader', PS), update(pat('value', 1), { reader: pat() }))),
    matchU('Waiting', st('waiting'), { Idle: ended }, direct('settle', { st: ended, result: err(text('closed')) }))))
await s.create('wake', { pipe: pipeT, scope: scopeT, st: St, item: option(result(Wake)) }, St,
  matchOption(p('item'), { kind: 'matchResult', input: pat(),
    okBody: matchU('Wake', pat(), {
      Arrived: direct('absorb', { ...PS, st: p('st'), input: of('Wake', 'Arrived', 'input') }),
      Finished: matchOption(st('outcome'), p('st'), direct('settle', { st: p('st'), result: err(of('Wake', 'Finished', 'reason', 1)) })) }),
    errBody: p('st') }, p('st')))

// Request / wait path
await s.create('pump', { pipe: pipeT, scope: scopeT, st: St, timers: list(wakeTask) }, St,
  matchOption(st('outcome'), p('st'),
    bind({ kind: 'taskWait', mode: 'any', tasks: cond(st('inputEnded'), p('timers'), { kind: 'listConcat', left: listOf(wakeTask, st('reader')), right: p('timers') }) },
      loop({ st: { kind: 'listEffectFold', source: pat(), seed: p('st'), function: 'wake', context: pairs(PS, 'parameter') } }))))
await s.create('request', { pipe: pipeT, scope: scopeT, st: St, method: string, param: string, limit: int, deadline: list(wakeTask) }, St,
  bind(call('send', { message: variant('Out', 'Call', { id: st('nextId'), method: p('method'), param: p('param') }) }),
    bind(direct('startTimer', { ...PS, ms: p('limit'), reason: concat(text('timeout '), p('method')) }),
      bind(direct('pump', { ...PS, timers: { kind: 'listConcat', left: listOf(wakeTask, pat()), right: p('deadline') },
        st: direct('expect', { st: update(p('st'), { nextId: { kind: 'int64WrappingAdd', left: st('nextId'), right: integer(1) } }), waiting: variant('Waiting', 'ForResponse', { id: st('nextId') }) }) }),
        bind({ kind: 'taskCancel', task: pat('value', 1) }, pat('value', 1))))))
await s.create('waitTurn', { pipe: pipeT, scope: scopeT, st: St, timers: list(wakeTask) }, St,
  matchOption(st('turnResult'), direct('settle', { st: p('st'), result: ok(pat()) }),
    direct('pump', { ...PS, timers: p('timers'), st: direct('expect', { st: p('st'), waiting: variant('Waiting', 'ForTurn') }) })))
const noTimers = { kind: 'listEmpty', itemType: wakeTask }
const ask = (source, method, param, label) => direct('record', { label: text(label), st: direct('request', { ...PS, st: source, method: text(method), param, limit: p('requestLimit'), deadline: noTimers }) })
await s.create('turnStep', { pipe: pipeT, scope: scopeT, st: St, prompt: string, requestLimit: int, turnLimit: int }, St,
  bind(direct('startTimer', { ...PS, ms: p('turnLimit'), reason: text('deadline') }),
    bind(direct('record', { label: text('turn'), st: direct('request', { ...PS, st: p('st'), method: text('turn/start'), param: p('prompt'), limit: p('requestLimit'), deadline: listOf(wakeTask, pat()) }) }),
      bind(cond(st('stopped', pat()), pat(),
        direct('record', { label: text('result'), st: direct('waitTurn', { ...PS, st: update(pat(), { turnText: direct('answerText', { st: pat() }) }), timers: listOf(wakeTask, pat('value', 1)) }) })),
        bind({ kind: 'taskCancel', task: pat('value', 2) }, pat('value', 1))))))

// Handler / close path
await s.create('cancelUnlessCommit', { done: unit, handler: Handler }, unit,
  cond(eqStr(field(p('handler'), 'Handler', 'tool'), text('commit')), unitValue, { kind: 'taskCancel', task: field(p('handler'), 'Handler', 'task') }))
await s.create('interrupt', { pipe: pipeT, scope: scopeT, st: St, turn: string }, string,
  bind(direct('request', { ...PS, st: p('st'), method: text('turn/interrupt'), param: p('turn'), limit: integer(2), deadline: noTimers }),
    cond(direct('succeeded', { st: pat() }),
      bind(direct('startTimer', { ...PS, ms: integer(2), reason: text('turn still running') }),
        bind(direct('waitTurn', { ...PS, st: pat('value', 1), timers: listOf(wakeTask, pat()) }),
          cond(direct('succeeded', { st: pat() }), text('interrupted'), text('interrupt-failed')))),
      text('interrupt-failed'))))
await s.create('closeStatus', { pipe: pipeT, scope: scopeT, st: St }, string,
  matchOption(st('turnText'),
    matchOption(st('turnResult'), text('ok'), cond(st('inputEnded'), text('ok'), direct('interrupt', { ...PS, st: p('st'), turn: pat('value', 1) }))),
    text('ok')))
await s.create('closeSession', { pipe: pipeT, scope: scopeT, st: St }, result(string),
  bind(update(p('st'), { closing: boolean(true), waiting: variant('Waiting', 'Idle'), outcome: none(outcome) }),
    bind({ kind: 'listEffectFold', source: st('handlers', pat()), seed: unitValue, function: 'cancelUnlessCommit' },
      bind(start('joinHandlers', { kind: 'listMap', source: st('handlers', pat('value', 1)), function: 'handlerTask' }),
        bind(direct('pump', { ...PS, st: pat('value', 2), timers: listOf(wakeTask, pat()) }),
          bind(direct('closeStatus', { ...PS, st: pat() }),
            ok({ kind: 'stringJoin', list: append(st('parts', pat('value', 1)), concat(text('close='), pat())), separator: text(';') })))))))

// Session
const stopped = st('stopped', pat())
await s.create('body', { pipe: pipeT, prompt: string, requestLimit: int, turnLimit: int, scope: scopeT }, result(string),
  bind(direct('startReader', PS),
    bind(construct('St', { reader: pat(), inputEnded: boolean(false), closing: boolean(false), nextId: integer(1), waiting: variant('Waiting', 'Idle'),
      outcome: none(outcome), turnText: none(string), turnResult: none(string), handlers: { kind: 'listEmpty', itemType: Handler }, parts: { kind: 'listEmpty', itemType: string }, stopped: boolean(false) }),
      bind(ask(pat(), 'initialize', text(''), 'info'),
        bind(cond(stopped, pat(), ask(pat(), 'thread/start', text(''), 'thread')),
          bind(cond(stopped, pat(), direct('turnStep', { ...PS, st: pat(), prompt: p('prompt'), requestLimit: p('requestLimit'), turnLimit: p('turnLimit') })),
            direct('closeSession', { ...PS, st: pat() })))))))
await s.create('session', { pipe: pipeT, prompt: string, requestLimit: int, turnLimit: int }, result(string),
  { kind: 'taskScope', body: 'body', context: pairs({ pipe: p('pipe'), prompt: p('prompt'), requestLimit: p('requestLimit'), turnLimit: p('turnLimit') }, 'parameter') })

// Run every scenario
let failures = 0
for (const sc of scenarios) {
  const r = await s.exchange('request', { op: 'runPureByName', arguments: { function: 'session', arguments: entryArguments(sc), expected: expected(sc) } })
  const res = r.result ?? r.error ?? r
  const actual = res.actual?.value
  const got = replies(res.calls ?? []).map(v => { const f = Object.fromEntries((v.fields ?? []).map(x => [x.field, x.value.value])); return `${f.id}:${f.ok}:${f.text}` }).sort()
  const pass = r.ok !== false && actual === sc.output && JSON.stringify(got) === JSON.stringify([...sc.replies].sort())
  if (!pass) { failures++; console.log(`FAIL ${sc.id}: got ${JSON.stringify(actual)} replies ${JSON.stringify(got)}`, r.ok === false ? JSON.stringify(r.error).slice(0, 1500) : '') }
  else console.log(`PASS ${sc.id}`)
}
const total = Object.values(s.nodes).reduce((a, b) => a + b, 0)
console.log(JSON.stringify(s.nodes), `functions=${Object.keys(s.nodes).length} nodes=${total} failures=${failures}`)
await s.call('close')
