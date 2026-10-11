# RunValue input quick reference

`nyrwvy_recipe_reference` describes **ExpressionRecipe** nodes used to author
functions. Execution fixtures instead use **RunValue** data. The authoritative
contract is [AuthoringSession](../../docs/specs/agent-authoring-session.md#name-resolved-ai-authoring),
including its execution limits; this page is a small `runPureByName` recipe.

Function arguments are positional, even when the operation ends in `ByName`.
Type, field and variant selectors inside values use names. Do not mix the
exact-ID `runPure` vocabulary (`typeId`, `fieldId`, etc.) with these selectors.

```json
{"kind":"product","type":"Stock","fields":[
  {"field":"sku","value":{"kind":"string","value":"A"}},
  {"field":"available","value":{"kind":"option","itemKind":"int64","hasValue":true,"value":"5"}}
]}
```

Use all declared Product fields exactly once. The stock example assumes a
`Stock { sku: String, available: Option<Int64> }` declaration. Unknown availability
is `{"kind":"option","itemKind":"int64","hasValue":false}`: omit `value`.
`Some(0)` is distinct from `None`. Int64 decimal strings preserve exact values.

A List of Products uses `itemType` with **tagged** values in `items`:

```json
{"kind":"list","itemType":{"kind":"named","name":"Stock"},"items":[]}
```

Replace `items` with the complete Product objects above. An empty list still
needs its item type. A scalar list may instead use the flat form
`{"kind":"list","itemKind":"int64","items":["1","2"]}`; do not put tagged
objects in that flat `items` array.

A Result with a Product payload uses both type references and a tagged payload:

```json
{"kind":"result","okType":{"kind":"named","name":"BatchReport"},
 "errorType":{"kind":"primitive","primitive":"string"},
 "isOk":false,"value":{"kind":"string","value":"UNKNOWN_AVAILABILITY:B"}}
```

For success use `isOk:true` and the complete `BatchReport` Product as `value`.
Keep both type references for either branch. A scalar Result can use `okKind`
and `errorKind` with a raw scalar payload instead; do not mix the two forms.

Generate only this wire encoding in a helper; keep business logic in the Nyrwvy
program and derive expected results independently. Save the JSON directly to a
file and let the client parse it. JSON escapes such as `"\u00e9"` and `"e\u0301"`
preserve different code-point sequences without visual transcription. The client
does not normalize strings or rewrite any typed values.
