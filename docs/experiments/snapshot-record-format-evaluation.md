# Snapshot record format evaluation

**Status:** Phases 1 and 2 ran and passed ([record](../verification/snapshot-record-format-20261023.md)); phase 3 pending.

This plan carries out the Revisit conditions of the [snapshot record format ADR](../decisions/snapshot-record-format.md). It fixes the corpus, the harness location and the pass criteria before anything is measured, so the result decides the ADR rather than being read after the fact. It does not reopen the ADR's resolved points.

## What the result decides

- **Accept, change or drop the persisted form.** Decided by the mechanical phase (phases 1 and 2) alone. It needs no model calls.
- **Whether agents get a record-text projection.** Decided by the agent phase (phase 3). Open point 6 settled that agents read the session's focus JSON, not the persisted text, so this phase does not gate acceptance. It runs only if phase 2 passes, and adds a projection only if the record text beats the current focus JSON.

## Harness location

The harness lives in `evaluation/snapshot-record-format/`, an F# project that references `src/Nyrwvy.Semantics` and is not part of `Nyrwvy.slnx`, so ordinary builds and CI ignore it. `src/` is not changed: the ADR stays unimplemented and JSON stays the reference codec.

After the result is recorded in `docs/verification/`, the harness follows the 2026-10-18 owner decision on evaluation code ([freeze tags](../operations/evaluation-freeze-tags.md)): it gets an `eval/snapshot-record-format-…` tag and leaves `main`. If the ADR is accepted, the codec moves into `src/Nyrwvy.Semantics` with ordinary unit tests, and the harness is retired the same way.

## Corpus

Two tiers, because no valid snapshot covers every node kind today.

- **Validated tier.** Snapshots that pass `SnapshotCodec.deserialize`, which validates:
  - the migrated fixtures: `examples/verification-planner/planner.snapshot.json`, `examples/mini-work-planner/planner.snapshot.json` and `examples/task-cancellation-outcome/fixtures/task-outcomes.json` (the largest tracked snapshot, 62 KB);
  - the snapshots that the authoring example programs commit when run (`bounded-stream`, `bounded-worker-stream`, `change-verification-batch`, `cleanup-shield`, `config-request-batch`, `dynamic-worker-collector`, `function-abort`, `scoped-result-handoff`), harvested into the harness's own fixture directory. They are valid by construction and reach node kinds the tracked files do not. The other examples only read or run existing snapshots.

  The two migrated copies kept only in local cutover evidence are not tracked and are left out.
- **Structural tier.** A generator, promoted into the harness from the open-point-2 check, builds one model holding all 204 node kinds and every type, type-reference, capability and obligation form, with UUID IDs in the right domains. It does not validate (an abort that is not the graph result, mismatched branch types and so on), so this tier skips validation and fingerprints and checks only the encode/decode shape.

Phase 1 reports which node kinds the validated tier covers. Kinds reached only by the structural tier are named in the result.

## Phase 1: the codec

An experimental codec in the harness: `encode: Snapshot -> string` and `decode: string -> Result<Snapshot, RecordError>`, where `RecordError` carries a line number. Decode accepts only canonical bytes: it re-encodes what it parsed and rejects the input when the two differ. On the validated tier it then runs the same validation as `SnapshotCodec.deserialize`. The grammar, keys and locators are the ADR's; where the codec finds a case the ADR does not cover, the ADR is amended in the same change, not the codec patched around it.

The codec is a bounded, self-contained change and may be delegated.

## Phase 2: mechanical measurements

Every measurement is a deterministic command in the harness with a fixed seed, and the result record states the commit it ran on.

| Measurement | Method | Pass |
| --- | --- | --- |
| Round trip | Validated tier: JSON → model → record → model → record. Structural tier: model → record → model. | Every fixture: models equal, the two record texts byte-equal, and on the validated tier the snapshot and semantic fingerprints recomputed from the decoded model equal the JSON's. |
| Diff locality | Apply authoring operations through `Nyrwvy.AuthoringSession` (add a node, rename a function, add a Union variant, migrate a Product field type) to the planner snapshot and write each before/after pair in every form. Count changed lines with `git diff --numstat`. The persisted JSON is a single line, so it is compared pretty-printed (`jq .`), and its changed bytes (the length of the span left after removing the common prefix and suffix) are reported for scale; Nyason v0 is the third form. | For every operation the record form changes no more lines than pretty-printed JSON, and every changed line belongs to the header or a record whose subject or `in=` owner the operation touched. |
| Corruption | 1,000 seeded mutations over the validated tier: 500 single-character (replace, insert, delete) and 500 single-line (delete, duplicate, swap with the next). Decode, then validate. | No mutation decodes to a valid snapshot with a different semantic fingerprint, except a free-value edit. The harness classifies one mechanically by comparing the decoded model with the original field by field: exactly one leaf differs, and it is a string or number value that is neither an ID nor a closed enumeration (a literal, a display name or other free text). Rejected mutations are those that fail to decode or to validate; of these, at least 95% report the line holding the first mutated byte (for a line swap or duplicate, either of the two lines). |
| Load | Build a synthetic snapshot 100 times the planner's size by copying its functions under fresh IDs. Time decode into a row table (one row per record) against `SnapshotCodec`'s JSON tree walk, both without validation, median of 20 runs after warm-up. | Record load is at most 1.5 times the JSON time. |

The ADR is accepted only if all four pass. Any failure changes the ADR (and the codec) and reruns phase 2, or drops the proposal; phase 3 does not run until phase 2 passes.

## Phase 3: agent projection comparison

Runs only after phase 2 passes. It uses a minimal harness, not the removed runners: one prompt per task, no tool loop.

- **Control.** The `readFunction` focus JSON from the session, which agents read today. Compared against the same function's records in the record form, and Nyason v0.
- **Tasks.** 12 per format on the planner functions: 8 comprehension questions (which node feeds an input, which effects a function requires, which arm handles a variant, what a call's callee is) and 4 edit requests, answered as an operation request JSON that names existing IDs. Edit answers are graded by running them through session `simulate`.
- **Models.** Claude Haiku and Claude Sonnet, 3 repetitions each: 12 tasks × 3 formats × 2 models × 3 = 216 calls.
- **Scores.** Correct answers, wrong-ID-domain rate (an ID of the wrong kind, or one that does not exist), malformed requests, and input and output tokens.
- **Pass.** The record form is adopted as a projection only if it is at least as correct as the focus JSON for both models and has a lower combined wrong-ID-domain and malformed rate. Otherwise open point 6 stands as it is.

## Recording

Results go to `docs/verification/snapshot-record-format-<date>.md`, with the commit, commands, per-fixture numbers and every mutation that broke a pass criterion. The ADR's status changes in the same change, and Q-001 gets the outcome.
