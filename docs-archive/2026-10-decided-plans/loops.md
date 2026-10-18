# Loops (design note)

**Status:** Decided 2026-10-04: the owner accepted all three recommendations below (option 1 as a stated guarantee, `BoolAnd`/`BoolOr` right operands as tail positions, mutual recursion left out), implemented as order 49 ([decision](../../docs/decisions/tail-self-calls.md)). The analysis below is kept as written.

## Context

Nyrwvy has no loop construct. Programs loop in three ways today, and each has a cost:

| Form | Where it was needed | Cost |
| --- | --- | --- |
| `ListFold` over `ListRange(0, bound)` | [Probe 21](../../docs/verification/map-set-probes-summary.md#probe-21): reachability and a topological order | The bound is the worst case, so the fold keeps running after the work is done. Each loop needs a state Product (`Walk`, `Topo`) and a step function that passes the state through unchanged once it is finished |
| `ListEffectFold` whose folder returns `Result`, stopping at the first `Err` | [Probe 13](../../docs/verification/rrawrlet-slice-probes-summary.md#probe-13): three `while` loops of a process supervisor | It does stop early, but needs a pre-built bound list (1,024 items built by doubling), `Err` to mean "done", and an arm for "bound reached". 11 functions and 199 nodes, and the bound diverges from Python's unbounded loop by construction |
| Self-recursion | [Probe 15](../../docs/verification/rrawrlet-slice-probes-summary.md#probe-15): an event loop | Natural to write, but each turn is a stack frame. Probe 15's loop overflowed at 12,000–14,000 turns; the workaround was nested `Result` folds over invented lists, 3 functions and 104 nodes |

Self-recursion is already the shortest form. Probe 21's loops rewritten as self-recursive functions, with the loop variables as parameters, matched the same cases:

| Loop | Counted fold | Self-recursive |
| --- | --- | --- |
| Topological order | `topoStep` + `topoOrder`: 51 nodes and the `Topo` state type | `topoRec` + `topoOrderRec`: 36 nodes, no state type, stops when nothing is ready |
| Reachability | `walkStep` + `reachable`: 22 nodes | `walkRec` + `reachableRec`: 25 nodes, stops when the frontier is empty |

The only thing wrong with it is depth. A minimal tail self-call (`sumTo(n, acc)`) overflows between 50,000 and 100,000 calls under `runPure`; a function with a larger frame overflows sooner, as probe 15's did. `runPure` reports the overflow and points the author to `ListFold`.

## Options

1. **Compile a self-call in tail position as a jump.** No new syntax. A function whose last step is to call itself runs in constant stack, so self-recursion becomes the loop.
2. **`IndexFold(count, seed, step)`.** A counted loop without building a list, as the [real-time note](../../docs/plans/realtime-processors.md) proposes for processors. It removes `ListRange` but keeps the state Product and the step function, and it cannot stop early.
3. **`While(seed, continue, step)` or `Iterate`.** A loop node with two callbacks. It stops early, but each loop still needs a state Product and two helper functions, so it costs more nodes than a self-call. Probe 13 estimated that a bounded `Iterate` would save about 40 of its 199 nodes.

## Recommendation: option 1, as a guarantee

**A self `DirectCall` in tail position never grows the stack.** It is stated as part of the language, not as an optimisation, so an author can rely on it for a loop with millions of turns.

**Tail position** is defined on the graph, recursively, starting from the function body's result:

- the result of the function body is in tail position;
- if a `Bind` is in tail position, its `body` is;
- if a `Conditional` is, both branches are;
- if a `MatchOption`, `MatchResult` or `MatchUnion` is, every arm and `otherwise` is;
- if a `BoolAnd` or `BoolOr` is, its `right` operand is.

Nothing else is. A call whose value is wrapped (`ResultOk`, `OptionSome`, a Product field) or used as an operand is an ordinary call. A call inside a callback (`ListFold`, `UseResource`, `TaskScope`, `ParallelAll`, …) is in that callback's own function: it is a tail call there if it calls that function, and never a tail call of the outer function. The rule applies to effectful functions too, which is what probe 15's event loop needs.

**Not covered:** mutual recursion (a placeholder callee is still needed to write it, and no probe has looped through two functions) and non-tail recursion such as a tree walk, which keeps its depth bound.

*Added with the decision:* mutual recursion is also a larger change in kind. A self-call can jump back to the start of its own C# method, but C# has no jump between methods, so a loop through two functions would need a dispatch method that holds every member's body, or a trampoline that returns thunks. The [decision record](../../docs/decisions/tail-self-calls.md#if-mutual-recursion-is-needed-later) lists the planned steps if a probe needs it.

**Infinite loops** are already possible with recursion in principle; today they end as a stack overflow, and afterwards they end at the `runPure` time limit (5 s) or run until cancelled. A loop inside a `ParallelAll` worker or task should still check `CancellationRequested`, as now. No totality check is added.

`IndexFold` stays the processor-side candidate of the real-time note, where the count must be bounded by the block size. It is not needed for ordinary code once self-recursion is stack-safe.

## Implementation sketch

C# is the only backend, and the execution IR already has what a loop needs:

- Blocks take parameters, and `Jump(block, arguments)` assigns them in two phases through temporaries before a `goto`, which is the parallel assignment a tail call needs (`CSharpBackendEmitter.fs` `emitFunction`). There are no back edges yet.
- Function parameters are copied into value locals at entry, and every parameter reference reads them with `ReadParameter`.

The change:

1. **Lowering** (`ExecutionIrLoweringCore.fs`, `lowerGraph`): pass an `isTail` flag into the arms and the `Bind` body of a structured node that is itself in tail position. A self `DirectCall` lowered with `isTail` ends its block with a new terminator, `TailCall(arguments)`, instead of a call instruction and a jump to the merge block. Node scheduling orders instructions by dependency and then by node ID, so the tail call is emitted last in its block; other nodes of the arm are independent of it and are placed before it.
2. **IR model and codec**: the new terminator, in `ExecutionIrModel.fs`, `ExecutionIrSerializer.fs`, the backend validation and the call graph (a self edge, already allowed).
3. **Emitter**: the new terminator assigns the parameter locals through temporaries and jumps to the label after the prologue, so the parameter copies are not redone. The function keeps its C# signature.
4. **Documentation**: the guarantee and the tail-position rule in the [language subset](../../docs/reference/language-subset.md) and the [expression authoring spec](../../docs/specs/expression-authoring.md#selfcall), and a revised `runPure` overflow message that names the rule.
5. **Tests**: a 10,000,000-turn tail self-call under `runPure`; a self-call in each tail position kind; a non-tail self-call (`ResultOk(f(x))`) that still overflows; an effectful loop with a fake capability; a call inside a `ListFold` callback; and the codec round trip.

No surface, recipe, Nyason or review change is needed. Whether review output should mark tail self-calls is left open; nothing so far has needed it.

## Questions for the owner

1. Option 1 as a stated guarantee, rather than option 2 or 3? (Recommended.)
2. Include `BoolAnd`/`BoolOr` right operands as tail positions? (Recommended, for consistency with the conditional they abbreviate.)
3. Leave mutual recursion out until a probe needs it? (Recommended.)
