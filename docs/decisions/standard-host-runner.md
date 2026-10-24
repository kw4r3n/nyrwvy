# A standard host runner for exported pure functions

- **Status:** Proposed
- **Date:** 2026-10-25
- **Issue:** none yet

This decision proposes one generated runner that lets a host process call a program's exported pure functions with JSON over standard input and output, instead of each port hand-writing that glue. Capability passing is a later step; this record says how it would extend. Nothing here is implemented.

## Decision

`emitCSharp` gains an option to also emit a runner for the program: a `Main` that serves the program's **exported pure functions** over JSON Lines on standard input and output.

**Served functions.** A function is served when its declaration is `exported`, it has no capability parameters, and it has no type parameters (step one has no way to pass type arguments). Other functions are not callable through the runner. The C# emitter makes every non-capturing function `public` whatever its `exported` flag (`CSharpBackendEmitter.fs`, function emission), so the runner's own dispatch table, not C# visibility, is the export boundary.

**Wire values.** Arguments and results use the name-resolved run-value form of `runPureByName` ([agent authoring session spec](../specs/agent-authoring-session.md), `runPureByName`): typed objects with a `kind`; Int64 as an exact decimal string; Float64 as a JSON number, or a string for NaN, the infinities and negative zero; Lists with `items`, Options with `hasValue`, Maps with `entries`, Results with `isOk`; Products and Unions with `type`, `variant` and `field` display names. Hosts do not know stable IDs, so the exact-ID form is not accepted. Output carries the same display names and, as `runPure` output does, the IDs beside them. Display names can change between snapshots, so a runner is bound to the snapshot it was emitted from.

**Codec.** The wire *shape* is reused; the decoding *code* cannot be. The JSON codec in `Protocol.fs` resolves names against the session's `AuthoringDeclarations` and lives in the `Nyrwvy.Tools.AuthoringSession` tool assembly, and `runPure`'s program reads a separate internal token format (`PureRun.fs`, argument encoding) through decoders generated from semantic types (`PureRun.fs`, decoder generation). The runner instead gets a per-program JSON encoder and decoder generated at emit time from the served functions' parameter and result types, in the way `PureRun.fs` already generates its token decoders. It enforces the value bounds of `runPure` (32 composite layers, 8192 value nodes and 4096 List elements per argument, 256 fields per Product or variant, 65536 UTF-16 code units per String) and reports a violation with the same `path` / `expectedType` / `actualType` / `reason` details.

**Requests and replies.** One JSON object per line in each direction:

```json
{"id": 1, "function": "planNext", "arguments": [{"kind": "string", "value": "a"}]}
{"id": 1, "ok": true, "value": {"kind": "result", "okType": "...", "errorType": "...", "isOk": false, "value": {"kind": "string", "value": "missing"}}}
{"id": 2, "ok": false, "error": {"code": "RunnerUnknownFunction", "message": "...", "candidates": ["planNext"]}}
```

Arguments are positional, as in `runPure`. Replies come in request order, one per request; `id` is echoed so a host can match them. A `describe` request returns the served functions with their parameter names and types in `TypeRef` form, so a host can check a runner before calling it.

**Error categories.** The runner keeps three kinds apart:

1. **Business errors** are values. A function that can fail returns a `Result`; its `Err` is a successful reply (`ok: true`) with `isOk: false`, exactly as program `Err` is a successful `runPure` execution. `Abort` lowers to returning `NyResult.Err` (`CSharpBackendEmitter.fs`, Abort lowering), so it is a business error too.
2. **Request errors** (`ok: false`, the process keeps serving): unparsable line, unknown or unserved function, wrong argument count, a value that does not decode or exceeds a bound. Codes are runner-specific (`RunnerMalformedRequest`, `RunnerUnknownFunction`, `RunnerArgumentInvalid`).
3. **Runtime failures** (`ok: false`, code `RunRuntimeFailure` with `exceptionType` and `message`): an exception escaping the function. The process keeps serving, because a pure function leaves no state behind.

A **transport failure** is the host's to detect: the process exits, closes its output or stops answering. Stack overflow and other process-fatal errors end the process; the host sees end of stream with no reply for the pending `id`.

