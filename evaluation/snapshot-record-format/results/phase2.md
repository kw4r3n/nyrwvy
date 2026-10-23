# Phase 2 results

Mechanical measurements of [snapshot-record-format-evaluation.md](../../../docs/experiments/snapshot-record-format-evaluation.md).
Pass criteria are the plan's; they were not edited. Raw per-measurement output: `diff-locality.json`,
`corruption.json`, `load.json` beside this file. `corruption.json` and `load.json` hold run 2;
`diff-locality.json` is run 1 output (not rerun); run 1's `corruption.json`, with its 189-entry `breaking`
list, is at commit `4075b60`.

## Follow-up (commit 709a2b1): accepted mutations, no-op exclusion, misspelling-rule ablation

- The corruption command now discards any draw that leaves the bytes unchanged and redraws the slot from
  the same seed stream; with seed 20261023 no draw was a no-op, so the 1,000 mutations are unchanged.
  Every entry in `corruption.json` `all` now carries `before`/`after`.
- The six accepted mutations, inspected: five are free-value edits (a character inserted or deleted in a
  display name or string literal: lines `parameter ... name=`, `union ... name=`, `string-literal ...
  value=`, two `function ... name=`), which the plan allows; the sixth (slot 102) replaces one character of
  the header's `snapshot:` ID, giving a different, canonical document whose semantic fingerprint is the same
  because the fingerprint excludes the snapshot ID. None changed bytes and decoded as the same document;
  no codec bug. Run 2's "5 accepted-same" were these free-value edits counted by fingerprint first; the
  classifier now checks free-value first (5 free-value, 1 same).
- Ablation of the one-edit misspelling rule (`corrupt --no-misspelling-rule`, results in
  `corruption-no-misspelling-rule.json`): rejections on the mutated line 949/994 = **95.5%** without the rule
  vs 951/994 = 95.7% with it; both pass. By kind without the rule: char-replace 166/155, char-insert
  163/153, char-delete 165/164, line-swap 167/162, line-delete 167/149, line-duplicate 166/166. The rule
  is kept (default on). Round trip on 709a2b1: 12 fixtures, 0 failed.

## Run 2 (commit f4c9e30): all four rows pass

Run 1 failed only the corruption line rule. Commit `f4c9e30` changed the decoder, not the format or the
criteria: before any model is built it checks the rows structurally and names a line (malformed subject or
owner; duplicate, on its second line; member out of canonical order; a reference or `in=` owner one edit
away from an existing subject is a misspelling reported where it is written; a reference to a record that
does not exist is reported on the line where that record would sit in canonical order). Run 1 relied on
`SnapshotCodec` validation errors mapped to the first ID in the message, which named the owning function.

| Row | Run 1 (4d79865) | Run 2 (f4c9e30) | Criterion | Pass |
| --- | --- | --- | --- | --- |
| Diff locality | 0 non-local lines, records <= JSON on all 4 operations | not rerun: the encoder is unchanged, `locality.mjs` does not decode | records <= pretty JSON lines; every changed line local | PASS |
| Corruption, semantics | 0 accepted with changed semantics | 0 (994 rejected, 5 accepted-same, 1 free-value) | none except free-value edits | PASS |
| Corruption, line rule | 805/994 = 81.0% | 951/994 = **95.7%** | >= 95% of rejections on the mutated line | PASS |
| Load | ratio 1.20 | ratio 1.17 (row table 26.23 ms, JSON tree walk 22.48 ms) | <= 1.5x | PASS |

Run 2 corruption by kind (rejected / on the mutated line): char-replace 166/160, char-insert 163/153,
char-delete 165/161, line-swap 167/162, line-delete 167/149, line-duplicate 166/166. The 43 remaining
wrong-line rejections are in `corruption.json` (`breaking`): mostly a deleted line whose record nothing
referenced by ID (a value-arm binding, a parameter whose function has one parameter, a variant), where the
error surfaces on the owner or in validation, and five swaps across a declaration boundary (a declaration
record or the blank line before it), where the ordering check fires a few lines later.

Run 2 load: row table 26.23 ms; JSON deserialize 56.95 ms, validate alone 34.47 ms, tree walk 22.48 ms,
`JsonDocument.Parse` 1.54 ms; record -> JSON text 64.39 ms; record full decode 171.15 ms.

## Run 1 (commit 4d79865)

| Row | Result | Criterion | Pass |
| --- | --- | --- | --- |
| Round trip | 11 validated fixtures + structural fixture, 0 failures (phase 1) | models equal, records byte-equal, fingerprints equal | PASS |
| Diff locality | see table below; 0 non-local record lines | record lines <= pretty JSON lines; every changed line local | PASS |
| Corruption | 1,000 mutations: 994 rejected, 5 accepted with the same semantics, 1 accepted free-value edit, 0 accepted with changed semantics; 805 of 994 rejections (81.0%) on the mutated line | no changed-semantics acceptance except free-value edits; >= 95% of rejections on the mutated line | semantics PASS, line rule **FAIL** |
| Load | row table 26.65 ms vs JSON tree walk 22.21 ms (ratio 1.20) | <= 1.5x | PASS |

