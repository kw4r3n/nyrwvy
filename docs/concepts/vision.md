# Vision

**Status:** Current

## Mission

Nyrwvy exists to make program understanding and program change safer and more effective for AI agents while remaining inspectable and governable by humans.

It is not primarily a textual programming language with AI assistance added around it. Its authoritative form is a persistent typed semantic graph. Agents interact with that graph through semantic queries and validated semantic operations; humans review structure, consequences, unresolved decisions, and execution behavior through semantic projections.

## Problem statement

Conventional source code is optimized for human text entry and line-oriented tools. AI-assisted development must repeatedly reconstruct identity and relationships from spelling, locations, syntax, symbol tables, type information, and control flow.

That reconstruction creates recurring failure modes:

- one conceptual change becomes many independent text edits;
- symbol identity is confused with spelling or location;
- references are missed when declarations move or names change;
- partially applied edits can leave invalid states;
- agents guess behavior where a real decision is still missing;
- review measures textual churn instead of semantic consequence;
- context is spent rediscovering facts the compiler or graph already knows.

Nyrwvy makes semantic identity, relationships, constraints, changes, and unresolved work explicit parts of the program model.

## Priority order

Evaluate design choices in this order:

1. **Semantic integrity of change** — meaning-level edits must not silently produce contradictory or partially applied states.
2. **Unambiguous machine interpretation** — semantic roles, targets, relations, and validation results should be structurally explicit.
3. **Useful agent capability and composability** — once meaning is explicit and validation is preserved, the system should make meaningful work straightforward rather than minimizing authority for its own sake.
4. **Persistent identity** — semantic objects retain identity across rename, movement, reordering, and projection changes.
5. **Queryable structure** — an agent retrieves the smallest meaningful slice needed for a task without hiding relevant impact.
6. **Human understanding and supervision** — a person can understand structure, proposed changes, impact, and unresolved obligations.
7. **Direct human authoring convenience** — useful when available, but not a leading constraint on the core representation.

This priority order is about the semantic model, not a rule that narrower agent authority is always better. Current bounded operations and protocols are implementation strategies whose value must be judged against Nyrwvy's own goals and evidence.

## Foundational principles

### Meaning first

The semantic graph is the program. Text, diagrams, generated backend code, reports, and storage encodings are projections or representations of that meaning.

### Identity is not spelling

Names are mutable presentation metadata. Stable semantic IDs carry identity.

### Operations, not patches

Ordinary changes target semantic objects and request typed transformations. The system owns propagation, validation, and publication.

The operation surface may be narrow or broad depending on the semantic role. A dedicated bounded operation is useful when it clarifies authority or enables an experiment, but it is not automatically preferable to a more general typed operation once that generalization is understood and validated.

### Explicit incompleteness

A missing domain decision is represented explicitly as an obligation when a safe incomplete state exists. It is not hidden as malformed state, a guessed default, or a comment.

### Transactional change

A graph-integrity failure never partially mutates the authoritative program. Publication occurs at an immutable snapshot boundary.

### Graph-first human understanding

Human-facing views emphasize relationships, flow, effects, change impact, and obligations rather than treating a complete source listing as the default representation.

### Backend independence

Core semantics are defined independently of C#, Rust, Kotlin, or another backend language. Backend limitations are surfaced downstream instead of silently becoming language rules.

### No invented semantics

Missing, ambiguous, unsupported, or unresolved meaning must not be silently repaired merely to keep processing moving. Behavior that violates an explicit semantic contract or authority boundary must still reject. Use structured diagnostics or explicit obligations when appropriate.

This is not a blanket instruction to reject capable operations or minimize agent authority. When semantics are explicit, validated, and reviewable, enabling useful work is preferable to additional restriction that does not protect a concrete Nyrwvy invariant.

## Design authority and provisional mechanisms

Nyrwvy distinguishes three kinds of design material so that implementation history and evaluation machinery do not become accidental core principles.

### Core Nyrwvy principles

These define the project unless deliberately reconsidered:

- the semantic graph is authoritative;
- semantic identity is explicit and persistent;
- ordinary changes are typed, validated semantic operations rather than raw representation patches;
- simulation/review/publication preserve the distinction between requested intent and resulting meaning;
- publication is atomic at an immutable snapshot boundary;
- backend and presentation representations do not redefine program semantics.

### Provisional implementation strategies

