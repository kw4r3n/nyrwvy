# Product update

- **Status:** Accepted
- **Date:** 2026-10-01
- **Roadmap:** [post-M2 queue from the 2026-10-01 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-01-owner-choice), order 32

This decision settles order 32 of the post-M2 queue. Four probes rebuilt whole records to change one or two fields: [probe 7](../verification/rrawrlet-slice-probe7-20260929.md) wrote 8 setters for its 8-field `Obs` state (134 nodes, 10% of the port), [probe 10](../verification/rrawrlet-slice-probe10-20260930.md) needed rebuilding helpers for its `Parser`, `Scan` and `Pass` states, [probe 15](../verification/rrawrlet-slice-probe15-20261001.md) in its 10- and 14-field server states (about 550 restated nodes by its estimate, and two functions pushed over the 256-node recipe limit), and [probe 17](../verification/rrawrlet-slice-probe17-20261001.md) in the 5-field `shlex` state, which it rebuilt in 11 places. The implemented surface is described by the [language subset](../reference/language-subset.md), the [expression authoring specification](../specs/expression-authoring.md#structured-declaration-bindings) and the [agent graph tools](../specs/agent-graph-tools.md), which remain the authority.

## Decision

**One node, `ProductUpdate(typeId, source, fieldBindings)`.** Its value is a copy of `source` with each bound field replaced by its value and every other field kept, like Python's `dataclasses.replace`. The source must produce exactly `TypeRef.Named typeId` of a product type. The bindings name at least one field of that type, each at most once, and each value has exactly the field's type. The result type is the product type. The node never fails.

**Names, like construction.** The name-resolved recipe is `{"kind":"productUpdate","source":<recipe>,"type":"T","fields":[{"field":"f","value":<recipe>}]}`; the stable-ID wire form uses `typeId` and `fieldId`. The type is explicit, as for `fieldAccess`, so field names resolve without inferring the source's type first. Snapshot JSON writes `productUpdate` with `typeId`, `sourceNodeId` and `fields`; Nyason writes `product-update`.

**Evaluation.** The source materializes first, then the bound values in field stable-ID order (the order the typed IR keeps), not declaration field order. The difference cannot be observed: effect ordering is the same as for any other node with several operands, so independent effectful operands are rejected as ambiguous ([effectful expression ordering](effectful-expression-ordering.md)) and no two values are left with effects whose order the field order would decide. Values may read the source, including the field they replace. The source value is unchanged; products are immutable values.

**Field evolution.** Adding a field to the product needs no plan for a `ProductUpdate`: the new field is kept from the source, whose own construction was given an explicit value for it. Removing a field that a `ProductUpdate` binds is rejected with `ProductFieldStillReferenced`, as for a field access, and changing a field's type includes every function that updates that field in the affected set.

**Backend.** The C# backend emits a `with` expression on the generated sealed record: `source with { F_a = v1, F_b = v2 }`, bindings in field-ID order.

## Reason

**It removes restatement, not a reach limit.** No probe was blocked: every rebuilt record could be written. The cost is size and noise. Re-authoring probe 17's `shStep` with `productUpdate` and nothing else changed takes it from 183 to 131 nodes (52 fewer, 28%; the probe estimated about 68), and both forms give the same results on 614 inputs, which match Python's `shlex.split` on every one (`rrawrlet-probe17/order32/shupd.py` in the project's shared files). For an `n`-field record, a one-field change through construction restates `n - 1` field accesses of two nodes each; the update needs the source and the one value.

**Keeping a field is not a default.** The [exhaustiveness over catch-all behavior](../concepts/ai-first-design-principles.md#exhaustiveness-over-catch-all-behavior) principle is about cases nobody decided ([D-015](foundational.md) states the same for the F# core's own `match` expressions). An updated record's kept fields were each decided where the source was built, including a field added later, so nothing is absorbed. That is also why adding a field needs no plan for an update but does for a construction.

**At least one field.** An update of no fields would be the source itself; rejecting it keeps every node meaningful and keeps an accidental empty list from passing silently.

**Explicit type, not inference.** Requiring the type keeps the recipe self-describing and consistent with `fieldAccess` and `productConstruct`, and lets name resolution report an unknown field before type checking.

## Consequences

- The node follows the usual path through the semantic graph, validation (`EmptyProductUpdate`, `ProductUpdateSourceTypeMismatch`, `InvalidProductUpdateTarget`, `UnknownProductUpdateType`, and the shared `FieldBindingMismatch`, `DuplicateFieldBinding` and `FieldValueTypeMismatch`), snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend, the viewer (role `product update`, with `Source` and per-field `FieldValue` dependencies), both recipe wire forms and the agent recipe reference.
- Recipe preflight reports `PureExpressionEmptyProductUpdate`, `PureExpressionFieldBindingMismatch`, `PureExpressionDuplicateFieldBinding`, `PureExpressionFieldBindingTypeMismatch` and `PureExpressionProductUpdateSourceMismatch`.
- No schema version changes; snapshots without the node keep their bytes.

## Revisit conditions

- **Nested paths:** a consumer repeatedly updates a field of a field (`st.inner.count`), and nesting two updates is the measured cost.
- **Union variant fields:** a consumer needs the same for a variant's fields; today a variant is rebuilt with `unionConstruct` inside its match arm.
