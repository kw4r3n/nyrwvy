# Offline JSON-RPC session continuation

Base: reviewed `e2fc3a0488171d9978a781f519c7a0dca20e1206`. **Depends on PR395
until merged.** This separate feature does not merge PR395. No probe-9 script or
snapshot was found in repository/history; only its verification document is
tracked there. The parser/binding boundary was explicitly re-authored.

[`examples/offline-leader-jsonrpc`](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-leader-jsonrpc/README.md)
documents the supported protocol, limits and reproduction commands. Ordinary
graph recipes own JSON syntax recognition, envelope validation, keyword binding,
dispatch choice, ID quoting/serialization and terminal notification. Validated
Requests enter the maintained `sessionCommandFold`; the host has no parser,
validator, Team registry or parallel controller. Existing generic string,
list, product, recursion, capability and task constructs suffice. One 278-node
recipe exceeded the 256-node authoring limit and was split into ordinary
functions; no expressive gap or core change was required.

| Boundary | Evidence | Limit / next work |
| --- | --- | --- |
| Raw decoded JSON text → graph parser | Exact parser suite, authoring execution and compiled C# | 4,096 code points, 16 containers, unique keys, scalar strings; no byte/framing/socket claims |
| Envelope and keyword binding | Malformed JSON, invalid envelope/types, unknown method/tool, required/extra/self keywords rejected before any command starts | Documented `ping` / `tools/call(session_command)` vocabulary; not historical Python keyword compatibility |
| Request / notification and ID correlation | Explicit null versus missing IDs, integer/string/escaped/Unicode IDs; silent invalid notifications; Member 2 spawned by notification | No batch, real MCP tool catalog, handshake or token/access policy |
| Wire → two Members → integrate / stop → terminal | Both inherited cancellation forms; overlapping providers; once-only Git effects; retained HEAD duplicate; exact serialized post-release terminal report | Finite synthetic sessions only |
| Responsive progress during held integration | Wait/stop replies precede integration release; cancelled queued spawn has no registration/workspace/Git effects | Uses inherited typed admission and Member semantics |
| Wrong-boundary negative controls | Wait-only catch, wrong response IDs, validation after dispatch rejected by identical corresponding acceptance | Deliberately unsafe test flags are excluded from supported runs |
| Save/reopen and moved replay | Exact snapshot/source/symbol/case/declaration/host/assembly bytes, identical outputs and contract observations | Fresh authoring IDs vary; moved replay keeps IDs and source exactly |
| Offline scope | No core/runtime/schema change, live socket/provider, physical integration Git or credential/access change | Historical relay equivalence, budgets, restart, whole-owner policy and broad shutdown remain unverified |

The host/test program is generated separately from symbols and the emitted C#
stays unedited. Thirteen compiled controls retain PR395's Member/command raw
fault identity and throwing frames, worker-local cancellation choice, cleanup
outside that choice, competing release precedence, close cutoff and explicit
worker/supervisor joins. Both success sessions return the same terminal Result
and emit the exact `session/terminal` notification only after release. The
wire ACK means the typed fold returned, not that a scheduled operation completed
or succeeded. Application results and ledger ownership are proven in graph
events and the terminal report; no broader protocol result contract is claimed.

The compiled correlation control changes IDs and fails the exact-output check.
The compiled validation-placement control emits the correct invalid-params
error but starts a spawn and creates a workspace, so the no-dispatch acceptance
rejects it. Invalid requests and notifications are otherwise processed before
the first spawn without Member registration or mutating effects. Real gates
establish ordering; no sleeps or retry-until-pass loops are used. Original
exploratory failures remain in ignored local bundles, separate from final proof.

The final retained bundle is `artifacts/wire/proof-bundle`, with moved replay in
`artifacts/wire/proof-moved`. Its `proof.json` closes snapshot, source, symbols,
declarations, cases, host, compiled assembly, observations and transcript hashes,
and also hashes the authoring/proof source files. `artifacts/wire/checks.json`
records successful check counts and log hashes. The checked-in
[`fixtures/proof.json`](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-leader-jsonrpc/fixtures/proof.json)
retains the compact observations, identities and both hash records. Fresh
authoring assigns new semantic IDs; these hashes identify this particular run.

All **83** parser/envelope/binding/correlation cases passed both authoring
execution and compiled C#. Both real success sessions emitted **100** exact
wire responses, including **72** answered invalid inputs before the first
spawn, and the exact post-release terminal notification. Silent notification
cases include invalid method/arguments and the valid Member-2 spawn. Thirteen
compiled controls passed their respective positive/rejection acceptance. Moved
replay preserved exact required bytes and identical contract observations.

The Release fast-test graph built with one worker and **zero errors/warnings**.
The fast suite passed **864** tests; the focused wire regression passed **1**;
the serial authoring/script aggregate passed **46**; adapter tests **8**, Bun
tests **23**, and serial evaluation/MCP transport tests **15**. Typechecking,
individual fish syntax checks and the maintained fish regression passed. Counts
are observations, not new count gates. No extended backend suite was needed:
only examples, documentation and the Node regression changed.

The isolated checkout initially omitted the workspace-local MCP dependency
directory when copying locked dependencies. One CLI test failed in the initial
aggregate (45/46), then the entire aggregate passed after copying that directory;
the failed log is retained as `aggregate-initial.log`. Bun initially received
relative paths normalized by Nushell; using absolute glob results resolved test
discovery. Both rejected invocations are retained separately. No expectations
were relaxed and no run was retried without correcting its identified cause.
Node child-process execution and VSTest's local test IPC required approved
sandbox exceptions, as in PR395. NuGet restore used only the installed local
cache; no package download, external service or credential change occurred.

No push, PR creation or merge is part of this local proof. Parent review
precedes the authorized non-force feature push and parent PR; PR395 remains
separate, and no branch was deleted.
