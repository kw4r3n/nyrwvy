# Zig session host

Status: accepted 2026-10-28 (owner: "OK, go ahead with recommends"). Step 0 in progress. Background: [zig-tools.md](zig-tools.md), section "Direction: a whole Zig session host".

Goal: a separate executable, `nyrwvy-host` built from `tools/zig/`, that speaks the same JSONL protocol as the F# host (`Nyrwvy.AuthoringSession open …`). It reaches parity step by step and then replaces the F# host in one switch. Rules:
- The Zig host never calls F#.
- Until the switch, the F# host by default never calls Zig (#546).
- An operation the Zig host does not have yet answers `UnsupportedOperation`. It never forwards to F#.

## What exists and what is left

| area | F# lines | Zig today |
|---|---|---|
| snapshot read (JSON, records, resolved), validator | SnapshotCodec 2.8K, RecordCodec 1.1K, Validation 2.9K, SnapshotSchema 0.5K | done (snapshot-reader 1.2K, model-reader validate) |
| typed IR, Execution IR lowering, Zig emitter | TypedSemanticIr 1.4K, Lowering 1.2K, ZigBackend 1K | done (model-reader 5.6K total, emitter 1.9K) |
| JSON writer (`.json`, `.nyrec`), fingerprints | parts of SnapshotCodec/RecordCodec, Fingerprint | canonical JSON only |
| queries (search, references, readiness, scopes) | SemanticQuery 0.6K, SemanticFacts 0.7K, Foundation parts | — |
| proposals: recipes, structural transactions, migrations, review | ExpressionRecipe* 3.4K, Transactions 2K, Agent*Workflow ~4K, StructuralContracts/Internals 0.9K | — |
| host: protocol, wire encode/decode, session, storage, locks, sidecars | AuthoringSession 13.3K (Protocol 2.2K, Session 2.3K, Wire* ~3.7K) | — |
| runPure | PureRun 2K + worker 0.4K, C# backend 4.2K, CSharpFacade, CBindingGenerator 0.6K | Zig backend's source only |

In total about 45K lines of F#, of which about 12K are already covered by about 8.7K lines of Zig. The rest is estimated at 25–35K lines of Zig.

## In-memory layout

The current tools work on `std.json.Value` trees. That is fine for one pass, but not for a long-lived host that edits models. The host gets a typed model:
- **IDs.** Every `domain:uuid` is interned once into a `u32` per domain (types, functions, nodes, fields, …). The UUID text is kept in a side table for the wire and the writer, so maps become arrays indexed by ID.
- **Strings.** Display names and literals are interned in one string table with `u32` handles.
- **Functions.** A function graph is stored as struct-of-arrays: node kind, operand slice offsets into one `u32` operand array, and a type per node. The node references come from `node_references.txt`, which the validator already uses.
- **Snapshots are immutable versions.** A snapshot is a root holding arrays of pointers to declarations. A proposal copies the root and replaces only the declarations it changes (path copying), so unchanged functions are shared.
- **Arenas.**
  - One arena per snapshot generation holds what that generation added.
  - One arena per request holds scratch data and is reset when the request ends.
  - Committed generations and open proposals hold references on the arenas they use. An arena is freed when nothing references it (a rejected proposal, a superseded commit).
- **Derived facts** (node types, readiness, fingerprints, the effect-order memo) are computed lazily per snapshot and cached on the snapshot, as `SnapshotComputationScope` does in F#.

## Order of steps

Each step is one or a few PRs. The sizes are rough Zig line counts.

