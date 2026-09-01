# AI-first semantic design principles — 2026-08-25

## Status

These principles refine the accepted direction in D-001 through D-005. They are default tie-breakers for semantic-core design, but they do not make milestone-specific restrictions or evaluation scaffolding permanent architecture.

NyamNyam does not optimize its authoritative representation for pleasant manual source authoring. A separate projection or future language can serve that goal. The semantic core is optimized first for reliable AI comprehension and editing, with human reviewability recovered through queries, structured projections, and the viewer.

## Priority rule

When two representations preserve the same semantics, prefer the representation that lets an AI agent identify semantic role, type, target, ownership, and edit impact with less contextual inference and fewer opportunities for a plausible but incorrect edit.

Conciseness and surface-language elegance are not valid reasons by themselves to choose a more implicit representation.

Explicitness is not the same thing as restriction. If two agent-facing operations preserve the same semantic invariants, prefer the one that enables useful composition and reduces artificial ceremony unless narrower authority protects a concrete contract or tests a deliberate hypothesis.

## Evidence-informed, not precedent-bound

Design patterns from RAST, evaluation harnesses, other repositories, or earlier NyamNyam milestones are inputs to a decision rather than decision authority.

Before preserving a pattern, ask what NyamNyam-specific problem it solves now. A pattern that was useful to establish an experimental boundary may later become unnecessary once the interaction model has evidence behind it.

Historical compatibility required for a frozen evaluation condition should remain in the evaluation or adapter layer unless the behavior is independently part of the maintained semantic contract.

## Boundedness is a bootstrap strategy, not a permanent invariant

Small closed operations and narrowly scoped structural recipes are valuable when introducing a new semantic interaction because they make authority, diagnostics, review, and experimental claims easy to inspect.

That does not imply every later semantic role needs its own permanent operation, protocol, corpus, and controlled evaluation. When multiple roles share a well-understood typed structure, a more general operation may be the better design if it preserves system-owned identity, validation, reviewability, and semantic authority boundaries.

Do not add a dedicated operation merely because the previous milestone used dedicated operations. Do not generalize merely to reduce line count either. Choose the boundary that best expresses the current semantics.

## Local semantic completeness

A semantic node should normally be understandable from:

1. the node itself;
2. its owning value graph/function where ownership matters; and
3. declarations directly referenced by stable ID.

Avoid designs that require an agent to infer a node's meaning from:

- serialized position;
- a nearby textual declaration that is not explicitly referenced;
- an expected type supplied only by a distant parent;
- later nodes;
- display names;
- a second representation of the same semantic fact.

Lexical branch scope is legitimate direct context. Whole-program reconstruction is not.

## Explicitness versus duplicate authority

Explicit redundancy is allowed when it makes one semantic fact easier to classify locally and all repeated information is mechanically constrained to agree.

Duplicate authority is not allowed.

Examples:

- Good: `ResultOk` carries the error-side type explicitly because that type is not derivable from its value child alone.
- Good: stable IDs have domain-specific F# types and may later carry a matching domain prefix in Nyason.
- Bad: storing a direct call both as a graph node and in a separate `Calls` collection.
- Bad: storing an independently editable owner type beside a variant ID when variant ownership is uniquely derivable.

When two separately named structures have exactly the same semantics and shape, prefer one shared semantic type over near-duplicate record types. Phase D applied this rule by sharing `ValueMatchArm` between `Option.Some` and `Result.Ok`, while retaining a structurally distinct `ErrorMatchArm` for the error role.

## Semantic roles should be structural

Prefer distinct node kinds and typed fields over generic operator bags or stringly typed discriminators inside the semantic model.

For example, prefer:

```text
FieldAccess
ProductConstruct
UnionConstruct
OptionSome
ResultOk
MatchResult
```

over a generic `Operation(kind, operands)` structure when the semantic contracts differ.

A larger, closed node taxonomy is acceptable when it reduces semantic ambiguity. Exhaustive F# pattern matching is deliberately used to make additions visible to maintainers and agents.

This rule concerns semantic representation. It does not require an agent-facing protocol command for every individual node kind when a typed higher-level construction can represent several roles without losing meaning.

## Identity and associations

Give stable identity to semantic objects that need independent targeting, continuity, provenance, diagnostics, or durable references.

For the Milestone 0 value model this currently includes:

- types;
- fields;
- variants;
- functions;
- parameters;
- pattern bindings;
- graph nodes;
- obligations;
- snapshots and transactions.

Association records such as argument bindings, field-value bindings, and match arms do not receive independent stable identity unless a future operation or diagnostic demonstrates that they need it.

References use typed stable IDs. Names and serialized order never substitute for identity.

## No semantic guessing

Missing or ambiguous semantics must not be repaired by fallback behavior merely to keep processing moving.

Examples of prohibited fallback behavior include:

- unknown type becoming `String`;
- missing argument receiving an arbitrary default value;
- unknown node kind being ignored;
- non-exhaustive union matching silently gaining a default arm;
- display-name matching standing in for stable identity;
- backend restrictions being silently encoded into semantic types.

Use a rejected transaction or an explicit typed obligation when a safe incomplete representation exists.

This principle governs missing meaning. It is not a general instruction to minimize agent capability or reject a valid, explicit operation solely because a narrower interface could be constructed.

## Order and scope

Serialized collection order is semantic only when explicitly declared as such.

Phase D value graphs therefore use graph dependencies rather than node-list position for pure value dependencies. Nested `ValueGraph` values establish lexical branch scope:

- a child graph can reference values visible in its parent graph;
- a child cannot make its internal values visible to the parent;
- sibling branches cannot reference each other's internal values;
- a graph's result node is owned directly by that graph.

Evaluation order for effectful execution is intentionally deferred until the effect model and Execution IR define it explicitly.

## Exhaustiveness over catch-all behavior

For the Milestone 0 semantic model, structured matching is exhaustive:

- union matches enumerate every stable variant ID exactly once;
- `Option` has explicit `Some` and `None` branches;
- `Result` has explicit `Ok` and `Err` branches;
- conditionals have explicit true and false branches.

Wildcard/default arms are not part of the current subset. This keeps the impact of adding a union variant mechanically observable instead of hiding it behind catch-all behavior.

## AI-facing APIs

The graph is authoritative, but agents should not normally be forced to reconstruct semantic relations by traversing raw storage.

Prefer focused semantic queries and typed semantic operations for ordinary work. Raw snapshot or Nyason mutation is reserved for controlled migration, recovery, fixtures, codec testing, and low-level debugging.

This keeps the representation explicit without turning every agent task into a graph-analysis exercise.

The exact breadth of the typed operation surface should evolve with evidence. A narrow surface is justified by semantic distinctions or by a deliberate experimental boundary, not by a standing assumption that less authority is always safer.

## Review rule for future features

Before adding a new semantic construct or agent-facing operation, ask:

1. Can an agent classify it from the construct plus direct references?
2. Are all semantic roles represented structurally rather than inferred from naming or position?
3. Does it create a second source of truth?
4. Can an invalid partial edit be detected precisely?
5. Can it be targeted and changed without unrelated graph churn?
6. Does it require stable identity, or is identity only adding graph noise?
7. Is any implicit behavior present only for surface-language convenience?
8. Is a narrow operation protecting a real semantic boundary, or only repeating a previous milestone pattern?
9. Is a broader operation introducing a genuinely new research hypothesis that deserves milestone or experimental validation?

If the answer exposes unnecessary inference or duplicate authority, redesign before optimizing syntax size or implementation convenience. If it exposes only inherited ceremony, prefer removing that ceremony rather than treating precedent as a constraint.
