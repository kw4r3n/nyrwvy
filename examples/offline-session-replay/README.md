# Reproduce the finite offline examples from Git

The four latest offline session examples use exact retained ancestor executables
and snapshots. Reauthoring an ancestor allocates different stable IDs and does
not recreate its historical byte hashes. The required inputs are now part of
this repository; no sibling checkout, private archive or pre-existing `artifacts/`
directory is needed.

`fixtures/retained-inputs/` contains only the four nine-file closures named by
[`additional-admission/fixtures/proof.json`](../offline-session-additional-admission/fixtures/proof.json):
`original-v7`, `parent`, `epoch-parent` and `disposition-parent`. Each file is
stored as gzip, including the exact `Runtime.dll` and its runtime configuration.
The manifest records compressed and raw sizes/hashes and the original proof's
hash. The input pack is approximately 5.5 MB (47.6 MB unpacked). These are the
same retained bytes already authenticated by that proof; historical evidence,
authority, graph IDs and outcomes are not rewritten.

Prerequisites are the normal locked Node dependencies (`npm ci`) and Release
AuthoringSession build (`dotnet build tools/Nyrwvy.AuthoringSession --configuration
Release`), the SDK selected by `global.json`, the exact .NET runtime recorded in
the retained runtime configuration (`Microsoft.NETCore.App` **10.0.12**, with
`rollForward: Disable`), and
the existing Linux disposable-fixture requirements: standalone SHA1 Git, Node
and Git available at `/usr/bin/node` and `/usr/bin/git`, and `/proc`. Dependency
installation may use a local package cache. After setup the commands below use
only tracked sources, installed tooling and fresh disposable local domains;
they do not contact models, providers or services.

Run from the repository root in Nushell, with a new output directory:

```nu
node examples/offline-session-replay/reproduce.mjs additional-disposition artifacts/replay-terminal
# Explicitly run all four maintained oracle drivers serially instead:
node examples/offline-session-replay/reproduce.mjs all artifacts/replay-all
```

Selections are `epoch`, `epoch-disposition`, `additional-admission`,
`additional-disposition`, or `all`. The runner authenticates all retained input
files before executing them, authors the selected graph, reopens/re-emits it,
compiles unedited generated C#, and runs its existing complete runtime matrix in
fresh domains. It copies the new bundle to a moved location, reopens/re-emits
exactly and compiles there. Runtime matrices run once per selected example;
relocation checks do not repeat those matrices. `reproduction.json`, per-example
proof/raw evidence, original bundles and moved bundles remain in the output.
Original and moved graph/source/symbol bytes must match. Fresh compiler binaries
are not required to match historical binary hashes across SDKs or build paths.
The retained historical binaries themselves are checked byte-for-byte.
Fresh domain paths, IDs, PIDs and trace hashes can differ from historical runs;
reproduction checks their contracts rather than claiming identical raw traces.
The existing fixture helpers require a checkout path without URL-escaped
characters; this command does not add non-Linux or such-path portability.

For the original per-example commands, materialize just the inputs first:

```nu
node examples/offline-session-replay/materialize-inputs.mjs artifacts/replay-inputs
node examples/offline-session-additional-disposition/verify-bundles.mjs artifacts/replay-inputs
```

Use `artifacts/replay-inputs/parent`, `epoch-parent`, `disposition-parent` and
`original-v7` for the corresponding positional arguments. An existing output
destination is rejected, including an empty directory; nothing is overwritten.
Missing, damaged or incomplete packs refuse before input output is created.

These are opt-in expensive reproduction commands. They establish the same
finite offline slice and its four unknown outcomes under the original trusted
serialized-writer, immutable-provisioning and intact-storage assumptions. They
add no admission/recovery capability, live-provider guarantee, storage-fault
guarantee or production Rrawrlet submission.
