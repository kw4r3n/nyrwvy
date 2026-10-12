// Synthetic regression graph; lifecycle policy is separate from the reusable helpers.
import { defineTaskWaitObserver, gen, p, pat, bind, unit, int, string, unitValue, text, ok, cond, ctx, direct, wait } from './author.mjs'
export const outcome = gen('result', string, string), task = gen('task', outcome)
export const states = gen('list', gen('option', outcome)), observed = gen('result', states, string)
const scope = gen('taskScope'), host = { kind: 'capability', name: 'Probe' }
const list = (type, ...items) => ({ kind: 'listConstruct', itemType: type, items })
const none = type => ({ kind: 'optionNone', itemType: type })
const poll = { kind: 'optionSome', value: { kind: 'int64Literal', value: '0' } }
const eq = value => ({ kind: 'stringEqual', left: p('policy'), right: text(value) })
const io = (operation, values = {}) => ({ kind: 'capabilityInvoke', capabilityValue: p('host'), capability: 'Probe', operation, arguments: ctx(values) })
const start = (worker, item, values) => ({ kind: 'taskStart', scope: p('scope'), worker, item, context: ctx(values) })
const obsTask = gen('task', observed)
const memberTasks = gen('list', task)
const coordinate = extra => direct('coordinate', { host: p('host'), tasks: p('tasks'), monitor: p('monitor'), caller: p('caller'), policy: p('policy'), ...extra })

export async function defineFixture(create, publish) {
  await defineTaskWaitObserver(create)
  await publish({ op: 'simulateCreateEffect', displayName: 'Io' })
  await publish({ op: 'simulateCreateCapabilityByName', displayName: 'Probe', operations: [
    ['work', { name: string }, outcome], ['gate', { label: string }, unit],
    ['report', { value: string }, unit], ['open', {}, gen('result', unit, string)], ['close', { handle: unit }, unit],
  ].map(([displayName, parameters, resultType]) => ({ displayName, parameters: Object.entries(parameters).map(([displayName, type]) => ({ displayName, type })), resultType, effect: 'Io' })) })
  const createIo = (name, parameters, resultType, body) => create(name, parameters, resultType, body, ['Io'])
  await createIo('worker', { host, name: string }, outcome, io('work', { name: p('name') }))
  await create('render', { values: states }, string,
    { kind: 'listFold', source: p('values'), seed: text(''), function: 'renderItem' })
}

// Called before defineFixture: render is pure and can therefore format ordered snapshots.
export async function defineRender(create) {
  await create('renderItem', { acc: string, item: gen('option', outcome) }, string,
    { kind: 'stringConcat', left: p('acc'), right: { kind: 'stringConcat', left: text('|'), right:
      { kind: 'matchOption', input: p('item'), someBody:
        { kind: 'matchResult', input: pat(), okBody: pat(), errBody: pat('error') }, noneBody: text('running') } } })
}

