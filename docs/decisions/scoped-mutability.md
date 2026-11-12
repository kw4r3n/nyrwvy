# Scoped mutability: a mutable lexical binding (draft, revised)

- **Status:** **Written down, not built.** Owner decision 2026-11-12:
  cross-invocation state (the entities step) is designed first, and
  this record is kept as the design of a scoped var should one be
  needed; nothing here is implemented or scheduled. No program needs it
  today (see *Context*). Two open points were decided by the owner on
  2026-11-12 and stay recorded as decided: purity is observational, and
  callback capture is out of scope for this step (a revisit
  trigger). Revised after three external design reviews. Later the
  same day the owner accepted [threaded state](threaded-state.md): a
  program holds long-lived state by threading it through a loop it
  owns, and durable state uses host-library primitives. No separate
  entities design is planned now, and this record is the design to
  revisit under that record's revisit conditions. Later still the
  same day, the owner took the entities design up; it lives in
  [entities](entities.md), and where the two records differ —
  entities reuses the existing all-pairs order check and needs no
  reference machinery, because an entity is named by a declaration id
  and cannot alias — the entities record governs. Where the entities
  record has since superseded this draft's expectations, again in
  favour of the entities record: the binding discipline carries over
  only as a style — explicit access nodes and the diagnostics style
  — not as bindings (entities has declarations, not bindings); the
  reference mechanism sketched in *The step after this one* was not
  adopted (entities orders entity accesses with the existing all-pairs
  effect check, where this draft's sketch said it would use the
  reference mechanism); and the demand list in *Context* below (a
  terminal's pane tree and binding set, a chat layer's cross-request
  state, caches) is superseded by the entities record's demand
  section — the chat layer's cross-request state turned out to be
  files and locks, not entities.
- **Date:** 2026-11-12
- **Builds on:** [explicit ordering for effectful expression composition](effectful-expression-ordering.md) (whose revisit conditions list mutation, but only for a concrete use case, which this record does not have), [runtime ordering](../specs/runtime-ordering.md), [scoped one-shot handoff](../specs/scoped-one-shot.md), [concurrency control beyond ParallelAll](concurrency-control.md), [tail self-calls](tail-self-calls.md), [host handles as capability values](capability-handles.md), [memory ownership in the Zig backend](zig-backend-memory-ownership.md)

## Context

