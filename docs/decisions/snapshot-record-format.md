# One record per line as the persisted snapshot form

- **Status:** Proposed. Nothing here is implemented. Every open point below is settled; acceptance waits for the evaluation under Revisit conditions.
- **Date:** 2026-10-23
- **Question:** [Q-001](open-questions.md#q-001--what-becomes-the-permanent-authoritative-persistence-medium), within the [Nyason and storage direction](../concepts/nyason-and-storage.md)

The session persists one strict JSON snapshot ([agent-authoring-session.md](../specs/agent-authoring-session.md#persistence-format)), and Nyason v0 renders the same snapshot as nested tagged records. Both express ownership by nesting: a node belongs to the value graph whose brackets enclose it. The compact JSON is one line, so every commit rewrites the whole file in a diff. Nyason v0 is line-oriented but spends most of its lines on wrappers (`nodes = [`, `result =`, `fields = []`).

## Decision

Propose a Nyason revision whose persisted form is a flat table: one record per line, each record naming its owner explicitly.

```text
file     := header (LF LF declaration)* LF
header   := "snapshot" SP snapshot-id SP "schema=" INT [SP "parent=" snapshot-id] [SP "dependency=" STRING SP "digest=" HEX]
record   := kind SP subject (SP key "=" value)*
subject  := id | locator
id       := domain ":" uuid
locator  := id "/" role ["/" id]
value    := id | locator | atom | INT | STRING | list | typeexpr
```

- **Identity.** An ID is spelled `domain:uuid` (`node:`, `field:`, `type:`, `parameter:`, …) with the full lowercase hyphenated UUID, so its domain is part of the token rather than implied by the key it appears under. No short IDs, aliases or per-file numbering.
- **Ownership.** Every member record carries `in=` naming its owner: `parameter … in=function:F position=N`, a graph node `in=function:F/body` or `in=node:C/when-false`. A value graph is not a record and gets no identity: it is the set of nodes whose `in=` names its locator, and its result is a key on the owner (`body=node:R`, `when-true=node:R`). A `matchUnion` arm, which has no ID of its own, is a record whose subject is a locator keyed by its first variant in canonical order, the key that already orders arms and names their lowered blocks (`UnionMatchArm.key`): `arm node:M/arm/variant:V body=node:R`, with its nodes `in=node:M/arm/variant:V` and its field bindings as `binding binding:B in=node:M/arm/variant:V source-field=field:F`. A [shared arm](match-union-shared-arms.md) also lists every variant it names in canonical order, `variants=[variant:V variant:W]`, whose first item must equal the locator's key; a one-variant arm omits `variants=`, as the JSON form writes `variantId` for one variant and `variantIds` only for several. Every variant belongs to exactly one arm, so the key is unique within the match.
- **Canonical form only.** Declarations are grouped in schema order (types, effects, capabilities, functions, obligations) and sorted by ID inside a group, separated by one blank line. Inside a declaration: the declaration record, its own type parameters and then parameters by `position`, then every other member record (nodes, bindings, arms, fields, variants, operations and their parameters) sorted by subject. Collections whose order is semantic keep their declared order; every other collection is keyed and sorted by key. A member record's place in the file follows its subject even when its collection is keyed by another ID (field bindings by `source-field=`); the reader rebuilds each collection in that key order. Empty collections, absent optionals and `exported=false` are omitted, and a missing collection reads as empty; UTF-8 without BOM, LF, single spaces, a final newline. The parser rejects every other spelling with line and column, so file bytes are canonical bytes.
- **Per-kind keys, derived from the JSON codec.** Record kinds, key names and key order are derived mechanically from the strict JSON codec's writer ([SnapshotCodec.fs](../../src/Nyrwvy.Semantics/SnapshotCodec.fs) `writeType`, `writeCapabilityOperation`, `writeNode`, `writeFunction`, `writeObligation`); the per-kind table is generated from it, not maintained by hand.
  - A JSON object with `id` is a record. Its `kind`, kebab-cased, is the record kind (`fieldAccess` → `field-access`); an object without `kind` takes the singular of its collection (`parameter`, `field`, `variant`, `operation`, `type-parameter`, `binding`, …). An ID's domain is its tag (`node`, `binding`, `capability-parameter`, `type-parameter`, `obligation`, …). Two writer shapes are named explicitly because the rule cannot see them: a type parameter, written as `{typeParameterId, displayName}`, is a `type-parameter` record keyed by `typeParameterId`; a match field binding, written as `{sourceFieldId, binding: {id, displayName?, type}}`, collapses into one `binding` record with keys `in=`, `source-field=`, then the inner binding's keys in writer order.
  - Every other key is the writer's name without its ID suffix (`NodeId`, `Id`; `NodeIds` and `Ids` become a plural), kebab-cased: `sourceNodeId` → `source`, `targetFunctionId` → `target-function`, `itemNodeIds` → `items`. The only renamed key is `displayName` → `name`. Keys follow the writer's property order, after `in=` and `position=`, the ordinal in a collection whose order is semantic (parameters, type parameters, capability-operation parameters).
  - A value graph under key `k` is `k=node:R` on its owner, with its nodes `in=<subject>/k`. A value arm (`bound-body`, `some-arm`, `ok-arm`, `err-arm`) is the same, plus its `binding` record `in=<subject>/k`; it has no arm record. An object without `id` or graph (argument, field-value and capability-argument bindings, implementations, callback contexts) is a keyed list on its owner in writer order, such as `arguments=[parameter:P=node:V]`; positional ID lists (`function-invoke` arguments, `list-construct` items) keep their order.
  - Values: booleans and integers bare, IDs `domain:uuid`, type expressions one token (`int64`, `type:T(a,b)`, `option(a)`, `(a,b)->r`), every other value, closed enumerations included, a JSON string literal spelled as the JSON codec's writer spells it (its escaping is part of the canonical bytes: `+` for `+`, `\uXXXX` for non-ASCII).
- **Presentation.** Presentation fields are exactly `name=` (and any later `doc=`).
- **Resolved local snapshots.** A snapshot that resolves against pinned dependencies carries the same marker as the JSON `dependency` object (`snapshotId`, `digest`) in its header. The dependency identity is a quoted string, copied exactly, because a combined set's ID (`dependency-set-<uuid>`, derived from its members' fingerprints) is not a `domain:uuid` ID of this graph. The key names are provisional.
- **Reading.** Agents and reviewers keep reading through the existing queries and projections, which are built from the model and not from the persisted text. Any later text rendering is a projection, not a second persisted form ([AI-first principles](../concepts/ai-first-design-principles.md)).

`requires_live` from [examples/verification-planner](../../examples/verification-planner/planner.snapshot.json) in this form:

```text
function function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab name="requires_live" exported=true result-type=bool body=node:01a0b371-4487-7e3d-9e9f-0938f8656308
parameter parameter:01a0b371-4487-748b-9bb3-18c78bc3b791 in=function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab position=0 name="input" type=type:01a0b371-055a-7787-8eb2-c71857419553
bool-literal node:01a0b371-4487-75d1-8769-73455dfabb48 in=node:01a0b371-4487-7e3d-9e9f-0938f8656308/when-true value=true
field-access node:01a0b371-4487-778a-a420-c8e771e0e4e2 in=function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab/body source=node:01a0b371-4487-77de-b292-42bfbe1ea1b3 field=field:01a0b371-055a-7539-8422-fc71fd54844f
parameter-reference node:01a0b371-4487-77de-b292-42bfbe1ea1b3 in=function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab/body parameter=parameter:01a0b371-4487-748b-9bb3-18c78bc3b791
field-access node:01a0b371-4487-78e2-afba-ab6e61b98bf9 in=node:01a0b371-4487-7e3d-9e9f-0938f8656308/when-false source=node:01a0b371-4487-7bae-a6f9-a89c83634258 field=field:01a0b371-055a-7233-bd10-f4c455efe13f
parameter-reference node:01a0b371-4487-7bae-a6f9-a89c83634258 in=node:01a0b371-4487-7e3d-9e9f-0938f8656308/when-false parameter=parameter:01a0b371-4487-748b-9bb3-18c78bc3b791
conditional node:01a0b371-4487-7e3d-9e9f-0938f8656308 in=function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab/body condition=node:01a0b371-4487-778a-a420-c8e771e0e4e2 when-true=node:01a0b371-4487-75d1-8769-73455dfabb48 when-false=node:01a0b371-4487-78e2-afba-ab6e61b98bf9
```

## Reason

- **Ownership is not positional.** The principles forbid inferring a node's meaning from serialized position. With nesting, one misplaced bracket moves a node into another graph and the result can still parse. With `in=`, a damaged record is a parse or validation error on its own line. Lexical scope stays checkable from records alone: a reference from X to Y is legal when Y's owner chain is a suffix of X's.
- **Diffs are local.** Adding a node adds one line; renaming changes one line. The whole-file hunk of compact JSON goes away.
- **Fingerprints are unaffected.** `SnapshotFingerprint`, `SemanticFingerprint` and the dependency `digest` already hash the frozen minimum-version canonical JSON of the decoded model, not the persisted bytes ([SnapshotCodec.fs](../../src/Nyrwvy.Semantics/SnapshotCodec.fs) `serializeCanonical`; [schema evolution policy](../specs/schema-evolution-policy.md)). Changing the persisted form is therefore a representation-only migration: existing pins, `--expect` fingerprints, combined dependency-set IDs and receipts stay valid.
- **The derived index is a direct load.** Each line is a row (`kind`, `subject`, `in`, attributes), which fits the portable snapshot plus derived database layout in the storage direction.
- **Size stays close to JSON.** A throwaway renderer (not the repository codec) gave, for the verification-planner snapshot: JSON 10,337 bytes on 1 line, Nyason v0 18,582 bytes on 503 lines, this form 11,727 bytes on 80 lines. Size is reported, not a target.

### Considered and not chosen

- **A cleaned-up nested Nyason** (ID and kind on one line, wrappers removed). It reads top-down better for one function, but ownership stays positional. It remains a candidate agent projection for the evaluation.
- **Binary encodings.** At this scale they give no concrete benefit and lose diff, inspection and recovery.
- **SQLite as authority.** The conditions under which the storage direction would accept a database authority (large-graph updates, partial loading, concurrency) are not observed yet; the database stays derived.
- **JSONL.** The same row shape, but it repeats quoted keys and leaves IDs as untyped strings.
- **One file per declaration.** It would break the single temp-file-and-rename publication and the exact-source check. Records are self-contained, so splitting later stays mechanical.
- **Lenient parsing.** It would break "file bytes equal canonical bytes". Recovery goes through an explicit repair command that reports what it normalized.

## Consequences

- A new Nyason schema identifier, and no new fingerprint domain: a domain changes only when the hashed facts change. The frozen canonical JSON writer stays as the fingerprint encoding even if JSON stops being a persisted form.
- Adoption needs a codec for the new form and a migration that, like the existing `migrate` command (which reads and writes JSON only today), reports before and after fingerprints; the semantic fingerprint must be unchanged, since the change is representation-only.
- The dependency sidecar keeps its JSON wrapper (`path`, `snapshotId`, `fingerprint`) and embeds the dependency in the new form under a new key beside `snapshotJson`; the loader already keys on exact field sets, so old manifests keep loading until migrated. Its `fingerprint` stays the model fingerprint. The embedded text is one escaped line, so upgrades are reviewed through `previewDependencyUpgrade`, not git diff.
- JSON remains the strict reference, debug and benchmark codec.

## Open points

1. **Member order of declarations.** Resolved 2026-10-23. Product fields, Union variants and their fields, and Capability operations are sets keyed by ID: the codecs write them in ID order, and execution, the viewer and the wire output read them in ID order. Only parameter and type-parameter order (function, type and capability operation) is meaningful. Canonical order is ID order for the former and declared order for the latter. The spec and dependency combination were aligned with this; a local workspace keeps append order in memory, since it installs exactly the snapshot it validated, while combined dependencies come back in ID order.
2. **Per-kind key order.** Resolved 2026-10-23: derived from the JSON codec's writer (see Per-kind keys). A throwaway renderer and inverse checked it on every tracked snapshot document and on a synthetic snapshot holding all 204 node kinds and every type, type-reference, capability and obligation form: 217 record kinds, no key collision within a record, and the record form maps back to the same JSON model. Defaults taken where the rule left a choice: writer names kept rather than shortened by an exception list (`required-effects`, `target-function`); `position=` rather than `index=`, which would read like the `index` operand of list operations; value arms without arm records; members sorted by subject, reading order being the projection's job; closed enumerations quoted; operation parameters as `parameter capability-parameter:P`. Only 29 node kinds occur in tracked snapshots, so the evaluation should add fixtures for the rest.
3. **Fingerprints and pin digests.** Resolved 2026-10-23: unchanged (see Reason and Consequences). Hashing the new form's file bytes was rejected: the format migration would change every fingerprint, which cascades into combined dependency-set IDs written in dependent snapshots and invalidates reviewed `--expect` values, while detecting no tampering that the model fingerprint misses, since only canonical bytes parse.
4. **Supported legacy ID spellings.** Resolved 2026-10-23: out of scope. IDs written before the stable-ID scheme settled need no spelling in the new form; only the full lowercase UUID is accepted.
5. **Shared match arms.** Resolved 2026-10-23: keyed by the arm's first variant in canonical order, with `variants=` listing all of them (see Ownership). Giving arms their own IDs was rejected: it adds an identity the model does not have and a schema change. Cost: when that first variant leaves the arm (removed, or the arm is split), the arm record and the nodes and bindings it owns directly change their locator (nodes in nested graphs keep theirs), so the diff is wider than the semantic change; the graph itself is unaffected.
6. **Reading projection.** Resolved 2026-10-23: unchanged. Agents already read the in-memory graph through session queries (`nyrwvy_inspect`, `readFunction`, whose function focus is structured JSON with sorted nodes, [WireGraph.fs](../../tools/Nyrwvy.AuthoringSession/WireGraph.fs)) and the viewer renders HTML from the same model; neither reads the persisted text. The persisted form therefore needs no companion projection. A text rendering for agents (nested or record form) is added only if the agent comprehension evaluation under Revisit conditions shows it beats the current focus JSON; no such comparison exists yet.

## Revisit conditions

Accept, change or drop this proposal after an evaluation over the planner snapshot, the largest test fixture and every migrated fixture. The [evaluation plan](../experiments/snapshot-record-format-evaluation.md) fixes the corpus and pass criteria; its agent comparison decides only open point 6, not acceptance:

- decode and encode round-trip identity and idempotence on every fixture, and a fingerprint recomputed from the decoded graph equal to the stored one;
- changed lines per authoring operation (add node, rename, add variant, migrate a field type) against JSON and Nyason v0;
- 1,000 single-character and single-line corruptions: none may parse and validate with changed semantics except literal-value edits, and the reported error line should be the mutated line;
- agent comprehension and edit tasks per format, scoring wrong-ID-domain and malformed-structure rates and tokens;
- load time into a row table against the JSON tree walk on a 100-times-larger synthetic snapshot.
