# One record per line as the persisted snapshot form

- **Status:** Proposed. Nothing here is implemented; the open points below must be settled before it can be accepted.
- **Date:** 2026-10-23
- **Question:** [Q-001](open-questions.md#q-001--what-becomes-the-permanent-authoritative-persistence-medium), within the [Nyason and storage direction](../concepts/nyason-and-storage.md)

The session persists one strict JSON snapshot ([agent-authoring-session.md](../specs/agent-authoring-session.md#persistence-format)), and Nyason v0 renders the same snapshot as nested tagged records. Both express ownership by nesting: a node belongs to the value graph whose brackets enclose it. The compact JSON is one line, so every commit rewrites the whole file in a diff. Nyason v0 is line-oriented but spends most of its lines on wrappers (`nodes = [`, `result =`, `fields = []`).

## Decision

Propose a Nyason revision whose persisted form is a flat table: one record per line, each record naming its owner explicitly.

```text
file     := header (LF LF declaration)* LF
header   := "snapshot" SP snapshot-id SP "schema=" INT [SP "parent=" snapshot-id]
record   := kind SP subject (SP key "=" value)*
subject  := id | locator
id       := domain ":" uuid
locator  := id "/" role ["/" id]
value    := id | locator | atom | INT | STRING | list | typeexpr
```

- **Identity.** An ID is spelled `domain:uuid` (`node:`, `field:`, `type:`, `parameter:`, …) with the full lowercase hyphenated UUID, so its domain is part of the token rather than implied by the key it appears under. No short IDs, aliases or per-file numbering.
- **Ownership.** Every member record carries `in=` naming its owner: `parameter … in=function:F index=N`, a graph node `in=function:F/body` or `in=node:C/when-false`. A value graph is not a record and gets no identity: it is the set of nodes whose `in=` names its locator, and its result is a key on the owner (`body=node:R`, `when-true=node:R`).
- **Canonical form only.** Declarations are grouped in schema order (types, effects, capabilities, functions, obligations) and sorted by ID inside a group, separated by one blank line. Inside a declaration: the declaration record, parameters by `index`, then the other member records sorted by subject. Collections whose order is semantic keep their declared order; every other collection is keyed and sorted by key. Keys follow a fixed per-kind order; empty collections and absent optionals are omitted; UTF-8 without BOM, LF, single spaces, a final newline. The parser rejects every other spelling with line and column, so file bytes are canonical bytes.
- **Presentation.** Presentation fields are exactly `name=` (and any later `doc=`), so the semantic fingerprint's neutralization becomes dropping those keys.
- **Reading.** Agents and reviewers keep reading through queries and projections. A nested, tree-shaped rendering of one function is a projection generated from the same model, not a second persisted form ([AI-first principles](../concepts/ai-first-design-principles.md)).

`requires_live` from [examples/verification-planner](../../examples/verification-planner/planner.snapshot.json) in this form:

```text
function function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab name="requires_live" exported=true result-type=bool body=node:01a0b371-4487-7e3d-9e9f-0938f8656308
parameter parameter:01a0b371-4487-748b-9bb3-18c78bc3b791 in=function:01a0b371-4487-7aa3-b9d3-2fc69ed62dab index=0 name="input" type=type:01a0b371-055a-7787-8eb2-c71857419553
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
- **Fingerprints simplify.** Because only canonical bytes are accepted, the exact-persisted-source check and the structural fingerprint can become one comparison over the file bytes.
- **The derived index is a direct load.** Each line is a row (`kind`, `subject`, `in`, attributes), which fits the portable snapshot plus derived database layout in the storage direction.
- **Size stays close to JSON.** A throwaway renderer (not the repository codec) gave, for the verification-planner snapshot: JSON 10,337 bytes on 1 line, Nyason v0 18,582 bytes on 503 lines, this form 11,727 bytes on 80 lines. Size is reported, not a target.

### Considered and not chosen

- **A cleaned-up nested Nyason** (ID and kind on one line, wrappers removed). It reads top-down better for one function, but ownership stays positional. It is kept as the projection for reading.
- **Binary encodings.** At this scale they give no concrete benefit and lose diff, inspection and recovery.
- **SQLite as authority.** The conditions under which the storage direction would accept a database authority (large-graph updates, partial loading, concurrency) are not observed yet; the database stays derived.
- **JSONL.** The same row shape, but it repeats quoted keys and leaves IDs as untyped strings.
- **One file per declaration.** It would break the single temp-file-and-rename publication and the exact-source check. Records are self-contained, so splitting later stays mechanical.
- **Lenient parsing.** It would break "file bytes equal canonical bytes". Recovery goes through an explicit repair command that reports what it normalized.

## Consequences

- A new Nyason schema identifier and a new structural fingerprint domain; the existing domains stay frozen. Adoption goes through the existing `migrate` command and its report, never a silent change.
- The dependency sidecar would embed this text instead of the snapshot JSON, once the open point on pin digests is settled.
- JSON remains the strict reference, debug and benchmark codec.

## Open points

1. **Member order of declarations.** The spec says the order of Product fields, Union variants and parameters is meaningful (an identical import must match it, [agent-authoring-session.md](../specs/agent-authoring-session.md#pinned-snapshot-dependencies)), and also that the persisted field order is canonical by ID (`addUnionVariant` under [Operations](../specs/agent-authoring-session.md#operations)). The canonicalization rules depend on which holds.
2. **Per-kind key order.** A schema table fixing the key order of every record kind, including effects, capabilities, obligations and type parameters, which the sample snapshot does not contain and were not rendered.
3. **Fingerprints and pin digests.** Whether the structural fingerprint becomes a hash of the canonical bytes, and how existing dependency pin digests over JSON migrate.
4. **Supported legacy ID spellings.** Whether any persisted ID is not a lowercase UUID, and how it would be spelled.
5. **Reading projection.** Which nested rendering agents and the viewer receive, and whether it is the same text for both.

## Revisit conditions

Accept, change or drop this proposal after an evaluation over the planner snapshot, the largest test fixture and every migrated fixture:

- decode and encode round-trip identity and idempotence on every fixture, and a fingerprint recomputed from the decoded graph equal to the stored one;
- changed lines per authoring operation (add node, rename, add variant, migrate a field type) against JSON and Nyason v0;
- 1,000 single-character and single-line corruptions: none may parse and validate with changed semantics except literal-value edits, and the reported error line should be the mutated line;
- agent comprehension and edit tasks per format, scoring wrong-ID-domain and malformed-structure rates and tokens;
- load time into a row table against the JSON tree walk on a 100-times-larger synthetic snapshot.
