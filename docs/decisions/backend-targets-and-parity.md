# Backend targets and the parity check

- **Status:** Accepted
- **Date:** 2026-10-27
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#9-tools), §9, Order step 4

A second backend (Zig) now runs nearly every program, so runs need a way to choose a backend without naming it on every request, and saved cases need a way to check that the backends agree.

## Decision

- **A workspace declares its target backends** in the hand-edited sidecar `<snapshot>.backends.json`: `{"targets": ["csharp", "zig"]}`, in preference order. It is tool configuration, not semantics, so it never enters the snapshot or its fingerprint, and the viewer never overwrites it.
- **`runPure` without `backend` uses the first declared target**; an explicit `backend` always wins. **There is no default** (owner choice, 2026-10-27): creating a workspace writes `{"targets": ["csharp"]}` (keeping one placed beforehand), and in a workspace without the sidecar a run without `backend` fails, naming the file to create. The repository's checked-in example snapshots carry a sidecar; workspaces elsewhere (snap, rrawrlet) need one added.
- **The parity check is the saved-fixture client**, not a new protocol operation: `examples/run-fixtures/client.mjs` with `--backends`, or with the workspace's declared targets when there are several, sends each fixture once per backend and compares `actual` and `calls`, or a failure's code, message, kind, exception type and calls. A backend that reports `RunBackendUnsupported` is reported as unsupported, never as agreement and never skipped silently.
- **Zig runners are cached per user and owner-only**, and `zig` starts from the Nyrwvy installation's pinned toolchain directory (see [SECURITY.md](../../SECURITY.md)).

## Reason

The client already loads fixtures, keeps a full transcript and has PASS/FAIL/UNKNOWN semantics; comparing per-backend replies there needs no protocol, MCP or response-encoding change for its one consumer. A session-side parity operation can follow if agents need parity from inside a session.

## Consequences

- `frames` are not compared: they name generated symbols and differ by backend.
- Fixtures must not carry `backend`; the client sets it.
- A workspace that wants Zig by default lists it first.
- Tests that write a snapshot directly instead of creating it write the sidecar too.

## Revisit conditions

Revisit when agents need parity inside a session (add an operation), or when the sidecar needs tool-owned editing like the snapshot.
