# Product field value recipes

- **Status:** Accepted
- **Date:** 2026-10-24
- **Issue:** none yet

This decision extends `addProductField` in `simulateOperations` and `simulateOperationsByName`. The [session contract](../specs/agent-authoring-session.md) is authoritative for the request shape.

## Decision

A construction plan of `addProductField` names its field value in one of two ways: `valueNodeId`, an existing node as today, or `value`, an `ExpressionRecipe`. Each plan uses exactly one of the two.

- A `value` recipe is bound, type-checked and materialized in the lexical scope of its `productConstruct` node. Parameters of the owning function and the pattern slots visible at that node are available, at the depths a targeted replacement of that node would use. This is the mechanism `addUnionVariant` arm plans already use (`AgentUnionVariantArmPlans`).
- The recipe must produce the new field type and may use only the owning function's `RequiredEffects`.
- Materialization runs before the operation is simulated. The core operation therefore carries the materialized nodes (`ProductFieldConstructionPlan.AddedNodes`). They are inserted into the construct's own graph, just before the construct, with fresh system-owned IDs. Commit re-simulates the same nodes.
- The plan-coverage rule is unchanged: every `productConstruct` of the Product needs exactly one plan.

## Reason

A field value that depends on each constructor's context, for example a list derived from that function's bound values, has no existing node of the field type to point at. Agents worked around this in two reviewed changes: bind a helper value into the body, add the field pointing at that node, then replace the body again to remove the bind. A recipe per construction removes the detour and keeps the change atomic.

Reusing the arm-plan path keeps one way of resolving a recipe at an existing graph location, using `tryFindTarget` frames, `inferTypeWithFrames` and `materialize`. It adds no new recipe kind.

## Consequences

- `ProductFieldConstructionPlan` gains `AddedNodes`, which is empty for a `valueNodeId` plan. The exact-ID adapter path (`WireProtocolAdapters.operation`) still accepts only `valueNodeId`.
- Field-value type checking in the core runs on the snapshot with the added nodes inserted, so a recipe root and an existing node are checked the same way.
- A recipe cannot refer to the field being added, because it is resolved against declarations without it.

## Revisit conditions

Revisit when a construction's value needs a node outside the construct's own graph to be created, when one value should be shared by several constructions, or when the same need appears for `addFunctionParameter` call-site plans.
