# Argument-keyed fake responses — 2026-09-27

**Status:** Implementation and deterministic verification complete.

Baseline: main `e736f3f` (TaskWait time limits with a virtual clock, #222).

The [runner contract](../specs/agent-authoring-session.md#argument-keyed-fake-responses)
adds operation `cases`, each with complete typed arguments and an independent
response queue. Existing `responses` scripts keep their call-order behavior.
This changes verification input only; snapshot and IR schemas are unchanged.

## Evidence

- `PureRunBindingTests` rejects mixed modes, duplicate/missing/foreign parameter
  IDs, wrong types, duplicate keys, oversized strings and response counts, and
  malformed wire shapes without publishing changes.
- Product keys interleaved A/B/A consume A1/B1/A2 in either case-list order.
  Empty cases report `FakeArgumentsUnmatchedException`; a matching empty queue
  reports exhausted responses, without falling through to another case. Frames
  and failed-call logs remain available. JSONL exposes `RunBindingNoMatch`.
- A zero-argument operation matches its one explicit empty argument key.
- Nested List/Option/Result/Union/Int64-list keys compare contents, including
  Int64.MinValue and fieldless variants. Reordered parameter IDs are accepted;
  reordered duplicate keys are rejected. Independently supplied Product values
  also match structurally rather than by CLR object identity.
- `TaskScopeTests` spawns a then b, then b then a. The same keyed work script
  fails only b in both orders; the actual work-call order is asserted, and each
  run releases both workspaces. Delayed keyed responses use the existing virtual
  clock.

## Validation

| Command | Result |
| --- | --- |
| `./scripts/test-fast.fish` | 486 F# and 29 Node/script tests passed. |
| `./scripts/test-extended.fish` | 52 Core Extended and 31 Extended-project tests passed. |
| `npm run test:node` | 41 Node and 11 MCP tests passed. |

Counts overlap. These are local Linux deterministic tests, not a new live-model
trial. Identical argument values intentionally share a queue and retain call-order
consumption. Partial matches, wildcards, name-resolved run values, and persistent
fake queues remain outside this change. Direct F# callers now specify `Cases =
None` for ordered scripts; wire callers need no migration.
