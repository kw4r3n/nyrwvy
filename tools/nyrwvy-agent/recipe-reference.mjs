const typeRef = {
  primitive: { kind: "primitive", primitive: "<string|bool|unit|int64|float64>" },
  named: { kind: "named", name: "<type display name>" },
  capability: { kind: "capability", name: "<capability display name>" },
  function: { kind: "function", parameters: ["<typeRef>"], resultType: "<typeRef>" },
  typeParameter: { kind: "typeParameter", name: "<function type parameter name>" },
  builtinGeneric: { kind: "builtinGeneric", generic: "<option|result|list|map|set|task|taskScope>", arguments: ["<typeRef>"] },
}

/**
 * Each category maps its kinds to a one-line signature in wire field names, so the index alone is
 * enough for most scalar kinds. Generic letters (T, U, S, E, R) stand for any type.
 * @satisfies {Record<string, Partial<Record<keyof typeof references, string>>>}
 */
const categories = {
  references: {
    "parameter": "(name) -> the parameter's type",
    "patternReference": "(scopeDepth, slot: value|error|field) -> the bound value's type",
  },
  literals: {
    "stringLiteral": "(value: JSON string) -> String",
    "boolLiteral": "(value: JSON boolean) -> Bool",
    "unitLiteral": "() -> Unit",
    "int64Literal": "(value: decimal string or JSON integer) -> Int64",
    "float64Literal": "(bits: 16 hex digits) -> Float64",
  },
  bool: {
    "boolNot": "(value: Bool) -> Bool",
    "boolAnd": "(left: Bool, right: Bool, evaluated only when left is true) -> Bool",
    "boolOr": "(left: Bool, right: Bool, evaluated only when left is false) -> Bool",
  },
  string: {
    "stringIsEmpty": "(value: String) -> Bool",
    "stringContains": "(value: String, substring: String) -> Bool",
    "stringEqual": "(left: String, right: String) -> Bool",
    "stringLessThan": "(left: String, right: String) -> Bool  // code-point order",
    "stringConcat": "(left: String, right: String) -> String",
    "stringLength": "(value: String) -> Int64  // code points",
    "stringSlice": "(value: String, start: Int64, end: Int64) -> String  // clamped",
    "stringCodePoints": "(value: String) -> List<String>",
    "stringIndexOf": "(value: String, substring: String, from: Int64) -> Option<Int64>  // str.find",
    "stringLastIndexOf": "(value: String, substring: String) -> Option<Int64>  // str.rfind",
    "stringSpan": "(value: String, set: String, from: Int64) -> Int64  // lstrip as a position",
    "stringSpanBack": "(value: String, set: String, end: Int64) -> Int64  // rstrip as a position",
    "stringStartsWith": "(value: String, prefix: String) -> Bool",
    "stringEndsWith": "(value: String, suffix: String) -> Bool",
    "stringSplit": "(value: String, separator: String) -> List<String>  // str.split(sep)",
    "stringSplitLines": "(value: String, breaks: String) -> List<String>  // str.splitlines",
    "stringJoin": "(list: List<String>, separator: String) -> String",
    "stringToLower": "(value: String) -> String",
    "stringToUpper": "(value: String) -> String",
    "stringUtf8ByteCount": "(value: String) -> Int64",
    "stringDecodeUtf8Lossy": "(value: String of bytes U+00-U+FF) -> String  // decode('utf-8', 'replace')",
    "stringFromCodePoint": "(value: Int64) -> Option<String>  // chr",
    "stringToCodePoint": "(value: String) -> Option<Int64>  // ord",
    "int64ToString": "(value: Int64) -> String",
    "stringParseInt64": "(value: String) -> Option<Int64>",
    "int64ToHexString": "(value: Int64) -> String  // format(x, 'x')",
    "stringParseInt64Hex": "(value: String) -> Option<Int64>  // int(s, 16), strict",
    "stringRepeat": "(text: String, count: Int64) -> String  // text * count",
    "float64ToString": "(value: Float64) -> String  // repr",
    "stringParseFloat64": "(value: String) -> Option<Float64>",
  },
  int64: {
    "int64WrappingAdd": "(left: Int64, right: Int64) -> Int64",
    "int64WrappingSubtract": "(left: Int64, right: Int64) -> Int64",
    "int64WrappingMultiply": "(left: Int64, right: Int64) -> Int64",
    "int64FloorDivide": "(left: Int64, right: Int64) -> Int64  // Python //",
    "int64FloorModulo": "(left: Int64, right: Int64) -> Int64  // Python %",
    "int64Min": "(left: Int64, right: Int64) -> Int64",
    "int64Max": "(left: Int64, right: Int64) -> Int64",
    "int64CheckedAdd": "(left: Int64, right: Int64) -> Option<Int64>  // left + right",
    "int64CheckedSubtract": "(left: Int64, right: Int64) -> Option<Int64>  // left - right",
    "int64CheckedMultiply": "(left: Int64, right: Int64) -> Option<Int64>  // left * right",
    "int64WrappingAbs": "(value: Int64) -> Int64  // MinValue stays MinValue",
    "int64Equal": "(left: Int64, right: Int64) -> Bool",
    "int64LessThan": "(left: Int64, right: Int64) -> Bool",
    "int64LessThanOrEqual": "(left: Int64, right: Int64) -> Bool",
    "int64GreaterThan": "(left: Int64, right: Int64) -> Bool",
    "int64GreaterThanOrEqual": "(left: Int64, right: Int64) -> Bool",
    "int64ToFloat64": "(value: Int64) -> Float64",
  },
  float64: {
    "float64Add": "(left: Float64, right: Float64) -> Float64",
    "float64Subtract": "(left: Float64, right: Float64) -> Float64",
    "float64Multiply": "(left: Float64, right: Float64) -> Float64",
    "float64Divide": "(left: Float64, right: Float64) -> Float64",
    "float64Equal": "(left: Float64, right: Float64) -> Bool",
    "float64LessThan": "(left: Float64, right: Float64) -> Bool",
    "float64IsNaN": "(value: Float64) -> Bool",
    "float64IsFinite": "(value: Float64) -> Bool",
    "float64IsInfinite": "(value: Float64) -> Bool",
    "float64Sqrt": "(value: Float64) -> Float64",
    "float64Abs": "(value: Float64) -> Float64",
    "float64Negate": "(value: Float64) -> Float64",
    "float64Floor": "(value: Float64) -> Float64",
    "float64Ceiling": "(value: Float64) -> Float64",
    "float64Truncate": "(value: Float64) -> Float64",
    "float64RoundToInt64": "(value: Float64) -> Option<Int64>  // nearest, half away from zero",
    "float64Round": "(value: Float64, digits: Int64) -> Float64  // decimal places, half away from zero",
    "float64FloorToInt64": "(value: Float64) -> Option<Int64>  // math.floor",
  },
  bits32: {
    "bits32FromInt64": "(value: Int64) -> Bits32",
    "bits32ToInt64": "(value: Bits32) -> Int64",
    "bits32Xor": "(left: Bits32, right: Bits32) -> Bits32",
    "bits32And": "(left: Bits32, right: Bits32) -> Bits32",
    "bits32Or": "(left: Bits32, right: Bits32) -> Bits32",
    "bits32Not": "(value: Bits32) -> Bits32",
    "bits32Add": "(left: Bits32, right: Bits32) -> Bits32",
    "bits32RotateRight": "(value: Bits32, amount: Int64) -> Bits32",
    "bits32ShiftRight": "(value: Bits32, amount: Int64) -> Bits32",
    "bits32ShiftLeft": "(value: Bits32, amount: Int64) -> Bits32",
  },
  list: {
    "listEmpty": "(itemType) -> List<T>",
    "listConstruct": "(itemType, items: [T]) -> List<T>",
    "listLength": "(value: List<T>) -> Int64",
    "listItemAt": "(list: List<T>, index: Int64) -> Option<T>",
    "listSlice": "(list: List<T>, start: Int64, end: Int64) -> List<T>  // clamped",
    "listRange": "(start: Int64, end: Int64) -> List<Int64>  // list(range(start, end))",
    "listReverse": "(value: List<T>) -> List<T>",
    "listAppendItem": "(list: List<T>, item: T) -> List<T>",
    "listConcat": "(left: List<T>, right: List<T>) -> List<T>",
    "listFold": "(source: List<T>, seed: S, function: (S, T) -> S, context) -> S",
    "listEffectFold": "(source: List<T>, seed: S, function: (S, T) -> S or Result<S, E>, context) -> S or Result<S, E>",
    "useResource": "(acquire: Result<R, E>, releaseCapability, capability, releaseOperation, body: R -> Result<T, E>, context) -> Result<T, E>",
    "parallelAll": "(source: List<T>, worker: T -> Result<U, E>, context) -> Result<List<U>, E>",
    "cancellationRequested": "() -> Bool",
    "listMap": "(source: List<T>, function: T -> U, context) -> List<U>",
    "listFilter": "(source: List<T>, function: T -> Bool, context) -> List<T>",
    "listSortBy": "(source: List<T>, function: (T, T) -> Bool, context) -> List<T>  // stable",
    "listAny": "(source: List<T>, function: T -> Bool, context) -> Bool",
    "listAll": "(source: List<T>, function: T -> Bool, context) -> Bool",
  },
  map: {
    "mapEmpty": "(keyType, valueType) -> Map<K, V>",
    "mapInsert": "(map: Map<K, V>, key: K, value: V) -> Map<K, V>",
    "mapGet": "(map: Map<K, V>, key: K) -> Option<V>",
    "mapContainsKey": "(map: Map<K, V>, key: K) -> Bool",
    "mapRemove": "(map: Map<K, V>, key: K) -> Map<K, V>",
    "mapSize": "(value: Map<K, V>) -> Int64",
    "mapKeys": "(value: Map<K, V>) -> List<K>  // insertion order",
    "mapValues": "(value: Map<K, V>) -> List<V>  // insertion order",
    "mapFold": "(map: Map<K, V>, seed: S, function: (S, K, V) -> S, context) -> S",
    "mapFromList": "(source: List<T>, function: T -> K, context) -> Map<K, T>",
    "mapFilter": "(map: Map<K, V>, function: (K, V) -> Bool, context) -> Map<K, V>",
  },
  set: {
    "setEmpty": "(itemType) -> Set<T>",
    "setFromList": "(value: List<T>) -> Set<T>  // first occurrence order",
    "setInsert": "(set: Set<T>, item: T) -> Set<T>",
    "setContains": "(set: Set<T>, item: T) -> Bool",
    "setRemove": "(set: Set<T>, item: T) -> Set<T>",
    "setUnion": "(left: Set<T>, right: Set<T>) -> Set<T>  // left order, then right's new items",
    "setIntersection": "(left: Set<T>, right: Set<T>) -> Set<T>  // left order",
    "setDifference": "(left: Set<T>, right: Set<T>) -> Set<T>  // left order",
    "setSize": "(value: Set<T>) -> Int64",
    "setToList": "(value: Set<T>) -> List<T>  // insertion order",
  },
  tasks: {
    "taskScope": "(body: TaskScope -> Result<T, E>, context) -> Result<T, E>",
    "taskStart": "(scope: TaskScope, worker: T -> Result<U, E>, item: T, context) -> Task<Result<U, E>>",
    "taskWait": "(tasks: List<Task<T>>, mode: any|all, limit: Option<Int64>) -> List<Option<T>>",
    "taskCancel": "(task: Task<T>) -> Unit",
  },
  structured: {
    "productConstruct": "(type, fields: [field, value]) -> the product",
    "fieldAccess": "(source: Product, type, field) -> the field's type",
    "productUpdate": "(source: Product, type, fields: [field, value]) -> the product",
    "unionConstruct": "(type, variant, fields: [field, value]) -> the union",
    "valueEqual": "(left: T, right: T) -> Bool  // T equatable",
  },
  optionResult: {
    "optionNone": "(itemType) -> Option<T>",
    "optionSome": "(value: T) -> Option<T>",
    "resultOk": "(errorType, value: T) -> Result<T, E>",
    "resultErr": "(okType, error: E) -> Result<T, E>",
  },
  calls: {
    "directCall": "(function, arguments: [parameter, value]) -> the function's result type",
    "functionReference": "(function) -> a function value",
    "functionInvoke": "(functionValue, arguments: [values in order]) -> its result type",
    "capabilityConstruct": "(capability, implementations: [operation, function]) -> the capability",
    "capabilityInvoke": "(capabilityValue, capability, operation, arguments: [parameter, value]) -> the operation's result type",
  },
  control: {
    "abort": "(error: E) -> ends the Result<T, E> function with Err",
    "conditional": "(condition: Bool, whenTrue: T, whenFalse: T) -> T",
    "matchUnion": "(type, input: Union, arms: [variant | variants, body: T], otherwise?: T) -> T",
    "bind": "(value: T, body: U) -> U",
    "matchOption": "(input: Option<T>, someBody: U, noneBody: U) -> U",
    "matchResult": "(input: Result<T, E>, okBody: U, errBody: U) -> U",
  },
}

