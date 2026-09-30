# rrawrlet slice probe 9 — 2026-09-30

**Status:** Complete. A new part of rrawrlet was authored by name only: the Team tool relay through which an OpenCode or Claude Leader calls the Team. That is one connection of `TeamToolBridge.handle`, which reads a line, runs `json.loads`, checks the envelope and token, then calls `respond` (JSON-RPC `initialize`, `ping`, `tools/list` and `tools/call`) and `Team.dispatch`. Dispatch covers Python keyword binding, `_bounded_turns`, `Team.wait`'s argument check and `Team.get` for IDs that are not strings. The relay ends with `json.dumps` of the reply. It was the first part to parse and print JSON text itself.

The first version had to emulate code point conversion with a literal table of the Basic Multilingual Plane. It matched the real Python on 212 of 228 relay lines. Every character outside the BMP and every surrogate escape (14 lines) was unrepresentable: a request literal cannot hold an unpaired surrogate, and nothing converts an Int64 to a character. **Order 28 adds `StringFromCodePoint` and `StringToCodePoint`.** Re-authored with them, the port matches on 226/228. The other 2 lines are known divergences: an integer beyond Int64, and a decimal with more significant digits than binary64 holds. The next cost is Float64 decimal text. Python's `float()` and `repr()` were emulated in 250 nodes, and a Float64 the program computes cannot be printed at all. It is queued as order 29.

**Date:** 2026-09-30

