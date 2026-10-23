# Phase 3 results: agent projection comparison

[Plan](../../../docs/experiments/snapshot-record-format-evaluation.md) phase 3, run with `phase3.mjs`
(`prepare`, `smoke`, `run`, `grade`). Pass criterion as written in the plan; not edited. Raw model outputs:
`phase3/raw/` (216 files), presentations: `phase3/presentations/`, tasks with derived answers:
`phase3/tasks.json` (written before any model call), per-call grades: `phase3/summary.json`.

## Setup

- Snapshot: `examples/verification-planner/planner.snapshot.json` (4 functions). Per function, three
  presentations with the same referenced declarations (the types, effects and capabilities the function's
  records name; the planner has no effects or capabilities): control = the session's `readFunction` focus
  JSON (`inspect view:function` from a real `Nyrwvy.AuthoringSession`) plus the referenced declarations as
  snapshot JSON; record form = the function's block plus the referenced declarations' blocks and the
  header; Nyason v0 = the same declarations cut from `NyasonCodec.serialize` output.
- Tasks (12): 8 comprehension (C1 conditional's condition node, C2 direct-call callee, C3/C5 arm body for a
  variant, C4 required effects, C6 field read by a field-access, C7 list-any source list, C8
  parameter-reference target) and 4 edits (rename a function, a Union variant, a parameter, a Product
  field), answered as `simulateOperations` requests naming exact IDs. Expected answers are read from the
  snapshot model; each edit's intended change is the gold request committed through a session on a copy.
  The rename operation shapes from `docs/specs/agent-authoring-session.md` are quoted identically in every
  condition.
- Calls: `claude -p --strict-mcp-config --tools "" --disallowed-tools advisor --system-prompt "You answer
  with exactly one JSON object and nothing else." --model {haiku|sonnet} --output-format json`, prompt as
  the argument, 4 concurrent. 12 tasks x 3 formats x 2 models x 3 repetitions = 216 calls (plus a 6-call
  smoke test, not counted). No built-in tools; the environment-injected `advisor` tool remained listed even
  with `--disallowed-tools`, but every call completed in one turn with no tool use. Models served:
  `claude-haiku-5-5` and `claude-sonnet-5-5`.
- Grading: a reply is malformed when no JSON object with the expected shape can be parsed; a comprehension
  answer is correct when it equals the derived ID (or ID list) ignoring an optional `domain:` prefix, and
  wrong-ID-domain when the ID does not exist in the snapshot or exists with another kind; an edit answer is
  checked for shape and ID domains, then simulated and committed on a fresh copy and compared with the gold
  commit (snapshot and parent IDs excluded).
- Tokens: input = `input_tokens + cache_creation_input_tokens + cache_read_input_tokens` as reported by the
  CLI; a one-line prompt costs 7,353 (haiku) / 7,361 (sonnet) of these, so the presentation accounts for the
  remainder. Output includes thinking tokens.

## Scores

| format / model | calls | correct | wrong | wrong-ID-domain | malformed | input tokens (mean) | output tokens (mean) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| focus JSON / haiku | 36 | 36 | 0 | 0 | 0 | 9,213 | 257 |
| focus JSON / sonnet | 36 | 36 | 0 | 0 | 0 | 9,214 | 47 |
| records / haiku | 36 | 36 | 0 | 0 | 0 | 8,457 | 260 |
| records / sonnet | 36 | 36 | 0 | 0 | 0 | 8,460 | 47 |
| Nyason v0 / haiku | 36 | 36 | 0 | 0 | 0 | 8,367 | 234 |
| Nyason v0 / sonnet | 36 | 36 | 0 | 0 | 0 | 8,369 | 47 |

Every task was answered correctly in every condition (18/18 per task). Presentation sizes summed over the
four functions (the files under `presentations/`): focus JSON 17,666 bytes (compact), records 25,081,
Nyason 38,912; in tokens the order reverses: the record and Nyason prompts cost about 750 and 850 fewer
input tokens than the focus JSON.

## Pass rule (plan, applied mechanically)

The record form is adopted as a projection only if it is at least as correct as the focus JSON for both
models **and** has a lower combined wrong-ID-domain and malformed rate.

- correct: records 36 >= focus JSON 36 for haiku, 36 >= 36 for sonnet: true
- combined wrong-ID-domain + malformed rate: records 0.000, focus JSON 0.000; records lower: **false**
- adopt record projection: **false** (open point 6 stands: agents keep reading the focus JSON)

## Caveats

- Ceiling effect: 216/216 correct, so the comparison cannot separate the formats on correctness or on the
  error rates; every answer ID appears verbatim in the presentation, and the planner functions are small
  (6 to 14 nodes). The only measured difference is prompt size.
- The result decides nothing in favor of the record form by construction of the rule (a tie on a zero rate
  is not "lower").
