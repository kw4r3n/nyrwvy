# Verified M1-B/C — Canonical Fingerprints and Reviewed Preview Receipts

## Verified boundary

Exact code head verified locally:

`2f16cd91e42632088b2b59ceb9851f0a3b6e1879`

Local verification environment:

- branch: `work/m1-fingerprint-preview-20260826-0510`
- .NET SDK: `10.0.111`
- clean `bin/` / `obj/` state
- Release build: 0 warnings, 0 errors
- full suite: 111 passed, 0 failed, 0 skipped
- `FingerprintTests`: 6 / 6 passed
- `PreviewReceiptTests`: 6 / 6 passed
- every focused TRX reports `executed = total` with zero failed, error, aborted, or notExecuted results

## M1-B — fingerprint boundary

Two different hashes are intentionally exposed:

- `SnapshotFingerprint` hashes the exact canonical snapshot, including revision identity and presentation/audit state.
- `SemanticFingerprint` hashes a normalized semantic state that removes snapshot revision identity, display metadata, and audit/provenance-only obligation identity while preserving stable semantic identities and semantic structure.

Both are domain-separated SHA-256 values rendered as `sha256:<64 lowercase hex characters>`.

`SemanticFingerprint` is deliberately not an alpha-equivalence or behavioral-equivalence claim. Stable semantic IDs remain part of the fingerprint.

## M1-C — reviewed preview receipt

Workspace preview now carries a receipt bound to:

- transaction ID;
- base snapshot ID;
- base snapshot fingerprint;
- proposed snapshot ID;
- proposed snapshot fingerprint;
- proposed semantic fingerprint.

Reviewed commit re-simulates the transaction and compares the reviewed receipt before publication. It distinguishes request mismatch, base mismatch, and result mismatch. Incomplete publication still requires the explicit `AllowBlockingObligations` policy.

The lower-level M0 commit API remains for compatibility, but the planned agent-facing workflow will expose reviewed commit as the normal publication path.
