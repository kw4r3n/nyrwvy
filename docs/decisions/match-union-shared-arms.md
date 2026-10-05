# MatchUnion arms shared by several variants

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), order 41
- **Design note:** [repeated `MatchUnion` arms](../plans/match-union-shared-arms.md)

This decision settles the `matchUnion` default-arm candidate, observed in probes 7, 10, 17, 18 and 19, where repeating one body across arms cost 8 to 11% of each port. The design note compared five options; the owner chose option D, with option E's shorthand left to the AI-facing judgment, which took it.

## Decision

- **Multi-variant arms (option D).** A `MatchUnion` arm names one or more variants and has one body. Every declared variant is named by exactly one arm. An arm with one variant binds each of its fields exactly once, as before; an arm with several variants binds no fields.
- **`otherwise` as authoring shorthand (option E).** A recipe may give one `otherwise` body. Materialization expands it into an explicit list of the variants no other arm names; the persisted graph has no catch-all. An `otherwise` with nothing left to cover is rejected. Its body sees no fields, even when it covers one variant.
- **No shared field bindings for now.** Binding fields that several variants have in common needs name and type matching rules and has no consumer; it waits for one.
- **Persisted form unchanged for one-variant arms.** Snapshots, Nyason and the graph read write `variantId` for a one-variant arm and `variantIds` (at least two) only for a shared arm, so existing graphs, their semantic fingerprints and their execution IR are unchanged. Since 2026-10-05 a snapshot with a shared arm is written at snapshot JSON 13 / Nyason `nayamnyam.snapshot.v9`, and older versions reject it; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- **Variant evolution.** Adding a variant "like" a source variant that sits in a shared arm adds the new variant to that arm instead of copying its body. A one-variant source arm is still copied.
- **Lowering.** One execution block per arm, named by its first variant in canonical order, and one union-match target per variant, so the execution IR model and the C# backend are unchanged.

## Reason

The [exhaustiveness principle](../concepts/ai-first-design-principles.md#exhaustiveness-over-catch-all-behavior) exists so that adding a variant makes the impact mechanically observable. A plain default arm (option B) would send a new variant to its body without a diagnostic. Shared arms keep every variant named by stable ID in the graph, so a new variant still leaves every match incomplete, while the body is stored, reviewed, fingerprinted and lowered once. `otherwise` saves the agent from looking up and spelling the remaining variants, and because it is expanded at authoring time it adds no catch-all.

## Consequences

- `UnionMatchArm.VariantIds` replaces `VariantId` in the semantic graph and typed IR; `NestedGraphRole.UnionArm` carries the list.
- New preflight codes: `PureExpressionMatchOtherwiseDuplicate` and `PureExpressionMatchOtherwiseUnused`; persisted graphs report `EmptyUnionMatchArm` for an arm with no variant.
- The agent recipe reference documents `variants` and `otherwise`.

## Revisit conditions

- A consumer needs to read fields shared by the variants of one arm.
- A probe shows agents using `otherwise` where a later variant should have taken a different body, and the `NonExhaustiveUnionMatch` repair after adding a variant is not enough to catch it.
