const typeRef = {
  primitive: { kind: "primitive", primitive: "<string|bool|unit|int64|float64>" },
  named: { kind: "named", name: "<type display name>" },
  capability: { kind: "capability", name: "<capability display name>" },
  builtinGeneric: { kind: "builtinGeneric", generic: "<option|result|list>", arguments: ["<typeRef>"] },
}

/** @satisfies {Record<string, (keyof typeof references)[]>} */
const categories = {
  references: ["parameter", "patternReference"],
  literals: ["stringLiteral", "boolLiteral", "unitLiteral", "int64Literal", "float64Literal"],
  scalar: [
    "stringIsEmpty", "stringContains", "stringEqual", "stringConcat", "int64ToString", "stringParseInt64", "stringLength", "stringSlice",
    "int64WrappingAdd", "int64WrappingSubtract", "int64WrappingMultiply",
    "int64Equal", "int64LessThan", "int64LessThanOrEqual", "int64GreaterThan", "int64GreaterThanOrEqual",
    "float64Add", "float64Subtract", "float64Multiply", "float64Divide", "float64Equal", "float64LessThan",
    "float64IsNaN", "float64IsFinite", "float64IsInfinite",
  ],
  list: ["listEmpty", "listConstruct", "listLength", "listAppendItem", "listConcat", "listFold", "listEffectFold", "useResource", "parallelAll", "cancellationRequested", "listMap", "listFilter", "listAny", "listAll"],
  structured: ["productConstruct", "fieldAccess", "unionConstruct"],
  optionResult: ["optionNone", "optionSome", "resultOk", "resultErr"],
  calls: ["directCall", "capabilityInvoke"],
  control: ["conditional", "matchUnion", "bind", "matchOption", "matchResult"],
}

export const recipeKinds = Object.freeze(Object.values(categories).flat())

const recipe = "<recipe>"
const type = "<typeRef>"
/** @param {string} kind */
const unary = kind => ({ shape: { kind, value: recipe } })
/** @param {string} kind */
const binary = kind => ({ shape: { kind, left: recipe, right: recipe } })
const callbackContext = [{ parameter: "<callback parameter display name>", value: recipe }]
/** @param {string} kind */
const listCallback = kind => ({
  shape: { kind, source: recipe, function: "<function display name>", context: callbackContext },
  notes: "Optional context binds callback parameters to values computed once before iteration; the remaining unbound parameter receives each item. Callbacks stay pure.",
})

