# Tail self-calls as loops

- **Status:** Accepted
- **Date:** 2026-10-04
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), order 49
- **Design note:** [Loops](../../docs-archive/2026-10-decided-plans/loops.md)
- **Refines:** the recursion depth bound of [string code points and the recursion depth bound](string-code-points.md), which now applies only to recursion that is not a tail self-call

Nyrwvy has no loop construct. Programs looped with a `ListFold` over a worst-case `ListRange` bound, with a `ListEffectFold` that stops at an invented `Err`, or with self-recursion, which was the shortest form but overflowed the stack: probe 15's event loop at 12,000–14,000 turns, and a minimal `sumTo(n, acc)` between 50,000 and 100,000 calls. The [design note](../../docs-archive/2026-10-decided-plans/loops.md) put three questions to the owner, who accepted every recommendation on 2026-10-04.

## Decision

**A self `DirectCall` in tail position never grows the stack.** It is part of the language, not an optimisation, so an author can rely on it for a loop with millions of turns. No syntax, recipe, Nyason or review form changes; a self-call is still an ordinary `DirectCall` in the graph.

Tail position is defined on the graph, recursively, from the function body's result:

- the result of the function body is in tail position;
- if a `Bind` is in tail position, its `body` is;
- if a `Conditional` is, both branches are;
- if a `MatchOption`, `MatchResult` or `MatchUnion` is, every arm and `otherwise` is;
- if a `BoolAnd` or `BoolOr` is, its `right` operand is.

Nothing else is. A wrapped call (`ResultOk(f(x))`), a call used as an operand or a match input, and a call whose value another node of its graph also uses are ordinary calls. A call inside a callback (`ListFold`, `UseResource`, `TaskScope`, `ParallelAll`, `TaskStart`, …) belongs to the callback's own function. The rule applies to effectful functions too.

Implementation:

- **Execution IR** gains one terminator, `TailCall(originNodeId, arguments)`, with the same `(TargetParameterId, ValueId)` arguments as a `DirectCall` instruction. Its target is implicitly the enclosing function. Lowering passes an `isTail` flag into the arms and `Bind` body of a structured node that is itself the tail node of a graph in tail position; a self `DirectCall` reached that way ends its block with `TailCall` instead of a call instruction and a jump to the merge.
- **Scheduling.** The tail node of a graph (its result, when it leads to a tail self-call and no other node of the graph uses it) is lowered after every other node of that graph. This is the only change to node order, and only graphs with a tail self-call see it. A structured node whose arms all abort or end in a tail call gets no merge block.
- **Serialization.** A program with a `tailCall` terminator serializes as Execution IR schema 9; others keep 6, 7 or 8.
- **C# emitter.** A generated method already copies its parameters into value locals in its prologue and then jumps to the entry block, and every parameter reference reads those locals. `TailCall` assigns the locals in two phases through temporaries, as a block jump does, and jumps to the entry block's label, so the prologue is not rerun and the method keeps its signature.
- **Captured values.** A loop reuses the method's value locals, so the one callback that runs after its instruction, `TaskStart`'s worker, now captures block-scoped copies of its context values and item instead of the locals. Without them, a loop that starts a task and continues would let the task see the next turn's values (`TaskScopeTests`' team loop started member b twice).
- **`runPure`.** The stack-overflow message says that a self-call in tail position runs as a loop and that other recursion is bounded by the stack.

## Reason

**Self-recursion was already the shortest loop.** Probe 21's reachability and topological order rewritten as self-recursive functions, with the loop variables as parameters, needed no state type and stopped when the work was done (the topological order went from 51 to 36 nodes). The only defect was depth. Making the depth constant turns the existing form into the loop, with no new node to learn, review or validate. `IndexFold` keeps the state Product and cannot stop early; a `While` node needs two callbacks and a state Product and costs more nodes than the self-call.

**A guarantee, not an optimisation.** A loop with millions of turns works only if the author can rely on it, so the rule is stated in the language and tested at 10,000,000 turns. The rule is structural and local: an author or reviewer decides it by following the result through `Bind`, conditionals, matches and the right operand of `BoolAnd`/`BoolOr`.

**`BoolAnd` and `BoolOr`.** Their right operand is the `whenTrue` or `whenFalse` branch of the conditional they abbreviate, and lowering already emits them as that branch, so excluding it would make `a && f(x)` and `if a then f(x) else false` behave differently.

**A new terminator rather than a jump to the entry block.** A `Jump` assigns block parameters, while a tail call assigns function parameters; reusing `Jump` with parameter values as targets would blur that and hide that the edge is a call. `TailCall` keeps the call's origin node and its argument shape, so the later generalisation to a target function (below) is one field.

**Effect and abort order are unchanged.** Effect-order validation already requires every observable node of a graph (capability calls, effectful calls, nested effects and aborts) to be ordered by dependency against an effectful call, so moving an effectful tail call after the graph's other nodes moves it past nothing observable. A pure call has no order against the other nodes of its graph: they are pure apart from aborts, and a pure call is not observable.

