# Snapshot record format harness

Phase 1 (corpus and experimental codec) of
[snapshot-record-format-evaluation.md](../../docs/experiments/snapshot-record-format-evaluation.md),
implementing [the ADR](../../docs/decisions/snapshot-record-format.md). It references
`src/Nyrwvy.Semantics` and is not in `Nyrwvy.slnx`; `src/` is unchanged.

## Build and run

From this directory, with .NET 10:

```sh
dotnet build -c Release
dotnet run -c Release --no-build -- roundtrip          # phase 2 round-trip row; exit 1 on any failure
dotnet run -c Release --no-build -- generate           # rewrite fixtures/structural/all-kinds.json
dotnet run -c Release --no-build -- schema             # the per-kind key table derived from the writer
dotnet run -c Release --no-build -- encode  <snapshot.json>          # record text to stdout
dotnet run -c Release --no-build -- decode  <snapshot.records.txt>   # canonical re-encoding, or `line N: ...`
```

`roundtrip` also writes `<fixture>.records.txt` beside every fixture (committed, so the
record form is reviewable) and prints validated-tier node-kind coverage.

To refresh the harvested fixtures, from the repository root after `npm ci` and
`dotnet build tools/Nyrwvy.AuthoringSession -c Release`:

```sh
evaluation/snapshot-record-format/harvest.sh [work-dir]
```

It runs each authoring example with an output directory, copies every snapshot document the
example wrote into `fixtures/validated/<example>-<path>.json` (deduplicated by content) and
prints a reason for each skip: a failed example, or a snapshot that `SnapshotCodec.deserialize`
cannot validate on its own (one with a pinned dependency).

## Design

- `RecordText.fs`: record text <-> the strict JSON codec's writer output, mechanically, with no
  model. `ofJson` walks the writer's JSON and renders records by the ADR's rules; `toJson`
  rebuilds the writer's JSON. The per-kind key table (record kind -> JSON keys in writer order
  and what each holds) is not written by hand: it is derived at startup by rendering the
  structural fixture (`AllKinds.fs`), which is written through the codec's own private writer by
  reflection. Three hand-stated facts remain, each cited to the writer: the element domain of
  bare-string ID arrays, the collections the writer omits when empty, and the kebab/suffix naming
  rule itself.
- `Codec.fs`: `encode = SnapshotCodec.serialize >> ofJson`; `decode = toJson >> SnapshotCodec.deserialize`,
  then re-encode and reject when the bytes differ (reported on the first differing line).
  Only canonical bytes decode. A `SnapshotCodec` shape or validation error has no line of its
  own: it is reported on the first record (in file order) whose subject UUID appears earliest in
  the error text, else on line 1. Errors carry a 1-based line, not a column.
- Strings are copied as the writer's JSON literals in both directions, so the writer's escaping
  (`+` for `+`, `\uXXXX` for non-ASCII) is the record spelling.

## Corpus

- `fixtures/validated/`: the three tracked snapshots plus the harvested example snapshots.
  Checked: JSON -> model -> record -> model -> record; models equal, record texts byte-equal,
  snapshot and semantic fingerprints of the decoded model equal the JSON's.
- `fixtures/structural/all-kinds.json`: every `SemanticNodeKind` case twice (lists filled and
  options present; lists empty and options absent), both type kinds, every type-reference form,
  a capability operation with and without a failure type, open and resolved obligations, a shared
  arm, an arm whose field bindings sort differently by binding and by source field, a nameless
  binding, construct type arguments, an unexported function and a dependency header. It does not
  validate. Checked: JSON -> record -> JSON byte-equal, record re-encode byte-equal, and the
  committed file equals the generator's output.
