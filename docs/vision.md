# Vision

## Mission

Nayamnyam exists to make program understanding and program change safer for AI agents while remaining inspectable and governable by humans.

It is not primarily a textual programming language with AI assistance added around it. It is an AI-native semantic programming system whose authoritative form is a persistent typed semantic graph. AI agents interact with that graph through constrained queries and validated semantic operations. Humans primarily inspect structure, consequences, unresolved decisions, and execution behavior through graph-oriented views.

## Problem statement

Conventional source code is optimized for human text entry and line-oriented tools. Its semantics must repeatedly be reconstructed from names, positions, syntax trees, symbol tables, type information, and control-flow analysis.

This creates recurring failure modes for AI-assisted development:

- a single conceptual change becomes many independent text edits;
- symbol identity is confused with spelling or location;
- references are missed when code moves or names change;
- partially applied changes leave the repository in an invalid state;
- agents guess implementations where a human decision is still required;
- review focuses on textual churn rather than semantic consequences;
- large amounts of context are consumed rediscovering facts the compiler already knows.

Nayamnyam addresses these problems by making semantic identity, relations, constraints, changes, and unresolved work explicit parts of the program model.

## Priority order

Design decisions should be evaluated in this order:

1. **Semantic safety of change** — meaning-level edits must not silently produce incomplete or contradictory program states.
2. **Unambiguous machine interpretation** — entities, relations, operation inputs, and validation results must be structurally explicit.
3. **Persistent identity** — declarations, parameters, fields, variants, bindings, and relevant nodes must survive renaming, movement, and re-projection without identity reconstruction.
4. **Queryable structure** — an agent should retrieve the smallest meaningful slice needed for a task instead of rereading the entire repository.
5. **Composable operations** — small validated operations should combine into larger changes with explicit conflict handling.
6. **Human understanding and supervision** — a person must be able to understand system structure, proposed changes, impact, and unresolved obligations.
7. **Direct human authoring convenience** — useful when available, but not a primary constraint on the core representation.

## Roles

### AI agents

AI agents are expected to:

- query definitions, references, dependencies, effects, and change impact;
- propose operation plans rather than raw text patches;
- simulate transactions before application;
- resolve obligations when sufficient intent exists;
- explain plans and semantic diffs to humans;
- avoid inventing behavior when the graph exposes an unresolved decision.

AI agents are not trusted to mutate the authoritative graph directly or to bypass validation.

### Humans

Humans are expected to:

- express goals and constraints;
- inspect focused graph views rather than reconstructing the whole program from source files;
- review semantic diffs and impact summaries;
- decide ambiguous policy or domain behavior;
- approve, reject, or revise transactions;
- inspect local logic at a lower level when necessary.

Humans may eventually perform constrained semantic operations through the viewer, but visual programming and direct graph construction are not initial requirements.

### The system

The Nayamnyam runtime and toolchain are responsible for:

- maintaining stable identity;
- enforcing graph integrity;
- validating types, effects, references, and control-flow constraints;
- separating executable completeness from explicitly represented incompleteness;
- applying operations atomically;
- generating semantic diffs and obligations;
- producing deterministic projections and backend artifacts.

## Foundational principles

### Meaning first

The program is the semantic graph. Text, diagrams, generated backend code, and reports are projections.

### Identity is not spelling

A name is mutable presentation metadata. A stable semantic ID is the identity of an entity.

### Operations, not patches

A change request names the semantic target and intended transformation. The system computes and validates the concrete graph update.

### Explicit incompleteness

Missing domain decisions are represented as typed obligations. They are not hidden as malformed graph state, compiler recovery, comments, or guessed code.

### Transactional change

A graph-integrity violation never partially applies. A transaction either commits a structurally valid new snapshot or leaves the previous snapshot untouched.

### Graph-first human understanding

The primary human interface presents focused semantic relationships, execution flow, effects, change impact, and obligations. It does not treat a full code listing as the default representation.

### Backend independence

Nayamnyam semantics are defined independently of C#, Rust, Kotlin, or any other host language. Backend limitations are reported during lowering rather than silently becoming core-language semantics.

### Conservative failure

Ambiguous, unsupported, unresolved, or unsafe behavior should produce structured diagnostics or obligations instead of permissive lowering.

## Human presentation

The viewer should optimize for the question a human is asking, not for displaying the entire graph at once.

Expected view families include:

- system and subsystem structure;
- focused function and data flow;
- type and variant relationships;
- effect and capability dependencies;
- transaction impact and semantic diff;
- unresolved obligations;
- local detailed logic as a secondary textual or structured view.

A giant force-directed graph is not the product goal. Graph slicing, stable layout, progressive disclosure, and relation filtering are central requirements.

## Non-goals

Nayamnyam is not initially intended to be:

- a conventional text-first language optimized for manual typing;
- a macro system or DSL embedded inside C# or another host language;
- a general-purpose visual node editor where humans wire every operation manually;
- a source-to-source formatter whose main artifact is generated C#;
- a permissive system that accepts unresolved semantics and hopes backend compilation catches them;
- a replacement for Git history, though it may provide richer semantic change records;
- a multi-backend implementation from the first milestone;
- a complete solution for ownership, distributed execution, asynchronous effects, or dynamic runtime loading in the first milestone.

## Success conditions

Nayamnyam is succeeding when the following are true:

1. An agent can make common repository-wide changes without locating and editing every textual occurrence.
2. Renaming or moving an entity does not require reconstructing its identity.
3. A failed operation cannot leave a partially mutated authoritative program.
4. Unresolved decisions are visible, typed, attributable, and block execution when necessary.
5. A human can understand the purpose and impact of a change without reading all generated code.
6. The same validated semantic graph produces deterministic execution artifacts.
7. Backend-specific concerns remain isolated behind explicit lowering boundaries.
8. The query API reduces unnecessary repository-wide context loading without hiding relevant impact.
9. The system can explain why a transaction is accepted, rejected, or incomplete.
10. The design remains usable by an AI agent without becoming opaque to a human reviewer.

## Relationship to previous work

The previous RAST project explored explicit AST-oriented syntax, conservative semantic checking, effect annotations, Result/Option handling, package resolution, backend-neutral IR, and Kotlin/C# generation.

Nayamnyam inherits lessons and selected semantic rules from that work, but changes the center of gravity:

```text
RAST:
text -> syntax AST -> semantic checking -> resolved IR -> backend

Nayamnyam:
queries and semantic operations -> persistent semantic graph -> validation -> execution IR -> backend
```

RAST is therefore a reference implementation and migration source, not the foundation of Nayamnyam's runtime architecture.