**Mutual recursion is out of scope.** In one method, a self-call can jump back to its own entry. A call between two functions cannot: C# has no jump between methods, so a loop through two functions needs either a dispatch method holding both bodies or a trampoline that returns thunks. Both are a larger change than this order, writing mutual recursion needs a placeholder callee today, and no probe has looped through two functions.

## Consequences

- `tests/Nyrwvy.Core.Tests/TailSelfCallTests.fs` covers the lowering (a `TailCall` and no self `DirectCall` for every tail-position kind, ordinary calls for wrapped and operand calls, no unreached merge block, codec determinism and schema 9), and under `runPure` a 10,000,000-turn `sumTo`, a loop through each tail-position kind, a self-call inside a `ListFold` folder, a parameter swap, an abort next to a tail call, small non-tail recursion, and an effectful loop whose fake capability records 200 calls in order.
- `PureRunProvenanceTests`' unbounded `loop` became non-tail (`loop(x + 1) + 1`), because the tail form now runs until the time limit instead of overflowing. Execution IR of an existing program changes only where it has a tail self-call; the task loops of `TaskScopeTests` now serialize as schema 9.
- `ExecutionCallGraph` reads instructions, so a tail call no longer appears among a function's call targets. The only target it drops is the function itself, which reachability already includes.
- An endless tail loop is not a stack overflow any more; under `runPure` it ends at the 5 s run limit. A loop in a `ParallelAll` worker or task should still check `CancellationRequested`. No totality check is added.
- Non-tail recursion, such as a tree walk, and mutual recursion keep the stack bound of several thousand nested calls.
- A self call takes the function's exact parameter types (2026-10-05 review, E-1). The jump assigns the argument values to the parameter locals of the one generic method, so a self call at other type arguments (`nest<T>(value: T, n)` calling `nest([value], n - 1)`, `T := List<T>`) has no lowering: the emitted C# did not compile. The [expression authoring](../specs/expression-authoring.md#selfcall) rule that there is no polymorphic self-recursion now applies to every self call, in the recipe preflight (`SelfCall` and a `DirectCall` to the owner's own `FunctionId`, which the ID-keyed wire `directCall` can produce) and in `SnapshotValidator` (`PolymorphicSelfCallUnsupported`), and `ExecutionIr` lowers only an exact-type self call as a `TailCall`, so an unvalidated graph still gets an ordinary call. Polymorphic recursion through another function stays an ordinary call and compiles, since C# generic methods permit it.

## Revisit conditions

- **A loop through two or more functions:** a probe or owner request needs mutual recursion as a loop. Follow the plan below.
- **Review cannot tell a loop from a call:** probes or reviewers misread which self-calls are tail calls. Then mark tail self-calls in review output; nothing has needed it so far.
- **Processors need a bounded count:** the [real-time note](../plans/realtime-processors.md)'s `IndexFold` stays the candidate there, where the count must be bounded by the block size.

## If mutual recursion is needed later

The planned route, in order. Names follow the code at the time of this decision.

1. **Authoring first.** Mutual recursion needs a placeholder callee today, and a later body replacement. Add a group form, `simulateCreateFunctionsByName`, mirroring `simulateCreateTypesByName` ([recursive type declarations](recursive-type-declarations.md)): members may name each other by name, and one commit publishes all of them.
2. **Detection.** Compute the strongly connected components of the call graph over tail calls only. A *tail group* is a component whose members call each other in tail position. `ExecutionCallGraph.targets` reads instructions, so the tail edges come from the `TailCall` terminators (or from the typed graph), not from it. By typing, every member of a tail group has the same result type, because a tail call returns the callee's result as the caller's result; that is what makes merging possible. A generic member agrees only up to its type arguments, so a first slice may take groups of non-generic functions only; a generic group would also need every tail edge to be at the members' own type parameters, as a self call already must be, since the merged method has one set of parameter locals.
3. **Lowering (recommended).** Emit a tail group as one C# method holding every member's body, with an entry label per member and the union of their value locals. Value locals, block labels and jump temporaries are already named from stable IDs, which are unique across functions, so the union needs no renaming. Each member keeps a thin wrapper with its own signature that calls the merged method with a member tag, dispatched by a `switch` at its top. A tail call to a member of the same group assigns that member's parameter locals through temporaries and jumps to its label, which generalises this order's `TailCall` with a target function. Non-tail calls still go through the wrappers, on the stack. Rejected alternative: a trampoline returning thunks, which allocates on every turn and changes the return type of every member.
4. **IR and validation.** `TailCall` carries its target function; validation requires the target to be in the caller's tail group (the caller itself today). The codec writes the target, under a new schema version.
5. **Tests.** Even/odd over 10,000,000 turns; a three-function state machine; tail and non-tail calls mixed in one group; an effectful group with a fake capability.
6. **Trigger.** A probe or owner request that loops through two or more functions.
