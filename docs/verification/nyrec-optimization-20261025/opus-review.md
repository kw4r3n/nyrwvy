Model: claude-opus-5-5. Fixed authorized implementation/test diff and related source snippets; no tools. This is the original review; dispositions are in the main report.

# Review: performance changes (5 production files)

## Verdict

**Approve with minor follow-ups.** I found no blocking correctness or trust-boundary defect in the payload. Each optimization keeps the old observable behavior for valid inputs, with two exceptions noted below. Both depend on context outside the payload.

## Per-change assessment

**Fingerprint.fs:15-24 (IncrementalHash).** The hashed byte stream is the same: domain bytes, then `\0`, then content. The output is unchanged, and the domains are untouched.
- Perf note only: this still allocates two arrays plus a native hash handle per call. For small inputs it may not beat one-shot `SHA256.HashData`. Your separate measurement should settle this.

**Fingerprint.fs (removed `normalizeTypeRef`).** The removed function only rebuilt the same structure. `TypeRef` has no display text, and `TypeParameter of id` carries no name (Foundation.fs:91-103).
- In the pre-21 canonical form, occurrence names come from the declared lists. `normalizeTypeParameters` still neutralizes those lists.
- Canonical bytes are therefore unchanged.

**SnapshotCodec.fs:61-84 (`ensureProperties`).** Error precedence is unchanged:
- A malformed name still raises during enumeration, before any duplicate, missing or unsupported diagnostic.
- Duplicates are still compared by decoded name, ordinally (`HashSet` with `Ordinal` matches F# `Set<string>`).
- The missing and unexpected lists are still emitted as sorted `Set`s, so message text is unchanged.
- The shared static sets (`unaryNodeProperties`, `typedDeclarationProperties`) are immutable and safe to share.

**SnapshotCodec.fs:1339 (`GetBuffer`).** This is correct only if `stream` is an exposable `MemoryStream`, such as one built with `new MemoryStream()`. A buffer-backed stream without `publiclyVisible=true` would throw `UnauthorizedAccessException`, which the `with` does not catch.
- Passing tests suggest the stream is exposable, but its construction is not in the payload.

**Validation.fs:229-234, 2500, 2539 (lazy `Dictionary`).** Last-wins insertion matches `Map.ofList` for duplicate IDs, and the new tests pin the diagnostics. The map is built only when obligations exist.
- This assumes `StableId` equality and hash are structural and consistent with its comparison. That is likely, but the definition is not shown.

**Workspace.fs:~168-172 (rename-only reuse).** Review binding is intact:
- `ProposedSnapshotFingerprint` is still computed from the exact proposed snapshot.
- `MatchesRequest` still checks the exact ordered request.
- The proposed snapshot is still validated (readiness evaluation and canonical serialization).
- Normalized base and normalized proposed are structurally identical for all 9 `RenameTarget` cases (Transactions.fs:1562-1579 shows all of them). So the reused value is the same, and so is any normalized-validation error, including its message, because IDs and names are neutralized.

## Remaining concrete risks

1. **GraphOrder.fs:~190-201: fast path uses reference equality where the old path used structural `=`.**
   - If `SemanticNodeKind.Float64Literal` holds a `float` (Validation reads it via `readFloat64Bits`), F# `=` uses PER semantics. A body containing a NaN literal never equals itself.
   - Before: an unchanged function with such a body and a pre-existing conflict was reported as a *new* conflict (`old.Body = fn.Body` was false).
   - After: a function-metadata-only edit takes the `ReferenceEquals` fast path and admits it.
   - This relaxes admission (arguably a fix, and consistent with the doc intent), but it is an observable compatibility change. The slow path keeps the old quirk, so results also depend on whether bodies happen to be reference-shared.
   - Action: decide the intended semantics. Then either document it or make the slow path's equality NaN-reflexive, and add a test either way.
   - This is a hypothesis until the payload type is confirmed.

2. **Workspace.fs:~169: `List.forall` is true for an empty `Operations` list.**
   - An empty request is therefore treated as rename-only.
   - This is only correct if `Transaction.simulate` makes no semantic change outside the applied operations, such as obligation repair, post-op normalization, or migration. That part of `Transaction.simulate` is not in the payload.
   - Action: add a test for empty operations, or one asserting that simulate rejects them. Also add a test where a rename is followed by an operation that is a no-op structurally.

3. **Workspace.fs:~169: future-coupling risk.**
   - The shortcut matches any `RenameDisplayName _`. A future `RenameTarget` whose text is semantic, or is not neutralized by `SemanticFingerprint`, would silently report a stale semantic fingerprint.
   - Binding would still hold through the exact fingerprint, but the reported value would be wrong.
   - The PreviewReceiptTests theory covers today's 9 cases only.
   - Action: enumerate the targets explicitly in the predicate, or add a reflection-based test that fails when `RenameTarget` gains a case.

4. **FingerprintTests: golden-value provenance.**
   - The theory proves preservation only if its hashes were generated from the *pre-change* build.
   - Action: confirm they were captured before these five files changed, not taken from the new output.

5. **Codec coverage gap (minor).**
   - The new precedence tests cover only the top-level `Snapshot` object.
   - Action: one nested case, such as a duplicate key inside a unary node using the shared `unaryNodeProperties`, would guard the shared-set refactor.
