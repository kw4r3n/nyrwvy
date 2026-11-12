// Authors the threaded-state-loop program into a session the caller opened: a State record carried
// through a tail-recursive loop, one event per turn. Shared by run.mjs; the snapshot stays in a temp dir.
const prim = name => ({ kind: "primitive", primitive: name })
const str = prim("string"), int = prim("int64")
const named = name => ({ kind: "named", name })
const State = named("State"), Inner = named("Inner"), Tree = named("Tree"), Phase = named("Phase")
const generic = (name, ...args) => ({ kind: "builtinGeneric", generic: name, arguments: args })
const listOf = t => generic("list", t)
const mapOf = (k, v) => generic("map", k, v)
const cap = name => ({ kind: "capability", name })

const P = name => ({ kind: "parameter", name })
const R = name => ({ kind: "patternReference", name })
const F = (type, variant, field, depth = 0) => ({ kind: "patternReference", scopeDepth: depth, slot: "field", type, variant, field })
const L = value => ({ kind: "stringLiteral", value })
const I = value => ({ kind: "int64Literal", value: String(value) })
const call = (fn, args = {}) => ({ kind: "directCall", function: fn, arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const invoke = (capParam, capability, operation, args = {}) => ({ kind: "capabilityInvoke", capabilityValue: P(capParam), capability, operation,
  arguments: Object.entries(args).map(([parameter, value]) => ({ parameter, value })) })
const bind = (name, value, body) => ({ kind: "bind", name, value, body })
const field = (source, type, name) => ({ kind: "fieldAccess", source, type, field: name })
const product = (type, fields) => ({ kind: "productConstruct", type, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
const update = (source, type, fields) => ({ kind: "productUpdate", source, type, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
const union = (type, variant, fields = {}) => ({ kind: "unionConstruct", type, variant, fields: Object.entries(fields).map(([field, value]) => ({ field, value })) })
const add = (left, right) => ({ kind: "int64WrappingAdd", left, right: typeof right === "number" ? I(right) : right })
const sub = (left, right) => ({ kind: "int64WrappingSubtract", left, right: typeof right === "number" ? I(right) : right })
const lt = (left, right) => ({ kind: "int64LessThan", left, right: typeof right === "number" ? I(right) : right })
const eq = (left, right) => ({ kind: "valueEqual", left, right: typeof right === "number" ? I(right) : right })
const iff = (condition, whenTrue, whenFalse) => ({ kind: "conditional", condition, whenTrue, whenFalse })
// A matchUnion whose arm bodies read variant fields through named binds.
const matchTree = (input, leafBody, nodeBody) => ({ kind: "matchUnion", type: "Tree", input, arms: [
  { variant: "Leaf", body: bind("value", F("Tree", "Leaf", "value"), leafBody) },
  // The second field's reference sits one bind deeper than the first, so its scopeDepth is 1.
  { variant: "Node", body: bind("left", F("Tree", "Node", "left"), bind("right", F("Tree", "Node", "right", 1), nodeBody)) } ] })
const matchPhase = (input, aBody, bBody) => ({ kind: "matchUnion", type: "Phase", input, arms: [
  { variant: "A", body: bind("n", F("Phase", "A", "n"), aBody) },
  { variant: "B", body: bind("n", F("Phase", "B", "n"), bBody) } ] })

export async function author(send) {
  const commit = async created => { await send({ op: "commit", proposalId: created.proposalId }) }
  await commit(await send({ op: "simulateCreateEffect", displayName: "System" }))
  await commit(await send({ op: "simulateCreateCapabilityByName", displayName: "Events", exported: true, operations: [
    { displayName: "next", parameters: [], resultType: int, effect: "System", mayWait: true } ] }))
  await commit(await send({ op: "simulateCreateTypesByName", types: [
    { displayName: "Inner", typeKind: { kind: "product", fields: [
      { displayName: "n", type: int }, { displayName: "label", type: str } ] } },
    { displayName: "Tree", typeKind: { kind: "union", variants: [
      { displayName: "Leaf", fields: [{ displayName: "value", type: int }] },
      { displayName: "Node", fields: [{ displayName: "left", type: Tree }, { displayName: "right", type: Tree }] } ] } },
    { displayName: "Phase", typeKind: { kind: "union", variants: [
      { displayName: "A", fields: [{ displayName: "n", type: int }] },
      { displayName: "B", fields: [{ displayName: "n", type: int }] } ] } },
    { displayName: "State", typeKind: { kind: "product", fields: [
      { displayName: "turns", type: int }, { displayName: "counter", type: int },
      { displayName: "arr", type: listOf(int) }, { displayName: "table", type: mapOf(int, int) },
      { displayName: "inner", type: Inner }, { displayName: "tree", type: Tree }, { displayName: "phase", type: Phase } ] } },
  ] }))
  const define = async (displayName, parameters, resultType, requiredEffects, body, exported = false) =>
    commit(await send({ op: "simulateCreateFunctionByName", displayName, exported, resultType, requiredEffects, body,
      parameters: Object.entries(parameters).map(([displayName, type]) => ({ displayName, type })) }))

  await define("pow2", { n: int }, int, [],
    iff(lt(P("n"), 1), I(1), { kind: "int64WrappingMultiply", left: I(2), right: call("pow2", { n: sub(P("n"), 1) }) }))
  await define("appendSeed", { acc: listOf(int), i: int, seed: int }, listOf(int), [],
    { kind: "listAppendItem", list: P("acc"), item: add(P("seed"), P("i")) })
  await define("insertSeed", { acc: mapOf(int, int), i: int, seed: int }, mapOf(int, int), [],
    { kind: "mapInsert", map: P("acc"), key: P("i"), value: add(P("seed"), P("i")) })
  await define("makeTree", { depth: int, base: int }, Tree, [],
    iff(lt(P("depth"), 1), union("Tree", "Leaf", { value: P("base") }),
      union("Tree", "Node", {
        left: call("makeTree", { depth: sub(P("depth"), 1), base: P("base") }),
        right: call("makeTree", { depth: sub(P("depth"), 1), base: add(P("base"), call("pow2", { n: sub(P("depth"), 1) })) }) })))
  await define("initialState", { seed: int }, State, [],
    product("State", {
      turns: I(0), counter: P("seed"),
      arr: { kind: "listFold", source: { kind: "listRange", start: I(0), end: I(64) },
        seed: { kind: "listEmpty", itemType: int }, function: "appendSeed", context: [{ parameter: "seed", value: P("seed") }] },
      table: { kind: "listFold", source: { kind: "listRange", start: I(0), end: I(64) },
        seed: { kind: "mapEmpty", keyType: int, valueType: int }, function: "insertSeed", context: [{ parameter: "seed", value: P("seed") }] },
      inner: product("Inner", { n: P("seed"), label: L("state") }),
      tree: call("makeTree", { depth: I(6), base: P("seed") }),
      phase: union("Phase", "A", { n: P("seed") }) }))
  await define("addStep", { acc: int, x: int }, int, [], add(P("acc"), P("x")))
  await define("mapAddStep", { acc: int, key: int, value: int }, int, [], add(P("acc"), P("value")))
  await define("treeSum", { tree: Tree }, int, [],
    matchTree(P("tree"), R("value"), add(call("treeSum", { tree: R("left") }), call("treeSum", { tree: R("right") }))))
  await define("leafCount", { tree: Tree }, int, [],
    matchTree(P("tree"), I(1), add(call("leafCount", { tree: R("left") }), call("leafCount", { tree: R("right") }))))
  await define("bumpLeaf", { tree: Tree, index: int }, Tree, [],
    matchTree(P("tree"), union("Tree", "Leaf", { value: add(R("value"), 1) }),
      iff(lt(P("index"), call("leafCount", { tree: R("left") })),
        union("Tree", "Node", { left: call("bumpLeaf", { tree: R("left"), index: P("index") }), right: R("right") }),
        union("Tree", "Node", { left: R("left"),
          right: call("bumpLeaf", { tree: R("right"), index: sub(P("index"), call("leafCount", { tree: R("left") })) }) }))))
  await define("checksum", { state: State }, int, [],
    add(field(P("state"), "State", "counter"),
    add({ kind: "listFold", source: field(P("state"), "State", "arr"), seed: I(0), function: "addStep", context: [] },
    add({ kind: "mapFold", map: field(P("state"), "State", "table"), seed: I(0), function: "mapAddStep", context: [] },
    add(field(field(P("state"), "State", "inner"), "Inner", "n"),
    add(call("treeSum", { tree: field(P("state"), "State", "tree") }),
        matchPhase(field(P("state"), "State", "phase"), R("n"), R("n"))))))))

  // One turn: turns + 1 always. Each branch is a single productUpdate of the state parameter itself,
  // carrying the turns increment and the branch's one place, whose read sits inside the update value.
  // Measured (see this example's README): in this shape the scalar, list and map fields update in
  // place (since c0206086); the nested record, the tree leaf and the union write-back still allocate.
  const S = P("state"), idx = R("idx")
  const current = get => ({ kind: "optionGetOrDefault", option: get, default: I(0) })
  const next = extra => update(S, "State", { turns: add(field(S, "State", "turns"), 1), ...extra })
  let stepBody = next({})
  stepBody = iff(eq(P("code"), 6),
    next({ phase: matchPhase(field(S, "State", "phase"),
      union("Phase", "B", { n: add(R("n"), 1) }), union("Phase", "A", { n: add(R("n"), 1) })) }), stepBody)
  stepBody = iff(eq(P("code"), 5), next({ tree: call("bumpLeaf", { tree: field(S, "State", "tree"), index: idx }) }), stepBody)
  stepBody = iff(eq(P("code"), 4),
    next({ inner: update(field(S, "State", "inner"), "Inner",
      { n: add(field(field(S, "State", "inner"), "Inner", "n"), 1) }) }), stepBody)
  stepBody = iff(eq(P("code"), 3),
    next({ table: { kind: "mapInsert", map: field(S, "State", "table"), key: idx,
      value: add(current({ kind: "mapGet", map: field(S, "State", "table"), key: idx }), 1) } }), stepBody)
  stepBody = iff(eq(P("code"), 2),
    next({ arr: { kind: "listSetAt", list: field(S, "State", "arr"), index: idx,
      item: add(current({ kind: "listItemAt", list: field(S, "State", "arr"), index: idx }), 1) } }), stepBody)
  stepBody = iff(eq(P("code"), 1), next({ counter: add(field(S, "State", "counter"), 1) }), stepBody)
  await define("step", { state: State, code: int }, State, [],
    bind("idx", { kind: "int64FloorModulo", left: add(field(S, "State", "turns"), 1), right: I(64) }, stepBody))

  await define("loop", { events: cap("Events"), state: State }, int, ["System"],
    bind("code", invoke("events", "Events", "next"),
      iff(lt(R("code"), 0), call("checksum", { state: P("state") }),
        call("loop", { events: P("events"), state: call("step", { state: P("state"), code: R("code") }) }))))
  await define("run", { events: cap("Events"), seed: int }, int, ["System"],
    call("loop", { events: P("events"), state: call("initialState", { seed: P("seed") }) }), true)
  // Saved-case entry points for step: pure, so runPure checks them on both backends.
  await define("stepChecksum", { code: int }, int, [],
    call("checksum", { state: call("step", { state: call("initialState", { seed: I(10) }), code: P("code") }) }), true)
  await define("stepTurns", { code: int }, int, [],
    field(call("step", { state: call("initialState", { seed: I(10) }), code: P("code") }), "State", "turns"), true)
  // Probes for the saved cases: the one place each code changes, read back after one step.
  await define("leafAt", { tree: Tree, index: int }, int, [],
    matchTree(P("tree"), R("value"),
      iff(lt(P("index"), call("leafCount", { tree: R("left") })),
        call("leafAt", { tree: R("left"), index: P("index") }),
        call("leafAt", { tree: R("right"), index: sub(P("index"), call("leafCount", { tree: R("left") })) }))))
  const stepped = () => call("step", { state: call("initialState", { seed: I(10) }), code: P("code") })
  await define("stepCounter", { code: int }, int, [],
    field(stepped(), "State", "counter"), true)
  await define("stepArr", { code: int }, int, [],
    { kind: "optionGetOrDefault", option: { kind: "listItemAt", list: field(stepped(), "State", "arr"), index: I(1) }, default: I(-1) }, true)
  await define("stepTable", { code: int }, int, [],
    { kind: "optionGetOrDefault", option: { kind: "mapGet", map: field(stepped(), "State", "table"), key: I(1) }, default: I(-1) }, true)
  await define("stepInner", { code: int }, int, [],
    field(field(stepped(), "State", "inner"), "Inner", "n"), true)
  await define("stepLeaf", { code: int }, int, [],
    call("leafAt", { tree: field(stepped(), "State", "tree"), index: I(1) }), true)
  await define("stepPhaseN", { code: int }, int, [],
    matchPhase(field(stepped(), "State", "phase"), R("n"), R("n")), true)
  await define("stepPhaseB", { code: int }, int, [],
    matchPhase(field(stepped(), "State", "phase"), I(0), I(1)), true)
}
