# Entities: program state with an address in the session

- **Status:** **Designed, not built.** Owner decisions 2026-11-12 recorded
  in *Context*: a form that leaves program state as an opaque handle in
  the host does not count as the state being written in Nyrwvy, so the
  residence of cross-invocation state belongs to the session; and the
  revisit conditions in [threaded state](threaded-state.md) are signs to
  judge by, not gates that require a measurement first. The first step
  is the minimal form in *The first step*; nothing here is implemented
  or scheduled. Revised after two external design reviews on 2026-11-12.
  Amended later the same day, after a review of the committed record:
  the C# refusal narrowed to selections that reach a cell, the
  kind-classification sites named, `runPure` kept to one function per
  case, the rhifill facts corrected to the prototype that exists, and
  the costs restated as expectations with their measurement conditions.
- **Date:** 2026-11-12
- **Builds on:** [threaded state](threaded-state.md), [scoped mutability](scoped-mutability.md), [Zig exports that keep state between calls](zig-stateful-exports.md), [host handles as capability values](capability-handles.md), [effectful expression ordering](effectful-expression-ordering.md)

## Context

The owner's direction is that everything should be writable in Nyrwvy,
including state and the execution of effects, with immutable and mutable
places kept explicitly apart rather than immutability treated as
absolute (see [scoped mutability](scoped-mutability.md), *Context*). For
state that changes over time, two forms are already settled:

- **Threading.** The program owns its loop and carries its state as an
  immutable value through the loop's arguments and returns. Accepted
  and measured in [threaded state](threaded-state.md).
- **Value handles.** The host carries the state between calls as an
  opaque `ny_value` and hands it back in
  ([Zig exports that keep state between calls](zig-stateful-exports.md),
  slice 3, built 2026-11-11). The state's contents and operations can
  all be written in Nyrwvy this way, but its residence is a pointer the
  host holds.

The fork between the handle form and this record was put to the owner,
and the owner decided (2026-11-12): leaving the state as an opaque
pointer in the host is too untidy to count as the state being written
in Nyrwvy. **An entity is therefore defined as a named place, owned by
the library session, that holds one value across invocations, which
several entry points can touch without the value crossing the boundary
as a parameter or a handle.** Entities move the pointer's residence
from the host into the session; they are not a new way to compute with
the value.

The demand, stated as plainly as the scoped-mutability record states
its own:

- **Rhifill's pane tree is the concrete instance.** Rhifill is a
  host-driven library: the host calls separate exports per key event,
  and one pane tree per tab is split, closed and re-ratioed across
  those calls. The tree cannot be threaded (the program does not own
  the loop), and under the owner's decision it should not reside in
  the host either. Stated precisely, the instance is prospective:
  rhifill's workspace today has no `PaneTree` type and no tree
  functions at all (the Nyrwvy tree rhifill once had was deleted in
  2026-11), and the running tree is the host's own Zig prototype
  (`spikes/native-pane/src/panes.zig`), which its probe record says
  does not claim to migrate Nyr pane logic. The tree is not passed
  across export calls today; the host operates on its own value.
  Moving it means first writing (or restoring) the pure tree
  functions in Nyrwvy, checked against the prototype's ten tests;
  the cell is where those functions' results would live.
- **Rrawrlet is not an instance.** Graph invocation there is one
  process per call, so nothing in-process survives to a next call; its
  cross-request state is files and file locks, which is the host
  library's durable-state primitives, not this record.