### Diff locality (`node evaluation/snapshot-record-format/locality.mjs`)

Base: `examples/verification-planner/planner.snapshot.json`; each operation from the same base through the
`nyrwvy_*` tool handlers (`simulateOperations` renameFunction / addUnionVariant, `replace` with a target node,
`simulateProductFieldTypeMigrationByName`), committed, then the three forms rendered before and after.
Lines are `git diff --numstat` added/deleted. JSON bytes are the compact persisted JSON's span after removing
the common prefix and suffix (before/after). "Local" means the changed record line is the header or its
subject or `in=` owner is an ID the operation named (declaration, function, match node), or is owned by one
transitively.

| Operation | records | pretty JSON | Nyason v0 | JSON bytes | non-local lines |
| --- | --- | --- | --- | --- | --- |
| rename function (`requires_live`) | 2/2 | 3/3 | 3/3 | 4339/4343 | 0 |
| add node (`requires_live` when-true: `true` -> `boolNot(false)`) | 3/2 | 8/3 | 8/4 | 5121/5240 | 0 |
| add Union variant (`ChangeArea.Tooling`, one arm) | 4/1 | 21/2 | 20/2 | 4203/4511 | 0 |
| migrate Product field type (`ChangeRequest.environmentDependent` bool -> int64, `requires_live` rewritten) | 11/9 | 30/19 | 27/18 | 5609/5862 | 0 |

The JSON byte spans are large for every operation because the header (`snapshotId`, `parentSnapshotId`) is at
the start of the document and the changed function near its end.

### Corruption (`corrupt`, seed 20261023)

500 single-character mutations (replace, insert, delete; alphabet printable ASCII + LF) and 500 single-line
(delete, duplicate, swap with the next), round-robin over the 11 validated fixtures' record texts.

| kind | total | rejected | on mutated line |
| --- | --- | --- | --- |
| char-replace | 167 | 166 | 149 |
| char-insert | 167 | 163 | 153 |
| char-delete | 166 | 165 | 156 |
| line-swap | 167 | 167 | 167 |
| line-delete | 167 | 167 | 36 |
| line-duplicate | 166 | 166 | 144 |

Accepted mutations: 5 whose decoded model keeps the semantic fingerprint (not inspected individually) and 1
free-value literal edit; none with changed semantics. The 189 criterion-breaking mutations (all wrong-line
rejections) are listed in `corruption.json` under `breaking`; 129 of them are `SnapshotCodec` validation
errors mapped by the phase 1 rule "the record whose subject ID appears first in the error text", which names
the owning function before the node, and most of the rest are structural errors reported on the owner
(`bound-body= needs exactly one binding record`, `owner ... not found`) instead of on the duplicated or
deleted line.

### Load (`load`)

Synthetic snapshot: the planner's functions copied 100 times under fresh IDs (every ID that occurs only in
the functions array, calls between copies included), normalized by the codec: 827,186 bytes JSON,
958,506 bytes record text, 5,419 rows. Median of 20 runs after 3 warm-ups:

| measurement | ms | includes |
| --- | --- | --- |
| record row table | 26.65 | tokenizing every line into (kind, subject, key=value) rows; no model |
| JSON deserialize | 57.71 | `SnapshotCodec.deserialize`: `JsonDocument` parse, tree walk into the model, validation |
| validate alone | 35.50 | `SnapshotValidator.validate` on a freshly deserialized model (new memoization key) |
| JSON tree walk without validation | 22.21 | deserialize minus validate alone; `SnapshotCodec` has no public non-validating reader |
| `JsonDocument.Parse` alone | 1.52 | the JSON tokenizer only, for scale |
| record -> JSON text | 49.86 | `RecordText.toJson`: rows to the writer's JSON, no model, no validation |
| record full decode | 159.07 | `Codec.decode`: toJson + deserialize + re-encode canonical check |

Ratio row table / JSON tree walk = 1.20 (criterion <= 1.5).

## Review fixes

Two measurement bugs were fixed after review; rerunning gave the same numbers in every row.

- The free-value classifier did not treat the JSON key `id` as an ID, so a mutated record ID that still decoded could have counted as a free-value edit. None of the six accepted mutations was such a case.
- The locality check merged the before and after ownership maps, so a line whose owner changed could be judged against the wrong owner. Deleted lines are now judged by the before text and added lines by the after text.

## Caveats

- **Load.** There is no public non-validating JSON reader, so the JSON tree walk is the median of `SnapshotCodec.deserialize` minus the median of validation alone, two independent medians. The record side builds rows, not a model, as the plan defines. The ratio is a rough comparison, not a precise one.
- **Snapshot identity.** One accepted mutation changed a character of the header `snapshot:` ID. It decodes to a different, valid document with the same semantic fingerprint, because the fingerprint excludes the snapshot ID. The format cannot detect a well-formed edit to an ID that nothing else in the file references; the workspace's exact-source and approval checks are what catch it.