The owner has set the direction that everything should be writable in
Nyrwvy, including state and the execution of effects, and that the language
should separate immutable and mutable places explicitly instead of treating
immutability as absolute. The inventory behind that direction (taken
2026-11-12) found the absence of mutable state to be the language's largest
expressiveness gap — but its concrete instances (a terminal's pane tree and
binding set, a chat layer's cross-request state, caches) are state that
survives across calls. That is the entities step, not this one.

This step's standing is stated plainly, because the `Bind` decision's
revisit conditions admit mutation only for a concrete use case, and that
condition is not met:

- **No current graph program needs a scoped var.** The graph programs
  that exist are small and decision-shaped (the largest Rhifill graph
  function has 122 nodes, all decision trees); none of them threads
  accumulators, and no concrete example of the construct's use exists
  today.
- **Accumulation over data is not this construct's territory.** A var
  cannot cross a callback or a tail call (see *Callbacks and tail calls*),
  so folds and loops keep carrying accumulators as parameters, exactly as
  today. Vars do not compete with that form (see the cost in *Runtime
  representation*).
- **What this record is for** is to write down, on the smallest surface
  that exhibits them, answers to the questions a mutable place raises:
  what it *is*, how its writes are ordered, how its presence classifies
  a function's purity, and what structural edits may drop. Only part of
  that carries to the entities step: the binding discipline (a mutable
  place is a distinct binding kind, written by an explicit node) and the
  diagnostics style. The per-binding ordering check and observational
  purity do **not** carry over — both rest on the var having exactly one
  name and one observer, which entity state does not have (see *The
  step after this one*).
- The shape the construct would serve is a **fixed sequence of steps
  within one invocation, where several locals are updated conditionally
  and later steps read earlier results**. No example of that shape exists
  in the current programs. (Rhifill's divider dragging in `panes.zig`
  was considered and does not qualify: its locals are straight-line
  constants, and its one write, setting the ratio, is to the pane tree —
  state that survives the call, which is the entities side.)

Terminology: this draft says **var**, not *cell*. *Cell* is `OneShot`'s
word for a value other tasks can observe; a var here has exactly one
observer, its owning scope.

In the language proper, the only scoped mutable state today is `OneShot<T>`:
a single-assignment cell owned by a `TaskScope`, built for cross-task
handoff. (Host handles also carry lifecycle state — an open file is open or
closed — but that is host state behind a capability, not language state.)

## Decision (proposed)

### The construct

Add one scoped binding construct in the `Bind` family:

```text
BindVar(initial, body)
```

- `initial` is a node ID in the enclosing graph, like `Bind`'s input,
  and is evaluated once, in the enclosing scope, before the body.
- The body is a nested graph (as in `Bind`) with one additional lexical
  binding, the *var binding*, whose type is the type of `initial`. The type
  is fixed at introduction; there is no subtyping or widening.
- Referencing the var binding reads it, with **snapshot semantics**: the
  reference is a node, and it evaluates to the value the var holds at the
  read node's own position — where the graph that owns the node
  schedules it — not where its result is used. A value read into another
  binding is unaffected by later writes. (In a counted backend, a read
  retains the value; a reference is never an alias for the slot.) This
  matches the existing rule that a `Bind` reference reuses an evaluated
  result. Two consequences follow from node placement: a read node owned
  by the `BindVar` body but used only inside a nested `Bind` body, after
  an `Assign` in that nested body, sees the value from before the write;
  and an `Assign` node owned by an outer graph but referenced from inside
  a match arm runs whether or not that arm is selected.
- A new node, `Assign`, writes it. `Assign` carries the target as a
  **binding reference**, *not* as a value operand — so an `Assign` never
  itself performs a read. In recipes the reference is the lexical
  depth/slot form bindings already use, or, on the wire, the name form
  (`{ "kind": "patternReference", "name" }` for reads, and the same
  `name` for an `Assign` target), resolved like every pattern reference
  to the **innermost binder with that name**; `BindVar` takes a `name`
  as `bind` does and joins the binder frames that resolution and the
  `bindingScopes` view count. In the persisted format the target is the
  binding's identity, in the same style as a pattern binding's identity.
  Its one value operand is the new value; the operand's type must match
  the binding's type exactly. `Assign` returns `Unit`.
- **Containment.** Every read of the var and every `Assign` to it is a
  node inside the `BindVar` body's subtree (the body graph or a graph
  nested in it). The name and depth/slot forms cannot express anything
  else, but the persisted form carries a binding identity, which can
  name a binding out of scope. For reads the existing lexical-scope
  check rejects that (`UnknownPatternReference`, in `Validation.fs`
  and in the Zig model reader). The `Assign` target is a new field
  that no existing check covers, so the same scope rule and the same
  diagnostic would be extended to it (see *Validation*).
- The construct returns the body's result. The var binding itself is not a
  value: it cannot be returned, stored in a declared type, passed as an
  argument or callback context, sent across a capability boundary, or appear
  in a run value. Its final value leaves the scope only if the body reads it
  and returns the read, like any other value.
- `BindVar` consumes one lexical depth level for its body, exactly as
  `Bind` does; references and `Assign` targets inside the body count it.
  Production binding IDs remain system-owned.

Because the binding is lexical and not a value, escape is impossible by
construction rather than by an ownership check — the same reason `Bind`'s
binding needs no lifetime rules. On scope exit — normal, `Err`, typed
abort, or host exception — the var simply ceases to exist. Nothing outside
the scope can observe it, so there is no rollback question and no
language-level release operation. (The Zig backend's ownership obligations
on exit are an implementation matter; see *Runtime representation*.)

### Ordering: a per-binding check, separate from effect order

Var accesses are **not** part of the `GraphOrder` observability machinery,
and this section defines the check that governs them instead. The reason is
mechanical: observability is derived per function, and an intrinsically
observable node makes its function observable and propagates ordering
obligations to callers and to `ParallelAll` workers. Classifying `Assign`
or a var read as observable would therefore make every var-using function
observable — contradicting the purity decision below — and the
all-observable-pairs rule would also reject read/read pairs and accesses
to different bindings, which this design intends to allow.

The var check is lexical and per binding. Within the body of one `BindVar`,
for its binding *b*:

- **Access summaries.** Every node has a may-read / may-write summary for
  *b*, computed bottom-up over **every nested-scope kind**. That set is
  closed: `nestedScopes` in `Foundation.fs` is the exhaustive list of
  nested-graph node kinds — `Conditional` (both arms), `BoolAnd` and
  `BoolOr` (right operand), `MatchUnion` (each arm), `Bind` (body),
  `MatchOption` and `MatchResult` (both arms) — plus the proposed
  `BindVar` body. A binding reference contributes a read, an `Assign` a
  write. A node's summary covers its own access, if any, and the
  accesses of the nodes **owned by** its nested graphs, at any depth. It
  does not include its operands, a `Bind`'s input, a `BindVar`'s
  `initial`, a control node's scrutinee, or outer nodes its nested
  graphs reference: each of those is its own node in some graph, already
  ordered against this one by dependency, and counting it again would
  report conflicts between accesses that are in fact ordered. A control
  node therefore summarizes as the union of what its arms own. A call,
  callback, or body of another function contributes nothing: those are
  separate functions and cannot reference *b* at all.
- **Conflict rule.** Evaluation order between two subexpressions is what
  the body's dependency edges and scope nesting fix — never node-list order
  or IDs. Two accesses to *b* conflict if no such order relates them and at
  least one is a write. Read/read pairs never conflict; accesses to
  different bindings never conflict. Accesses in mutually exclusive arms of
  one control node are never compared with each other; a later access is
  ordered against the control node as a whole through its summary, so a
  read after a match whose arms each assign (each `Assign` owned by its
  arm's graph) observes the selected arm's write.
- **Premises.** The check is sound only because of four facts about the
  current graph model, and it states them so a change to any of them
  reopens it:
  1. A node's dependencies include the outside nodes its nested graphs
     reference (`Foundation.fs` `dependencies`), so a node is ordered
     after everything its nested graphs read from outside.
  2. Lowering orders nodes by dependencies only; its tie-breaks among
     ready nodes (lowest stable ID first, a Bytes update deferred behind
     ready non-updates, the tail node last) never reorder dependent
     nodes (`ExecutionIrLoweringCore.fs`, the Kahn scheduler).
  3. Every nested-scope kind runs its graph at most once per evaluation
     of its node.
  4. No node owns two nested graphs that both run: every multi-graph
     kind's graphs are exclusive arms.

  Premises 3 and 4 are preconditions, not accidents: any future
  nested-scope kind — an iterating body, for example — must re-examine
  them before this check may cover it.
- **No cross-ordering with effects.** A var access and a capability call
  order against each other only through data dependency or `Bind` nesting,
  exactly as authored. Effects cannot observe the var, so no other relation
  is needed; an author who needs a call between two writes writes
  `Bind(call, _ -> Assign(...))`.
- **Where checked.** The effect-order check has one F# core,
  `GraphOrder.tryUnorderedEffects` (`src/Nyrwvy.Semantics/GraphOrder.fs`),
  called from validation (`Validation.fs`, via `tryEffectOrderConflict`),
  from `validateEffectOrder` in `ExecutionIrAnalysis.fs`, and from
  `lowerFunction` in `ExecutionIrLoweringCore.fs`; the Zig model reader
  has a separate reimplementation, `unorderedEffects`
  (`tools/zig/model-reader/main.zig`), called from its validation and
  its lowering. The var check takes the same shape: one F# core beside
  `tryUnorderedEffects` in the Semantics layer, called from those same
  three F# sites, and one Zig function beside `unorderedEffects`, called
  from the Zig validation and lowering. It runs in full validation and in
  proposal simulation, and lowering re-checks it. The existing
  consistency test (`EffectClassificationConsistencyTests.fs`) ties only
  F# `GraphOrder` to F# analysis and lowering and contains no Zig, so it
  does not cover F#/Zig agreement; that is a new acceptance criterion
  below. New diagnostic: `AmbiguousVarAccess`, naming the function, the
  binding, and both unordered nodes, and asking for `Bind` sequencing —
  the same message shape as `AmbiguousEffectOrder`.

Worked consequences: sequential updates are written as nested `Bind`s, as
effectful sequencing is today. A match whose two arms assign different
values to the var, followed by a continuation that reads it, is accepted:
the arms are exclusive, and the read is ordered after the match as a whole.

### Purity and declared effects (decided 2026-11-12, owner)

A var binding is *locally* ordered by the check above but does not affect
the enclosing function's purity classification or declared effects. Because
the var cannot escape and cannot be observed from outside, a function using
only vars is observationally pure: same inputs, same result, no external
effect. Declared effects are about the outside world; widening them for
confined mutation would misstate the contract and would bar a function
that uses vars internally from being passed as a pure callback (for
example a pure `ListFold` folder). That is about the folder's own vars;
capturing an outer var into a callback is a separate matter, out of
scope here (see *Callbacks and tail calls*).

Rejected: treating `Assign` like the `OneShot` operations (intrinsically
effectful for purity as well). It is more conservative, but it makes almost
every realistic use classify its function as impure, and the restriction
protects no observable property. `OneShot` differs in kind, not degree: its
cell is shared across tasks and observable by other parties, so its
effectfulness is real. A `BindVar` var has exactly one observer, the scope
that owns it.

Determinism and replay are therefore preserved: a `runPure` execution of a
function using vars is fully determined by its inputs and fake responses,
and the virtual clock is unaffected (var operations never wait;
`mustNotWait` analysis is unchanged, since var nodes carry no wait fact).

**Edit safety is a separate classification, at function granularity.**
The existing drop-style structural edits (`removeFunctionParameter`,
`removeUnionVariant`) are gated on "the function that would lose
evaluation declares no effects, so removal cannot silently drop
effectful evaluation". The function meant is the one containing the
affected nodes: for a variant removal, the function whose arm would be
dropped; for a parameter removal, each caller that would lose an
argument binding. That precondition gains a second clause for these
edits: *and that function contains no `Assign`*. An `Assign`'s `Unit`
result is normally unused, yet dropping it changes the function's
result, so a var write counts as non-droppable evaluation for editing
purposes even though it widens no declared effect. The refusal uses a
new failure code, `MayDiscardVarWrites`, named beside the existing
`*MayDiscardEffects` family without claiming var writes are effects.
Because the gate is per function, it over-refuses: an edit that drops
nodes unrelated to any `Assign` is refused too when the same function
contains one. That is an accepted cost, the same one the effect gate
already pays.

Targeted replacement is **not** gated. A replacement is an intentional,
reviewed edit, and the targeted-replacement decision already lets it
remove an old effectful computation that nothing else uses; a
replacement may discard an `Assign` the same way, and the review shows
it.

### Concurrency

A var binding belongs to one invocation in one task.

- It cannot be captured by a `TaskStart` worker or a `ParallelAll` worker:
  workers receive values as context, and the binding is not a value. This
  is automatic, not a new check.
- A `BindVar` inside a worker is that worker invocation's own var.
- Cross-task handoff of a changing value remains `OneShot`'s job in this
  step. Shared mutable vars between tasks are named in the out-of-scope
  list of the capability-handles decision and stay out of scope here.

### Callbacks and tail calls (decided 2026-11-12, owner)

- A var binding cannot be referenced from a callback body in this step.
  Callbacks are separate functions whose inputs are values; since the
  binding is not a value, there is nothing to pass. Accumulation across a
  fold keeps using the fold's accumulator parameter in this step, which
  also keeps count-of-one reuse for the accumulator (see *Runtime
  representation*).
  Capturing a var into same-invocation callbacks (notably an effectful
  `ListEffectFold` folder) is the natural follow-on: it is a
  revisit trigger below, not a silent permanent exclusion. Passing a var
  to a callback would need a new notion — an invocation-bound reference —
  and would widen the ordering and validation rules by a full step, which
  this step deliberately does not pay for before a concrete program needs
  it. One dependency is recorded now: with capture, an `ExceptionCatch`
  handler could observe a var's intermediate writes, so the capture step
  must specify what a handler sees; without capture the question cannot
  arise.
- Tail position: the tail-position enumeration — stated in both the
  language reference and the tail-self-calls decision — gains one clause:
  *if a `BindVar` is in tail position, its body is*, alongside the
  existing `Bind` clause. A tail self-call in the body's tail position
  therefore replaces the frame as usual, and the var's dynamic extent
  ends with that invocation, consistently: the new invocation sees only
  vars it introduces itself. Loop-carried state continues to be passed as
  call arguments in this step.

### Runtime representation

Semantics impose two requirements on any backend representation: reads
are snapshots (above), and a var's current value is released **exactly
once on every exit** from its scope — normal, `Err`, abort, host
exception, and the tail-jump. Fatal failures (allocation failure, stack
overflow) are excluded: they end the process, as for every other value
(`zig-backend-memory-ownership.md`). Subject to those, the
representation is an implementation choice, recorded in the
implementation report; this draft decides the requirements and the
expected shape, not the emitter's internals.

- Shared IR: the Execution IR is consumed by both the Zig backend and the
  C# backend (`tools/zig/csharp-emitter/root.zig`), and `runPure`
  compiles the program with one of them and runs it; it is not a
  separate evaluator. The var slot is therefore an IR matter both
  backends implement: a declaration in the function's frame, a load for
  each reference, a store for each `Assign`.
- C#: the slot is a local variable; release is not a concern. **Bytes is
  the exception** — reasoned from source, not run. The C# backend has no
  reference count; it decides in-place Bytes updates statically, in the
  backend-neutral Bytes ownership plan
  (`tools/zig/csharp-emitter/ownership.zig`), from the IR's use lists
  and alias sets, plus a per-invocation owner token at run time
  (`docs/plans/bytes-and-buffers.md`). An update is planned in place
  when no later use shares its input's alias set. If slot loads produce
  fresh value IDs not united with the slot's other loads, and the store
  is not counted as an escape, then in
  `Bind(v, old -> Bind(Assign(v, BytesSet(v, 0, 1)), _ -> old))`, with a
  Bytes created in this invocation, the second load has no later use,
  `BytesSet` is planned in place, the owner token matches, and `old`
  changes — breaking snapshot semantics. Requirement: the ownership plan
  treats a var store as an escape, or treats every load of one slot as
  one alias set. (The Zig backend is unaffected: `nyBytesSet` goes
  through `nyListSetAtOwned`, which writes in place only when the
  count is one.)
- Zig (expected shape): the slot as above. `Assign` evaluates its operand
  first (the operand may read the var), retains the new value, then
  releases the replaced one. The emitter's tail-jump cleanup, which today
  releases parameter values before resetting them, must cover live var
  slots the same way. An SSA encoding that meets the same two
  requirements is an acceptable alternative; compatibility with the
  undecided callback-capture step is explicitly **not** a reason to
  prefer either, since that step re-derives its own machinery (see the
  sketch below).
- **Cost, per operation.** While an `Assign` operand is evaluated, the
  slot holds one reference and the operand's read holds another, so the
  value being replaced is shared (count above one) in that position.
  What that costs depends on the operation (Zig runtime,
  `tools/zig/emitter/runtime.zig`):
  - Append (`nyListAppendOwned` → `nyExtend(T, list, 1)`) and
    concatenation (`nyListConcatOwned`, same path): a unique list is
    reused or grown; a *shared*, non-static list still extends in place
    when its last item is the storage's last used item and capacity
    remains. Count one is not required, so these usually stay cheap
    through a slot.
  - `ListSetAt` (`nyListSetAtOwned` → `nyExtend(T, list, 0)`): the
    shared in-place branch needs extra length, so a shared list is
    copied; a unique one is updated in place. `BytesSet` uses the same
    path.
  - Slicing (`nyListSliceOwned`): a shared list's prefix keeping at
    least half the used items is shared; any other range is copied; a
    unique list is trimmed in place.
  - `ProductUpdate`: a unique box is written in place; a shared one is
    copied (fields shared).
  - `SetInsert` and `MapInsert`: their item, key and value lists follow
    append (and `MapInsert` on an existing key follows `ListSetAt` for the
    value); a shared hash index, which sets and maps have above a small
    size, is rebuilt.

  So an `Assign` whose operand updates the var by index, product field,
  or hash insertion copies the value once per `Assign`. Since a var
  cannot cross a callback or a tail call, the only accumulation it can
  express is a bounded straight-line sequence in one invocation; the
  cost is one copy per such `Assign`, not a per-element loop cost. Loops
  and folds keep the accumulator-parameter form, which arrives count-of-one
  and keeps in-place reuse. Revisit only on a measured workload, per the
  stateful-exports precedent.

### Boundaries and persistence

- The var binding never appears as a type, so it adds nothing to the
  placement rules: what may appear in capability signatures, declared
  types, or run values is unchanged. The var's *contents* are ordinary
  values of an ordinary type and obey the existing rules for that type
  wherever the value itself flows (the initial expression, `Assign`
  operands, reads) — the same placement a `Bind` binding's value has.
- No var content is serialized. The construct is graph structure, so it
  persists like `Bind` does: the scope, binding identity, and input
  relationship must survive persistence, fingerprints, typed projection,
  semantic queries, change review, and execution lowering.
- Introducing the node kinds (`BindVar` and `Assign`) is a closed schema
  change and would take the next schema version under the schema
  evolution policy, following the `OneShot` precedent (constructs select
  the newer schema; old graphs keep their minimum versions).

### Validation (new diagnostics, existing families)

- `Assign` target resolves to a binding that is not a var binding — a
  `Bind` binding or a match-arm pattern binding: rejected,
  `AssignToImmutableBinding`. Assignability is a property of the binding
  kind. This includes a name-form target that a shadowing inner binder
  captures: an `Assign` to `x` inside a `Bind` named `x` that is itself
  inside a `BindVar` named `x` resolves to the inner, immutable binding.
- `Assign` operand type differs from the binding's type: rejected by the
  existing exact-type operand checks; no new diagnostic.
- Unordered conflicting accesses to one binding: rejected,
  `AmbiguousVarAccess` (see *Ordering*).
- A persisted var reference or `Assign` target whose binding is not in
  scope at that node: rejected, `UnknownPatternReference`, the existing
  lexical-scope diagnostic for pattern references (`Validation.fs`, Zig
  model reader); the check is extended to the new `Assign` target field.

## Acceptance criteria

Following the precedent shape of the task-scope and one-shot records,
implementation is accepted when all of these are observed:

- Read-after-write through nested `Bind`s observes the written value,
  with identical results from `runPure` on the C# backend and on the Zig
  backend.
- Snapshot semantics: `Bind(x, old -> Bind(Assign(x, Add(x, 1)), _ -> old))`
  yields the value from before the write, on both backends.
- Bytes snapshot: `Bind(v, old -> Bind(Assign(v, BytesSet(v, 0, 1)), _ ->
  old))`, with a Bytes value created in the same invocation, returns the
  unchanged bytes on both backends. (The integer case above passes
  whether or not the C# ownership plan sees the slot; this one does not.)
- A match whose arms each assign a different value, each `Assign` owned
  by its arm's graph, followed by a read, observes the selected arm's
  value; the unselected arm's write is never observed.
- Node placement, two cases: (a) a read node owned by the `BindVar` body,
  referenced only from inside a nested `Bind` body that first assigns the
  var, yields the value from before that write (the write is owned by
  the nested body, so the read is ordered before the `Bind` node and the
  case is accepted); (b) an `Assign` node owned by the body but
  referenced only from one arm of a match runs even when the other arm
  is selected.
- Nested scopes: an inner `BindVar` body reads and writes an outer var and
  its own var, with ordering composing across the nesting.
- Shadowing: an outer `BindVar` named `x` containing an inner `Bind`
  named `x`, with a name-form `Assign` to `x` inside the inner body, is
  rejected with `AssignToImmutableBinding`; the same recipe with the
  inner binder renamed is accepted.
- Scope: a persisted graph whose `Assign` target names a var binding
  from a `BindVar` that does not enclose the `Assign` is rejected with
  `UnknownPatternReference`, by both the F# and the Zig validation.
- Unordered write/read and write/write pairs are rejected with
  `AmbiguousVarAccess` at full validation and at proposal simulation;
  unordered read/read pairs and accesses to different bindings are
  accepted. Included cases: an `Assign` in a `BoolAnd` or `BoolOr` right
  operand, unordered with a read in the enclosing graph, is rejected,
  and the same pair sequenced by `Bind` is accepted; a `Bind` whose input
  is an `Assign`, used as the input of two unordered sibling `Bind`s of
  which only one body reads, is accepted (summaries count bodies, not
  inputs).
- The lowering re-check gives the same verdict as validation on every
  saved case.
- F#/Zig agreement: one set of saved cases, accepting and rejecting, runs
  through the F# check and the Zig check, with identical verdicts and
  identical diagnostic codes.
- A function using `BindVar` internally is accepted as a pure `ListFold`
  folder and as a `ParallelAll` worker, and its call sites gain no
  ordering obligations (the purity decision, observed). This covers the
  folder's own vars only; no case captures an outer var.
- A drop-style structural edit that would drop evaluation in a function
  containing an `Assign` is refused with `MayDiscardVarWrites`, whether
  or not the dropped nodes include the `Assign`; a targeted replacement
  that discards an `Assign` is accepted and its review shows the
  removed `Assign`.
- A self-call in a `BindVar` body tail position runs millions of turns
  without stack growth, and no var value leaks into the next turn.
- In the Zig backend, allocation accounting over `BindVar` graphs shows
  no leak and no double release across all exit kinds (normal, `Err`,
  abort, host exception, tail jump).
- A graph containing `BindVar` survives a persistence round trip with an
  identical fingerprint; graphs without `BindVar` retain their existing
  minimum schema versions, bytes, and fingerprints.
- A staged computation written with vars and the same computation written
  with `Bind` threading produce identical results on the same inputs.
  This is an equivalence check of the construct, not evidence of demand;
  the program that motivates building it (see *Revisit conditions*) is
  the case to use, once one exists.

## Open question (for the owner, not decided here)

Should var access nodes be forbidden from being referenced across
nested-graph boundaries? Today's rule (a read happens at the read node's
own position, an `Assign` runs where its owning graph schedules it) is
consistent and checkable, but the two node-placement cases above can
surprise a reader who expects a read to happen where it is used, or a
write to happen only in the arm that uses it. Forbidding such references
would remove both surprises at the cost of one more validation rule.

## The step after this one, sketched (not decided)

The next steps — an invocation-bound reference that callbacks can receive,
and long-lived entities whose state survives calls — **cannot reuse this
step's ordering check**, and this draft does not claim they can. The
lexical check is sound because two names for one var cannot exist:
references introduce aliasing, and aliasing is exactly what the check
assumes away.

The sketched shape for that step, recorded so the cost is visible now: an
access through a reference joins the observable-order domain. Functions
gain derived may-read / may-write summaries for their reference parameters;
two accesses through possibly-aliasing references with no order between
them are rejected by the existing unordered-pair search, as capability
calls are today. References provably fresh (created in one scope, never
merged with another) may be exempted by that freshness, keeping the common
case cheap. The sketch's known weakness is recorded with it: the
all-pairs rule it borrows is the one this draft rejected for vars, because
it over-rejects (read/read pairs, provably distinct references); the
freshness exemption is load-bearing, and if it does not hold up, that step
needs a per-reference check of its own. Whether the sketch survives its
own design pass is that step's question. What is decided here is only the
honest accounting: the ordering machinery of this step is lexical, the
reference step adds a second mechanism beside it, and the diagnostics
style and binding discipline — not the check itself — are what carry over.

## Non-goals for this step

- State that survives a call (long-lived entities, graph-owned objects).
- Vars as values: storing, returning, or keying by a var.
- Vars shared between tasks; any cross-task real-time ordering promise.
- Stateful or effectful program-defined capability implementations.
- A loop construct; iteration stays tail calls and folds.
- Persisting or serializing live var contents.

## Consequences

- The language would gain a mutable place that is visibly separated in
  the graph: a reader (human or agent) could see exactly which bindings
  are mutable, and review would show writes as explicit `Assign` nodes.
  Immutability would remain the default everywhere else.
- The semantic additions would be two node kinds (`BindVar` and
  `Assign`), the binding-kind distinction, one ordering check beside —
  not inside — `GraphOrder`, one tail-position clause, and the
  edit-safety precondition extension. No new type kind; the schema bump
  would be the routine closed change.

## Revisit conditions

- **Build trigger.** A concrete program needs a scoped var, or the
  entities design shows that it needs this binding as its base: then
  this record is revisited for building, starting from the open
  question above.
- A concrete program needs a var captured by a same-invocation callback:
  that is the capture step, designed against the sketch above, and it must
  specify what an `ExceptionCatch` handler observes.
- A concrete program needs state across calls: that is the entities step.
  It reuses this step's binding discipline and diagnostics style; its
  ordering check is the reference mechanism sketched above, not this
  step's lexical check.
- Use-after-scope or cross-task sharing pressure appears: handle through
  the shared-vars design, not by widening `BindVar`.
- If observational purity proves unsound in a corner — for example a
  diagnostic or a host-exception path that would expose var contents —
  revisit the purity decision; the fix should close the leak, not
  reclassify all uses.
- If the per-`Assign` copy cost (indexed, product and hash updates
  through a shared slot) bites a measured workload, revisit the
  representation, not the semantics.