These are useful current techniques, not permanent requirements:

- one dedicated operation or agent protocol per semantic role;
- very small closed structural recipes;
- current proposal-handle/session lifecycle details;
- current OpenCode adapter shape;
- current persistence and serialization choices;
- other restrictions introduced primarily to establish an initial experimental boundary.

A strategy should be retained while it provides clarity or evidence. It should not survive merely because an earlier milestone used it.

### Evaluation and research infrastructure

Controlled corpora, frozen conditions, protocol revisions, orchestrators, model-run metrics, and historical replay support exist to produce trustworthy evidence. They are downstream consumers of Nyrwvy's design.

Evaluation can reveal a weakness and motivate a design change, but incidental behavior of an evaluation harness or frozen historical protocol does not become semantic authority. Compatibility needed only to preserve historical evidence belongs in the evaluation layer.

Practices from RAST or other projects may inspire Nyrwvy, but they require a Nyrwvy-specific justification. Prior use elsewhere is evidence to consider, not authority.

## Evidence already earned

The completed controlled agent evaluations are important evidence, not a template that every later feature must repeat.

M1 and M2 provide evidence that, under the tested conditions, agents can successfully work with several central interaction patterns:

- stable semantic-ID targeting;
- semantic inspection followed by simulate -> review -> commit;
- system-owned production identity allocation;
- structured diagnostics for invalid or stale requests;
- stale reviewed-proposal rejection followed by fresh inspection and recovery;
- bounded structural authoring and higher-order references such as the specialized ListMap slice;
- exact committed-state verification with zero unrelated semantic churn in the recorded conditions.

That evidence can be reused while its assumptions remain materially unchanged. Adding another semantic role that follows the same established interaction model does not by itself require another large controlled model tranche.

A new experimental evaluation is justified when Nyrwvy introduces a materially new hypothesis: for example substantially broader structural authoring authority, a different interaction model, new conflict/merge semantics, or another change that could invalidate the evidence above.

See the [verification philosophy](../developer/verification.md) for how routine, milestone, and experimental validation differ.

## Roles

### AI agents

Agents are expected to query semantic facts, propose typed operations, simulate changes, inspect review results, and avoid inventing decisions that remain explicitly unresolved. Ordinary agents do not receive direct mutable access to authoritative graph state.

The exact breadth of the typed operation surface is an evolving design question rather than a requirement that every role remain separately bounded forever.

### Humans

Humans express goals and constraints, inspect focused semantic views, review requested intent versus resulting change, and resolve policy or domain ambiguity. Direct graph construction and visual programming are not initial requirements.

### The system

The Nyrwvy toolchain maintains stable identity, validates the graph, applies operations atomically, tracks obligations, produces deterministic projections, and lowers executable snapshots through backend-neutral boundaries.

## Non-goals

Nyrwvy is not initially intended to be:

- a conventional text-first language optimized for manual typing;
- a macro or annotation layer embedded in a host language;
- a general-purpose visual node editor where humans wire every operation manually;
- a source-to-source system whose generated C# becomes editable upstream source;
- a permissive system that relies on backend compilation to discover unresolved semantics;
- a complete ownership, distributed-execution, asynchronous-effect, or dynamic-loading design from the first milestones.

## Success conditions

Nyrwvy is succeeding when:

1. agents can make common cross-cutting changes without locating every textual occurrence;
2. rename or movement does not require identity reconstruction;
3. failed changes cannot leave partially mutated authoritative state;
4. unresolved decisions are visible, typed, attributable, and block the appropriate stage;
5. humans can understand change intent and consequence without reading all generated code;
6. the same validated semantic state produces deterministic downstream artifacts;
7. backend-specific concerns remain isolated behind explicit lowering boundaries;
8. focused queries reduce unnecessary whole-repository context without hiding relevant impact;
9. acceptance, rejection, and incompleteness can be explained from structured facts;
10. the system can expand agent capability without turning historical experimental restrictions into permanent architecture by default.

## Relationship to previous work

Nyrwvy is informed by earlier RAST experiments in explicit semantics, conservative validation, effects, `Result`/`Option`, and backend-neutral lowering, but RAST is not a runtime, semantic, or design-authority dependency.

Conceptual ancestry does not require special provenance metadata. When concrete material is directly copied or substantially adapted, use ordinary source attribution. See the [RAST provenance decision](../decisions/rast-provenance.md).