const references = {
  parameter: {
    shape: { kind: "parameter", name: "<parameter display name>" },
    notes: "The name must resolve uniquely in the new or existing target function.",
  },
  patternReference: {
    shapes: [
      { kind: "patternReference", scopeDepth: "<non-negative integer>", slot: "value" },
      { kind: "patternReference", scopeDepth: "<non-negative integer>", slot: "error" },
      { kind: "patternReference", scopeDepth: "<non-negative integer>", slot: "field", type: "<union type display name>", variant: "<variant display name>", field: "<field display name>" },
    ],
    notes: "scopeDepth 0 refers to the innermost Bind/match frame; field slots identify the owning Union, variant, and field by display name.",
  },
  stringLiteral: { shape: { kind: "stringLiteral", value: "<string>" } },
  boolLiteral: { shape: { kind: "boolLiteral", value: "<boolean>" } },
  unitLiteral: { shape: { kind: "unitLiteral" } },
  int64Literal: { shape: { kind: "int64Literal", value: "<decimal string or JSON integer>" }, notes: "Use a decimal string when JavaScript safe-integer precision would be insufficient." },
  float64Literal: { shape: { kind: "float64Literal", bits: "<16 lowercase hexadecimal digits>" } },
  stringIsEmpty: unary("stringIsEmpty"),
  int64ToString: { ...unary("int64ToString"), notes: "Int64 to canonical invariant decimal String; '-' only for negative values." },
  stringParseInt64: { ...unary("stringParseInt64"), notes: "String to Option<Int64>: Some only for an optional '-' followed by ASCII digits within range; '+', whitespace, empty text or overflow give None." },
  stringContains: { shape: { kind: "stringContains", value: recipe, substring: recipe } },
  stringEqual: { ...binary("stringEqual"), notes: "Ordinal, case-sensitive exact equality; both operands must be String." },
  stringLength: { ...unary("stringLength"), notes: "Int64 count of code points: a surrogate pair counts once, an unpaired surrogate once." },
  stringSlice: { shape: { kind: "stringSlice", value: recipe, start: recipe, end: recipe }, notes: "Code points at Int64 positions [start, end). Both bounds clamp to [0, length]; end <= start gives \"\". Never fails; never splits a surrogate pair." },
  stringConcat: { ...binary("stringConcat"), notes: "Concatenates String operands in left-to-right order without separators or normalization." },
  int64WrappingAdd: binary("int64WrappingAdd"),
  int64WrappingSubtract: binary("int64WrappingSubtract"),
  int64WrappingMultiply: binary("int64WrappingMultiply"),
  int64Equal: binary("int64Equal"),
  int64LessThan: binary("int64LessThan"),
  int64LessThanOrEqual: binary("int64LessThanOrEqual"),
  int64GreaterThan: binary("int64GreaterThan"),
  int64GreaterThanOrEqual: binary("int64GreaterThanOrEqual"),
  float64Add: binary("float64Add"),
  float64Subtract: binary("float64Subtract"),
  float64Multiply: binary("float64Multiply"),
  float64Divide: binary("float64Divide"),
  float64Equal: binary("float64Equal"),
  float64LessThan: binary("float64LessThan"),
  float64IsNaN: unary("float64IsNaN"),
  float64IsFinite: unary("float64IsFinite"),
  float64IsInfinite: unary("float64IsInfinite"),
  listEmpty: { shape: { kind: "listEmpty", itemType: type }, typeRef },
  listConstruct: { shape: { kind: "listConstruct", itemType: type, items: [recipe] }, typeRef },
  listLength: unary("listLength"),
  listAppendItem: { shape: { kind: "listAppendItem", list: recipe, item: recipe } },
  listConcat: binary("listConcat"),
  listFold: { shape: { kind: "listFold", source: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "Pure folder: (accumulator, item) -> accumulator over its parameters not bound by the optional context. Iterates left to right, returning the seed for an empty list. Context binds further folder parameters to values computed once." },
  parallelAll: { shape: { kind: "parallelAll", source: recipe, worker: "<function display name>", context: callbackContext }, notes: "Runs the worker (item -> Result<U, E>, effects allowed, capabilities via context) for every item concurrently and awaits all. The first Err or exception cancels the others. Result: the lowest-index Err, else Ok of all values in order." },
  cancellationRequested: { shape: { kind: "cancellationRequested" }, notes: "Bool: whether the current ParallelAll worker was asked to stop; false outside workers. Check it between steps to stop cooperatively." },
  useResource: { shape: { kind: "useResource", acquire: recipe, releaseCapability: recipe, capability: "<capability display name>", releaseOperation: "<operation display name>", body: "<function display name>", context: callbackContext }, notes: "acquire: Result<R, E>. The body gets R through its one unbound parameter and returns Result<T, E>. Release (R -> Unit or Result<Unit, E>) always runs after a successful acquire, on Ok, Err or a host exception; a body Err wins over a release Err." },
  listEffectFold: { shape: { kind: "listEffectFold", source: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "Effectful fold, left to right, each item exactly once. The folder may use effects the enclosing function declares and receives capabilities through context. If it returns Result<S, E> for seed S, the fold stops at the first Err and yields Result<S, E>; otherwise it returns S." },
  listMap: listCallback("listMap"),
  listFilter: listCallback("listFilter"),
  listAny: listCallback("listAny"),
  listAll: listCallback("listAll"),
  productConstruct: {
    shape: { kind: "productConstruct", type: "<product type display name>", fields: [{ field: "<field display name>", value: recipe }] },
    notes: "fields must cover the declaration exactly; field order is not semantic.",
  },
  fieldAccess: {
    shape: { kind: "fieldAccess", source: recipe, type: "<product type display name>", field: "<field display name>" },
  },
  unionConstruct: {
    shape: { kind: "unionConstruct", type: "<union type display name>", variant: "<variant display name>", fields: [{ field: "<field display name>", value: recipe }] },
    notes: "fields must cover the selected variant exactly; use [] for a fieldless variant.",
  },
  optionNone: { shape: { kind: "optionNone", itemType: type }, typeRef },
  optionSome: unary("optionSome"),
  resultOk: { shape: { kind: "resultOk", errorType: type, value: recipe }, typeRef },
  resultErr: { shape: { kind: "resultErr", okType: type, error: recipe }, typeRef },
  directCall: {
    shape: { kind: "directCall", function: "<function display name>", arguments: [{ parameter: "<callee parameter display name>", value: recipe }] },
    notes: "arguments must cover the callee parameters exactly; argument order is not semantic.",
  },
  capabilityInvoke: {
    shape: { kind: "capabilityInvoke", capabilityValue: recipe, capability: "<capability display name>", operation: "<operation display name>", arguments: [{ parameter: "<operation parameter display name>", value: recipe }] },
    notes: "The target function must already declare the operation's required effect.",
  },
  conditional: {
    shape: { kind: "conditional", condition: recipe, whenTrue: recipe, whenFalse: recipe },
    notes: "condition must produce Bool; both branches must produce the same type.",
  },
  matchUnion: {
    shape: { kind: "matchUnion", type: "<union type display name>", input: recipe, arms: [{ variant: "<variant display name>", body: recipe }] },
    notes: "arms must cover every variant exactly once. A branch can read variant fields with patternReference slot=field.",
  },
  bind: {
    shape: { kind: "bind", value: recipe, body: recipe },
    notes: "The body can reuse the bound value with patternReference {scopeDepth:0, slot:'value'}.",
  },
  matchOption: {
    shape: { kind: "matchOption", input: recipe, someBody: recipe, noneBody: recipe },
    notes: "someBody can read the item with patternReference {scopeDepth:0, slot:'value'}; the None branch still consumes one lexical scope depth.",
  },
  matchResult: {
    shape: { kind: "matchResult", input: recipe, okBody: recipe, errBody: recipe },
    notes: "okBody uses patternReference slot=value and errBody uses slot=error at scopeDepth 0.",
  },
}

/** @typedef {keyof typeof references} RecipeKind */

/** @param {RecipeKind} [kind] */
export function recipeReference(kind) {
  if (kind === undefined) {
    return {
      format: "nameResolvedExpressionRecipe",
      categories,
      typeRef,
      usage: "Pass one listed kind to receive its exact ordinary-tool wire shape. Shapes use display names where ordinary tools resolve semantic identities.",
    }
  }
  const value = references[kind]
  if (!value) throw new Error(`Unknown expression recipe kind '${kind}'.`)
  return { format: "nameResolvedExpressionRecipe", kind, ...value }
}
