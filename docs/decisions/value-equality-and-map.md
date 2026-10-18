# Value equality, and Map revisited again

- **Status:** Accepted
- **Date:** 2026-09-28
- **Superseded in part:** 2026-10-03, Map added ([decision](map.md))
- **Roadmap:** [post-M2 queue from the 2026-09-28 probe 3](../plans/post-m2-roadmap.md#queue-from-the-2026-09-28-probe-3), order 11

This decision settles order 11 of the post-M2 queue: how a program compares keys for Map-like replace-or-append. [Probe 3](../verification/rrawrlet-slice-probes-summary.md#probe-3) (friction 1) replaced-or-appended in 3 places of rrawrlet's `usage_stats` core (statuses, extras and groups) and compared a 6-field `Option<String>` group key by hand. That meets the Map revisit condition in [task fields, list indexing, and Map revisited](task-fields-list-indexing-and-map.md#revisit-conditions). The implemented surface is described by the [language subset](../reference/language-subset.md) and the [expression authoring specification](../specs/expression-authoring.md), which remain the authority once implemented.

## Decision

**`ValueEqual(left, right): Bool`.** Both operands have one type `T`, and `T` must be *equatable*:

- String, Int64, Bool and Unit;
- `List`, `Option` and `Result` of equatable types;
- declared Products and Unions whose fields are all equatable, through any nesting and including recursive types.

Float64 is not equatable because of NaN (compare with `Float64Equal` explicitly). Function types, `Task`, `TaskScope`, capabilities and type parameters are not equatable either, nor is any type that reaches one of them. Equality is structural: the same variant with pairwise equal fields; Strings by ordinal code units; Options and Results by case and payload; lists by length and then item by item in order. It never fails. Recipe and wire name `valueEqual`, Nyason `value-equal`.

**Diagnostics.** A persisted graph whose operands differ in type reports `ValueEqualOperandTypeMismatch`. A recipe with that mistake reports `PureExpressionOperandTypeMismatch`, as `ListItemAt` and the other constructs do. A type that is not equatable reports `ValueEqualTypeNotEquatable` in both forms. The message names the path to the first offending part by display names, for example `Holder.measures -> List item -> Measure.value`, and says why that part is not equatable.

**`StringEqual`, `Int64Equal` and `Float64Equal` stay unchanged.** They remain the per-type forms.

**Map stays deferred.**

## Reason

**The key does not fit a Map.** The group key in `usage_stats` is a Product of a role and 5 `Option<String>` choices. A Map with String or Int64 keys, the smallest Map candidate so far, cannot hold it without an encoding into one String, and that encoding would have to keep `None` apart from `""`, as Python does. Structural equality compares the key as it is.

**Measured effect.** The probe 3 core was rewritten with `valueEqual` and reran 7/7 against Python on the same cases (`probe3/usage11.py`, outside the repository). It went from 7 types, 24 functions and 324 recipe nodes to 7 types, 23 functions and 270 nodes. `optStrEq` (12 nodes) disappeared, and `sameGroup` fell from 47 nodes to 5: one `valueEqual` of the two `key` fields. The 4 equality helpers had been 75 nodes; the remaining 3 are 21. The probe estimate that all 4 functions would disappear did not hold. The generic `upsert` still takes an equality callback, and `sameStatus` and `sameExtra` compare only the key fields of a counted entry, not whole values. They are 5- and 11-node callbacks, not equality code. The remaining upsert cost is the one generic helper written once (`anySame`, `mergeIf` and `upsert`, 27 nodes), which served all 3 collections unchanged. That is less than a new generic Map type through every layer: the semantic graph, validation, codecs, typed and execution IR, the C# backend, the viewer, run values and both recipe wire forms.

**Why type parameters are excluded.** A generic helper that compared its `T` values directly would drop the callbacks too. But nothing constrains a type argument to be equatable, and adding that constraint means checking every call, callback and function value that instantiates the helper. Without evidence that a callback is too costly, the smaller contract is kept.

**Why Float64 is excluded.** Under IEEE 754 NaN is not equal to itself, while a structural contract needs reflexivity. `Float64Equal` states the IEEE comparison where a program wants it, and no probe needed Float64 keys.

**Bool equality settled.** The [Bool decision](bool-operators-and-map.md) deferred Bool equality until a program needed it. `ValueEqual` covers it as a byproduct, so no separate `BoolEqual` is added.

## Consequences

- `ValueEqual` follows the usual path through the semantic graph, validation, snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend, the viewer (`value equal`, left and right operand dependencies) and both recipe wire forms.
- `EquatableTypes` in the semantics foundation, beside `TaskTypes.bearing`, decides equatability transitively. Each declared type is inspected once, and a type counts as equatable where it recurs. It also renders the path and the reason for diagnostics.
- The C# backend emits `NyEquality.Equal(left, right)` over `EqualityComparer<T>.Default`. Its structural behavior depends on each emitted type: `NyList<T>` now implements `IEquatable<NyList<T>>` by length and ordered items with a matching `GetHashCode`, instead of reference equality. Products and Union variants are sealed records, whose generated equality compares the runtime variant and every field. `NyOption` and `NyResult` are record structs. See the [C# backend](../specs/csharp-backend.md#value-equality).
- A generic helper keeps taking an equality callback for its type parameters.

## Revisit conditions

- **Map:** measured fold cost matters at real sizes, or a consumer needs lookup where a linear fold is the dominant cost. The smallest candidate is a Map keyed by an equatable type. [Probe 7](../verification/rrawrlet-slice-probes-summary.md#probe-7-scale) is the first measured consumer: after one restructuring, linear keyed lookups were most of the time of an event observer (about 3.4 s of 3.7 s at 19,200 events and 1,600 keys, within 10 times Python), but nothing was blocked, so Map stayed deferred.
- **Equatable type parameters:** equality callbacks passed to generic helpers become a measured authoring cost across several consumers.
- **Float64 in `ValueEqual`:** a real program needs structural equality over Float64 fields. It would need an explicit NaN rule, for example bitwise equality of canonical NaN.
