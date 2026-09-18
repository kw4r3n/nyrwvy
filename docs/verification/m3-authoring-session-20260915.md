# M3 snapshot-backed authoring session verification — 2026-09-15

**Status:** Observed result

This record captures deterministic repository verification for issue #153 and PR #158. It is implementation evidence for the maintained authoring path, not a live model-evaluation result.

## Verified implementation

- Repository: `kw4r3n/nyrwvy-lang`
- Implementation issue: #153
- Implementation PR: #158
- Exact verified implementation head: `214e01c2405f9ac0151d70da14606bba4267104d`
- Branch: `feat/153-authoring-session`
- GitHub Actions workflow run: CI #1155 (`34958868991`)
- .NET SDK: `10.0.401`

The implementation reuses #148 targeted expression replacement and #149 atomic function creation. It does not introduce a second graph authority or expression-construction engine.

## Verification result

The maintained CI path completed successfully at the exact implementation head:

```text
Release build:
  warnings: 0
  errors: 0

Fast suite:
  passed: 256
  failed: 0
  skipped: 0
```

No extended/live suite was added to ordinary CI for this milestone.

## Completion evidence

The focused `AuthoringSessionTests` establish the new session/persistence contracts required by #153:

1. A fresh minimal workspace creates a helper and a caller through the public authoring session, discovers the system-allocated function/parameter IDs, composes the caller against those IDs, and commits both declarations.
2. Closing and reopening the persisted workspace preserves the published snapshot ID, snapshot fingerprint, semantic fingerprint, function IDs, and referenced semantic identities.
3. The reopened session performs a targeted nested-expression replacement through the #148 workflow while retaining the caller root and an unrelated direct-call node identity.
4. Invalid simulation, explicit proposal rejection, and stale proposal commit do not advance the in-memory or persisted authoritative state.
5. A second maintained writer is rejected by the sidecar lock. A persistence I/O failure does not publish the candidate workspace, and an externally modified authoritative file is detected instead of overwritten.
6. The committed workspace projects through `AgentExecution.projectTypedSemanticIr`, lowers through `ExecutionIr.lower`, and emits through the existing C# backend without introducing a new runtime path. Existing backend execution tests continue to provide execution evidence for those established layers.
7. The selected process-boundary JSONL test creates and commits in one process, exits, reopens the same file in a second process, and observes the same committed semantic identity and fingerprints.

The fast suite also keeps the existing exact-review, structural-authority, persistence, lowering, and backend contracts green. One pre-existing authority-surface expectation was corrected to include the `ParameterSlot` recipe case introduced by #149.

## Maintained boundaries

The resulting path is intentionally small:

- explicit `open` or `create` over one selected snapshot file;
- one writer per workspace plus external-modification detection;
- process-local proposal handles;
- existing semantic queries and reviewed mutation workflows;
- strict snapshot-codec persistence before in-memory publication success;
- JSON-lines as the initial process transport;
- semantic/execution readiness kept separate from actual execution.

Type/effect/capability declaration creation remains outside M3 and is queued separately by #154. No MCP/HTTP/cloud service, durable proposal database, semantic merge/rebase, live paid evaluation, or new provider/runtime framework was added.
