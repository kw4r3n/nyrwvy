const typeRef = {
  primitive: { kind: "primitive", primitive: "<string|bool|unit|int64|float64>" },
  named: { kind: "named", name: "<type display name>" },
  capability: { kind: "capability", name: "<capability display name>" },
  function: { kind: "function", parameters: ["<typeRef>"], resultType: "<typeRef>" },
  typeParameter: { kind: "typeParameter", name: "<function type parameter name>" },
  builtinGeneric: { kind: "builtinGeneric", generic: "<option|result|list|task|taskScope>", arguments: ["<typeRef>"] },
}

/** @satisfies {Record<string, (keyof typeof references)[]>} */
const categories = {
  references: ["parameter", "patternReference"],
  literals: ["stringLiteral", "boolLiteral", "unitLiteral", "int64Literal", "float64Literal"],
  scalar: [
    "boolNot", "boolAnd", "boolOr",
    "stringIsEmpty", "stringContains", "stringEqual", "stringLessThan", "stringConcat", "int64ToString", "stringParseInt64", "stringLength", "stringSlice", "stringCodePoints",
    "stringIndexOf", "stringLastIndexOf", "stringSpan", "stringSpanBack", "stringStartsWith", "stringEndsWith", "stringSplit", "stringSplitLines", "stringJoin", "stringToLower", "stringUtf8ByteCount",
    "int64WrappingAdd", "int64WrappingSubtract", "int64WrappingMultiply", "int64FloorDivide", "int64FloorModulo",
    "int64Equal", "int64LessThan", "int64LessThanOrEqual", "int64GreaterThan", "int64GreaterThanOrEqual",
    "float64Add", "float64Subtract", "float64Multiply", "float64Divide", "float64Equal", "float64LessThan",
    "float64IsNaN", "float64IsFinite", "float64IsInfinite", "int64ToFloat64",
  ],
  list: ["listEmpty", "listConstruct", "listLength", "listItemAt", "listSlice", "listAppendItem", "listConcat", "listFold", "listEffectFold", "useResource", "parallelAll", "cancellationRequested", "listMap", "listFilter", "listSortBy", "listAny", "listAll"],
  tasks: ["taskScope", "taskStart", "taskWait", "taskCancel"],
  structured: ["productConstruct", "fieldAccess", "unionConstruct", "valueEqual"],
  optionResult: ["optionNone", "optionSome", "resultOk", "resultErr"],
  calls: ["directCall", "functionReference", "functionInvoke", "capabilityConstruct", "capabilityInvoke"],
  control: ["abort", "conditional", "matchUnion", "bind", "matchOption", "matchResult"],
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
  stringLessThan: { ...binary("stringLessThan"), notes: "Bool: left sorts strictly before right in ordinal Unicode code-point order (lexicographic; a proper prefix sorts first; case-sensitive, no locale). Characters above U+FFFF sort after every BMP character, as in Python. Combine with stringEqual for tie-breaking keys." },
  valueEqual: { ...binary("valueEqual"), notes: "Bool structural equality of two values of one equatable type: String, Int64, Bool, Unit, List/Option/Result of equatable types, and declared Products and Unions whose fields are all equatable (recursive types included). Same variant with pairwise equal fields; lists by length and item by item in order. Float64 (use float64Equal), functions, tasks, capabilities and type parameters are not equatable. Never fails." },
  boolNot: { ...unary("boolNot"), notes: "Bool negation of a Bool value." },
  boolAnd: { ...binary("boolAnd"), notes: "Bool left and right. right is a nested branch evaluated only when left is true (its effects, calls and aborts happen only then); it must produce Bool or abort." },
  boolOr: { ...binary("boolOr"), notes: "Bool left or right. right is a nested branch evaluated only when left is false (its effects, calls and aborts happen only then); it must produce Bool or abort." },
  stringLength: { ...unary("stringLength"), notes: "Int64 count of code points: a surrogate pair counts once, an unpaired surrogate once." },
  stringCodePoints: { ...unary("stringCodePoints"), notes: "List<String> of each code point as a one-code-point String, in order, in linear time; never fails. A surrogate pair is one item, an unpaired surrogate its own item, so the length equals stringLength. Iterate text with listFold over it instead of self-recursion per character, which overflows the stack at a few thousand characters." },
  stringIndexOf: { shape: { kind: "stringIndexOf", value: recipe, substring: recipe, from: recipe }, notes: "Option<Int64> code-point position of the first ordinal occurrence of substring at or after from, like Python str.find(sub, start) but None instead of -1. from clamps to [0, length] like stringSlice (no negative-from-the-end); an empty substring is Some(clamped from). A match never splits a surrogate pair. Never fails." },
  stringSpan: { shape: { kind: "stringSpan", value: recipe, set: recipe, from: recipe }, notes: "Int64 code-point position of the first code point at or after from that is not in set (any code point of the set String), or the length when all are, like Python start + len(v[start:]) - len(v[start:].lstrip(set)). from clamps to [0, length]; an empty set gives the clamped from. Use for runs of a character class (skip whitespace, digits end) instead of self-recursion. Linear time; never fails." },
  stringSpanBack: { shape: { kind: "stringSpanBack", value: recipe, set: recipe, end: recipe }, notes: "Int64 least code-point position p <= end with every code point in [p, end) in set, like Python len(v[:end].rstrip(set)). end clamps to [0, length]; an empty set gives the clamped end. Python strip(chars) is stringSlice(v, stringSpan(v, set, 0), stringSpanBack(v, set, stringLength(v))). Linear time; never fails." },
  stringLastIndexOf: { shape: { kind: "stringLastIndexOf", value: recipe, substring: recipe }, notes: "Option<Int64> code-point position of the last ordinal occurrence, like Python str.rfind(sub) but None instead of -1; an empty substring is Some(length). Never fails." },
  stringStartsWith: { shape: { kind: "stringStartsWith", value: recipe, prefix: recipe }, notes: "Bool: value begins with prefix, ordinal and case-sensitive; an empty prefix is true. A prefix ending in half of a surrogate pair does not match." },
  stringEndsWith: { shape: { kind: "stringEndsWith", value: recipe, suffix: recipe }, notes: "Bool: value ends with suffix, ordinal and case-sensitive; an empty suffix is true." },
  stringSplit: { shape: { kind: "stringSplit", value: recipe, separator: recipe }, notes: "List<String> of the pieces between non-overlapping ordinal occurrences of separator, left to right, keeping empty pieces, like Python str.split(sep): \"a,,b\" by \",\" gives [\"a\", \"\", \"b\"]; \"\" gives [\"\"]. An empty separator never fails: it gives [value] (use stringCodePoints for characters). Linear time." },
  stringSplitLines: { shape: { kind: "stringSplitLines", value: recipe, breaks: recipe }, notes: "List<String> of the lines of value: each code point in breaks (any code point of the breaks String, as stringSpan counts them) ends a line and is dropped; when both \"\\r\" and \"\\n\" are in breaks, \"\\r\\n\" is one break. Like Python splitlines: \"\" gives [], a final break adds no trailing empty line (\"a\\n\" gives [\"a\"], \"a\\n\\nb\" gives [\"a\", \"\", \"b\"]). breaks \"\\n\\r\" is Python bytes.splitlines; add \\v\\f\\x1c\\x1d\\x1e\\x85\\u2028\\u2029 for str.splitlines. An empty breaks gives [value]. Use it instead of a listFold over stringCodePoints. Linear time; never fails." },
  stringJoin: { shape: { kind: "stringJoin", list: recipe, separator: recipe }, notes: "String: the List<String> items in order with separator between adjacent items, like Python separator.join(list): [] gives \"\", [\"a\"] gives \"a\", [\"a\", \"\", \"b\"] by \",\" gives \"a,,b\". Inverse of stringSplit. Never fails. Linear time." },
  stringToLower: { ...unary("stringToLower"), notes: "String with each code point lowercased by the Unicode simple (one-to-one) mapping, culture invariant (I to i, Kelvin sign to k); the length in code points never changes and an unpaired surrogate is kept. Like Python str.lower() except \u0130 gives \"i\" (Python \"i\u0307\") and a word-final Sigma gives \u03c3 (Python \u03c2). Not casefold. Never fails." },
  stringUtf8ByteCount: { ...unary("stringUtf8ByteCount"), notes: "Int64 length in bytes of the UTF-8 encoding, like Python len(text.encode(\"utf-8\")): 1-4 bytes per code point; an unpaired surrogate counts 3. Never fails." },
  stringSlice: { shape: { kind: "stringSlice", value: recipe, start: recipe, end: recipe }, notes: "Code points at Int64 positions [start, end). Both bounds clamp to [0, length]; end <= start gives \"\". Never fails; never splits a surrogate pair." },
  stringConcat: { ...binary("stringConcat"), notes: "Concatenates String operands in left-to-right order without separators or normalization." },
  int64WrappingAdd: binary("int64WrappingAdd"),
  int64WrappingSubtract: binary("int64WrappingSubtract"),
  int64WrappingMultiply: binary("int64WrappingMultiply"),
  int64FloorDivide: { ...binary("int64FloorDivide"), notes: "Int64 floor(left / right), rounding toward negative infinity. Divisor 0 gives 0; MinValue / -1 wraps to MinValue. Never fails." },
  int64FloorModulo: { ...binary("int64FloorModulo"), notes: "Int64 left - right * floor(left / right); nonzero results take the divisor's sign. Divisor 0 gives left; MinValue % -1 is 0. Never fails." },
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
  int64ToFloat64: { ...unary("int64ToFloat64"), notes: "Float64 nearest to the Int64: exact for |value| <= 2^53, otherwise IEEE round to nearest, ties to even (as C# (double)long and Python float(int)). Never fails. There is no Float64 to Int64 conversion." },
  listEmpty: { shape: { kind: "listEmpty", itemType: type }, typeRef },
  listConstruct: { shape: { kind: "listConstruct", itemType: type, items: [recipe] }, typeRef },
  listLength: unary("listLength"),
  listItemAt: { shape: { kind: "listItemAt", list: recipe, index: recipe }, notes: "Option<T>: Some(item) at a zero-based Int64 index when 0 <= index < length, else None. Negative indexes do not count from the end. Never fails." },
  listSlice: { shape: { kind: "listSlice", list: recipe, start: recipe, end: recipe }, notes: "List<T> (same type as list) of the items at Int64 positions [start, end), like stringSlice: both bounds clamp to [0, length]; negative bounds do not count from the end (unlike Python list[start:end]); end <= start gives the empty list. Never fails." },
  listAppendItem: { shape: { kind: "listAppendItem", list: recipe, item: recipe } },
  listConcat: binary("listConcat"),
  listFold: { shape: { kind: "listFold", source: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "Pure folder: (accumulator, item) -> accumulator over its parameters not bound by the optional context. Iterates left to right, returning the seed for an empty list. Context binds further folder parameters to values computed once." },
  parallelAll: { shape: { kind: "parallelAll", source: recipe, worker: "<function display name>", context: callbackContext }, notes: "Runs the worker (item -> Result<U, E>, effects allowed, capabilities via context) for every item concurrently and awaits all. The first Err or exception cancels the others. Result: the lowest-index Err, else Ok of all values in order." },
  cancellationRequested: { shape: { kind: "cancellationRequested" }, notes: "Bool: whether the current ParallelAll worker, task or task scope body was asked to stop; false outside them. Check it between steps to stop cooperatively." },
  taskScope: { shape: { kind: "taskScope", body: "<function display name>", context: callbackContext }, notes: "Runs the body with a new TaskScope through its one unbound parameter; the body returns Result<T, E>, which is the result. On every exit of the body (Ok, Err or a host exception) each unfinished task of the scope is cancelled and awaited before the node completes. A task's host exception is rethrown then unless the body threw first." },
  taskStart: { shape: { kind: "taskStart", scope: recipe, worker: "<function display name>", item: recipe, context: callbackContext }, notes: "Starts the worker (item -> Result<U, E>, effects allowed, capabilities via context) as a task of the TaskScope and returns Task<Result<U, E>> at once. A task's Err does not cancel other tasks." },
  taskWait: { shape: { kind: "taskWait", tasks: recipe, mode: "<any|all>", limit: recipe }, notes: "tasks: List<Task<T>>. limit (may be omitted): Option<Int64> milliseconds; None or absent waits without a limit, a negative limit counts as 0. Waits until any (or all) listed tasks have finished or the limit elapses, and returns List<Option<T>> in input order: Some(result) when finished, None while running. Tasks keep running. any on [] returns [] at once. A handle whose scope has completed is finished." },
  taskCancel: { shape: { kind: "taskCancel", task: recipe }, notes: "Unit. Requests cooperative cancellation of one task and returns without waiting; idempotent, a no-op once finished. Wait for the task to see what its worker returned." },
  useResource: { shape: { kind: "useResource", acquire: recipe, releaseCapability: recipe, capability: "<capability display name>", releaseOperation: "<operation display name>", body: "<function display name>", context: callbackContext }, notes: "acquire: Result<R, E>. The body gets R through its one unbound parameter and returns Result<T, E>. Release (R -> Unit or Result<Unit, E>) always runs after a successful acquire, on Ok, Err or a host exception; a body Err wins over a release Err." },
  listEffectFold: { shape: { kind: "listEffectFold", source: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "Effectful fold, left to right, each item exactly once. The folder may use effects the enclosing function declares and receives capabilities through context. If it returns Result<S, E> for seed S, the fold stops at the first Err and yields Result<S, E>; otherwise it returns S." },
  listMap: listCallback("listMap"),
  listFilter: listCallback("listFilter"),
  listSortBy: { shape: { kind: "listSortBy", source: recipe, function: "<function display name>", context: callbackContext }, notes: "List<T> in stable order under a pure less-than callback (left, right) -> Bool over its two parameters not bound by the optional context. Items the callback does not order keep their input order. Descending: swap the comparison; composite keys: compare the first key, and on equality the next (e.g. role ascending then count descending). Deterministic even for an inconsistent callback; never fails." },
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
  abort: { shape: { kind: "abort", error: recipe }, notes: "Terminates the current Result<T,E> function with Err(error). Only allowed at graph results, including Bind and branch bodies; it has no normal value. The payload must have type E." },
  resultErr: { shape: { kind: "resultErr", okType: type, error: recipe }, typeRef },
  functionReference: {
    shape: { kind: "functionReference", function: "<monomorphic pure function display name>" },
    notes: "A capture-free function value, without executing the function. Transitive effects and ambient cancellation reads are rejected. Use a concrete wrapper for a generic target.",
  },
  functionInvoke: {
    shape: { kind: "functionInvoke", functionValue: recipe, arguments: [recipe] },
    notes: "Invokes a pure function value with positional arguments in signature order; arity and types must match exactly.",
  },
  directCall: {
    shape: { kind: "directCall", function: "<function display name>", arguments: [{ parameter: "<callee parameter display name>", value: recipe }] },
    notes: "arguments must cover the callee parameters exactly; argument order is not semantic. Naming the function being created or replaced makes a self call (recursion): arguments then use the owner's parameter names, cover each exactly once with its declared type, and the result has the owner's result type. Mutual recursion still needs an existing placeholder callee.",
  },
  capabilityConstruct: {
    shape: { kind: "capabilityConstruct", capability: "<capability display name>", implementations: [{ operation: "<operation display name>", function: "<monomorphic pure function display name>" }] },
    notes: "Cover every operation exactly once. Implementation parameters match operation parameter types in declaration order, with the exact result type. No state or captures; construction does not invoke the functions. Invocation still requires the operation's declared effect.",
  },
  capabilityInvoke: {
    shape: { kind: "capabilityInvoke", capabilityValue: recipe, capability: "<capability display name>", operation: "<operation display name>", arguments: [{ parameter: "<operation parameter display name>", value: recipe }] },
    notes: "The target function must already declare the operation's required effect.",
  },
  conditional: {
    shape: { kind: "conditional", condition: recipe, whenTrue: recipe, whenFalse: recipe },
    notes: "condition must produce Bool; normally completing branches must produce the same type. An aborting branch contributes no normal value type.",
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