Baseline: nyrwvy `10b5e11` (main after #260, clean); rrawrlet `ee03576`. The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`, once from the baseline and once with order 28. The oracle ran rrawrlet's real `TeamToolBridge.handle`, `respond` and `Team.dispatch` with Python 3.11.

The method was probe 8's: the `Nyrwvy.AuthoringSession` JSON-lines protocol (`simulateCreateTypesByName`, `simulateCreateTypeByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `commit` and `runPureByName`) and no `declarations` call. One thing was new on the client side. Recipes were written with pattern references that name their frame (`let(value, lambda v: …)`), and the client computed `scopeDepth` when sending. Scripts, cases and transcripts are kept outside the repository in the project's shared files (`rrawrlet-probe9/`).

## Why this part

Probes 7 and 8 received JSON already decoded, with `json.loads` at the host boundary, and probe 1 left "the leader's LLM tool dispatch (dynamic dispatch by tool name over JSON)" out of scope. The relay is where both live. Its input is one untrusted line of text. Its behaviour depends on Python details: which JSON is accepted, what `str()` prints, and which `TypeError` a `method(**arguments)` call raises. Its output is the exact text the Leader reads.

## What was ported

| Python | Port |
| --- | --- |
| `json.loads(await reader.readline())` | `parseJson(text): Option<Json>`: one `ListFold` over `StringCodePoints` with a `Parser { mode: Mode, stack: List<Frame>, result }` state machine. It follows the C scanner: `NaN`/`Infinity`/`-Infinity`, strict control characters, `\uXXXX` escapes, the last duplicate key winning at the first key's position, and "Extra data" |
| numbers: `int(text)` / `float(text)` | `numParts` checks `NUMBER_RE` against the whole token. An `Int` goes through `StringParseInt64`. A `Float` keeps its value and its Python `repr` text, both emulated (see cost 2) |
| envelope, token, `respond` | `relay(line, token, tools, team: Team): Option<String>`. `None` means no reply: bad JSON, a request or message that is not an object, a wrong token, or an exception that escapes `handle` (an unhashable tool name) |
| `Team.dispatch` and `method(**arguments)` | `dispatch` routes by tool name. `bindArgs` reproduces CPython's binding errors (`got an unexpected keyword argument`, `got multiple values for argument 'self'`, `missing 1 required positional argument`, `missing 2 … 'a' and 'b'`) |
| `Team.get(run_id)` for a non-string ID; `Team.wait`'s check | `getError` (`Unknown attempt: 1.5`, `unhashable type: 'list'`). `waitTool`: truthiness, mode, `type(seconds) in (int, float)` and bounds, then Python iteration of `run_ids` (a list, a dict's keys, a string's characters, or `'int' object is not iterable`) with a `listEffectFold` of `waitCheck` |
| `_bounded_turns` | `boundedTurns`, `boundTurn` and `preview`, with Python dict update order (`upsert`) |
| `json.dumps` (`ensure_ascii`, `", "`, `": "`) | `dumps` and `dumpMember`, which call each other through a placeholder as in probe 8; `quote` maps `escChar` over `StringCodePoints` and joins. Astral characters become surrogate-pair escapes |
| the Team's state | Capability `Team` with 8 operations (`catalog`, `inspect`, `spawn`, `waitCheck`, `wait`, `stop`, `integrate`, `discard`), each typed as the tool schema says and returning `Result<Json, String>` where Python raises `ValueError` |

The oracle ran the real `handle` on an in-memory reader and writer. Its `Team` was a real `Team` instance with a recording fake for each state-dependent method. The fakes have the real signatures and qualified names, so binding errors are CPython's own; `Team.get` and the verbatim `wait` check ran as they are. Each recorded call became an argument-keyed fake case, and the port had to make the same calls with the same typed arguments.

Out of scope:

- The socket, asyncio, the 5-second read timeout, `closing`, and `await_cleanup` for mutating tools.
- Byte decoding: the port receives decoded text, and `runPure` strings must be well-formed UTF-16.
- `spawn` arguments of a type the schema does not allow. What happens to those depends on `Team.spawn`'s body; the port returns no reply and no case covers it.
- Integers beyond Int64.

## What was authored

| Part | Declarations | Functions / nodes |
| --- | --- | --- |
| Types | 10 (`Json` and `Member` in one group, `Frame`, `Mode`, `Parser`, `NumParts`, `SpawnRequest`, `Call`, `Rpc`, `OrdAcc`), 1 effect, 1 capability with 8 operations | — |
| JSON access and Python semantics (`truthy`, `pyStr`, `getError`, `upsert`) | — | 14 / 158 |
| Code point conversion | — | workaround 4 / 117 (`bmpTable` 65); with order 28, 2 / 4 |
| `json.loads` | — | 32 / 1,009 (`step` 141, `floatJson` 91, `numParts` 89, `reprDigits` 69) |
| `json.dumps` | — | 7 / 201 |
| `Team.dispatch`, binding, `_bounded_turns` | — | 27 / 684 (`spawnTool` 104, `waitTool` 70) |
| `respond` and `handle` | — | 9 / 316 (`rpcFor` 65) |
| **Total with order 28** | 10 types | **91 / 2,379**; 206 requests / 208 KB sent |

There were **2 recipe rejections**, both `StructuralRecipeNodeLimitExceeded` (256 nodes):

- The workaround's table, as 256 string literals of 256 code points, came to 257 nodes. It became 64 literals of 1,024 code points.
- `toolsCall` came to 274 nodes, because the "Invalid Team call" reply object was inlined three times. That object became a function.

There were no `scopeDepth` or type errors. Name-framed references removed the depth arithmetic that probe 6 did by hand, but they belong to the client, not to Nyrwvy.

The differential run found **2 port bugs**. Both were fixed and rerun:

- `-Infinity` was never recognised: the check for `-` followed by `I` sat inside the number-character branch.
- `1e400` printed as `1e+400` instead of `Infinity`. The first fix decided overflow from the approximate magnitude, which in turn broke `1.7976931348623157e308` and `5e-324`. The second fix decides overflow from the decimal exponent.

## Results vs Python

228 relay lines. Each check compares the exact reply line (or no reply) and the exact list of Team calls with their typed arguments.

| Group | What it covers | Workaround | With order 28 |
| --- | --- | --- | --- |
| Envelope and `json.loads` (97) | No reply for bad JSON, EOF, a non-object, missing message, a wrong, missing, `5` or `null` token, and extra data. Whitespace, compact separators, duplicate keys, every escape, surrogate pairs and lone surrogates as escapes, raw non-ASCII, `\x7f`, raw control characters, bad escapes and hex. 35 numbers (signed zero, exponents, fixed and scientific `repr`, `1e16`, `1e400`, `5e-324`, the largest finite value) and 12 malformed numbers. Literals, `null` and missing IDs (the `null` line), nesting to 200 levels, trailing commas | 91/97 | 95/97 (2 known) |
| JSON-RPC routing (22) | `initialize`, `tools/list` (the real `TOOLS` and a custom list), unknown or missing or non-string method, a notification, string and bool IDs. `params`: missing, null, a list or a string (`-32602`). Tool names: unknown, int, null, list or dict (no reply). `arguments`: missing, a list, null or a string | 22/22 | 22/22 |
| `team_catalog`, unknown team tool (3) | Junk and `self` arguments ignored; a tool in the list without a method | 3/3 | 3/3 |
| `team_inspect` (23) | All, by ID, bounded turns (last 3, a 2,003-character prompt previewed, `commands` dropped, `turn_count` kept in place). Falsy IDs list all; int, float, `NaN`, `-Infinity`, `1e20`, `True`, list and dict IDs; unexpected, `self` and non-ASCII IDs | 14/23 | 23/23 |
| `team_spawn` (11) | Minimal and full, OpenCode with non-ASCII, missing one or both required arguments, unexpected keys (including `a b` and `it's`), `self`, an error from the Team | 10/11 | 11/11 |
| `team_wait` (42) | Mode and seconds bounds (`True`, `"5"`, `60.5`, `-0.0`, `NaN`, `1e400`, `1e-400`); `run_ids` that are empty, null, a string, a dict, a number or a bool; non-string items; a Leader ID; unknown IDs; missing and unexpected arguments | 42/42 | 42/42 |
| `team_stop`, `team_integrate`, `team_discard` (30) | Success, a Team error, unknown, int, null, list, `False` and float IDs, missing and unexpected arguments | 30/30 | 30/30 |
| **Total** | | **212/228** | **226/228** |

The 16 workaround failures:

- 14 are characters the table cannot produce or print. JSON `😀` and lone-surrogate escapes do not decode, and printing any character outside the BMP gave `\u????`.
- 2 are the known divergences below.

A full run took about 660 s, about 2.9 s per line, mostly the per-run build.

## Scale and depth

Lines were generated inside the program, since a `runPure` String argument holds at most 65,536 UTF-16 code units. Times are `runPure` wall time; about 3.3–3.6 s of each is the build and process start, measured with the smallest input. Python's own relay took 1–9 ms for every line below.

| Line | Size | Port | Python |
| --- | --- | --- | --- |
| `team_catalog` with an ignored string argument (parse only) | 16,000 / 64,000 / 96,000 characters | 3.3 / 3.8 / 5.8 s | reply |
| same | 128,000 characters | `RunTimeout` (5 s limit) | reply |
| An ID of `é` repeated, echoed as `\u00e9` | 64,000 characters in, 384,043 out | 4.0 s | reply |
| An ID `[1, 1, …]` | 16,000 items | 3.9 s | reply |
| same | 64,000 items | `RunTimeout` | reply |
| An ID nested `[[…]]` | 1,000 / 10,000 levels | 3.3 / 3.5 s, reply | `RecursionError`, no reply |
| same | 30,000 levels | stack overflow in `dumps` | `RecursionError`, no reply |

The parser appends one character at a time with `StringConcat`, and `ListAppendItem` copies the list. Both are linear per step, so a long string costs time quadratic in its length. The relay accepts messages up to 4 MiB, but a Leader's tool call is far smaller: the arguments are an assignment and some IDs. Nesting behaves the other way round. The parser keeps its own stack in the fold state, so the port answers where Python's C scanner raises `RecursionError` by 1,000 levels, which gives no reply. Only `dumps` recurses, and it overflows the stack by 30,000 levels. Printing is linear: `quote` maps and joins, and with order 28 each escape is constant time.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **No conversion between a code point and an Int64** | 14/228 lines failed: every astral character and surrogate escape. The workaround cost 4 functions and 117 nodes, including a 64-literal table of the BMP looked up with `StringIndexOf`. A literal cannot hold an unpaired surrogate at all, because the transport rejects it and the snapshot writer replaces it with U+FFFD | **Done (order 28):** `StringFromCodePoint(code): Option<String>` (Python `chr`; surrogate codes give one unpaired code unit, so escapes concatenate into pairs) and `StringToCodePoint(text): Option<Int64>` (Python `ord`). Re-authored: 2 functions, 4 nodes, and 226/228 |
| 2 | **No Float64 decimal text** | Python `float()` and `repr()` were emulated in 6 functions and 250 nodes (`floatJson`, `magnitude`, `pow10`, `reprDigits`, `expText`, `zeros`). They are exact only within Clinger's fast path (at most 15 digits and exponent within ±22). More significant digits than binary64 holds diverge (`1.00000000000000011`), and the finite limits needed the second bug fix. A Float64 the program computes cannot be printed at all: `Json.Float` carries its `repr` text beside the value, and the Team fake had to supply the text of `elapsed_seconds` | **Queued (order 29):** Python `float(text)` over the JSON number grammar, correctly rounded, and `repr(value)` as the shortest round-trip text |
| 3 | **Character-at-a-time text building is quadratic** | See the scale table | None queued. Splitting the line on `"` with `StringSplit` would take string contents whole. Revisit if a real message or result nears the limit |
| 4 | **256-node recipe limit** | 2 rejections: an inlined constant used three times, and a large literal table | None; the limit names the largest subtrees, and both fixes were a new function |
| 5 | **Python int beyond Int64** | 1 known divergence (`12345678901234567890` as an ID) | None; no consumer needs big integers |

Map was not needed: objects in the relay have a handful of keys. Neither were function groups: the one cycle, `dumps` and `dumpMember`, cost one placeholder transaction, as in probe 8. Keyword binding (`bindArgs` and `badKey`, 69 nodes) and Python's truthiness and `str()` were plain data work. Call arguments are reported in parameter-ID order rather than declaration order (already recorded after probe 3), so the harness compared them by name.

## Follow-up: order 29, Float64 decimal text

Order 29 added `Float64ToString(value): String` (Python `repr`) and `StringParseFloat64(text): Option<Float64>` (Python `float` on the JSON number grammar); see the [decision](../decisions/float64-decimal-text.md). The port was re-authored with them in the project's shared probe files as `author29.py`:

- **Parsing.** A number token with a fraction or an exponent goes to `StringParseFloat64`. The grammar check of `numParts` stays, because Python's `NUMBER_RE` rejects leading zeros. Its exponent is no longer parsed into an Int64, so an exponent of any length is accepted, as in Python.
- **Printing.** `Json.Float` holds only its value, and `dumps` and `pyStr` print it with `Float64ToString`.
- **Removed emulation.** `floatJson`, `magnitude`, `pow10`, `reprDigits`, `expText` and `zeros` are gone.
- **Size.** The port shrank from 91 functions and 2,379 nodes to 85 functions and 2,116 nodes, authored in 194 requests with no retries.

| Run | Before | After |
| --- | --- | --- |
| The 228 relay lines | 226/228 (2 known) | 227/228 (1 known: the integer beyond Int64) |
| 182 new float-heavy lines | not run | 180/182 |

The 182 new lines (`mkcases29.py`) each echo JSON numbers as a request ID:

- the smallest normal and subnormal values;
- halfway points below and above the smallest subnormal;
- the largest finite value, and the values just past it that overflow;
- `2^53 + 1` with and without long fractions;
- exact binary expansions, plus one with a trailing digit;
- 300-digit fractions;
- exponents of 20 digits;
- 80 `repr`s of random bit patterns;
- 80 random 16–30 digit decimals with exponents near both ends of the range;
- one array of 40 of them.

Both misses contain `1` followed by 310 zeros. That is integer text, a Python `int` beyond Int64: the same known divergence, not a Float64 error.

The known divergence for more digits than binary64 holds (`1.00000000000000011`) is gone. A Float64 the program computes, such as a fake's `elapsed_seconds`, now prints without carrying source text.
