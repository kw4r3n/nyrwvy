# Task communication probe 26

Harness for [probe 26](../../docs/verification/task-communication-probe26-20261007.md): a reduced `codex.py` client written as an event loop (`eventloop.mjs`) and as a reader task with scoped `OneShot` chains (`oneshot.mjs`), against the same specification (`SPEC.md`), declarations (`declare.mjs`) and scenarios (`cases.mjs`). `spike-chain.mjs` is the smaller check that ran first.

From the repository root, after `npm ci` and `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`:

```sh
node evaluation/task-communication-probe26/eventloop.mjs
node evaluation/task-communication-probe26/oneshot.mjs
node evaluation/task-communication-probe26/spike-chain.mjs
```

Each program prints `PASS` per scenario and its node count per function. Snapshots and transcripts go to a new temporary directory.