/** @typedef {keyof typeof categories} RecipeCategory */
export const recipeCategories = /** @type {RecipeCategory[]} */ (Object.freeze(Object.keys(categories)))
export const recipeKinds = /** @type {(keyof typeof references)[]} */ (Object.freeze(Object.values(categories).flatMap(category => Object.keys(category))))

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
  int64ToHexString: { ...unary("int64ToHexString"), notes: "Int64 to lowercase hexadecimal String with the fewest digits, as Python format(x, 'x'): 255 gives 'ff', -255 gives '-ff', 0 gives '0'. No '0x' prefix or padding (pad with stringRepeat). Never fails." },
  stringParseInt64Hex: { ...unary("stringParseInt64Hex"), notes: "String to Option<Int64>: Some only for an optional '-' followed by one or more ASCII hex digits of either case within range ('-8000000000000000' is MinValue); '0x', '+', whitespace, '_', empty text or overflow give None. Leading zeros are accepted." },
  stringRepeat: { shape: { kind: "stringRepeat", text: recipe, count: recipe }, notes: "String: text repeated count (Int64) times, as Python text * count; count <= 0 gives \"\". A result too large to allocate is a host failure, as for stringConcat. Pad to a width with stringRepeat(\" \", width - stringLength(s))." },
  float64ToString: { ...unary("float64ToString"), notes: "Float64 to String exactly like Python repr(float): the shortest digits that read back as the same Float64; fixed notation with at least one fractional digit when the decimal exponent is in [-4, 16) (\"1.0\", \"0.0001\", \"0.30000000000000004\", \"1234567890123456.0\"), otherwise exponent notation with a signed two-or-more-digit exponent (\"1e+16\", \"1.5e-05\", \"5e-324\"); \"nan\", \"inf\", \"-inf\", \"-0.0\". Never fails." },
  stringParseFloat64: { ...unary("stringParseFloat64"), notes: "String to Option<Float64>, like Python float(text) restricted to the JSON number grammar -?digits(.digits)?([eE][+-]?digits)? in ASCII digits (leading zeros allowed): Some correctly rounded nearest Float64 (ties to even) for any digit count; overflow gives Some(+/-infinity) and underflow a signed zero, as json.loads does. '+', whitespace, \".5\", \"5.\", \"inf\", \"nan\", underscores, hex, non-ASCII digits and \"\" give None. Inverse of float64ToString for finite values. Never fails." },
  float64RoundToInt64: { ...unary("float64RoundToInt64"), notes: "Float64 to Option<Int64>: the nearest integer, half away from zero (2.5 gives 3, -0.5 gives -1; Python round gives 2 and 0). None for NaN, an infinity, or a result outside Int64. Never fails." },
  float64Round: { shape: { kind: "float64Round", value: recipe, digits: recipe }, notes: "Float64 rounded to digits decimal places (negative digits: tens, hundreds, ...), half away from zero, on the shortest decimal text of the value: float64Round(2.675, 2) is 2.68 and (1250, -2) is 1300 (Python round gives 2.67 and 1200). NaN, infinities and zeros unchanged; a result rounded to zero keeps the sign. Never fails." },
  float64FloorToInt64: { ...unary("float64FloorToInt64"), notes: "Float64 to Option<Int64> like Python math.floor(x): the largest integer not above x (-0.5 gives -1). None for NaN, an infinity, or a result outside Int64. Never fails." },
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
  stringToUpper: { ...unary("stringToUpper"), notes: "String with each code point uppercased by the Unicode simple (one-to-one) mapping, culture invariant (i and dotless \u0131 to I); \u00df gives \u1e9e (capital sharp s), so stringToLower undoes it. The length in code points never changes and an unpaired surrogate is kept. Unlike Python str.upper(), never expands (Python gives \"SS\" for \u00df). Never fails." },
  stringUtf8ByteCount: { ...unary("stringUtf8ByteCount"), notes: "Int64 length in bytes of the UTF-8 encoding, like Python len(text.encode(\"utf-8\")): 1-4 bytes per code point; an unpaired surrogate counts 3. Never fails." },
  stringDecodeUtf8Lossy: { ...unary("stringDecodeUtf8Lossy"), notes: "Decodes bytes held one per code point (U+0000..U+00FF, as a latin-1 String) as UTF-8, like Python bytes.decode(\"utf-8\", \"replace\"): each maximal ill-formed subpart (invalid, overlong, surrogate, above U+10FFFF or cut short) becomes one U+FFFD. A code point above U+00FF is not a byte and reads as the invalid byte 0xFF. Never fails." },
  stringFromCodePoint: { ...unary("stringFromCodePoint"), notes: "Int64 code to Option<String>, like Python chr(code): Some one-code-point String for 0..0x10FFFF, including a surrogate code 0xD800..0xDFFF (one unpaired surrogate; stringConcat of a high and a low one forms the pair, as when decoding JSON \\uXXXX escapes); None for a negative code or one above 0x10FFFF. Never fails." },
  stringToCodePoint: { ...unary("stringToCodePoint"), notes: "String to Option<Int64>, like Python ord(text): Some code point when value is exactly one code point as stringLength counts it (a surrogate pair gives its scalar value, an unpaired surrogate its code unit); None for \"\" or longer text. Inverse of stringFromCodePoint. Never fails." },
  stringSlice: { shape: { kind: "stringSlice", value: recipe, start: recipe, end: recipe }, notes: "Code points at Int64 positions [start, end). Both bounds clamp to [0, length]; end <= start gives \"\". Never fails; never splits a surrogate pair." },
  stringConcat: { ...binary("stringConcat"), notes: "Concatenates String operands in left-to-right order without separators or normalization." },
  int64WrappingAdd: binary("int64WrappingAdd"),
  int64WrappingSubtract: binary("int64WrappingSubtract"),
  int64WrappingMultiply: binary("int64WrappingMultiply"),
  int64FloorDivide: { ...binary("int64FloorDivide"), notes: "Int64 floor(left / right), rounding toward negative infinity. Divisor 0 gives 0; MinValue / -1 wraps to MinValue. Never fails." },
  int64FloorModulo: { ...binary("int64FloorModulo"), notes: "Int64 left - right * floor(left / right); nonzero results take the divisor's sign. Divisor 0 gives left; MinValue % -1 is 0. Never fails." },
  int64Min: { ...binary("int64Min"), notes: "The smaller Int64, as Python min(left, right). Never fails." },
  int64Max: { ...binary("int64Max"), notes: "The larger Int64, as Python max(left, right). Never fails." },
  int64CheckedAdd: { ...binary("int64CheckedAdd"), notes: "Exact Python left + right as Option<Int64>: None when the result is outside Int64, where the wrapping form would wrap. Never fails." },
  int64CheckedSubtract: { ...binary("int64CheckedSubtract"), notes: "Exact Python left - right as Option<Int64>: None when the result is outside Int64, where the wrapping form would wrap. Never fails." },
  int64CheckedMultiply: { ...binary("int64CheckedMultiply"), notes: "Exact Python left * right as Option<Int64>: None when the result is outside Int64, where the wrapping form would wrap. Never fails." },
  int64WrappingAbs: { ...unary("int64WrappingAbs"), notes: "Int64 absolute value with two's-complement wrapping: MinValue gives MinValue (Python abs gives 2^63). Never fails." },
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
  int64ToFloat64: { ...unary("int64ToFloat64"), notes: "Float64 nearest to the Int64: exact for |value| <= 2^53, otherwise IEEE round to nearest, ties to even (as C# (double)long and Python float(int)). Never fails. float64RoundToInt64 and float64FloorToInt64 convert back." },
  float64Sqrt: { ...unary("float64Sqrt"), notes: "IEEE 754 square root, correctly rounded: Float64 to Float64. A negative value gives NaN (Python math.sqrt raises instead); -0.0 gives -0.0. Never fails." },
  float64Abs: { ...unary("float64Abs"), notes: "Float64 with the sign cleared, as Python abs(x): -0.0 gives 0.0. Never fails." },
  float64Negate: { ...unary("float64Negate"), notes: "Float64 with the sign flipped, as Python -x: 0.0 gives -0.0 (subtracting from 0.0 would give 0.0). Never fails." },
  float64Floor: { ...unary("float64Floor"), notes: "Largest integral Float64 not above x, as Python math.floor(x) but staying Float64: -0.5 gives -0.0; NaN, infinities and zeros unchanged. For an Int64 use float64FloorToInt64. Never fails." },
  float64Ceiling: { ...unary("float64Ceiling"), notes: "Smallest integral Float64 not below x, as Python math.ceil(x) but staying Float64: -0.5 gives -0.0; NaN, infinities and zeros unchanged. Never fails." },
  float64Truncate: { ...unary("float64Truncate"), notes: "x rounded toward zero, as Python math.trunc(x) but staying Float64: -1.5 gives -1.0; NaN, infinities and zeros unchanged. Never fails." },
  bits32FromInt64: { ...unary("bits32FromInt64"), notes: "Bits32 holding the low 32 bits of the Int64 (value mod 2^32; -1 gives all ones). Bits32 has no literal: write constants as bits32FromInt64 of an int64Literal. Never fails." },
  bits32ToInt64: { ...unary("bits32ToInt64"), notes: "The Bits32 read as an unsigned Int64, 0..4294967295. Never fails." },
  bits32Xor: { ...binary("bits32Xor"), notes: "Bitwise exclusive or of two Bits32. Never fails." },
  bits32And: { ...binary("bits32And"), notes: "Bitwise and of two Bits32. Never fails." },
  bits32Or: { ...binary("bits32Or"), notes: "Bitwise or of two Bits32. Never fails." },
  bits32Not: { ...unary("bits32Not"), notes: "Bitwise complement of a Bits32. Never fails." },
  bits32Add: { ...binary("bits32Add"), notes: "Bits32 addition modulo 2^32 (wraps). Never fails." },
  bits32RotateRight: { shape: { kind: "bits32RotateRight", value: recipe, amount: recipe }, notes: "Rotates the Bits32 value right by the Int64 amount taken floor-modulo 32; a negative amount rotates left. Never fails." },
  bits32ShiftRight: { shape: { kind: "bits32ShiftRight", value: recipe, amount: recipe }, notes: "Logical right shift of the Bits32 value by the Int64 amount (zeros enter); amount <= 0 gives the value unchanged and amount >= 32 gives 0. Never fails." },
  bits32ShiftLeft: { shape: { kind: "bits32ShiftLeft", value: recipe, amount: recipe }, notes: "Left shift of the Bits32 value by the Int64 amount; amount <= 0 gives the value unchanged and amount >= 32 gives 0. Never fails." },
  setEmpty: { shape: { kind: "setEmpty", itemType: type }, notes: "The empty Set<T>. T must be equatable (as for valueEqual). Sets are immutable values visited in insertion order." },
  setFromList: { ...unary("setFromList"), notes: "Set<T> of the list items without repeats, in the order of their first occurrence (Python list(dict.fromkeys(items)))." },
  setInsert: { shape: { kind: "setInsert", set: recipe, item: recipe }, notes: "Set<T> with item added last; unchanged when already present. O(log n)." },
  setContains: { shape: { kind: "setContains", set: recipe, item: recipe }, notes: "Bool: whether item is present (valueEqual). O(log n)." },
  setRemove: { shape: { kind: "setRemove", set: recipe, item: recipe }, notes: "Set<T> without item; unchanged when absent. Inserting it again later puts it last." },
  setUnion: { ...binary("setUnion"), notes: "Set<T>: the left items in their order, then the right items not in left in theirs. Never fails." },
  setIntersection: { ...binary("setIntersection"), notes: "Set<T>: the left items that right contains, in left order. Never fails." },
  setDifference: { ...binary("setDifference"), notes: "Set<T>: the left items that right does not contain, in left order. Never fails." },
  setSize: unary("setSize"),
  setToList: { ...unary("setToList"), notes: "List<T> of the items in insertion order." },
  mapEmpty: { shape: { kind: "mapEmpty", keyType: type, valueType: type }, notes: "The empty Map<K, V>. K must be equatable (as for valueEqual: no Float64, functions, tasks, capabilities or maps). Maps are immutable values: mapInsert and mapRemove return a new map." },
  mapInsert: { shape: { kind: "mapInsert", map: recipe, key: recipe, value: recipe }, notes: "Map<K, V> with key set to value. A new key goes last in iteration order; an existing key keeps its position and gets the new value. Keys compare with valueEqual. O(log n). Never fails." },
  mapGet: { shape: { kind: "mapGet", map: recipe, key: recipe }, notes: "Option<V>: the value for key, or None. O(log n). Never fails." },
  mapContainsKey: { shape: { kind: "mapContainsKey", map: recipe, key: recipe }, notes: "Bool: whether key is present." },
  mapRemove: { shape: { kind: "mapRemove", map: recipe, key: recipe }, notes: "Map<K, V> without key (unchanged when absent). Inserting the key again later puts it last." },
  mapSize: unary("mapSize"),
  mapKeys: { ...unary("mapKeys"), notes: "List<K> of the keys in insertion order (the order keys were first inserted, as Python dict and JavaScript Map)." },
  mapValues: { ...unary("mapValues"), notes: "List<V> of the values in the same order as mapKeys." },
  mapFold: { shape: { kind: "mapFold", map: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "S: folder(state, key, value) over the entries in insertion order, like listFold. The folder is a pure function with exactly three parameters not bound by context." },
  mapFromList: { shape: { kind: "mapFromList", source: recipe, function: "<function display name>", context: callbackContext }, notes: "Map<K, T>: the items indexed by function(item), which must return an equatable K. On a duplicate key the later item wins and the first position is kept, as with repeated mapInsert. Index records by id: mapFromList(rows, rowId)." },
  mapFilter: { shape: { kind: "mapFilter", map: recipe, function: "<function display name>", context: callbackContext }, notes: "Map<K, V> (same type) of the entries for which function(key, value) is true, keeping their insertion order. The callback is a pure function returning Bool with exactly two parameters not bound by context." },
  listEmpty: { shape: { kind: "listEmpty", itemType: type }, typeRef },
  listConstruct: { shape: { kind: "listConstruct", itemType: type, items: [recipe] }, typeRef },
  listLength: unary("listLength"),
  listRange: { shape: { kind: "listRange", start: recipe, end: recipe }, notes: "List<Int64> start, start + 1, ..., end - 1, as Python list(range(start, end)); end <= start gives the empty list. A list too large to allocate is a host failure, as for listConcat. With listFold or listEffectFold it is a counted loop." },
  listReverse: { ...unary("listReverse"), notes: "List<T> (same type) with the items in reverse order, as Python list(reversed(items)). Linear time; never fails." },
  listItemAt: { shape: { kind: "listItemAt", list: recipe, index: recipe }, notes: "Option<T>: Some(item) at a zero-based Int64 index when 0 <= index < length, else None. Negative indexes do not count from the end. Never fails." },
  listSlice: { shape: { kind: "listSlice", list: recipe, start: recipe, end: recipe }, notes: "List<T> (same type as list) of the items at Int64 positions [start, end), like stringSlice: both bounds clamp to [0, length]; negative bounds do not count from the end (unlike Python list[start:end]); end <= start gives the empty list. Never fails." },
  listAppendItem: { shape: { kind: "listAppendItem", list: recipe, item: recipe } },
  listConcat: binary("listConcat"),
  listFold: { shape: { kind: "listFold", source: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "Pure folder: (accumulator, item) -> accumulator over its parameters not bound by the optional context. Iterates left to right, returning the seed for an empty list. Context binds further folder parameters to values computed once. To stop at the first Err (a folder returning Result<S, E>), use listEffectFold instead of a Result accumulator." },
  parallelAll: { shape: { kind: "parallelAll", source: recipe, worker: "<function display name>", context: callbackContext }, notes: "Runs the worker (item -> Result<U, E>, effects allowed, capabilities via context) for every item concurrently and awaits all. The first Err or exception cancels the others. Result: the lowest-index Err, else Ok of all values in order." },
  cancellationRequested: { shape: { kind: "cancellationRequested" }, notes: "Bool: whether the current ParallelAll worker, task or task scope body was asked to stop; false outside them. Check it between steps to stop cooperatively." },
  taskScope: { shape: { kind: "taskScope", body: "<function display name>", context: callbackContext }, notes: "Runs the body with a new TaskScope through its one unbound parameter; the body returns Result<T, E>, which is the result. On every exit of the body (Ok, Err or a host exception) each unfinished task of the scope is cancelled and awaited before the node completes. A task's host exception is rethrown then unless the body threw first." },
  taskStart: { shape: { kind: "taskStart", scope: recipe, worker: "<function display name>", item: recipe, context: callbackContext }, notes: "Starts the worker (item -> Result<U, E>, effects allowed, capabilities via context) as a task of the TaskScope and returns Task<Result<U, E>> at once. A task's Err does not cancel other tasks." },
  taskWait: { shape: { kind: "taskWait", tasks: recipe, mode: "<any|all>", limit: recipe }, notes: "tasks: List<Task<T>>. limit (may be omitted): Option<Int64> milliseconds; None or absent waits without a limit, a negative limit counts as 0. Waits until any (or all) listed tasks have finished or the limit elapses, and returns List<Option<T>> in input order: Some(result) when finished, None while running. Tasks keep running. any on [] returns [] at once. A handle whose scope has completed is finished." },
  taskCancel: { shape: { kind: "taskCancel", task: recipe }, notes: "Unit. Requests cooperative cancellation of one task and returns without waiting; idempotent, a no-op once finished. Wait for the task to see what its worker returned." },
  useResource: { shape: { kind: "useResource", acquire: recipe, releaseCapability: recipe, capability: "<capability display name>", releaseOperation: "<operation display name>", body: "<function display name>", context: callbackContext }, notes: "acquire: Result<R, E>. The body gets R through its one unbound parameter and returns Result<T, E>. Release (R -> Unit or Result<Unit, E>) always runs after a successful acquire, on Ok, Err or a host exception; a body Err wins over a release Err." },
  listEffectFold: { shape: { kind: "listEffectFold", source: recipe, seed: recipe, function: "<function display name>", context: callbackContext }, notes: "Effectful fold, left to right, each item exactly once. The folder may use effects the enclosing function declares and receives capabilities through context. If it returns Result<S, E> for seed S, the fold stops at the first Err and yields Result<S, E>; otherwise it returns S. A pure folder is allowed and needs no effects: then it is the pure fold that stops at the first Err (seed S, not Ok(S); no pass-through Err arm)." },
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
  productUpdate: {
    shape: { kind: "productUpdate", source: recipe, type: "<product type display name>", fields: [{ field: "<field display name>", value: recipe }] },
    notes: "A copy of source (which must have the named product type) with the listed fields replaced and every other field kept, like Python dataclasses.replace. List at least one field, each once; field order is not semantic. Field values may read source, which is unchanged. Never fails.",
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
    shape: { kind: "matchUnion", type: "<union type display name>", input: recipe, arms: [{ variant: "<variant display name>", body: recipe }, { variants: ["<variant>", "<variant>"], body: recipe }], otherwise: recipe },
    notes: "Every variant is named by exactly one arm, or taken by the optional otherwise. An arm names one variant (variant) or several sharing one body (variants). otherwise covers every variant no arm names; it is stored as an explicit variant list, so a variant added later is not absorbed, and it is an error when no variant is left for it. Only an arm with exactly one variant can read that variant's fields with patternReference slot=field; arms with several variants and otherwise see no fields.",
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

const usage = "Pass category for its kinds' one-line signatures (wire field names and types; often enough to write a scalar recipe), kind for one exact ordinary-tool wire shape with notes, or kinds for several shapes in one call. Shapes use display names where ordinary tools resolve semantic identities."

/** @param {RecipeKind} kind */
function entry(kind) {
  const value = Object.hasOwn(references, kind) ? references[kind] : undefined
  if (!value) throw new Error(`Unknown expression recipe kind '${kind}'.`)
  return { kind, ...value }
}

/**
 * Omit every argument for the whole signature index, or pass exactly one of category, kind or kinds.
 * @param {{ category?: RecipeCategory; kind?: RecipeKind; kinds?: RecipeKind[] }} [query]
 */
export function recipeReference(query = {}) {
  const { category, kind, kinds } = query
  const given = [category, kind, kinds].filter(value => value !== undefined).length
  if (given > 1) throw new Error("Pass at most one of category, kind or kinds.")
  if (kind !== undefined) return { format: "nameResolvedExpressionRecipe", ...entry(kind) }
  if (kinds !== undefined) {
    if (kinds.length === 0) throw new Error("kinds must list at least one recipe kind.")
    return { format: "nameResolvedExpressionRecipe", entries: [...new Set(kinds)].map(entry) }
  }
  if (category !== undefined) {
    if (!Object.hasOwn(categories, category)) throw new Error(`Unknown recipe category '${category}'.`)
    return { format: "nameResolvedExpressionRecipe", category, signatures: categories[category] }
  }
  const names = Object.fromEntries(Object.entries(categories).map(([name, signatures]) => [name, Object.keys(signatures)]))
  return { format: "nameResolvedExpressionRecipe", categories: names, typeRef, usage }
}
