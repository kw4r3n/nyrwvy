# Recipe kinds retired from the agent reference

- **Status:** Accepted
- **Date:** 2026-10-18
- **Roadmap:** [selection rule](../plans/post-m2-roadmap.md#selection-rule-from-here) (language operations must be warranted)

Six builtin operations duplicate what other operations already express directly, which the [selection rule](../plans/post-m2-roadmap.md#selection-rule-from-here) of 2026-10-18 counts against an operation being warranted:

| Retired kind | Use instead |
| --- | --- |
| `stringEqual` | `valueEqual`: String is equatable, and `ValueEqual` on Strings is the same ordinal comparison. |
| `int64Equal` | `valueEqual`: Int64 is equatable, with the same result. |
| `mapGetOrDefault` | `optionGetOrDefault` of `mapGet`: the same value and the same evaluation of every operand. |
| `int64GreaterThan` | `int64LessThan` with swapped operands: `a > b` is `b < a`. |
| `int64LessThanOrEqual` | `boolNot` of `int64LessThan` with swapped operands: `a <= b` is `not (b < a)`. |
| `int64GreaterThanOrEqual` | `boolNot` of `int64LessThan`: `a >= b` is `not (a < b)`. |

The three Int64 comparisons were retired on 2026-10-18 (owner decision) by the argument the [sort and String ordering decision](list-sort-and-string-ordering.md) made for Strings: a total order needs one strict comparison, and the other three forms are that comparison with swapped operands or negated, so they add no meaning. Int64 has no NaN, so the negation is exact. Float64 and String keep only `LessThan` for the same reason and get no counterparts.

`Float64Equal` stays: it is the IEEE 754 comparison, and Float64 is not equatable under `ValueEqual` ([decision](value-equality-and-map.md)). `StringContains` stays: `StringIndexOf` gives an `Option<Int64>`, so testing it takes a match (and `ListContains` was added beside `ListIndexOf` for the same reason, [decision](list-contains.md)).

## Decision

- The retired kinds are left out of the agent-facing recipe reference (`nyrwvy_recipe_reference`), so an agent is no longer offered them. `OperationSignatures.retired` lists them with their replacements, and the generated reference signatures omit them.
- The wire readers still accept them, and their node kinds, validation, codecs, IR and C# backend are unchanged, so existing snapshots and the authoring scripts of the examples (whose fixtures record authenticated graph hashes) keep working unchanged.

## Reason

Rejecting the kinds at the wire boundary would make every example that authors them unreproducible from its own script, since re-authoring with a replacement changes the graph and its recorded hashes. Hiding them from the reference stops new use at no such cost.

## Consequences

- `RecipeKindConsistencyTests` checks that a retired kind is accepted by both readers and absent from the reference.
- Removing the node kinds entirely would need a snapshot migration that rewrites existing nodes, and new hashes for the examples that author them.

## Revisit conditions

Revisit when the examples that author these kinds are retired or re-recorded, or when a snapshot migration is otherwise needed. The [legacy cutover](../plans/post-m2-roadmap.md#legacy-cutover-planned) plans exactly that.
