# Documentation conventions

**Status:** Current

NyamNyam documentation is organized by semantic purpose. A document should answer one primary kind of question, make its authority clear, and avoid competing silently with another representation of the same decision or contract.

The shared model is intentionally compatible with the corresponding FuwaFuwa documentation structure.

## Purpose-oriented taxonomy

| Category | Primary question |
| --- | --- |
| `concepts/` | Why does this exist, and how should I think about it? |
| `guides/` | How do I accomplish a task? |
| `reference/` | What is the exact current behavior or contract surface? |
| `developer/` | How is the implementation organized and maintained? |
| `decisions/` | Why was this direction chosen? |
| `specs/` | What must the implementation satisfy? |
| `plans/` | How is a change intended to be implemented or evaluated? |
| `verification/` | What evidence shows that requirements were satisfied? |
| `experiments/` | What was explored or measured without becoming a current contract? |
| `docs-archive/` | What described a previous, superseded, or frozen historical state? |

Do not create a category merely for symmetry. Create it when maintained content actually needs that semantic home.

The central invariant is:

> **Decision != Spec != Plan != Verification**

A decision explains why. A spec constrains what must be true. A plan describes intended future work. A verification record captures evidence about completed work. One document should not drift among these roles without an explicit reason.

## Status vocabulary

Design-oriented documents should state their status near the top when the status is not obvious from the category.

Use these shared terms where they fit:

- **Current** — maintained description of current direction or behavior.
- **Experimental** — actively explored; not a current contract.
- **Proposed** — intended direction awaiting acceptance or evidence.
- **Historical** — records a past state and is not current authority.
- **Superseded** — replaced by a newer current source.
- **Procedure** — maintained operational instructions.
- **Observed result** — evidence or measurement, not a normative rule by itself.

Decision entries may additionally use statuses such as `Accepted`, `Proposed`, or `Superseded`; open questions use `Open`, `Partially resolved`, `Deferred`, or `Resolved` as appropriate.

## Authority and conflicts

Authority depends on the question being asked:

| Question | Preferred current authority |
| --- | --- |
| What must the implementation satisfy? | Accepted `specs/` plus applicable accepted decisions. |
| What does the implementation currently expose or support? | `reference/`, executable tests, and implementation evidence. |
| Why was the rule chosen? | `decisions/`. |
| What is the high-level system direction? | `concepts/` and `developer/architecture.md`, constrained by accepted decisions/specs. |
| What are we intending to change next? | `plans/`. |
| What happened in one completed run or implementation slice? | `verification/`. |
| What might be worth trying? | `experiments/`. |
| What did an older version say? | `docs-archive/`. |

Historical prose never overrides maintained current sources.

If two current sources disagree, do not resolve the conflict by category name alone. Determine whether the conflict is an implementation bug, a stale reference, or a changed/underspecified normative rule. When the intended authority is clear, update the stale side. When it is genuinely ambiguous, create an issue rather than silently guessing a new contract.

## Decision form

Use this shape for durable accepted design choices:

```md
## D-XXX — Decision title

- **Status:** Accepted
- **Date:** YYYY-MM-DD

### Decision
...

### Reason
...

### Consequences
...

### Revisit conditions
...
```

A later decision may refine or supersede part of an earlier decision. Record that relationship explicitly instead of rewriting history invisibly.

## Open-question form

Use bounded questions rather than free-form TODO prose:

```md
## Q-XXX — Question

- **Status:** Open

### Question
...

### Decision criteria
...

### Resolution target
...
```

When partially resolved, add the accepted current position and state exactly what remains open. When resolved, add a `Resolution` section or move the durable answer into a decision/spec/reference and keep a concise resolution pointer.

## Current documents versus dated records

A date-stamped design note may be useful during implementation, but once it defines durable current behavior it should normally be distilled into a stable maintained document under the appropriate category. The dated note may remain in verification, experiments, or the archive when its chronology/evidence is still useful.

Do not make readers compare several dated milestone documents to reconstruct the current contract.

## Archive policy

Historical material remains reachable but must not compete silently with current maintained documentation.

- Archive entry points must say clearly that archived material is non-authoritative for current behavior.
- When an archive becomes crowded, group clearly inactive material one level deeper by coherent event, phase, or time period rather than allowing a flat directory to grow indefinitely.
- Ordinary documentation reorganization does not delete historical records by default.
- Deleting archived material is a separate maintenance action and requires explicit confirmation.
- A historical document may intentionally disagree with current code or docs; that is acceptable when the archive warning and current replacement are clear.

The frozen pre-Issue-30 documentation snapshot is kept under [`../../docs-archive/2026-08-pre-reorg/`](../../docs-archive/2026-08-pre-reorg/) so its original relative links and chronology remain intact.

## Entry points

- Root `README.md` is a concise project landing page, not a changelog or verification ledger.
- `docs/README.md` is the neutral documentation portal.
- `docs/developer/README.md` is the contributor/reviewer entry point.
- Add a user-facing entry point only when there is enough stable user workflow documentation to justify one.

Entry points should direct readers to authoritative material rather than becoming exhaustive inventories of every file.
