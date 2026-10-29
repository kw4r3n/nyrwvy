# Zig session host

Status: accepted 2026-10-28 (owner: "OK, go ahead with recommends"). Steps 0, 1 and 2 done. Background: [zig-tools.md](zig-tools.md), section "Direction: a whole Zig session host".

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

Decided 2026-10-29 for step 3, the first write path (the owner: "うん、お願い"). Steps 1 and 2 only read, and their encoders read the model reader's tree (UTF-16 strings, its index maps).

- **IDs.** Every stable ID text is interned once into a `u32` per domain: function, type, field, variant, parameter, node, pattern binding, effect, capability, operation, capability parameter, obligation, transaction and type parameter. A side table keeps each text for the wire, the writer and sorting.
- **Sort order of IDs.** F# sorts IDs by their text, ordinal UTF-16. The host compares the interned texts at sort time (UTF-16 unit order); no order is precomputed. So an ID a proposal creates sorts correctly the moment it exists, in every generation, and no key needs recomputing.
- **Strings.** Display names and String literals are interned in one table of UTF-16 units with `u32` handles. They stay UTF-16 because the writers escape per unit, as the F# writers do.
- **Declarations.** One record type per kind (function, type, effect, capability, obligation), holding `u32` references. Member lists (fields, variants, parameters, operations) are slices in the arena of the generation that created them.
- **Function bodies.** A struct-of-arrays graph per function: per node a kind (`u8`), an ID (`u32`), and a payload offset and length into one `u32` extra array. Nested graphs (branches and arms) are sub-ranges of the same arrays. The payload layout per kind follows `model_kinds.txt`, which the reader already uses. A body is one contiguous allocation, cheap to share.
- **Generations.** A snapshot is an immutable generation: its snapshot ID, declaration tables (pointers to records, in ID order), the dependency selection, and lazily cached derived facts (node types, readiness, fingerprints, the effect-order memo, as `SnapshotComputationScope` caches them in F#).
  - Each generation owns one arena with what it added, and holds references on the arenas of the records it shares.
  - The committed generation and each open proposal hold one reference. Rejecting a proposal, or committing past one, drops it; an arena is freed at zero.
  - Each request has a scratch arena, reset when the request ends. Nothing long-lived may point into it.
- **Proposals copy only what they change.** A proposal's generation copies its parent's pointer tables and replaces only the entries its transaction changes: a body rewrite allocates one new body, a created function one record and one body, and every other declaration and body is shared. Records are reached only through `*const` pointers, so no proposal writes into a shared record. A proposal keeps its base generation, so commit detects a stale base as F# does, and commit installs the proposal's generation without copying.
- **Interning tables belong to the session and are compacted.** An interned entry is live while some live generation refers to it. IDs and strings from rejected proposals, or from generations no longer referenced, are dead. When a commit or rejection frees an arena and the dead entries make up more than half of a table, the host rebuilds that table from the live generations and remaps their `u32` references. A long session therefore holds at most about twice what its live generations use.
- **`u32` numbers never leave the process.** They never appear in the wire, files, logs, fingerprints or error messages; every output writes the ID's text. Byte parity with F# checks this, and it is an invariant in its own right: an interned number is valid only inside one process and one table version.
- **Reads.** During step 3 the step-2 reads keep working on the reader's tree; a proposal's generation writes canonical JSON for them to read. They move to the typed model one by one, behind the same parity tests. That move is a condition of the switch (below).

## Order of steps

Each step is one or a few PRs. The sizes are rough Zig line counts.

0. **Host skeleton and parity harness, 0.2K (done).** `tools/zig/host` reads one request per line and answers in the F# envelope: blank and malformed lines, a missing or non-string `id` or `op`, and `UnsupportedOperation` for every operation. Its JSON strings are escaped as `JavaScriptEncoder.Default` does. The extended test `ZigHostParityTests` feeds each transcript in `tests/host-parity` to both hosts and compares the responses byte for byte; only .NET's parser text after `Invalid JSON:` is compared by prefix. The shared library and the `ZigSpeed` host row move to step 1, which has their first user: until the host opens a snapshot, there is nothing to share or to time. (The F# host has no `close` operation; the end of input closes it.)
1. **Open, in three PRs.**
   - **1a. Fingerprints, 0.3K (done).** `status` reports `snapshotFingerprint` and `semanticFingerprint`, and a dependency sidecar stores the first. Both are SHA-256 over `SnapshotCodec.serializeCanonical`, which writes at the smallest version holding every construct the snapshot uses (`SnapshotSchema.required`). Most snapshots need less than 21, and below 21 the writer has no `exported` markers, omits a function's implied type parameter list and names each type parameter occurrence. `model-reader --fingerprints` computes both values:
     - `schema_minimums.txt` now holds every construct row.
     - The reader marks the rows that depend on a payload: a timed TaskWait, a shared MatchUnion arm, type arguments, generic types, builtin generics, private declarations and non-derived function type parameters.
     - Two more passes of the canonical writer then produce the minimum-version text and the semantic normalization.
     - The extended test `ZigFingerprintTests` compares both fingerprints with F# on every example, the all-kinds corpus, the milestone-zero fixture (obligations) and three generic cases.
   - **1b. Host open and status, 0.5K (done).**
     - The model and snapshot readers are imported by the host as modules. Each exposes one entry point: `readDocument`, the `--validate` lines plus the fingerprints, and `readRecords`, the record line plus the JSON. `ZigTools` builds a tool with its imports (`-M`); the cache key covers every imported file and the build options.
     - `-O` is a per-module option. Placed once at the end, it left the imported modules in Debug: the host took 420 ms instead of 55.
     - `open` covers unresolved `.json` and `.nyrec`, read-only or writable. The steps follow `openUsing`: the file check, the symbolic-link check, `<path>.lock` held with `flock` (as .NET's `FileShare.None` holds it, so each host locks the other out), the temporary-file sweep, the dependency marker without a sidecar, then the readers' verdict worded as `WireEncode.describeCodecError`. `status` reports `pendingProposalCount` 0 and `build` `nyrwvy-zig-host`.
     - A sidecar makes the Zig host refuse to start until 1c. A legacy schema is refused with a message naming the migration on the F# host.
     - `ZigHostParityTests` runs every transcript against each example snapshot and a JSON one. It compares nine failing startups (missing file, syntax, shape, version, an invalid model, a JSON or record marker without a sidecar, two record errors), read-only and writable, by stderr line and exit code. It also covers the lock both ways and the sweep. `status.build` and .NET's JSON parser text are masked.
     - `ZigSpeed` row "start, merged model": Zig host 55 ms, F# host 505 ms, spawn to the first `status` reply on Rhifill's merged model (three runs: 54.8–55.5 against 502.5–506.4).
   - **1c. Dependencies, 0.6K (done).** The dependency sidecar as `PinnedDependency.loadFrom` reads it.
     - A single member, embedded (`snapshotJson`, `snapshotRecords`) or legacy by path, or a set of at least two embedded members.
     - Each member is decoded, must have no open obligation, and its fingerprint and ID must be the recorded ones.
     - A set is combined under the collision policy (`model_reader.combine`: one copy of an identity with the same canonical text, else the conflict with its providers) with the derived `dependency-set-` ID.
     - The local document is read against the dependency (`readResolvedDocument`): the `Resolved snapshot` shape, the marker against the dependency's ID and digest (SHA-256 of its minimum-version text, 1a), then the merged model, the dependency's declarations first, validated and fingerprinted for `status`. A resolved record reads with the dependency's subjects (`RecordCodec.dependencySubjects`).
     - On a failure the two recovery hints are checked: a matching `.dependency.json.previous` (an interrupted replacement) and a snapshot that reads without its sidecar (an interrupted first addition).
     - The model reader keeps its state in globals; `resetState` (also `--bench`'s) starts each document afresh, so one process reads several.
     - `ZigHostParityTests` opens every example split into a dependency and a local part in each form: JSON, records, by path, a set, a colliding set. It also covers ten broken sidecars or markers and both hints, read-only and writable, and compares exit code, stderr and the `status` reply. It checks that the accepted forms open, that the others fail, and that at least twelve distinct failures occur, the collision among them.
     - `ZigSpeed`: the Zig host opens Rhifill's resolved workspace in 47.2–47.9 ms against the F# host's 605.2–606.2.
2. **Read-only requests, in five PRs.** The encoders (`tools/zig/host/wire.zig`) read the model reader's tree of the open model in canonical order. Its strings are UTF-16 units, so ordering and escaping already follow .NET.
   - **2a. `dependency`, `declarations`, `proposals`, 0.4K (done).**
     - `declarations` follows `WireEncodeCommon.declarations`: the provenance of each visible declaration (local, or imported with the members that provide it), then the functions' signatures, the types, the effects and the capabilities. Imported declarations the dependency does not export are left out.
     - `dependency` follows `PinnedDependency.describe`: an anonymous record, so its fields come in name order. Each member gives its path, ID, fingerprint and storage, and a set gives a `dependencies` list.
     - `proposals` is empty until simulate exists. `readProposal` and `viewProposal` read their input as F# does and answer `ProposalNotFound`.
     - Parity: `step2a-declarations.jsonl` on every example and a JSON snapshot. The resolved-snapshot test also asks `declarations` and `dependency` on every form.
   - **2b. `search`, `references`, `unreferenced`, 1.1K (done).**
     - `references` follows `SemanticQuery.references` (in the model reader, beside the node shapes it uses) and `WireEncode.references`. Its order was F#'s `%A` text of each site; it is now the structural order of `ReferenceSite` (case, then its IDs, ordinal), which both hosts produce. Subjects resolve by exact ID, display name or selector object (`id`, `name`, `dependencySnapshotId`, `ownership`), with the same advice and name hints.
     - `search` compares as `OrdinalIgnoreCase` and name hints lower-case as `ToLowerInvariant`, through code point runs F# generates from .NET (`tools/zig/host/case_runs.zig`; a fast test fails when it is stale). A `limit` that is not a JSON Int32 is now `ProtocolError` `limit must be a JSON Int32.` in both hosts; F# used to answer `SessionError` or `InternalError` with .NET's text.
     - `unreferenced` builds the same reference edges between the visible declarations.
     - Parity: `step2b-queries.jsonl` (error forms, non-ASCII search) on every example and a JSON snapshot, and requests built from each snapshot's declarations: every declaration and member by ID, by name and by selector, with ownership and pin guards, searches and `unreferenced` with roots. The resolved-snapshot test asks the same on every form that opens, so hidden imports and provenance guards are covered.
   - **2c. `bindingScopes` and compact `readFunction`, 0.9K (done).**
     - `bindingScopes` walks the body's binding frames as `WireEncodeCommon.bindingFrames` does.
     - Compact `readFunction` follows `SemanticQuery.focusFunction` and `compactFocus`: the effect requirements, the wait path, the aborts (with the error node's inferred type, from the model reader's node typing), callers, callees, open obligations and the literals. The model reader derives these facts (`focusOf`).
     - Every request's `detail` (and `structuredReview` for a full review) is checked before its operation, as F#'s `encodeProposal` does.
     - Parity: `step2c-focus.jsonl`, and the generated queries ask `bindingScopes` at every node and compact `readFunction` of every function. They also run on the all-kinds snapshots, the milestone-zero graph with an open obligation, and a long literal, and they check that the F# replies show aborts, waits, open obligations, frames and truncated literals.
   - **2d. `readFunction` at `outline` and `full`, `copiedUpdates`.**
     - `outline` (done) follows `WireOutline.focus`: the compact focus and one line per node from the result node through operands and nested bodies, operands named from the visible declarations, cut before the reply passes 16384 bytes. The model reader exposes the node shape it already had (`operandIdsOf`, `scopesOf`, `functionIdsOf`).
     - Parity: the generated queries ask `outline` of every function; a 400-node chain passes the byte budget and caps the indent, and a literal cut inside a surrogate pair keeps its lone surrogate escaped.
     - `full` (done) follows `WireGraph.focus`: the function with its structured body and the node types. `tools/zig/host/graph_kinds.txt` gives each node kind's wire kind and, in wire order, each field's wire name, snapshot property and shape; nested arguments, arms and bindings are rewritten as `WireGraph` names them. Parity asks `full` of every function and checks that the F# replies show every node kind.
     - `copiedUpdates` (done) ports `ExecutionIrBytesOwnership.plan`'s copied updates over the model reader's Execution IR (`executionIrOf`, the lowering `--execution-ir` writes): alias classes through jump arguments and parameter and pattern reads, then the first use of an update's buffer later in its block or in a block reachable from it. It is absent when the model does not lower, as in F#. Parity reads the functions of `BytesTests.ownershipFunctions` (loops, tail calls, conditionals, in-place and copied updates) at every detail.
   - **Viewer data (`viewFunction` done).** The viewer page is one fixed asset that renders viewer data embedded as JSON (decided 2026-10-29, [page format](../developer/architecture.md#human-review-and-projections)), so the Zig host ports no HTML: it writes `viewFunction`'s data byte for byte as `ViewerData` does and embeds it in the same asset, and `viewProposal` and `verifyChange` follow with the proposals of step 3. Parity compares the written pages.
     - `viewer.zig` writes `ViewerData.ofFocus` over `ViewerProjection.focus`: the related signatures, the declaration closure, node roles, relations, branches and value dependencies. `dependency_roles.txt` gives each node kind's operands in `ValueDependency.ofNode` order, each with its property and role; a fast test derives it from `ofNode` over a node of every kind built with every optional part and compares.
     - The asset is the module `viewer_asset` (`src/Nyrwvy.Viewer/asset.zig`, which embeds `viewer.html`); `ZigTools` builds the host with it and hashes its directory into the build key.
     - `view_file.zig` ports `ViewerFile.write` and `ConfinedDirectory`: the output's confinement and refusals, the link check of every component, the generated-file marker, and the write through directory handles into a temporary file renamed over the target. An I/O failure's message names the errno, not .NET's text; parity leaves those out.
     - Parity: each host writes into its own copy of the snapshot's directory, the replies are compared with that directory masked and the pages byte for byte: every function by ID and by name, a page replaced, and each refusal (blank, not `.html`, outside by `..` and absolute, through a link, a file without the marker, a file as a directory).
   - **2e. `executionReadiness` (done).** The model reader lowers the open model (`executionReadinessOf`, the lowering `--execution-ir` writes) and reports the function count, or the projection's or the lowering's case name as `SemanticProjectionFailure` or `ExecutionLoweringFailure`, as `ProjectExecution` does. Parity asks it of every snapshot, the incomplete one included. With it, every read-only operation of step 2 is ported; `previewDependencyUpgrade(s)` comes with the migrations (step 5), `runPure` and `emitCSharp` with steps 6 and 7.
3. **First write path, 4–6K.** This is the first slice an agent can really use. One PR per step:
   - **3a. The deterministic ID seed in the F# host (done).**
   - **3b.** The typed model and generations, loaded from the canonical tree; the writer for canonical `.json` and `.nyrec`; fingerprints from the typed model. No new operation. Parity: every example, constructed and all-kinds snapshot round-trips byte for byte, and its fingerprints are equal.
   - **3c.** Expression recipes (name resolution, inference, materialization) for `simulateCreateFunction(ByName)`, and the proposal review encoder (compact, full, `structuredReview`). It splits into recipe decoding with its rejections and inference with materialization if it outgrows one PR.
   - **3d.** `simulateReplaceBody(ByName)`: the structural transaction for a body replacement, its diff and its review.
   - **3e.** `commit` and `reject`: the stale-base check, revalidation at commit, atomic writes of the snapshot and its sidecars, the `.previous` copies, the lock and `ExternalModification`.
   - **3f.** `readProposal` and `viewProposal` over real proposals; the viewer's `proposalReview` data reuses the focus encoder.
4. **Remaining declarations, 2–3K.** Types, effects, capabilities, `simulateReplaceTarget`, `simulateOperations`, the batch forms and the `…sByName` plural forms.
5. **Migrations, 3–4K.**
   - Parameter type, result type, product field type, capability operation signature, and add-union-variant-like.
   - `previewDependencyUpgrade(s)` and `simulateDependencyUpgradeByName`.
6. **runPure, Zig backend, 1–2K.** Fakes, arguments, the build cache and the worker. The lowering and emitter already exist.
7. **runPure C# backend and C# facades, 5–6K, or not ported (decided at this step).** This covers `emitCSharp`, `externalImplementations` and C bindings. The host would still call `dotnet` to compile generated C#, as it calls `zig` today; that is an external compiler, not a mixed runtime. Rhifill declares only the C# target, so dropping C# drops Rhifill's runPure unless Rhifill moves to the Zig target. Each declared capability shape would then need Zig backend support.
8. **Switch and removal** (below).

The MCP and agent adapters (`tools/nyrwvy-mcp`, `tools/nyrwvy-agent`) get one setting for the host executable. That way their Node tests run against either host from step 2 on.

## How parity with F# is checked

- **Differential transcripts.** Each request stream is fed to both hosts, as separate processes, and every response line is compared byte for byte. Both hosts get a test-only deterministic ID seed (`NYRWVY_TEST_ID_SEED`), because new UUIDv7 IDs and timestamps are the only nondeterministic output.
  - **The seed (step 3a, done).** When the variable is set and not empty, a host's `n`-th new ID in the process (from 1, in allocation order) is a UUIDv7: its 48-bit timestamp is 1 700 000 000 000 ms plus `n`, and its other bits come from SHA-256 of the UTF-8 text `seed:n`. Bytes 6 and 8 take the version (`0x70 | h[0] & 0x0F`) and variant (`0x80 | h[2] & 0x3F`); byte 7 is `h[1]` and bytes 9–15 are `h[3..9]`. The text is the lower-case hyphenated form. `StableIdAllocator.seededPayload` is the F# definition, and a fast test fixes its first values. The variable is read once per process, so the parity tests run the F# host as its own process.
  - **Guard.** A seed set by accident on real data would hand out the same IDs again after every restart. So, under the seed, a host warns on stderr at startup (`Warning: NYRWVY_TEST_ID_SEED is set, so new IDs are deterministic test IDs. Use it only on test copies.`), and every snapshot or sidecar text it reads moves the count past the largest `n` of an ID that the same seed derives (`StableIdAllocator.observe`, over the text, so JSON and records alike). Both hosts read the same files, so their sequences stay equal; a seeded run's IDs are never handed out twice. The Zig host must do the same before it allocates its first ID (step 3c).
- **Files written by commit (step 3e).** Each host runs a transcript against its own copy of the snapshot's directory, and the replies are compared with that directory masked. After every `commit`, every file in both directories is compared byte for byte: the snapshot (`.json` or `.nyrec`), its `.dependency.json` and `.backends.json`, and the `.previous` copies F# keeps. The tests also check that no temporary file is left behind. Each host then reopens the files the other wrote and answers a read transcript the same way. Failures carrying OS text (a read-only directory, a file changed on disk) are compared by code only, as for the views.
- **Corpus.**
  - Transcripts recorded from the existing F# session tests: a recording hook in `AuthoringProtocol.executeLine` under an environment variable.
  - The Node adapter tests and the examples.
  - Rhifill: read-only, and writes on copies.
  - Mutated requests: wrong IDs, bad recipes, conflicting proposals, so that error codes and messages match too.
- **Stored state.** After every commit, the snapshot files and sidecars written by both hosts must be byte-identical.
- **Gate.** A step merges only when the transcripts of its operations are identical, the fast and extended suites pass, and the `ZigSpeed` host rows are recorded.
- **Unit level.** The existing tool parity tests (reader, validator, lowering, emitter) stay until the switch.

## The switch

1. All operations have parity, the adapters' Node tests pass against the Zig host, and Rhifill works through the Zig host. Every read works on the typed model; no part of the host reads the model reader's JSON tree any more.
2. One PR makes the Zig host the default in the adapters and docs, and marks the F# host deprecated in its README and in `docs/decisions/zig-tools.md`.
3. Tag the last commit with the F# host, for example `fsharp-host-final`, and push the tag, so that the F# version stays on the remote (the user's idea: mark F# deprecated, cut a tag, keep it on the remote).
4. Before removal, record the corpus responses of the F# host as golden files. After removal, the Zig host is tested against the goldens instead of a live F# host.
5. A following PR removes the F# projects and the `NYRWVY_ZIG_*_CHECK` modes.
6. Legacy migration (schemas 9–20) stays in the F# tag only; it is not ported (decided 2026-10-28). The viewer is not ported either: its page is a fixed asset, and the Zig host writes its data (step 2).

## Accepted differences

These differences between the hosts are accepted. The parity tests do not cover them, and the switch does not wait for them:
- **I/O failure text.** A file the host cannot read or lock prints `IoFailure: <Zig error name>` (for example `AccessDenied`). The F# host prints .NET's exception message. The code is the same, and the message names an OS condition the user resolves the same way.
- **JSON parser text.** Invalid JSON in a request or a snapshot reports the Zig parser's text after `Invalid JSON:` or `The snapshot is not valid JSON:`, not .NET's. The code and the prefix are the same.
- **Deep JSON in the marker check.** Before decoding, `requireSidecarForResolvedSource` parses a JSON snapshot with .NET's default depth (64) to look for the dependency marker. The Zig host has no depth limit there. A snapshot nested deeper than 64 levels with a marker and no sidecar reports the missing sidecar in Zig, while F# goes on to decode it (and the decode reports the marker as an unsupported property). Real snapshots do not nest that deep at their root.
- **Legacy schemas.** Schemas 9 to 20, for a snapshot or a dependency, are refused with a message naming the migration on the last F# host (not ported).

## Open decisions

- The C# backend (port it, 5–6K, or drop it and move Rhifill to the Zig target): decided at step 7.
