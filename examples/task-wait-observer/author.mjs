// Reusable ordinary recipes. `create` publishes a simulateCreateFunctionByName request.
// Helpers declare no nominal effects; their intrinsic ordering still requires Bind.
export const prim = primitive => ({ kind: 'primitive', primitive })
export const gen = (generic, ...arguments_) => ({ kind: 'builtinGeneric', generic, arguments: arguments_ })
export const p = name => ({ kind: 'parameter', name })
export const pat = (slot = 'value', scopeDepth = 0) => ({ kind: 'patternReference', slot, scopeDepth })
export const bind = (value, body) => ({ kind: 'bind', value, body })
export const unit = prim('unit'), int = prim('int64'), string = prim('string')
export const unitValue = { kind: 'unitLiteral' }
export const text = value => ({ kind: 'stringLiteral', value })
export const ok = (value, errorType = string) => ({ kind: 'resultOk', value, errorType })
export const err = (error, okType) => ({ kind: 'resultErr', error, okType })
export const cond = (condition, whenTrue, whenFalse) => ({ kind: 'conditional', condition, whenTrue, whenFalse })
export const ctx = values => Object.entries(values).map(([parameter, value]) => ({ parameter, value }))
export const direct = (function_, values) => ({ kind: 'directCall', function: function_, arguments: ctx(values) })
export const wait = (tasks, mode = 'all', limit) => ({ kind: 'taskWait', tasks, mode, ...(limit ? { limit } : {}) })
export const cancellation = { kind: 'cancellationRequested' }

export async function defineTaskWaitObserver(create) {
  const u = { kind: 'typeParameter', name: 'U' }, e = { kind: 'typeParameter', name: 'E' }
  const outcome = gen('result', u, e), tasks = gen('list', gen('task', outcome))
  const states = gen('list', gen('option', outcome)), signal = gen('oneShot', states)
  const observed = gen('result', states, e), limit = gen('option', int)
  for (const mode of ['any', 'all']) {
    await create(`observeTasks_${mode}`, { tasks, signal, limit, item: unit }, observed,
      bind(wait(p('tasks'), mode, p('limit')),
        bind({ kind: 'oneShotComplete', cell: p('signal'), value: pat() }, ok(pat('value', 1), e))))
  }
  // A completed OneShot wins its own final observation. This explicit check instead
  // chooses cancellation at the caller's final check, including after Some.
  await create('awaitTaskObservation', { signal, limit, noValueError: e, item: unit }, observed,
    bind({ kind: 'oneShotWait', cell: p('signal'), limit: p('limit') },
      cond(cancellation, err(p('noValueError'), states),
        { kind: 'matchOption', input: pat(), someBody: ok(pat(), e), noneBody: err(p('noValueError'), states) })))
  // The owner must watch both tasks: a throwing monitor never publishes a cell.
  // Normal return only signals progress; join the caller to consume its Result.
  await create('superviseTaskObservation', { monitor: gen('task', observed), caller: gen('task', observed) }, unit,
    bind(wait({ kind: 'listConstruct', itemType: gen('task', observed), items: [p('monitor'), p('caller')] }, 'any'), unitValue))
}
