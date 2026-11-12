# Threaded state: program state as a value through the program's own loop

- **Status:** Accepted (owner decision 2026-11-12; measured 2026-11-12). The revisit conditions
  below were reworded after acceptance, later the same day, on the owner's clarification that
  they are signs to judge by rather than measurements to demand; the conditions themselves are
  unchanged.
- **Date:** 2026-11-12
- **Builds on:** [host handles as capability values](capability-handles.md), [Zig exports that keep state between calls](zig-stateful-exports.md)

A program that runs for a long time — an event loop, a session, a watch — needs state that changes
as it goes. Nyrwvy values are immutable and the language has no mutable place, so the question is
whether that is a real lack or only a habit from other languages. A scoped mutable binding (a
lexically bound variable that is not a value) is written down, not built, in
[scoped mutability](scoped-mutability.md), and is not adopted here; this record decides the form to use now, on measurements rather than on taste.

## Recommendation

**Threading is the official form for in-process state.** A long-lived program carries its state as
an immutable value through the arguments and return of its own loop — in practice one exported
entry that builds the initial state and tail-calls a step function per event, as in
`examples/threaded-state-loop`. The loop is the program's own, effects enter through capabilities
as usual, and the state never crosses the library boundary, so none of the ABI limits on recursive
or handle values apply to it. Mutable places are not added this time; the conditions that would
reopen the question are at the end of this record.

## The proof

`examples/threaded-state-loop` threads one record holding a turn counter, a scalar counter, a
64-element list, a 64-key map, a nested record, a depth-6 binary tree (64 leaves) and a two-variant
union. Each event code updates exactly one place (code 0 updates none). The harness wraps the value
allocator in a counting allocator and measures each code at 100,000 and 1,000,000 turns, plus the
runtime's recording-arena capacity, the process RSS, and leaks after the export returns. (The
recording arena — `ny_arena_capacity()` — is the arena the runtime keeps for its own records, not
a per-export work arena; the arena a host declaration works in is created and freed per call, read
from the emitter's source, and the RSS column is what covers growth at the process level.)
Results (2026-11-12):

| code | update | allocations / turn (before the emitter fix) | allocations / turn (after the emitter fix) |
| --- | --- | --- | --- |
| 0 | none | 0 | 0 |
| 1 | scalar field | 0 | 0 |
| 2 | list element | 1 | 0 |
| 3 | map value | 1 | 0 |
| 4 | nested record field | 1 | 1 |
| 5 | tree leaf | 7 | 7 |
| 6 | union variant payload | 1 | 1 |

Every code runs a million turns to completion — the evidence that the tail call is a loop, since a
stack-consuming call would not survive a million turns — and the result is the initial checksum
plus the turn count (code 0: the initial checksum). The recording arena reports 0 at both scales,
and nothing leaks after the export returns. `step` also passes saved cases under `runPure` on both
backends, including a probe of the one place each code changes. Peak live bytes are identical at
the two scales within every run — memory is flat in the turn count, for every shape — but the
values themselves are not constants: across the runs measured they vary from 6,312 to 6,984 bytes
(6,984 for every code in some runs, lower and differing by code in others), so no peak value is
pinned here. RSS likewise stays in a 2,736–2,872 kB band at both scales; the first reading of a
run can sit about 128 kB below the rest (pages not yet touched), so the harness allows 1 MiB of
slack on RSS rather than equality — far above that noise, far below what retaining anything per
turn would add over 900,000 extra turns. The "after" column was re-measured with the fix in place
(`c0206086`); the "before" column is kept as the history that motivated it (see Costs by shape).

## Costs by shape

- A scalar field of the threaded record updates **in place**: the turn that changes only the
  counter allocates exactly as much as the turn that changes nothing (zero).
- A list or map directly under the record now updates **in place** in this shape, and the harness
  asserts codes 2 and 3 equal code 0. Before the emitter fix (`c0206086`, PR #660) it was
  **copied whole** on each update: one allocation per turn at 64 elements, with the copied bytes
  growing with the element count, so the cost was proportional to the collection's size, not
  constant. (The proof could not show that growth — at 64 elements the copy was one allocation
  whatever its size — so the proportionality is a property of what a copy is, not a slope measured
  here.) The follow-up investigation found the copy, before the fix, under every natural writing it tried — six
  forms, including reading the field once and reading and updating inside one branch (those
  variants were tried outside the repository; this tree keeps no evidence for them). One writing
  avoided it (bind every other field out, then rebuild the record whole), but it depended on the
  emitter's internal ordering, so it was not adopted as the way to write programs. The cause, as
  read from the emitter, was that each reference to the argument became a separate alias, so the
  move-out treatment (`moveOutUpdatedFields`) could not pair a field's read with its update. The
  fix unifies repeated argument reads within a block and joins blocks entered by a simple jump,
  which lets the pairing succeed. The copy is still what happens when a branch or a merge
  separates the read from the update; the nested-record, tree and union costs below are unchanged by
  the fix.
- A nested record's field costs **one** allocation per turn: the inner box is copied (the read
  makes its reference count two, as the reference-counting notes describe).
- A union variant's payload costs **one** allocation per turn: matching the value and writing it
  back as the other variant allocates a box for the new variant.
- A tree leaf costs **the path length plus one** (seven boxes at depth 6): the root-to-leaf path is
  rebuilt, everything off the path is shared.

## Where threading does not reach

- **Host-driven libraries.** Threading needs the program to own its loop. A library the host drives
  by calling several exports cannot thread state through a loop it does not have. The substitute
  that is built is the host carrying the state between calls as a value handle
  ([Zig exports that keep state between calls](zig-stateful-exports.md), slice 3), at one top-box
  copy per call. The owner has since decided (2026-11-12) that a host-held handle does not count
  as the state being written in Nyrwvy; the designed form for this case is
  [entities](entities.md) (designed, not built), which moves the state's residence into the
  session. That step was opened by the decision, not by the fourth revisit condition below
  being met.
- **Several tasks sharing one state.** Two loops cannot thread the same value and both keep their
  changes; merging is the program's own problem, and a program that truly needs shared changing
  state has no form yet.
- **State that must outlive the process** is not this record's subject: it belongs to the host
  library's file primitives, with the procedure written in the graph (the model-cooldown port in
  `examples/model-cooldown` is the worked example; it lands together with this record, as part of
  the same state work).

## Revisit conditions

Mutable places come back onto the table when a real program runs into any of these. The
conditions are signs to judge by, not gates to pass: a concrete need in a real program is
reason enough to reopen the question, and measuring the harm first is not required —
measurement is the strongest form of the evidence, not a prerequisite for it. (Owner
clarification, 2026-11-12: the first wording of this section read as if measurement were
required; that was stronger than the intent.)

- a copy this record describes (the table, or a field read separated from its update by a branch) became a real harm in a real program (in the program itself, as
  opposed to this proof);
- threading state through parameters and returns became a real harm in a real program (the state
  plumbing obscuring the program that carries it);
- a program needs several tasks sharing one changing state;
- value-handle threading proves insufficient for a host-driven library.

The design to start from, for state inside one invocation, is
[scoped mutability](scoped-mutability.md); for state across invocations,
[entities](entities.md).