**Lifetime.** The process is resident: it serves requests until standard input reaches end of file, then exits with status 0. It never writes anything but replies to standard output; diagnostics go to standard error. Served functions are pure, so there is no state to release at session end; this becomes real with capabilities. The runner sets no per-call time limit; a host that needs one kills the process.

**Generated, not shipped.** The runtime support types (`NyList`, `NyMap`, `NyResult`, `NyStr`, ...) are emitted inline into each program's source (`CSharpBackendEmitter.fs`, support type emission), so there is no shared runtime assembly a library runner could reference. The runner, its codec and its dispatch table are therefore generated into the same source file as the program, and use only `System.Text.Json` from the base library. A shipped library is a later consolidation, not part of this decision.

**Signatures.** The symbol manifest (`CSharpBackendModel.fs`, `CSharpSymbolManifest`) holds only ID-to-name pairs: no parameter or result types and no `exported` flag. The runner is therefore generated inside the emitter, which has the semantic entities, not by an external script from the manifest. `examples/mini-work-planner/materialize.py` shows the external route today: it joins the manifest with `declarations` to fill a hand-written, app-specific `Program.cs` template.

## Reason

The ports so far each wrote the same four pieces by hand: converting between host values and generated C# types, keeping a process resident, telling business errors from transport failures, and releasing state at the end. `examples/verification-planner/runner/Program.cs` and the `mini-work-planner` template parse CLI strings into generated variants and print results by hand, and only work for their own types.

`runPure` already defines a typed JSON value form that covers every runnable type, with bounds and precise decode diagnostics, and agents already write values in it. Reusing that shape means a host can call the runner with values an agent tested through `runPureByName`, and a test case can move between the two unchanged. One line per request and reply over stdio is how the session already talks to its runner worker ([persistent worker](runpure-persistent-worker.md)), though that worker carries an internal format rather than run values; the transport needs no network, port or package.

Pure functions first keeps step one free of host code: nothing the host provides runs inside the runner, so it needs no trust opt-in.

## Consequences

- A port of an app whose core is pure needs no hand-written C# glue: it emits the program with the runner and starts the process.
- Adding the runner does not change the generated program's own code or its symbol names, and a program emitted without the option is unchanged.
- The runner adds a per-program codec to the generated source; it must decode exactly what `runPureByName` accepts for those types. Tests should run the same cases through both.
- Generic and capability-taking exported functions are not served in step one; `describe` lists only what is served.
- The runner is bound to one snapshot. A host that upgrades the program regenerates and restarts the runner.

### How capabilities would extend it

A later step can serve functions with capability parameters. Two routes exist:

- **Host implementations compiled in.** The `hostCapability` binding of `runPure` already compiles C# classes implementing the generated capability interface beside the program (spec, host sources). The runner would accept such classes at build time and bind each capability parameter to one. This keeps the wire protocol unchanged and reuses the existing trust rule that host code is an explicit opt-in.
- **Calls back over the stream.** The runner sends an operation call as a request to the host on standard output and waits for its reply on standard input. Any host language works, but replies must be routed to the waiting call, which collides with concurrent tasks (`ParallelAll`, task scopes).

Either route is where releasing state at session end matters: the runner would dispose capability implementations when input ends. Choosing the route is part of that step.

### Out of scope

Browser and WebAssembly hosts, compatibility libraries that mimic another runtime's API, transports other than stdio (HTTP, sockets), changing the `hostSources` trust model, and per-call time limits or sandboxing.

## Open questions

- **Open:** whether the `PureRun.fs` decoder generator can move from the tool assembly into the C# backend so `runPure` and the runner share one generator, or whether the runner needs its own. Not checked.
- **Open:** whether `emitCSharp` should take a request option or a separate operation should emit the runner, and whether `describe` should report generated C# names as well.
- **Open:** whether the runner's bounds should stay fixed at `runPure`'s values or be configurable by the host.
- **Open:** how an exported function whose name collides with a non-exported one, or a display name that is not unique across declarations, is addressed by name. `runPureByName`'s resolution rules were not checked for this.

## Revisit conditions

Revisit when a port needs capability-taking functions served, when a host needs a transport other than stdio, when generic exported functions need to be called, or when a second backend needs the same runner.