export async function defineController(create) {
  const createIo = (name, parameters, resultType, body) => create(name, parameters, resultType, body, ['Io'])
  const snapshots = handles => direct('render', { values: wait(handles, 'all', poll) })
  const report = value => io('report', { value })
  const stop = name => ({ kind: 'taskCancel', task: p(name) })
  // Cancellation must finish the caller while both members AND its monitor are still pending.
  const cancellationReport = bind(wait(list(obsTask, p('caller'))),
    bind(report({ kind: 'matchOption', input: { kind: 'listItemAt', list: pat(), index: { kind: 'int64Literal', value: '0' } },
      someBody: { kind: 'matchOption', input: pat(), someBody: { kind: 'matchResult', input: pat(), okBody: text('wrong-success'), errBody: pat('error') }, noneBody: text('caller-pending') }, noneBody: text('missing') }),
      bind(report(snapshots(p('tasks'))),
        bind(report({ kind: 'matchOption', input: { kind: 'listItemAt', list: wait(list(obsTask, p('monitor')), 'all', poll), index: { kind: 'int64Literal', value: '0' } },
          someBody: { kind: 'matchOption', input: pat(), someBody: text('monitor-finished'), noneBody: text('monitor-running') }, noneBody: text('missing') }), ok(text('done'))))))
  const ordinaryReport = bind(direct('superviseTaskObservation', { monitor: p('monitor'), caller: p('caller') }),
    bind(wait(list(obsTask, p('caller'))),
      bind(report({ kind: 'matchOption', input: { kind: 'listItemAt', list: pat(), index: { kind: 'int64Literal', value: '0' } },
        someBody: { kind: 'matchOption', input: pat(), someBody: { kind: 'matchResult', input: pat(), okBody: direct('render', { values: pat() }), errBody: pat('error') }, noneBody: text('caller-pending') }, noneBody: text('missing') }), ok(text('done')))))
  await createIo('coordinate', { host, tasks: memberTasks, monitor: obsTask, caller: obsTask, policy: string }, outcome,
    bind(io('gate', { label: text('ready') }),
      cond(eq('owner-exit'), ok(text('exited')),
        cond(eq('cancel'), bind(stop('caller'), cancellationReport), ordinaryReport))))
  await createIo('startCaller', { host, scope, tasks: memberTasks, monitor: obsTask, signal: gen('oneShot', states), policy: string }, outcome,
    bind(start('awaitTaskObservation', unitValue, { signal: p('signal'), limit: none(int), noValueError: text('unobserved') }), coordinate({ caller: pat() })))
  await createIo('startMonitor', { host, scope, tasks: memberTasks, signal: gen('oneShot', states), mode: string, limit: gen('option', int), policy: string }, outcome,
    bind(cond({ kind: 'stringEqual', left: p('mode'), right: text('any') },
      start('observeTasks_any', unitValue, { tasks: p('tasks'), signal: p('signal'), limit: p('limit') }),
      start('observeTasks_all', unitValue, { tasks: p('tasks'), signal: p('signal'), limit: p('limit') })),
      direct('startCaller', { host: p('host'), scope: p('scope'), tasks: p('tasks'), signal: p('signal'), monitor: pat(), policy: p('policy') })))
  await createIo('makeSignal', { host, scope, tasks: memberTasks, mode: string, limit: gen('option', int), policy: string }, outcome,
    bind({ kind: 'oneShotCreate', scope: p('scope'), valueType: states },
      direct('startMonitor', { host: p('host'), scope: p('scope'), tasks: p('tasks'), signal: pat(), mode: p('mode'), limit: p('limit'), policy: p('policy') })))
  await createIo('afterStart', { host, scope, tasks: memberTasks, mode: string, limit: gen('option', int), policy: string }, outcome,
    bind(cond(eq('settled'), bind(wait(p('tasks')), unitValue), unitValue),
      direct('makeSignal', { host: p('host'), scope: p('scope'), tasks: p('tasks'), mode: p('mode'), limit: p('limit'), policy: p('policy') })))
  await createIo('body', { host, scope, mode: string, limit: gen('option', int), policy: string }, outcome,
    cond(eq('empty'), direct('makeSignal', { host: p('host'), scope: p('scope'), tasks: list(task), mode: p('mode'), limit: p('limit'), policy: p('policy') }),
      bind(start('worker', text('a'), { host: p('host') }), bind(start('worker', text('b'), { host: p('host') }),
        direct('afterStart', { host: p('host'), scope: p('scope'), tasks: list(task, pat(), pat('value', 1)), mode: p('mode'), limit: p('limit'), policy: p('policy') })))))
  await createIo('resourceBody', { host, mode: string, limit: gen('option', int), policy: string, handle: unit }, outcome,
    { kind: 'taskScope', body: 'body', context: ctx({ host: p('host'), mode: p('mode'), limit: p('limit'), policy: p('policy') }) })
  await createIo('run', { host, mode: string, limit: gen('option', int), policy: string }, outcome,
    { kind: 'useResource', acquire: io('open'), releaseCapability: p('host'), capability: 'Probe', releaseOperation: 'close', body: 'resourceBody', context: ctx({ host: p('host'), mode: p('mode'), limit: p('limit'), policy: p('policy') }) })
}
