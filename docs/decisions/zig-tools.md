# Tools in Zig, starting with the Zig emitter

- **Status:** Accepted (first stage)
- **Date:** 2026-10-27
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#9-tools), §9

§9 makes the formats, not the F# implementation, the contracts, so the tools can later leave .NET (for fewer dependencies). This decision starts that move with one part whose input and output are both fixed text: the Zig backend's emitter, which turns Execution IR JSON into Zig source.

## Decision

- **Zig tools live under `tools/zig/<tool>/`** and build with the Zig pinned in `mise.toml` (0.17.0), one `zig build-exe main.zig` each, with no `build.zig` and no dependencies beyond `std`.
- **The emitter (`tools/zig/emitter`) is a port of `ZigBackend.emit` and `ZigBackend.fakeCapabilities`.** It reads one Execution IR JSON file (the form `ExecutionIrSerializer` writes) named on its command line and prints the source on standard output (exit 0), or the unsupported constructs one per line (exit 2). Anything else is a failure.
- **runPure's Zig route uses it.** The authoring session builds it once per process into the per-user runner cache (`emitter-<hash of its sources>`, moved into place after the build), writes the IR to a temporary file there and reads the source back. `typeOf`, `functionName`, `fakeArgument` and the runner's `main` stay in F#: they are runPure dispatch, not emission.
- **The F# emitter stays as the reference for this stage.** With `NYRWVY_ZIG_EMITTER_CHECK` set (the extended test settings set it), the Zig route also runs the F# emitter and fails with `RunBackendMismatch` on any byte difference, or when only one of them rejects the program; the message lists of two rejections are not compared. So every Zig parity test (`ZigBackendTests`) is also an emitter parity test, and one more test emits every repository snapshot, whole and per entry, through both.
- **Shared text is one file.** The runtime the emitter copies into every program is `tools/zig/emitter/runtime.zig`, embedded in the F# assembly as a resource and in the Zig emitter by `@embedFile`. The case-mapping runs are computed from .NET's Unicode data in F#, the mapping the C# backend applies; the Zig emitter embeds a generated copy (`case_mapping.zig`), and a fast test fails when it differs from what .NET computes.

## Changes to the F# emitter

Its output had to become a function of the IR JSON alone, so the Zig port can reproduce it. Neither change alters what a program does.

- **Order:** functions, blocks, capabilities and operations, jump arguments, `ProductUpdate` fields and callback contexts are taken in the order the IR JSON lists them (sorted by ID), not in the in-memory list order.
- **Instance names:** a generic instance's suffix hashes the IR JSON of its type arguments instead of F#'s `%A` text, which only .NET can produce. The Zig emitter writes that JSON itself, with System.Text.Json's default escaping, because substituted type arguments are not in its input.

## Reason

The emitter's input and output are both text with a byte-exact reference, so the port can be checked completely and every existing Zig test checks it for free. It is also the part a Zig-only tool chain needs first: a runner for Zig programs that does not start .NET to generate them.

## Consequences

- runPure's Zig route needs the repository's `tools/zig/emitter` beside `mise.toml` (the place `zigDirectory` already finds); the first Zig run in a process may build the emitter (a few seconds, cached after).
- A change to the emitter is made in both languages until the F# one is retired; the extended suite reports any difference.
- Unsupported-construct messages name a type by its IR JSON on the Zig route, not by F#'s `%A` text.
- The emitted source now crosses a process boundary, so runPure's 1 MiB output limit (`RunOutputTooLarge`) applies to it; the runtime and case mapping take about 70 KB, so only very large programs reach it.
- The check covers only the instruction kinds the extended suite reaches; on 2026-10-27 that is all 192 the emitter knows (one test exists only to reach the kinds no other Zig test used). A new kind needs a checked program too.
- Strings compare by UTF-8 bytes in Zig and by UTF-16 code units in F#; the two orders differ only beyond the Basic Multilingual Plane, which stable IDs never use.

## Next stage

The suggested second stage, snapshot reading and validation in Zig with matching results on every repository snapshot, is not started. It is about ten times the emitter's logic (about 7,800 lines of F# in the codec, schema, migration and validator, plus the semantic model, against about 800), and the repository holds only three snapshots, which would check it far less completely than the Zig tests check the emitter. Start it with its own scope and a larger corpus (for example the saved run cases' snapshots and generated ones).

## Revisit conditions

Retire the F# emitter (and the check) once the Zig emitter has run unchanged through several backend slices; revisit the boundary if runPure's dispatch (`typeOf`, the runner's `main`) moves to Zig too.
