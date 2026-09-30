# runPure arguments as input and the build cache — 2026-10-01

**Status:** Implemented and verified. Baseline: `8193d0d`.

Every `runPure` call built a C# program, because the arguments and fake scripts were compiled into it as C# expressions. After compiler warm-up, a build took 90–180 ms for small programs (0.35–1.1 s in the rrawrlet probes). For small runs the build took longer than the run. Two changes remove the build from repeated calls:

- **Arguments and fake scripts are input.** The runner writes them to `input.txt` in the run directory and passes its path as the child's only command-line argument. The generated program decodes them there. The program now depends only on the entry, the emitted snapshot code (entry-reachable functions and the backend profile), the argument *types*, which capability parameters are fakes or host bindings, and the host sources. It does not depend on argument values, fake responses, keyed cases or throw messages. Batched cases of one entry, with or without fakes, therefore build the same source.
- **Built programs are cached in the session process.** `GeneratedBuild.compileCached` keys each build by a SHA-256 fingerprint of every source's name and text plus the build identity: the Roslyn version, the language version, the runtime version, the fixed compiler options and the reference set. A hit writes the cached assembly, PDB and runtime configuration into the run's directory and skips Roslyn. The cache is least-recently-used, bounded by **32 assemblies or 64 MiB** of assembly and PDB bytes, whichever comes first. An entry larger than 64 MiB is not kept. Failed and timed-out builds are not cached, so their diagnostics always name the current run's directory. The cache lives only as long as the session process.

The child process, its 5 s limit, stack overflow reporting, core-dump opt-out, the output envelope and the run directory's lifetime are unchanged. Semantics, diagnostics and result encoding are unchanged, and no client-visible field changed.

## Input format

The input is one line of space-separated tokens. The generated decoder reads them by type, in argument order:

| Value | Tokens |
| --- | --- |
| Unit | `u` |
| String | `s` + Base64 of the UTF-16 code units (native byte order; parent and child share the machine) |
| Bool | `1` / `0` |
| Int64, Bits32 | decimal |
| Float64 | the 16 hexadecimal digits of its bits |
| List | count, then the items |
| Option | `0`, or `1` and the value |
| Result | `1` and the Ok value, or `0` and the Err value |
| Product | fields in canonical (stable ID) order |
| Union | the variant's index in canonical order, then its fields |

A fake binding is read by its generated class: for each operation of the capability, in canonical order, it reads whether the operation is keyed, the number of queues, and each queue's responses (`0` return value, `1` throw message, `2` awaitCancellation value, `3` delay and inner response). A keyed case carries its complete arguments as one key token: `k` and the argument tokens in canonical parameter order, joined by commas. At a call, the generated fake writes the actual arguments in the same form and selects the case with the equal key. This matches the old generated comparison: Strings compare ordinally, Float64 compares by bits with every NaN equal and -0.0 distinct from 0.0, and a null Product, List or String argument matches no case. Key writing stops once the key is longer than every case key.

The response kinds and their order of effects are unchanged. A call is recorded and its response is selected atomically under the fake log's lock. Waits, delays, returns and throws happen outside the lock, in the fake method itself, so runtime frames still start with the `capabilityOperation` frame. Host bindings are unchanged.

## Measurements

Measured on one Linux machine, with the same script on `8193d0d` and on this change (`PureRun.run` from F#, a two-parameter String entry, a new session process):

| Call | Before (`8193d0d`) | After |
| --- | --- | --- |
| First call of the process (compiler warm-up) | 1,639 ms | 1,588 ms |
| First call of a second entry (build after warm-up) | 218 ms | 244 ms |
| Ten more calls of one entry, each with different arguments | median 213 ms (180–236) | median 75 ms (73–79) |

A cache hit costs about 75 ms, most of it starting the child `dotnet` process. A miss costs slightly more than before, because every program now includes the fixed input reader and fake-script classes. Larger snapshots have longer builds, so their hits save more.

Extended Core tests (77), `dotnet test --settings tests/extended.runsettings`, Release:

| | Before | After |
| --- | --- | --- |
| Wall time | 51–55 s | 46–53 s |
| Sum of per-test durations (trx) | 532 s | 366 s |

The wall time is set by the longest test, `StringPositionOffsetTests` (44–55 s), which spends its time running the program, not building it. Tests that call one entry repeatedly gain the most: `Int64DivisionTests` went from 21.9 s to 5.1 s, `ValueEqualTests` from 22.7 s to 11.3 s, `PureRunUnionTests` from 10.0 s to 2.9 s.

## Verification

- `PureRunBuildCacheTests` (Extended):
  - Repeated runs of one entry, with Products of every run-value type (String with spaces, commas, quotes, newlines and astral characters, Int64 and Bits32 extremes, NaN, -0.0 and infinities, nested Lists of Products, Options of Unions and scalar Results), round-trip exactly and hit the cache after the first call.
  - Another entry builds a different program.
  - A body replacement in the snapshot builds again (a miss with a new fingerprint), and the new body's result is returned.
  - Keyed cases with Product and String keys select the right case.
  - Near misses and a key longer than every case key fail as `FakeArgumentsUnmatchedException`, with the call recorded.
  - Ordered scripts that throw (with the exact message and frames), return or run out are all cache hits on the same build.
- `GeneratedBuildTests` (fast):
  - LRU eviction by count and by bytes, an entry over the byte bound, and replacing an entry.
  - Fingerprints differ for a changed source name, text or source list.
  - `compileCached` hits on the second build and the cached program runs.
  - Failed builds are not cached.
- `./scripts/test-fast.fish` and `./scripts/test-extended.fish` pass. No existing test changed.
