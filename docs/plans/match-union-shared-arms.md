# Repeated `MatchUnion` arms (design note)

**Status:** Proposed. A design note for the `matchUnion` default-arm candidate in the post-M2 [open candidates](post-m2-roadmap.md#open-candidates). Nothing here is implemented or accepted; the owner chooses among the options below.

## Context

`MatchUnion` must list every declared variant exactly once, each with its own body ([expression authoring](../specs/expression-authoring.md#matchunion)). Five probes paid for this when most variants share one body:

| Probe | Cost |
| --- | --- |
| [7](../verification/rrawrlet-slice-probe7-20260929.md) | 8 shallow `Json` matches spell out about 45 default arms |
| [10](../verification/rrawrlet-slice-probe10-20260930.md) | 130 of 225 arms repeat a sibling arm: 257 nodes, 10% of the port, mostly fixed results over the 14 `Mode` variants |
| [17](../verification/rrawrlet-slice-probe17-20261001.md) | 25 matches over the 7 `Json` variants: 298 nodes, 11% |
| [18](../verification/rrawrlet-slice-probe18-20261001.md) | 17 matches over the 8 `Json` variants: 117 default arms, 276 nodes, 10% |
| [19](../verification/rrawrlet-slice-probe19-20261001.md) | 20 matches over the 8 `Json` variants: 133 default arms, 295 nodes, 7.8% |

The cost has two parts. The **graph** holds the same body once per variant, and every one is reviewed, fingerprinted and lowered. The **request** spells out every arm, so the agent must look up the full variant list and repeat the body.

What a plain default arm would give up is the [exhaustiveness principle](../concepts/ai-first-design-principles.md#exhaustiveness-over-catch-all-behavior): adding a union variant makes every match that does not handle it invalid, so the impact of the new variant is mechanically observable. A catch-all would silently send the new variant to its body. (D-015's wildcard rule in the [foundational decisions](../decisions/foundational.md) is the F# core's own code style, not a rule about Nyrwvy programs; the roadmap already records this.)

## Invariant to keep

After a variant is added to a union, every `MatchUnion` over that union is invalid until someone decides, for that match, which body the new variant takes. The [fieldless variant evolution workflow](../../src/Nyrwvy.Semantics/AgentFieldlessUnionVariantEvolutionWorkflow.fs) already makes that decision explicit, by copying a named source variant's arm.

## Options

### A. Keep the rule (no change)

No new surface. The probes keep paying 8 to 11% of their nodes, and the cost grows with the variant count.

### B. A semantic default arm

`MatchUnion(input, arms, otherwise)`, where `otherwise` takes every variant not listed. This removes both costs, but breaks the invariant: a new variant goes to `otherwise` with no diagnostic, which is the catch-all the principle rules out. **Not recommended.**

### C. Authoring-only expansion

The recipe accepts `otherwise`, and materialization writes one ordinary arm per remaining variant, each a copy of the body. The persisted graph is unchanged and the invariant holds. It removes the request cost only: the graph still holds the repeated bodies (the 295 nodes of probe 19 stay), and review shows every copy.

### D. Multi-variant arms in the graph (recommended)

An arm names a non-empty **set** of variants and has one body, like an or-pattern (`| A | B -> body`):

```text
UnionMatchArm = { VariantIds: VariantId list (non-empty); FieldBindings; Body }
```

- Validation: the arms' variant sets are disjoint and their union is exactly the declared variant set, as today for single variants.
- Field bindings: an arm with one variant binds its fields as today. An arm with several variants binds no fields; sharing the fields the variants have in common is a later extension, only if a consumer needs it.
- The invariant holds: every variant is still named by ID in the persisted graph, so a new variant leaves every match incomplete. Variant evolution adds the new variant to the source variant's arm instead of copying its body.
- Both costs drop: the body is stored, reviewed, fingerprinted and lowered once, and the C# backend can emit stacked `case` labels.
- Recipe form: `arms: [{ variants: ["Null", "Bool", ...], body }]`, with the single-variant form kept as is.

### E. D plus an authoring shorthand

D, and the recipe also accepts `otherwise` for the last arm, which materialization expands into an explicit **variant list** (not body copies). The simulate review shows the expanded list, so the agent sees which variants it covered; the persisted graph is plain D. This is C's request saving on top of D's graph saving, without a semantic catch-all. Since the expansion is fixed at authoring time, a later variant addition still makes the match invalid.

## Recommendation

**D, with E's shorthand if the owner wants it.** D keeps the invariant exactly and removes the graph repetition the probes measured; E removes the variant-list lookup on top. Neither adds a catch-all.

## Cost of D

`UnionMatchArm` is used in about 20 files (semantic graph, validation, both codecs, typed and execution IR, the C# backend, the viewer, the wire forms and the variant evolution workflow). The persisted snapshot and Nyason forms change shape, so the codecs need to read the current single-variant form as a one-element set (or the format version moves). The semantic fingerprint changes for every existing match, unless single-variant arms keep their current encoding.

## Evidence it worked

Re-author probe 19 (or 18) with the change and compare: the node count of the port's own matches (295 nodes in probe 19), the number of arms in requests, and the same Python cases still matching. Then add a variant to `Json` and confirm every match over it reports an incomplete match.

## Choices for the owner

1. Which option: A, C, D or D + E.
2. For D: whether a multi-variant arm may bind shared fields now, or never until a consumer needs it (recommended: not now).
3. For D: keep the single-variant persisted encoding (stable fingerprints for existing graphs) or move every arm to the set form.
