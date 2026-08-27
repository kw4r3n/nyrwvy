# Vision

**Status:** Current

## Mission

NyamNyam exists to make program understanding and program change safer for AI agents while remaining inspectable and governable by humans.

It is not primarily a textual programming language with AI assistance added around it. Its authoritative form is a persistent typed semantic graph. Agents interact with that graph through bounded semantic queries and validated semantic operations; humans review structure, consequences, unresolved decisions, and execution behavior through semantic projections.

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

NyamNyam makes semantic identity, relationships, constraints, changes, and unresolved work explicit parts of the program model.

## Priority order

Evaluate design choices in this order:

1. **Semantic safety of change** — meaning-level edits must not silently produce incomplete or contradictory states.
2. **Unambiguous machine interpretation** — semantic roles, targets, relations, and validation results should be structurally explicit.
3. **Persistent identity** — semantic objects retain identity across rename, movement, reordering, and projection changes.
4. **Queryable structure** — an agent retrieves the smallest meaningful slice needed for a task.
5. **Composable operations** — small validated transformations combine with explicit conflict and review behavior.
6. **Human understanding and supervision** — a person can understand structure, proposed changes, impact, and unresolved obligations.
7. **Direct human authoring convenience** — useful when available, but not a leading constraint on the core representation.

## Foundational principles

### Meaning first

The semantic graph is the program. Text, diagrams, generated backend code, reports, and storage encodings are projections or representations of that meaning.

### Identity is not spelling

Names are mutable presentation metadata. Stable semantic IDs carry identity.

### Operations, not patches

Ordinary changes target semantic objects and request typed transformations. The system owns propagation, validation, and publication.

### Explicit incompleteness

A missing domain decision is represented explicitly as an obligation when a safe incomplete state exists. It is not hidden as malformed state, a guessed default, or a comment.

### Transactional change

A graph-integrity failure never partially mutates the authoritative program. Publication occurs at an immutable snapshot boundary.

### Graph-first human understanding

Human-facing views emphasize relationships, flow, effects, change impact, and obligations rather than treating a complete source listing as the default representation.

### Backend independence

Core semantics are defined independently of C#, Rust, Kotlin, or another backend language. Backend limitations are surfaced downstream instead of silently becoming language rules.

### Conservative failure

Ambiguous, unsupported, unresolved, or unsafe behavior should become a typed diagnostic or explicit obligation rather than permissive inference.

## Roles

### AI agents

Agents are expected to query semantic facts, propose typed operations, simulate changes, inspect review results, and avoid inventing decisions that remain explicitly unresolved. Ordinary agents do not receive direct mutable access to authoritative graph state.

### Humans

Humans express goals and constraints, inspect focused semantic views, review requested intent versus resulting change, and resolve policy or domain ambiguity. Direct graph construction and visual programming are not initial requirements.

### The system

The NyamNyam toolchain maintains stable identity, validates the graph, applies operations atomically, tracks obligations, produces deterministic projections, and lowers executable snapshots through backend-neutral boundaries.

## Non-goals

NyamNyam is not initially intended to be:

- a conventional text-first language optimized for manual typing;
- a macro or annotation layer embedded in a host language;
- a general-purpose visual node editor where humans wire every operation manually;
- a source-to-source system whose generated C# becomes editable upstream source;
- a permissive system that relies on backend compilation to discover unresolved semantics;
- a complete ownership, distributed-execution, asynchronous-effect, or dynamic-loading design from the first milestones.

## Success conditions

NyamNyam is succeeding when:

1. agents can make common cross-cutting changes without locating every textual occurrence;
2. rename or movement does not require identity reconstruction;
3. failed changes cannot leave partially mutated authoritative state;
4. unresolved decisions are visible, typed, attributable, and block the appropriate stage;
5. humans can understand change intent and consequence without reading all generated code;
6. the same validated semantic state produces deterministic downstream artifacts;
7. backend-specific concerns remain isolated behind explicit lowering boundaries;
8. bounded queries reduce unnecessary whole-repository context without hiding relevant impact;
9. acceptance, rejection, and incompleteness can be explained from structured facts.

## Relationship to previous work

NyamNyam is informed by earlier RAST experiments in explicit semantics, conservative validation, effects, `Result`/`Option`, and backend-neutral lowering, but RAST is not a runtime or semantic dependency.

Conceptual ancestry does not require special provenance metadata. When concrete material is directly copied or substantially adapted, use ordinary source attribution. See the [RAST provenance decision](../decisions/rast-provenance.md).
