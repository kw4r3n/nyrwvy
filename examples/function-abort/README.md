# Typed function abort through ordinary graph tools

This deterministic example builds `readValidated(path, files)` through the same
`nyrwvy_*` tool handlers used by OpenCode and MCP, inspects the reviews, commits,
closes and reopens the snapshot, then executes the common `runPure` runner.
The script supplies symbolic recipes and discovers every system-owned ID from
responses. The language program lives entirely in the semantic graph.

From the repository root:

```sh
npm ci
npm run build:host
node --experimental-strip-types examples/function-abort/run.mjs
```

An optional first argument chooses an output directory. The default is a new
temporary directory. Its `workspace.json` can be reopened with `nyrwvy_open`;
`transcript.jsonl` contains every tool request, review, publication and result.
Creating over an existing snapshot is rejected.

| Scenario | Expected result | Observed operation sequence checked by the example |
| --- | --- | --- |
| Empty input | `Err("empty path")` | No capability calls |
| Read fails after acquisition | `Err("read failed")` | `open → read → close` |
| Read succeeds | `Ok("contents")` | `open → read → audit → close` |

`readBody` matches a failed read and explicitly aborts its own invocation. Its
later audit does not run. The enclosing `UseResource` releases the acquired
handle before returning the body's error. A caller can then match that Result.
The effect contract remains `IO`; abort introduces no nominal effect.

For other errors, nested cleanup, function values and parallel-worker cancellation,
see the maintained [contract](../../docs/specs/typed-function-abort.md) and
[verification record](../../docs/verification/typed-function-abort-20260926.md).
This example is deterministic tool integration evidence, not a live-model trial.
