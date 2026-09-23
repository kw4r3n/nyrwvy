# Retained dependency pins (#209)

**Status:** Observed result

The chosen bounded strategy is **retaining immutable selected content**, not a
new semantic dependency-upgrade workflow. New dependency sidecars contain the
canonical strict source snapshot as `snapshotJson`, bound by the existing source
ID/fingerprint and the unchanged local snapshot dependency digest. The original
absolute `path` is provenance, not a reopen/publication prerequisite.

Legacy source-only manifests remain readable without silent migration and keep
their fail-closed source checks until explicitly pinned. CLI `pin --snapshot ...`
and MCP/OpenCode `nyrwvy_open {mode:"pin", ...}` validate the exact old selection
before atomically replacing only the manifest. Optional `dependency` locates an
exact original copy. Different versions, including edits retaining SnapshotId,
are rejected; no review authorization for a semantic upgrade is invented.

An old digest cannot reconstruct lost contents. Recovery of a pre-retention pin
after its source was overwritten requires an exact original copy. New pins avoid
that loss by retaining content at creation. A future deliberate semantic version
upgrade remains a separate reviewed migration feature, not a hidden open option.

## Verification

- Focused xUnit dependency/session suite: 37 passed, including eight new cases.
- Fast xUnit suite: 377 passed.
- Node transport/script suite: 40 passed; MCP stdio suite: 11 passed.
- Shared adapter/typecheck tests: 8 passed; Bun: 31 passed; both typechecks passed.
- Source edits cannot change an existing embedded pin, including a commit with a
  proposal simulated before the upstream edit.
- Deleting the original source and relocating/renaming the local graph plus
  sidecar preserves workspace identities/fingerprints and execution readiness.
- Legacy recovery accepts only the exact selected contents, preserves local graph
  bytes, retains single-writer exclusion and becomes source-independent afterward.
- Ordinary open never writes a format migration; pin is idempotent for a retained
  selection. Dependency discovery returns metadata, not the stored graph payload.
- Modified embedded contents reject against the fingerprint; even substituting a
  matching new fingerprint cannot bypass the local digest binding.
- A manifest change after review invalidates publication and the session.
- Pin cannot attach a dependency to a standalone graph; nested dependency graphs
  remain rejected.
- On this non-root Linux run, read-only directory permissions caused the real
  temporary-write failure during legacy pinning: both files remained byte-exact
  and a later retry acquired the released lock. This permission fixture is not
  asserted on Windows or root runners.
- Real MCP/CLI flow covers upstream publication, cached reopen, legacy version
  mismatch, explicit recovery from an archived original, and malformed manifests.

Persistence scope remains same-directory atomic visibility/process-restart
persistence, not power-loss or cross-file crash atomicity. No local graph codec or
fingerprint format changed. Old clients cannot read the new four-field sidecar;
that tool-level format change is intentional and documented.
