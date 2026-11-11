# C# emitter

`root.zig` emits the C# backend from canonical `ExecutionIrSerializer` JSON, with
no managed invocation. The host imports it as `csharp_emitter`:

```zig
const emitted = try csharp_emitter.emit(arena, execution_ir_json, profile);
```

The result is `.artifact` (`source`, `symbols`, `generated_namespace`,
`function_container_name`) or `.failure` (`code`, `message`, `details`). The
symbol manifest and failures use the existing session wire format. Use a request
arena for the returned source, JSON and temporary emission data.

Profiles use stable operation/capability IDs, after mapping-table name resolution:

```json
{"capabilityBindings":[{"operationId":"operation-id","kind":"externalImplementation"}],"concurrentCapabilityIds":["capability-id"]}
```

Binding kinds are `externalImplementation`, `systemConsoleWriteLine`, and `native`.
A native binding adds `native: {handle, call}`; `call: null` declares the operation
unavailable. Native calls have `library`, `symbol`, `arguments`, `returns`, and an
optional `failure`. Native value/argument cases are objects with a lower-camel
`kind`; their canonical payload fields are exercised in `native.zig` tests.

`worker_source` exposes the same worker source embedded by the managed session.
All runtime blocks in `runtime/` are shared resources. `Core.cs` ends before List
and Bytes; `Task.cs` follows OneShot. Preserve their LF and trailing empty lines.

## Focused checks

```sh
zig test tools/zig/csharp-emitter/root.zig -O Debug
zig test tools/zig/csharp-emitter/native.zig -O Debug
zig build-exe tools/zig/csharp-emitter/main.zig -O Debug -femit-bin=/tmp/nyrwvy-csharp-emitter
```

The CLI takes `IR.json [profile.json]` and writes the artifact/failure as JSON.

The F# C# emitter that `parity.fsx` used as its oracle was removed with the
F# authoring layer (audit #7, 2026-11-11), and `parity.fsx` with it. The Zig
emitter is now verified by the host's own tests and the ZigHost parity corpus.

The shared-resource extraction proof (`resource-parity.fsx`, which compared the backend and F# host
DLLs) is at tag `fsharp-host-final`; the F# host was removed.
