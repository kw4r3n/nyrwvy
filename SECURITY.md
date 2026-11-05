# Security

## Reporting a vulnerability

Report suspected vulnerabilities privately through GitHub's "Report a vulnerability" form on this repository's Security tab (private vulnerability reporting), not in a public issue or pull request. Include the affected tool or protocol operation, a minimal snapshot or request sequence that shows the problem, and the Nyrwvy commit you tested. The maintainer acknowledges reports as time allows; this is a one-person research project without a fixed response window.

## What runs code

Nyrwvy's authoring tools validate and review graph changes; publishing a change does not execute anything. Execution happens only through these paths, all of which compile the committed program to C# and run it on the local machine with the permissions of the user who started the tool host:

- `runPure` / `runPureByName` (`nyrwvy_request`) compile the program and run one function in a child process with fake capability responses. The child is bounded in wall time, output size and value size, but these are value-conversion bounds, **not a resource sandbox**: a program can still allocate memory or spin the CPU until the time limit.
- With `"backend": "zig"`, runPure builds the program with `zig build-exe` and caches the runner executable by a hash of its source in a per-user, owner-only directory (`$XDG_CACHE_HOME/nyrwvy/zig-runners`, else `~/.cache/nyrwvy/zig-runners`), never in a shared temporary directory, so another local user cannot plant a runner the session would execute. The Zig tools it builds (`tools/zig/<tool>`, such as the emitter it runs first, built from the sources beside the pinned `mise.toml`) are cached the same way by a hash of their sources, and the IR it reads is written to a temporary file in the same directory. The Zig compiler is `NYRWVY_ZIG` or `zig` on the session's PATH.
- Runner caches under the per-user cache directory (`nyrwvy/zig-runners`, `nyrwvy/csharp-runners`) grow with each distinct program and are never pruned. They hold only rebuildable output, so remove that directory when no host is running to reclaim the space (`rm -rf ~/.cache/nyrwvy`).
- `hostSources` on those operations compile and run arbitrary C# files supplied by the caller. They are off by default and refused with `RunHostSourcesDisabled`; an operator enables them for a whole host process (`--allow-host-sources` or `NYRWVY_ALLOW_HOST_SOURCES=1`), never per request. Enable them only for snapshots and hosts you trust.
- The committed generated runners under `examples/` and anything a user builds from `emitCSharp` output run as ordinary .NET programs.

The MCP server and the OpenCode plugin are local stdio processes; they open no network listener. Snapshot paths given to `nyrwvy_open` are resolved on the server's file system, so an MCP client that an untrusted model controls can read and create snapshot files wherever that process can.

See the [MCP guide](docs/specs/mcp-graph-tools.md#host-sources-run-arbitrary-code) and the [tool contract](docs/specs/agent-graph-tools.md) for the exact configuration.
