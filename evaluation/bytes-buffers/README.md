# Bytes and buffers benchmark

Measures today's `NyList`/`NyStr` against in-place `byte[]`/`long[]` for the
[bytes and buffers note](../../docs/plans/bytes-and-buffers.md).

`Program.cs` uses the runtime that every emitted program starts with. Copy it from
any `emitCSharp` output (everything before `public static class NyFunctions`) into
`Runtime.cs` here, then:

```sh
dotnet run -c Release
```

Recorded 2026-10-26 (.NET 10, Release, the runtime emitted at `db6b162`): see the
note's Evidence table.
