# Run saved fixtures through ordinary MCP

For repeated verification, save the complete `nyrwvy_request` input as JSON and
send that object directly. Do not print an encoder's output and copy it back into
a model tool call. This example changes no graph, tool schema or RunValue contract.

Prerequisites: the repository's Node and .NET versions, `npm ci`, and
`dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`.

```sh
node examples/run-fixtures/client.mjs /path/to/workspace.json case-a.json case-b.json
```

Paths are relative to the shell's working directory, or absolute. The snapshot
must already exist. Each fixture is the **tool wrapper** below, not an
AuthoringSession JSON-lines request (do not add `id`):

```json
{
  "op": "runPureByName",
  "arguments": {
    "function": "identity",
    "arguments": [{ "kind": "string", "value": "é é" }],
    "expected": { "kind": "string", "value": "é é" }
  }
}
```

This sample expects an existing `identity(String): String` function; use your
program's function and independently derived expected result. A fixture is data,
not an expression recipe. See [RunValue input](run-values.md) for composite shapes.

The client loads all fixtures first, opens the snapshot through `nyrwvy_open`,
then calls `nyrwvy_request` sequentially with each parsed object unchanged.
Both `runPure` and `runPureByName` are accepted; every case must have `expected`.
The server still owns request IDs, name resolution, type/shape validation,
expected-value comparison and semantic mismatch paths. No simulation, commit,
host source, model call or new batching API is involved. Host sources are disabled
even if the launching shell has `NYRWVY_ALLOW_HOST_SOURCES=1`.

Only short PASS/FAIL/UNKNOWN summaries go to the console. A new temporary
directory holds `transcript.jsonl` with every full request and MCP response,
including expected/actual values, mismatch paths and diagnostics. Its path is
printed before execution. Keep that file with the fixtures when sharing evidence;
it can contain sensitive program data. The client does not truncate responses.
This avoids displaying large results to the model; it does not change server or
host transport size limits.

Mismatches and tool errors fail the run. Missing comparison results are unknown.
A transport interruption records an unknown outcome and stops without retrying;
remaining fixtures are not sent. Reopen and inspect state before deciding what to
run again. Any failure or unknown returns exit status 1. The MCP connection closes
on success or failure. `runPure` remains the bounded child-process execution
described in [SECURITY.md](../../SECURITY.md), not a sandbox.

The deterministic client tests use recorded-shaped fake replies, including a
large response and canonically distinct Unicode strings. Run:

```sh
node --test scripts/tests/run-fixtures.test.mjs
```

The CLI regression also crosses the real SDK and MCP server with a fake
AuthoringSession process, using the SDK owned by the MCP workspace even when
npm installs it below that workspace rather than at the repository root. These
tests verify forwarding and evidence handling, not compiled program semantics. The ordinary MCP integration suite remains the end-to-end contract.