- **No program yet needs several tasks sharing one changing state**
  (threaded state's condition (iii)). That part of the design below —
  refusing cell access in tasks and workers — is written against an
  anticipated need, and is recorded as anticipation. The owner has
  clarified (2026-11-12) that the revisit conditions are signs, not
  measurement gates, so anticipation recorded as such does not block
  the design.

This design is written ahead of demand, and the record says so
plainly: no program is blocked on it today — the pane tree runs as
host code, and threading and value handles cover the programs that
exist. It was designed now because the owner's fork decision above
settles what the construct must be, and designing while the demand
instance is small keeps the design honest. The posture is the one
[Zig exports that keep state between calls](zig-stateful-exports.md)
took with "built ahead of trigger", one notch earlier: designed
ahead of trigger.

This record is also where the entities design that
[scoped mutability](scoped-mutability.md) anticipated now lives; that
record's Status carries both the earlier decision to design entities
first and a later same-day line that no separate entities design was
planned, and the owner's decision to take the design up (2026-11-12)
is what this record follows.

## The design

### Declaration

A cell is a new snapshot declaration kind, alongside capabilities and
functions. A declaration carries an id, a display name, one fixed
value type, an initializer and one `EffectId`.

- The type has no type parameters. A type containing a capability
  handle, a `Task`, a `OneShot`, a `TaskScope`, a function value or a
  captured exception is refused: such a value cannot be stored,
  shared, reset or seeded meaningfully.
- Cells are not published outside the library in the first step; no
  host and no other library can name one. Revisit if a real program
  needs to.
- **One cell per declaration.** Several instances are a value-level
  concern: the cell holds a `Map<Key, T>`, and key minting stays where
  it is today — the host mints Rhifill's tab and pane ids, and a
  program that wants to mint its own keeps a counter in a cell. There
  is no instance machinery in the language, so the questions of
  minting, lifetime and missing keys are the existing Map questions.

### Access: three nodes, not a capability

Three nodes name the declaration by id: `read`, `set`, `update`. A
cell is not a value: it cannot be passed, stored, returned or aliased.
Because the name is a declaration id and nothing else, no aliasing is
possible, and none of the reference machinery sketched for scoped vars
(may-read / may-write summaries) is needed here.

Access is not a capability, for three reasons. Capability operations
cannot use type parameters, so no generic cell operation can be
declared. A capability's implementation is supplied by the host per
export, and there is no ambient path that delivers one to a library
function. And the host binding names Zig declarations for operations;
there is no path for a host operation to call back into a graph
function, so `update(f)` cannot be implemented as an operation.

- `read` returns the current value as a shared snapshot. The value is
  immutable, so sharing needs no copy; what it costs if the snapshot
  is held across an update is in *Costs by shape*.
- `set` replaces the whole value.
- `update(f)` applies a pure function `f` to the cell's value and
  stores what `f` returns. Arguments reach `f` the way existing
  callbacks receive theirs — bound as context arguments at the call
  site; function values capture nothing, so the operation's arguments
  (a pane id, a ratio) are passed that way. The runtime takes the
  value out of the cell — the cell is empty while `f` runs — so `f`
  receives it with reference count one, and field updates inside `f`
  are positioned to reuse the boxes they change, as in threading's
  in-place rows, under the conditions *Costs by shape* states. The
  empty interval is not observable: `f` is pure and cannot wait, so
  the export lock (see *Failure and waiting*) is held throughout it,
  and `f` cannot touch any cell (cell access is effectful), so
  re-entry into the same cell cannot occur either. Nor can `f` spawn
  workers inside the interval: `ParallelAll` is itself an
  intrinsically effectful kind, so it cannot appear in a pure
  function at all.
- `update`'s callback does not return a `Result` in the first step.
  A consumed argument is written in place, so by the time a callback
  could return `Err` the old value no longer exists to put back;
  restoring it would require the runtime to hold a second reference
  throughout, and every update would copy the top box. A fallible
  change is instead written in the check-then-commit shape: `read`
  the value, run the pure operation (`PaneTree ->
  Result<PaneTree, E>`), and on `Ok` `set` the value it produced;
  on `Err` write nothing and return the `Err`. A `Result`-returning
  `update` can be added later at the copy cost above, if a program
  wants it.
- The check in that shape runs against a `read` snapshot, at
  reference count two: a check that builds a new tree is expected to
  pay the threaded copy rows for what it builds, and discards it on
  `Err`.
  A hot path therefore splits the shape differently: the check is a
  predicate that builds nothing, and the change itself runs inside
  `update`, whose callback sees reference count one. `setRatio` in
  the worked example is written that way; split and close use
  check-then-commit.

### Effects and ordering

A cell access is an inherently effectful node and joins the existing
all-pairs order check unchanged (`AmbiguousEffectOrder`); no new check
is added. Two unordered accesses to a cell are refused, and the author
sequences them with `Bind`, as for capability calls. The check is over
observable nodes as a class, not over one cell, and the record states
its actual reach: two unordered `read`s of *different* cells are
refused, a `read` unordered beside a capability call is refused, and
two calls to cell-reading functions inside one record construction
are refused. The declaration's
`EffectId` must be declared by any function that touches the cell,
diagnosed by the existing `MissingEffectRequirement`; the granularity
is the author's choice, as for capability operations.

Touching a cell is a call-transitive fact, as `MayWait` itself is:
a function touches a cell when it contains an access node, or calls
— directly, or through a callback in any role — a function that
touches one. Every rule in this record that speaks of a function
touching, reading or writing a cell uses the transitive fact: the
effect requirement above, the worker and task refusal below, the
may-wait write refusal in *Failure and waiting*, and the emission
diagnostic for buffer-copied results. `update`'s callback is held
to the pure-callback role: it cannot touch a cell at all, which the
purity check enforces.

A function that touches a cell is not pure — including not
observationally pure. A scoped var's writes are invisible outside its
invocation, which is what its observational purity rests on; a cell's
contents are observable by other invocations, which is the point of
the construct. That basis does not carry here, as the scoped-mutability
record itself notes.

One weakness is accepted: the all-pairs check does not distinguish
reads, so two unordered `read`s of one cell are refused too — the same
inconvenience capability calls already have. If a real program keeps
tripping on it, add an exemption for read–read pairs; not before.

Access inside `ParallelAll` workers and tasks is refused by
validation in the first step. Ordering between them is undefined, and
no program needs shared cell access yet (see *Context*).

### Failure and waiting

Writes take effect immediately and are visible to later accesses and
later invocations. There is no invocation-level transaction, by
deliberate choice: capability effects cannot be rolled back (a spawned
pane cannot be unspawned), so rolling back only the cell would make
the cell disagree with the world. The discipline is ordering instead —
compute, perform effects, `set` last — which the worked example uses.
Validation-shaped failures are handled before the write, by the
check-then-commit pattern in *Access*.

Two re-entry hazards shape what may be written where. Library exports
are serialized by one mutex, but the mutex is released around an
operation that may wait, and an export whose result is copied into a
caller buffer is re-run by the host when the buffer is too small
(status 3).

- **Buffer-copied results.** If an export the wrapper can return
  status 3 for — one whose result, in any part, the generated
  wrapper marks `buffered` (the emitter's `abiResult`): a string,
  bytes, a numeric list or a row, including one nested inside an
  `Option`, a flattened record field, or the `Ok` side of a
  status-4 `Result` — also writes a cell, the
  status-3 re-run applies the write twice. Such an export must
  therefore not write a cell. This is a fact about the ABI, not
  about the graph, so it is diagnosed where library exports are
  emitted, not by graph validation.
- **Waits.** While an export waits, another thread's export can run.
  A `read`, a wait, then a `set` can overwrite an update that
  interleaved, losing it. A `set` or `update` inside a function that
  may wait is therefore refused by validation. `ParallelAll` and
  `TaskScope` themselves count as may-wait, so a function that
  computes with them cannot write a cell under this rule; that
  consequence is accepted. Neither releases the lock itself — no
  host operation is involved — so this part of the refusal is a
  deliberate over-approximation, and the first candidate to relax
  if it bites in a real program. An `update` itself completes under the
  lock once started — its callback cannot wait — so narrowing the
  refusal (for example, allowing an `update` that follows a wait in
  the same function) is a possible later refinement, not the first
  step's rule.

A cell is never left holding a half-updated value — and in the
first step it cannot be left empty either. The take-out interval
has no failure exit: `Abort` is `Result` control flow, and
`update`'s callback returns no `Result`; the directly failing node
kinds are all inherently effectful, so a pure callback contains
none of them; and the failures that remain (out of memory, stack
overflow, safety panics) abort the process, leaving no session to
observe a state. The owner's rule for an empty cell is therefore
recorded for the step that first creates such a path — a fallible
callback, or the C# backend, whose pure code can throw: **the cell
is poisoned** (owner decision 2026-11-12). The failing call has
released the taken value, and from then on every access — `read`,
`set` and `update` alike — fails, and the initializer is not
re-evaluated, until the reset entry returns the cell to
uninitialized. Recovery is the reset entry followed by whatever
restore exports the program provides; reset is session-wide, so
one poisoned cell costs every cell's contents. That step must also
define the access failure's type and how the host detects it: the
failure is catchable inside the graph, so loudness toward the host
is not automatic. The first step implements and tests none of this.

### Lifetime, initialization, reset

In this record a *session* is the lifetime of one loaded library,
from the load that creates its cell storage to the release that ends
it — not an authoring session, and not a host's session file. A
`runPure` case runs in a fresh process (Zig) or a fresh load context
(C#), so there a session spans exactly one case.

The initializer is a nullary pure function, evaluated lazily on first
access. A `set` on an uninitialized cell stores its value without
evaluating the initializer, and seeding a test case does the same.
A cell lives from the library's load to its release; its contents
are the session's state.

The Zig library's surface today has no release or reset entry (it
exports `ny_release`, `ny_open_handles`, `ny_last_failure` and
`ny_arena_capacity`). The first step adds one library-level entry,
named `ny_reset_cells` here, returning nothing. It returns every
cell in the session to **uninitialized**, not to its initializer's
value: reset must not evaluate initializers, because
a live value held by a cell is counted by the leak checks (the
`runPure` runner fails a run that ends with values still live), and
an uninitialized cell holds nothing. The next access re-evaluates the
initializer lazily, as on first access. The entry doubles as the
teardown a host runs before unloading the library or ending the
process — "release" in this record means that teardown; there is
no other release entry. It takes the export lock, so it cannot land
in the middle of an export's non-waiting parts. Used as that
teardown, it carries one precondition: **no export is in flight.** A
waiting export that read a cell again after a reset would re-run the
initializer and could return results computed from post-reset state
as if nothing had happened, and could leave values in cells the
teardown meant to empty. Serializing the reset against whole exports
is therefore the host's obligation when it uses the entry this way,
not a property the entry provides on its own. (An earlier draft of
this record accepted cell reads straddling a reset; that acceptance
is withdrawn.) Reset covers every cell in the session, including
cells declared by libraries the session's libraries depend on.

Cells give up one property of value handles: that nothing global
exists in the library. On the Zig backend a cell is process state, so
two instances of one library in one process share its cells. What an
instance means on the C# backend is defined with that backend's step.
Accepted for the first step; instance separation is a later question,
if a real program needs two independent copies in one process.

### Persistence and snapshot evolution

Cells are in-process only. Durable state stays with the host library's
file primitives. Saving a cell is an ordinary export that reads it and
writes it out; restoring is an export that sets it. On the Nyrwvy
side, Rhifill's session JSON round-trips this way with no new
machinery. On Rhifill's side the session file's format does change:
today it stores the prototype tree's internal node array as it
stands, inside each tab record, and a tree that lives in a cell is
written out through an export in whatever shape the program declares.

A cell exists only inside one process, so a snapshot change never
meets a live cell: there is no migration rule. What a new declaration
kind does meet is the snapshot format itself; that version cost is
listed in *The first step*.

### Tests

`runPure` cases stay one function and its arguments, as today. A
case that needs several steps against one session is written as one
test-only function that sequences the operations with `Bind`, which
is what cross-invocation state means inside one case. A case then
needs only two additions: per cell, optional initial contents keyed
by declaration id (not by name — names do not decide identity) as
input, and final contents in the reply. The alternative — a case as
a list of exports called in sequence — was not taken: it changes
the request shape for every client of saved cases (the fixture
parity client and `verifyChange`'s observation ride on the same
request), and on the Zig route each invocation already runs in its
own process, so a sequence inside one case would need a process
model the route does not have. On the Zig route each run is already
a fresh process, so starting from the initializer needs nothing new;
the seeding and read-back are a change to the request and reply
format and to the generated runner, and saved-case files
(client-side JSON) keep the new fields optional so old cases still
load. Read-back reports a never-accessed, never-seeded cell as
uninitialized, and does not evaluate its initializer to do so. The
generated runner calls the reset entry after read-back, on the
failure path as well as the success path, so a case never ends with
cell values still live; the runner's uncaught-failure path
(`nyUncaught` in `runner_support.zig`) calls its leak check
directly, so it resets before that check, and the host library's
own checks (`libraries/host/check/run-zig.mjs`), which assert no
leaks after each export, reset before those assertions for a
library that has cells. Most logic stays testable as today:
operations on the value are pure functions
(`PaneTree -> Result<PaneTree, E>`), and only thin exports touch
the cell.

### Costs by shape

These are expectations, stated so the first step's acceptance tests
can measure them — not measurements.

- An `update` on a scalar field, a list element or a map value
  directly under the cell's value: reference count one inside the
  callback, so threading's in-place rows are the expectation (zero
  allocations per turn). Zero was *measured*, in the threaded-state
  proof, for one shape only: the read and the write in one block, no
  branch or merge between them, and a scalar field. That shape is
  the expectation's condition, not a general property of `update`.
- Deeper shapes follow threading's other rows, not the zero row. The
  pane tree is a tree stored as a map value, so a nested field costs
  one box and a tree leaf costs the path length plus one; `setRatio`
  is expected to allocate per motion event. The acceptance tests
  measure it rather than assume it.
- `setRatio`'s callback is not the zero shape either: it finds the
  node and branches on the node's kind between reading and writing,
  which is the branch-separated shape the emitter's own note says
  keeps the copies. Its expectation is the container rows — the
  map-value row for the tab's tree, then the list-element row for
  the node — and test 11 measures the whole export against them.
- A `read` snapshot held across an `update`: reference count two, so
  the top box copies, and a list whose element is set copies as well
  — the borrowed-argument cost recorded for value handles. Programs
  that read, compute and update in one step use `update` and do not
  pay it.

## Worked example: Rhifill's pane tree

Today the tree lives in the host's Zig prototype: a fixed array of
at most 15 nodes for at most 8 panes (`capacity = 8`), addressed by
node index. Split builds the new tree value, spawns the pane, and
commits by assigning the produced value (`work.tree = tree`) only
when the spawn succeeds — a copy of the fixed-array struct, not a
pointer replacement. Tab and pane ids are host-minted; the session
file stores the tree's internal array as it stands, inside each tab
record.

With entities, one cell holds `Map<TabId, PaneTree>`, where
`PaneTree` mirrors the prototype's shape — a node list of at most 15
entries and a root index — and the tree operations are pure functions
written for this, checked against the prototype's ten tests; they do
not exist in Nyrwvy today (see *Context*). `setRatio` takes a node
index and a per-mille ratio, as the prototype's does
(`setRatio(t, index, ratio)`). In the array shape the index is stable
while the node lives; had the tree been a recursive record instead,
the same operation would be addressed by a path, and a path is only
as stable as the tree's shape — one reason the example keeps the
prototype's array shape. How a split is written depends on
whether the spawn operation may wait, because a function that may
wait cannot write (see *Failure and waiting*):

- If the spawn cannot wait, one export does the whole flow: `read`
  the tree, check the split with the pure function, perform the
  spawn, and `set` on success. On failure it returns `Err` before any
  write; the cell is untouched. The commit-by-replacement discipline
  survives, but the residence is the session.
- If the spawn may wait — or while the effect still lives outside
  the graph — the flow is two exports, and no candidate tree crosses
  the boundary. A read-only propose export returns only the scalars
  the spawn needs. The commit export takes the operation's arguments
  (the pane id, the direction, the new pane's id), `read`s the tree
  and re-runs the pure split: on `Ok` it `set`s the new tree; on
  `Err` it writes nothing and returns the `Err`, and the host,
  seeing it, tears down the pane it spawned. The split computation
  runs twice; in exchange there is no handle to release, and a split
  an interleaved event has invalidated fails openly instead of
  committing a stale candidate. An earlier draft of this example
  passed the candidate itself across, and a design review showed
  that value is a slice-3 handle, with exactly those failure modes.

Which of the two shapes Rhifill takes is decided when Rhifill's
spawn is written as a capability: by the spawn operation's wait
attribute, and by whether the effect still lives outside the graph
while it runs; it is not known today. In both shapes the generated
`Pty` handle stays with the host — a capability handle is one of the
refused cell types, so no shape puts the handle itself in the cell;
the cell holds the tree and the tab data only.

`setRatio` during a divider drag is a predicate check and an
`update` per motion event; its allocation count is the measurement
in the acceptance tests below.

## Where this record overtakes others

This step makes statements in other records stop holding, and they
are named here rather than edited there; the concept and spec
documents are not rewritten by this record.

- [Semantic independence](../concepts/semantic-independence.md)'s
  table says changing shared state is possible only through a
  capability operation, so its effect is declared and visible. The
  substance survives cells — a cell's effect is declared (the
  declaration's `EffectId`) and visible (`MissingEffectRequirement`)
  — but the letter does not: a cell access is not a capability
  operation. When cells land, that row's wording is wrong, and this
  record is the notice.
- The same document lists "runtime state identity is not
  distinguished from definition identity" as a deferred gap. Cells
  take a position on it: a cell's identity is its declaration, and
  its runtime state is exactly one place per process (see *What a
  later step would have to rebuild*).
- Its dedicated-construct table asks of each construct how it
  expands into existing meaning, or why it is dedicated. Cells do
  not expand into passing host-held value handles in and out — the
  owner considered that form and refused it (*Context*). The
  construct is dedicated because the residence is the point.
- [Zig exports that keep state between calls](zig-stateful-exports.md):
  its Demand section and triggers describe a rhifill that stays on
  C# and might one day need its pane tree in Nyr; both are overtaken
  (its Status now carries a dated note). Its out-of-scope line — two
  Nyr libraries in one process — stands, and is the boundary of this
  record's sharing rule: cells are shared by the libraries of one
  library graph in one process; nothing here puts two independent
  libraries into one process. Its value handles remain the built
  substitute, not a form this record adopts.

## What a later step would have to rebuild

Most later steps relax a refusal and rebuild nothing. The exception
is identity. In this design a cell is one place per process, its
identity is its declaration id, and the reset entry takes no
argument identifying a session or an instance. If a later step wants
several independent sessions or library instances in one process,
every export gains a context it does not have today and the library
ABI changes; the singleton is not a default that can be widened in
place. The C# backend's meaning for a cell is likewise deferred
whole to its step, not sketched here.

## The first step

Zig backend only. One cell per declaration; the `read`, `set` and
`update` nodes; validation refusals for cell access in workers and
tasks and for writes in `MayWait` functions; the emission diagnostic
for buffer-copied results; `runPure` seeding and read-back; the
library reset entry. The surface this touches, beyond the nodes
themselves:

- the snapshot schema rises (24 to 25) for the new declaration kind
  and node kinds, with `RecordCodec` and `SnapshotCodec` on the F#
  side and the snapshot-reader and model-reader on the Zig side, and
  the IR and emitter mappings;
- the two refusals are new transitive per-function facts with new
  diagnostic codes, in both validations (F# and the Zig
  model-reader) — the order check itself is unchanged, which is all
  "no new check" ever covered. The diagnostic set also gains the
  declaration checks (the refused types; an initializer that is
  nullary, pure and of the declared type), the typing of the three
  nodes, the purity of `update`'s callback, and references to a
  deleted cell. The new diagnostics are named here so the sections
  agree: `InvalidCellDeclaration` for the declaration checks,
  `UnknownCell` for a node or a test seed naming a declaration that
  does not exist, and `CellInCallback` for an `update` callback that
  touches a cell. The ordering and effect-requirement refusals reuse
  `AmbiguousEffectOrder` and `MissingEffectRequirement`;
- the classification of the three new node kinds as intrinsically
  effectful, in every place kinds are classified:
  `SemanticNodeShape.isIntrinsicallyEffectful` (`Foundation.fs`),
  used by `GraphOrder` and `PureFunctions`;
  `ExecutionIrAnalysis.orderingDescriptor`; and the Zig model
  reader's `effectful_kinds`. The `update` callback's role joins the
  role plumbing those places already have
  (`FunctionReferenceRole.isInvoked`, `GraphOrder.carriesCalleeEffects`,
  the reader's role branch), held to the pure-callback role.
  `MissingEffectRequirement` is checked per node kind and per call
  shape, so each of the three kinds needs its section. On the spec
  side, `body-effect-requirements`' evidence sources gain a cell
  declaration as a third source kind, and `runtime-ordering`'s
  account of observable operations gains cell accesses. The order
  algorithm itself is unchanged; the classification lists are the
  surface;
- the authoring surface gains cell creation, renaming, deletion and
  retyping, and recipes for the three nodes — today it creates only
  types and functions;
- no emitter work: the duplicate-read unification
  (`mergeRepeatedReads`, PR #660) targets parameter and
  pattern-binding reads only, and the block joining preserves
  instruction order, so a cell `read` is never unified or moved by
  the emitter as it stands. That is recorded as an invariant for
  future emitter work to preserve, not as a task in this step;
- the reset-before-leak-check calls in the generated runner's
  uncaught-failure path and in the host library's own checks (see
  *Tests*);
- the C# route refuses cells at the granularity of a selection, not
  of a snapshot: a `runPure` case, a `verifyChange` observation or
  an emitted output is refused, with a diagnostic naming the cell,
  when a function it selects or emits — or one reachable from it —
  touches a cell, until the C# step lands. A workspace that declares
  cells but selects only functions that never reach one runs on C#
  exactly as before: the workspace's default target is C#, and
  `runPure` already selects by reachability from its entry. The
  clients whose selection the refusal rides on are named here for
  that reason: `runPure` and `verifyChange` in the authoring
  session, and the saved-fixture parity client
  (`examples/run-fixtures/client.mjs`), which sends each fixture
  once per backend and compares.

The C# backend follows later — its tasks run truly in parallel, so
`update` needs a lock there — and is deferred deliberately, not by
oversight.

Acceptance tests:

1. Two calls to exports in one process: the second call's `read` sees
   the first call's `update`.
2. The check-then-commit pattern: a pure operation run against a
   `read` value returns `Err`, nothing is written, and the cell is
   unchanged; on `Ok`, the produced value is `set`.
3. A failure after an `update` in the same export leaves the update
   visible (no rollback).
4. Two unordered accesses are refused with `AmbiguousEffectOrder`; a
   function touching a cell is refused as a pure fold's callback, and
   as an effectful fold's callback without the cell's effect declared
   it is diagnosed with `MissingEffectRequirement`.
5. Access inside a `ParallelAll` worker or a task, and a write inside
   a function that may wait, are refused by validation. An export
   returning a buffer-copied result that writes a cell is refused at
   library emission.
6. A million `update` turns: memory flat, and scalar, list-element and
   map-value updates allocate zero, measured the way
   [threaded state](threaded-state.md) measures. A second loop whose
   callback finds its target by a search that branches, then writes,
   is counted separately, against the branch-separated expectation
   in *Costs by shape*.
7. A value `read` before an `update` still shows the old contents
   afterwards.
8. `runPure` seeds a cell by declaration id and reads back the final
   contents; with no seed given, each run starts from the
   initializer. A multi-step case is one test-only function
   sequencing the operations with `Bind`.
9. After the reset entry runs, the cell is uninitialized: the next
   access re-evaluates the initializer, the runner's live-value check
   counts nothing for the cell, and the open-handle count is zero.
10. The pure pane-tree functions written for the worked example pass
    the operation sequences of the prototype's ten tests
    (`spikes/native-pane/src/panes.zig`) — splits, closes, ratios
    and picks, including failure cases leaving the tree unchanged;
    carried on a cell, the same sequences pass through thin exports.
11. `setRatio` driven 100,000 times as a whole export (predicate
    check plus `update`): memory flat; the tree afterwards is
    asserted equal to the pure function's result for the same
    sequence; and the allocations per call are counted and recorded
    (expected: the container rows of *Costs by shape*, not zero).
12. An `update` performed while a `read` snapshot is held: the
    snapshot is asserted to still show the old contents, and the
    allocations are counted and recorded (expected: the top box and
    any list whose element is set).
13. F#/Zig agreement: a snapshot with two unordered cell accesses is
    refused by both validators with the same diagnostic, and one
    whose `update` callback touches a cell is refused by both. The
    existing consistency tests tie F# `GraphOrder` to F# analysis
    and lowering only and contain no Zig, so F#/Zig agreement is a
    new criterion here — as the scoped-mutability draft already
    required of its own check.
14. `update`'s callback touching a cell — its own or another — is
    refused with `CellInCallback`; a callback that calls a function
    touching a cell is refused the same way.
15. `set` on an uninitialized cell stores the value without
    evaluating the initializer: with an initializer whose value is
    a large list, the case allocates nothing of that shape, and the
    read-back returns the set value.
16. Read-back of a cell no access has touched reports it
    uninitialized and does not evaluate its initializer (the same
    large-list observation as 15).
17. The C# refusal and its scope: in a workspace declaring a cell, a
    `runPure` case whose entry never reaches a cell-touching
    function runs on the C# backend; a case whose entry touches a
    cell is refused with the diagnostic.

## Not in this step

- The C# backend.
- Keyed instance families as a language feature (a `Map` value is the
  form).
- Transactions.
- Invariants spanning several cells: they cannot be expressed. Things
  that change together belong in one cell — guidance, not a check.
- Scoped vars: unchanged — designed in
  [scoped mutability](scoped-mutability.md), built only if a program
  needs them.
- Persistence of cells (durable state is the host library's file
  primitives).
- The poisoned state: specified in *Failure and waiting* for the
  step that first gives a cell a failure path; not implemented in
  this step.
