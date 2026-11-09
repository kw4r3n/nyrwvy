# JSON library

An RFC 8259 parser and serializer for a recursive `Json` Union. It uses existing nodes only, so it runs on every backend and needs no host code.

## Types

| Type | Shape |
| --- | --- |
| `Json` | Union: `Null`, `Boolean { value: Bool }`, `Number { value: Float64 }`, `Text { value: String }`, `Array { items: List<Json> }`, `Object { members: List<JsonMember> }` |
| `JsonMember` | Product `{ key: String, value: Json }`; an object keeps its members in input order, duplicates included |
| `JsonError` | Product `{ position: Int64, message: String }` |

Match on `Json` directly, or use the accessors.

## Functions

| Function | Result |
| --- | --- |
| `jsonParse(text)` | `Result<Json, JsonError>`: one value with optional whitespace around it |
| `jsonSerialize(value)` | `String`: compact JSON, no whitespace |
| `jsonQuote(text)` | `String`: `text` as a JSON string literal, quotes included |
| `jsonNumberText(value)` | `String`: a Float64 as `jsonSerialize` writes it |
| `jsonField(value, key)` | `Option<Json>`: the first member named `key`; `None` for a missing key or a non-object |
| `jsonItem(value, index)` | `Option<Json>`: the item at a zero-based index; `None` out of range or for a non-array |
| `jsonMembers(value)`, `jsonItems(value)` | `Option<List<JsonMember>>`, `Option<List<Json>>` of an object or array |
| `jsonText(value)`, `jsonNumber(value)`, `jsonBool(value)` | `Option` of the scalar, `None` for another variant |
| `jsonIsNull(value)` | `Bool` |

## Errors

A parse never fails the run: every malformed input is an `Err`. `position` is the zero-based code-point index of the problem, counted as `StringLength` counts (a character above U+FFFF is one). The messages are `unexpected end of input`, `unexpected character`, `unexpected text after value`, `expected , or ]`, `expected , or }`, `expected string key`, `expected :`, `unterminated string`, `invalid escape`, `invalid \u escape`, `unpaired surrogate`, `control character in string`, `leading zero in number`, `invalid number`, `number out of range` and `nesting too deep`. A string error points at the escape's backslash or the offending character.

## Limits

- **Numbers are Float64.** `StringParseFloat64` reads them, rounding to nearest, so an integer above 2^53 loses precision (`123456789012345678` reads as `123456789012345680`). A number whose magnitude overflows (`1e400`) is `number out of range`; underflow gives zero.
- **Nesting is capped at 512** arrays and objects, because a nested value recurses (two frames per level) and a stack overflow ends the run. Without the cap, `[[…]]` overflowed the C# stack between 4,000 and 8,000 levels and the Zig one past 16,000. `jsonSerialize` also recurses per level and has no cap, so a value built by hand far deeper than 512 can still overflow it.
- **Surrogates must pair.** `😀` becomes one code point; a lone or mismatched `\uD800`…`\uDFFF` escape is `unpaired surrogate`, since a String cannot hold one.
- **No byte-order mark** or other non-JSON whitespace: only space, tab, LF and CR are skipped.
- **Duplicate keys** stay in `members`; `jsonField` returns the first.
- **Serializing** escapes only `"`, `\` and the controls below U+0020 (as `\u00XX`); everything else, `/` and non-ASCII included, is written as is. A number is `Float64ToString` (Python `repr`) without a trailing `.0`: `100`, `-0`, `0.1`, `1e+21`, `1.5e-05`. NaN and the infinities, which JSON lacks, are written `null`. So parse then serialize returns the same value, not always the same text.
- **Memory.** The parser first splits the input into a `List` of one-code-point Strings, because positional String operations (`StringSlice`, `StringSpan`) scan from the start on the Zig backend, which would make a scan over the text quadratic. Parsing is linear in time on both backends: 600,000-character inputs parse in well under the 5 s `runPure` limit.

## Using it

Import the workspace: `create --dependency libraries/json/json.json`. The sidecar keeps a copy of the imported snapshot, so a program takes later changes by upgrading the dependency.

## Changing it

`node libraries/json/author.mjs` writes `json.json` again from its recipes, with new IDs, so a program must upgrade its dependency afterwards.

## Check

`node libraries/json/check/run.mjs` (needs the Zig session host built by `npm run build:host`, `zig` and `dotnet`) imports the library and runs it on the Zig and C# backends: valid and invalid RFC 8259 inputs, escapes and surrogate pairs, numbers, nesting at and past the cap, long strings, arrays and objects, and a sample shaped like rrawrlet's `pricing.json`. `JSON_CHECK_TIMES=1` prints each call's time.