0. **Host skeleton and parity harness, 0.2K (done).** `tools/zig/host` reads one request per line and answers in the F# envelope: blank and malformed lines, a missing or non-string `id` or `op`, and `UnsupportedOperation` for every operation. Its JSON strings are escaped as `JavaScriptEncoder.Default` does. The extended test `ZigHostParityTests` feeds each transcript in `tests/host-parity` to both hosts and compares the responses byte for byte; only .NET's parser text after `Invalid JSON:` is compared by prefix. The shared library and the `ZigSpeed` host row move to step 1, which has their first user: until the host opens a snapshot, there is nothing to share or to time. (The F# host has no `close` operation; the end of input closes it.)
1. **Open, in three PRs.**
   - **1a. Fingerprints, 0.3K (done).** `status` reports `snapshotFingerprint` and `semanticFingerprint`, and a dependency sidecar stores the first. Both are SHA-256 over `SnapshotCodec.serializeCanonical`, which writes at the smallest version holding every construct the snapshot uses (`SnapshotSchema.required`). Most snapshots need less than 21, and below 21 the writer has no `exported` markers, omits a function's implied type parameter list and names each type parameter occurrence. `model-reader --fingerprints` computes both values:
     - `schema_minimums.txt` now holds every construct row.
     - The reader marks the rows that depend on a payload: a timed TaskWait, a shared MatchUnion arm, type arguments, generic types, builtin generics, private declarations and non-derived function type parameters.
     - Two more passes of the canonical writer then produce the minimum-version text and the semantic normalization.
     - The extended test `ZigFingerprintTests` compares both fingerprints with F# on every example, the all-kinds corpus, the milestone-zero fixture (obligations) and three generic cases.
   - **1b. Host open and status, 1.5–2.5K.**
     - Turn the tools into modules that share one model and one build (`ZigTools` hashes every directory a tool imports). The existing tools stay as thin entry points.
     - `open` and `status` for unresolved `.json` and `.nyrec`, read-only and writable.
     - The `<path>.lock` file, held with `flock` as .NET's `FileShare.None` does, so both hosts exclude each other; the temporary-file sweep.
     - Startup errors on stderr with exit code 2, compared with the F# host. `status.build` is masked, since it names each host's own build.
     - A host row in `ZigSpeed`.
     - Legacy schemas 9–20 are rejected with a message that names the tagged F# host for migration (not ported).
   - **1c. Dependencies, 1–2K.** The dependency sidecar (single, sets, embedded or by path, fingerprint check), the recovery hints of an interrupted replacement, and resolved `.json` and `.nyrec` sources (the record path needs the dependency's subjects, which #543 had and which closed with it).
2. **Read-only requests, 3–4K.** `declarations`, `readFunction`, `viewFunction`, `search`, `references`, `unreferenced`, `executionReadiness`, `bindingScopes`, `proposals`, `readProposal`, `viewProposal`. This is mostly the wire encoders (WireEncode, WireOutline, WireGraph).
3. **First write path, 4–6K.**
   - `simulateCreateFunction(ByName)` and `simulateReplaceBody(ByName)`: expression recipes (inference and materialization), the structural transaction, and the proposal review.
   - `commit` and `reject`: the writer for both formats, atomic writes and history.
   - This is the first slice an agent can really use.
4. **Remaining declarations, 2–3K.** Types, effects, capabilities, `simulateReplaceTarget`, `simulateOperations`, the batch forms and the `…sByName` plural forms.
5. **Migrations, 3–4K.**
   - Parameter type, result type, product field type, capability operation signature, and add-union-variant-like.
   - `previewDependencyUpgrade(s)` and `simulateDependencyUpgradeByName`.
6. **runPure, Zig backend, 1–2K.** Fakes, arguments, the build cache and the worker. The lowering and emitter already exist.
7. **runPure C# backend and C# facades, 5–6K, or not ported (decided at this step).** This covers `emitCSharp`, `externalImplementations` and C bindings. The host would still call `dotnet` to compile generated C#, as it calls `zig` today; that is an external compiler, not a mixed runtime. Rhifill declares only the C# target, so dropping C# drops Rhifill's runPure unless Rhifill moves to the Zig target. Each declared capability shape would then need Zig backend support.
8. **Switch and removal** (below).

The MCP and agent adapters (`tools/nyrwvy-mcp`, `tools/nyrwvy-agent`) get one setting for the host executable. That way their Node tests run against either host from step 2 on.

## How parity with F# is checked

- **Differential transcripts.** Each request stream is fed to both hosts, as separate processes, and every response line is compared byte for byte. Both hosts get a test-only deterministic ID seed (`NYRWVY_TEST_ID_SEED`), because new UUIDv7 IDs and timestamps are the only nondeterministic output. This seed is added to the F# host first, in its own PR.
- **Corpus.**
  - Transcripts recorded from the existing F# session tests: a recording hook in `AuthoringProtocol.executeLine` under an environment variable.
  - The Node adapter tests and the examples.
  - Rhifill: read-only, and writes on copies.
  - Mutated requests: wrong IDs, bad recipes, conflicting proposals, so that error codes and messages match too.
- **Stored state.** After every commit, the snapshot files and sidecars written by both hosts must be byte-identical.
- **Gate.** A step merges only when the transcripts of its operations are identical, the fast and extended suites pass, and the `ZigSpeed` host rows are recorded.
- **Unit level.** The existing tool parity tests (reader, validator, lowering, emitter) stay until the switch.

## The switch

1. All operations have parity, the adapters' Node tests pass against the Zig host, and Rhifill works through the Zig host.
2. One PR makes the Zig host the default in the adapters and docs, and marks the F# host deprecated in its README and in `docs/decisions/zig-tools.md`.
3. Tag the last commit with the F# host, for example `fsharp-host-final`, and push the tag, so that the F# version stays on the remote (the user's idea: mark F# deprecated, cut a tag, keep it on the remote).
4. Before removal, record the corpus responses of the F# host as golden files. After removal, the Zig host is tested against the goldens instead of a live F# host.
5. A following PR removes the F# projects and the `NYRWVY_ZIG_*_CHECK` modes.
6. Legacy migration (schemas 9–20) and `Nyrwvy.Viewer` stay in the F# tag only; they are not ported (decided 2026-10-28).

## Open decisions

- The C# backend (port it, 5–6K, or drop it and move Rhifill to the Zig target): decided at step 7.
