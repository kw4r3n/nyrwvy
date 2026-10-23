# Snapshot record format evaluation — 2026-10-23

Evaluation of the [snapshot record format ADR](../decisions/snapshot-record-format.md) under its [evaluation plan](../experiments/snapshot-record-format-evaluation.md). Harness: `evaluation/snapshot-record-format/`, an experimental codec outside `src/`. Phases 1 and 2 ran at `732eb3a` (PR #443 and #444). The pass criteria were fixed in PR #442 before anything was measured and were not edited. Per-run output and every criterion-breaking mutation are in `evaluation/snapshot-record-format/results/`.

## Result

All four mechanical criteria pass, so the ADR is accepted. Phase 3 (the agent projection comparison) decides only open point 6; it ran afterwards (PR #447) and leaves point 6 as it is.

| Measurement | Result | Criterion |
| --- | --- | --- |
| Round trip | 12 fixtures (11 validated, 1 structural), 0 failures: models equal, record bytes equal, snapshot and semantic fingerprints equal | pass |
| Diff locality | Changed lines (added/deleted), record vs pretty-printed JSON vs Nyason v0: rename a function 2/2, 3/3, 3/3; add a node 3/2, 8/3, 8/4; add a Union variant 4/1, 21/2, 20/2; migrate a Product field type 11/9, 30/19, 27/18. No changed line outside the touched records | pass |
| Corruption (seed 20261023, 1,000 mutations) | 0 accepted with changed semantics; 994 rejected, of which 951 (95.7%) report the mutated line | pass |
| Load (planner × 100: 827 KB JSON, 5,419 rows) | Row table 26.2 ms vs JSON tree walk 22.5 ms, ratio 1.17 | pass (≤ 1.5) |

## Corpus

- Validated tier: the three tracked migrated snapshots and the snapshots committed by the authoring examples (`harvest.sh`); 52 of the 204 node kinds occur in it. Program snapshots that resolve against pinned dependencies were not decoded (the harness uses `SnapshotCodec.deserialize`, which rejects the `dependency` marker); their dependency libraries were harvested from the sidecars instead.
- Structural tier: one generated model holding all 204 node kinds and every type, type-reference, capability and obligation form. It does not validate, so it checks only JSON → record → JSON byte equality and idempotence.

## Observations

- **Error lines.** The first corruption run reported the mutated line for 81.0% of rejections and failed: the codec mapped `SnapshotCodec` validation errors to the first ID in the message, usually the owning function. The decoder then gained structural checks before the model is built (subject and owner syntax, duplicates, canonical order, missing owners, dangling references), and the ADR's canonical-form bullet now says so. The second run reached 95.7%. Its rule that a missing ID one edit away from an existing subject is a misspelling is shaped by the mutation model, so it was also measured without that rule: 95.5%, still a pass.
- **Accepted mutations.** Five accepted mutations edited a free value (a display name or string literal), which the criterion allows. One changed a character of the header snapshot ID: a different, valid document with the same semantic fingerprint, since the fingerprint excludes the snapshot ID. The format cannot detect a well-formed edit to an ID nothing else references; the workspace's exact-source and approval checks catch it.
- **Load.** Three reruns gave ratios of 1.16 to 1.20, so the 20% is a real cost, not noise. In absolute terms it is about 4 ms at 100 times the planner. The JSON side is the median of `SnapshotCodec.deserialize` minus the median of validation alone, because no public reader skips validation. The experimental codec's full decode takes 171 ms against 57 ms for JSON, but it is not a design under test: it converts the records to JSON text, decodes that, and re-encodes the result to check canonical bytes. A native codec must show its own full-decode cost; see the ADR's consequences.
- **Tokenizer tuning (after acceptance).** Validating string literals by the JSON escape grammar instead of a `JsonDocument` per literal, checking the record kind without a regex, sharing one string per key spelling, and scanning lines without intermediate arrays brought the row table from 26.7 ms to 18.8 ms: a ratio of 0.84 over three runs, faster than the JSON tree walk. Round trip and the corruption results were unchanged.
- **Measurement fixes.** Review found two bugs that could only have inflated the result: the free-value classifier did not treat the key `id` as an ID, and the locality check merged the before and after ownership maps. Both were fixed; rerunning gave the same numbers.

## Phase 3: agent projection comparison

12 tasks on the four planner functions (8 comprehension, 4 edits graded by committing the answer on a copy and comparing with the expected commit), each presentation holding the same declarations, `claude -p` with no tools, Claude Haiku and Claude Sonnet, 3 repetitions: 216 calls, every one a single turn without tool use.

| Format | Correct (Haiku, Sonnet) | Wrong-ID-domain or malformed | Mean input tokens |
| --- | --- | --- | --- |
| Focus JSON (control) | 36/36, 36/36 | 0 | 9,213 |
| Record form | 36/36, 36/36 | 0 | 8,457 |
| Nyason v0 | 36/36, 36/36 | 0 | 8,367 |

The pass rule needs the record form to have a lower error rate than the focus JSON; both are zero, so no record projection is added and open point 6 stands. This is a ceiling effect, not evidence that the formats are equal: the functions have 6 to 14 nodes, every task names the IDs it asks about, and every expected ID appears verbatim in the presentation. Input tokens include about 7,355 tokens of fixed per-call overhead; beyond it, the record form used about 750 fewer tokens than the focus JSON although its text is larger (25,081 against 17,666 bytes over the four functions). A comparison that can tell the formats apart needs larger functions and tasks that require following references.

## Not covered

- Decoding resolved snapshots with a `dependency` marker, and the dependency sidecar's embedded form.
- Node kinds outside the validated tier, beyond structural round trip.
- The two migrated copies kept only in local cutover evidence.
