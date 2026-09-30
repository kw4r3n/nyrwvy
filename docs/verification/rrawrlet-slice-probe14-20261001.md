# rrawrlet slice probe 14 — 2026-10-01

**Status:** Complete. A part of rrawrlet that no earlier probe had touched was authored by name only: all of `repo_map.py` except the tree-sitter binding itself. That covers `repo_map`, `source_files` (`git ls-files -z`, or `os.walk` with its pruning and symlink rule), the `paths` filter (`expanduser`, `resolve`, `is_relative_to`), `outline` (the size, NUL and generated-name skips), `_headings` (the `HEADING` and `FENCE` byte regexes), and `_parsed` with `_named`, `_declarator_name` and `_signature` over the syntax tree a `Host` capability returns. The port matched the real Python on **310/310 cases on its first run**, including every host call in order. There were no recipe rejections and no port bugs, and there are no known divergences. **Nothing is blocked and nothing is queued.**

The largest costs are bytes, which the language has no type for, and one performance trap. Bytes travel as Strings of one code point per byte, which kept every byte offset exact. The cost is `bytes.decode("utf-8", "replace")`, written by hand: 4 functions and 228 nodes, 13% of the port. The trap is in that decoder's first draft. It built its output with one `StringConcat` per character. That is quadratic: 97.5 KB took 3.0 s and 195 KB timed out. The language subset already documents this, and the list-and-join form fixed it in 12 requests: 1.56 MB now takes 0.85 s. The syntax tree could not be passed as a recursive run value, because real trees are deeper than the 32-layer bound allows, so the `Host` returns it as preorder text and the port rebuilds it. Throughput is close to Python's: 1,000 files take 1.7 s where Python takes 1.07 s. The 5 s run limit is reached at about 2,500–3,000 files per run.

**Date:** 2026-10-01

Baseline: nyrwvy `7277ecd` (main after #276, clean); rrawrlet `714ed17` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real `repo_map` on Python 3.11.15 with `tree-sitter` 0.26.0 and the grammar packages rrawrlet declares: `tree-sitter-python` 0.25.0, `-javascript` 0.25.0, `-typescript` 0.23.2, `-go` 0.25.0, `-rust` 0.24.2, `-java` 0.23.5, `-c` 0.24.2, `-cpp` 0.23.4, `-c-sharp` 0.23.5 and `-fsharp` 0.3.12. The container's system Python has no tree-sitter, so these were installed into a separate virtual environment. rrawrlet's own `tests/test_repo_map.py` passed there (9 tests).

The method was probe 13's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests (`simulateCreateTypesByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `simulateOperationsByName`, `commit` and `runPureByName`), no `declarations` call, and `runPure` batches through a `runCases` entry. Probe 11's session client and recipe helpers were reused unchanged. None of the earlier ports was reused, because this part shares no logic with them. The harness, cases, snapshots and transcripts are in the project's shared files (`rrawrlet-probe14/`) and are not committed.

## Why this part

`repo_map.py` exercises what probes 4 to 13 left out:

- **Bytes, not text.** The module reads files with `read_bytes`, tests for `b"\0"` in the first 8,192 bytes, splits with `bytes.splitlines()` (only `\n`, `\r` and `\r\n`), matches byte regexes, slices at tree-sitter byte offsets and decodes with `errors="replace"`. The map text is then measured again in UTF-8 bytes against a budget.
- **A syntax tree from a library.** tree-sitter is a C library that the port cannot contain, so it becomes a capability. The logic on top of it is a recursive walk that asks each node for its `named_children` and for its `name`, `body`, `declarator` and `value` fields, and follows C declarator chains.
- **Directory traversal.** `os.walk` with in-place pruning of hidden and vendored directories, its rule of listing a symlinked directory without entering it, and sorted output; `git ls-files -z` as an alternative that may fail; and paths checked against the repository after `expanduser` and `resolve`, where `resolve` can raise for a symlink loop.

The other untouched modules were less new. `results.py`, `verify.py` and `review.py` are record plumbing of the kind probes 9 to 12 covered. `mcp.py` and `web_live_requests.py` are request validation, and `codex.py`, the backends and `tui.py` are process and UI plumbing that would port as a capability surface with little logic left.

## What was ported

| Python | Port |
| --- | --- |
| Host effects: `Path.is_dir`, `exists`, `is_file`, `read_bytes`, `resolve`; `os.path.expanduser`; `os.scandir` inside `os.walk`; `subprocess.run(["git", "ls-files", …])`; `Parser.parse` | Capability `Host` with 9 operations: `isDir`, `exists`, `isFile` (Bool), `readBytes` and `resolve` (`Result<String, String>`, the error being Python's `str(exc)`), `expandUser(part): String`, `listDir(path): Option<List<Entry>>` with `Entry {name, isDir, isLink}` (None when `scandir` fails), `lsFiles(repo): Option<String>` (None when `git` fails) and `parse(extension, source): String` |
| Bytes | Strings of one code point per byte (latin-1). Code-point positions are then byte offsets, so `source.find(b"\n", start_byte)`, `source[start:end]`, `len(source)` and `b"\0" in source[:8192]` are `StringIndexOf`, `StringSlice`, `StringLength` and `StringContains` with no conversion. `bytes.splitlines()` is `StringSplitLines(source, "\n\r")` |
| `bytes.decode("utf-8", "replace")` | `decode`: an all-ASCII input (`StringUtf8ByteCount == StringLength`) is returned as is. Otherwise a fold over the code points carries `{out, need, cp, lo, hi}`. Each lead byte sets how many continuation bytes follow and the allowed range of the first one (`E0`: A0–BF, `ED`: 80–9F, `F0`: 90–BF, `F4`: 80–8F), and a byte outside that range emits one U+FFFD for the cut sequence and starts again. This is Python's "maximal subpart" rule. A cut sequence at the end emits one more U+FFFD |
| `HEADING = rb"^(#{1,6})[ \t]+(.+?)[ \t#]*$"`, `FENCE = rb"^[ \t]*(```|~~~)"` | `headingLevel`: `StringSpan` over `#` gives k. The line matches when 1 ≤ k ≤ 6, the next byte is a space or tab, and at least one byte follows it, because the lazy `(.+?)` then always reaches the end through `[ \t#]*`. So `"#  "` is a heading and `"#######"` is not. `isFence`: a span over space and tab, then `StringStartsWith` |
| `_headings` | A fold over the lines carrying `{number, fenced, found}`; a fence line toggles even inside a fence; the text is `strip()` of the decoded line (Python's 29 whitespace characters) cut to 100 code points |
| tree-sitter's tree | `parse` answers with one line per node in preorder: depth, type, `start_byte`, row, the field the node hangs under, and whether it is named. Every named node is listed, and an anonymous node only when it hangs under one of the four fields the module asks for (C#'s `get` accessor name is one). The oracle checked, for every node in every case, that the port's lookups give the same answer as `child_by_field_name` and `named_children`. `readTree` rebuilds a recursive `Node {type, start, row, field, named, kids: List<Node>}` with a stack: each line first closes the open nodes down to its depth into their parents' `kids` |
| `_named`, `_declarator_name`, `_signature` | `child(node, field)` is the first kid with that field. `declaratorName` follows `declarator` fields until a name type, by self-recursion over `Option<Node>`. `signature` ends at the line end or at the body's start when that comes first, decodes, strips, drops trailing `{`, strips again, and cuts to 99 code points plus `…` above 100 |
| `_parsed`'s nested `walk` | `walk(acc, node, depth, source)` folds `walkKid` over the named kids. A definition that `named` accepts is appended with its signature and entered one level deeper unless it is a leaf. `parameters`, `formal_parameters` and `argument_list` are not entered; everything else is entered at the same depth. `walk` and `walkKid` are mutually recursive, so `walk` was created with a placeholder body and replaced once `walkKid` existed |
| `outline`, `_generated` | Skipped above 1,048,576 bytes, on a NUL in the first 8,192 bytes, or for a generated name. The line count is taken after the skip, the suffix follows `PurePath.suffix` and is lowercased, and Markdown and grammar suffixes are tested against literal lists |
| `_git_files`, `_walked_files` | `gitNames`: split the raw output on NUL before decoding (a NUL is never inside a UTF-8 sequence), drop empties, decode, sort by `StringLessThan` (Python's code-point order) and drop adjacent duplicates, which is `sorted(set(...))`. `walkDir`: the listing's files sorted, then each kept directory (not hidden and not one of the six skipped names), sorted, entered only when it is not a symlink. `walkDir` and its folder `walkStep` are mutually recursive, placed the same way as `walk` |
| `source_files`'s `paths` | `Path(raw).expanduser()`: only a leading `~` part is sent to `expandUser`, and an answer that still starts with `~` is `RuntimeError: Could not determine home directory.` Then `resolve` of the path, and `resolve` of the repository twice (once for `is_relative_to` and once for `relative_to`), in Python's order. `relativeTo` compares resolved paths by whole parts. A `resolve` error, such as a symlink loop, propagates as `RuntimeError` |
| `str(Path(...))`, `Path / name` | `pathNorm`: `//` kept only when a third `/` does not follow, empty and `.` parts dropped, `.` for nothing; `pathJoin`: an absolute name replaces the base |
| `repo_map` | The absolute-directory and `max_bytes >= 256` checks, then an effect fold over the names: `isFile`, `outline`, and a section, an "other" entry, a skip or an error line. `f"{line:>{width}}"` pads to the width of the largest line number. Sections are sorted stably with Markdown last, then fitted to the UTF-8 byte budget with a `Result` fold that stops at the first section that does not fit. The omitted list shows 20 names and `…`, and the other files get the room left, at least 200 bytes |

Case outputs are one string: the four counts, the error lines, then the map, or `ValueError: …` or `RuntimeError: …`. `max_bytes`' type check (`type(max_bytes) is not int`) has no counterpart, because the parameter is `Int64`.

Out of scope: the tree-sitter parse itself (the host's work), `symbols()` (a thin wrapper that nothing in the map uses), non-UTF-8 file names in walk mode (Python gives them lone surrogates and then fails in `len(text.encode())`; git mode, where names are decoded with `replace`, is covered), and the 1 MB skip, which fakes cannot carry and which only the scale runs reached.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 11 in two requests (`Node` alone as a self-recursive group, then `Found`, `Entry`, `Outline` (a Union of `Symbols`, `Text` and `Skipped`), `Section`, `Dec`, `HAcc`, `MapAcc`, `Fit`, `Room`, `Case`); 1 effect; `Host` with 9 operations |
| String helpers: membership in a literal list, repeat, `strip`, `rstrip(chars)` | 6 / 40 |
| `decode` | 4 / 228 (`decStart` 126) |
| Paths: `pathNorm`, `pathJoin`, `pathName`, `suffix`, `expandUser`, `relativeTo` | 7 / 184 |
| `source_files`: git names, `os.walk`, the `paths` prefixes and filter | 18 / 248 (`prefixStep` 61) |
| `_headings` | 4 / 105 |
| Reading the tree text | 5 / 126 |
| `_named`, `_declarator_name`, `_signature`, `walk` | 9 / 257 (`walkKid` 86, `named` 68) |
| `outline`, `_generated` | 2 / 115 |
| `repo_map`: sections, width, sort, budget, omitted and other notes, output | 14 / 405 (`mapStep` 102) |
| Case dispatch | 3 / 25 |
| **Total** | **72 / 1,733** in 156 requests |

There were **no recipe rejections**, and the differential run found **no port bugs**. The two mutual recursions took 2 more requests than direct self-calls would have, through the placeholder-then-replace route that [order 27](../plans/post-m2-roadmap.md#findings-from-the-2026-09-29-probe-8)'s diagnostic names. The literal lists for `DEFINITIONS` (36 names), the 23 grammar suffixes and the other constant sets are about 90 nodes. They are tested with `ListAny` and `StringEqual`, as a `set` would be.

The follow-up to the decoder (below) replaced `Dec` with `DecL` (`out: List<String>`), created `decStartL` and `decStepL`, replaced `decode`, and deleted the old two functions and type by name: 12 requests. The port is then 72 functions and 1,735 nodes.

## Results vs Python

310 cases. Each case is a real directory under `/tmp/rm14/<id>`, built by the case generator, and the oracle ran `repo_map` on it with the host calls recorded. A case matches when its output is equal and the port made the same host calls with the same arguments, in the same order: 4,473 calls in all, including 1,332 `readBytes` and 581 `parse`.

| Group | What it covers | Result |
| --- | --- | --- |
| Grammars (24) | One file per grammar and suffix: Python (classes, methods, decorators, async, nested functions, a `lambda` default), JavaScript (arrow and function expressions in `const`/`let`/`var`, generators, classes, getters, `export default`), TypeScript (interface, type alias, enum, abstract class, namespace, `declare`), TSX, Go (structs, interfaces, grouped types, methods, a function literal), Rust (mod, struct, enum, trait, impls, `macro_rules!`), Java (nested class, interface, enum, record, lambda), C and headers (struct uses without a body, typedefs, function-pointer declarators), C++ (namespaces, constructors, destructors, `operator=`, templates, a lambda), C# (delegate, interface, record, struct, properties with `get; init;`), F# (`.fs`, `.fsi`, `.fsx`); `.PY`, `.mjs`, `.cjs`, `.jsx`, `.mts`, `.pyi`, `.cc`, `.cxx`, `.hh`, `.hpp` | 24/24 |
| Signatures (31) | 100 and 101 characters, long lines with 2-byte, 3-byte and 4-byte characters, a body brace on the same line or the next, `{{`, CRLF and CR-only files, no final newline, Latin-1 bytes, invalid UTF-8 (overlong, surrogates, cut sequences, above U+10FFFF), U+3000 before a comment, a BOM, tabs, 30 nested functions, syntax errors, empty and comment-only files, line numbers of 1 to 4 digits; multi-declarator `const`, C declarator chains, qualified C++ names, F# records with members and `let rec … and` | 31/31 |
| Markdown (16) | Levels 1–7, `#NoSpace`, trailing `##`, tabs, `"#  "` and `"#\t"`, fences with backticks, tildes, indentation, two or four marks and no closing mark, `.markdown` and `.MD`, long headings, invalid UTF-8, CRLF, CR, VT, FF, `\x1c` and NEL (which `bytes.splitlines` does not split), Unicode whitespace to strip, 12,000 lines | 16/16 |
| Skips and text files (17) | NUL at byte 0, 8,191 and 8,192; the four generated names and four suffixes, at the root and in a subdirectory, and near misses (`min.js`, `.map`, `lock`, `package-lock.json.bak`); text files with no outline; line counts over `\r\n`, `\r`, VT and NEL; `PurePath.suffix` edge names (`.py`, `a.`, `dot..py`) | 17/17 |
| Walking and git (12) | Sort order across case, `_`, `é`, `ａ` and an emoji directory; hidden files kept and hidden directories pruned; the six skipped directories at the root and deeper; symlinks to a directory (listed, not entered), a file, a dangling target, a loop and `..`; empty directories; a `.git` file that is not a repository (git fails, the walk runs); git with `.gitignore`, a staged file deleted from the work tree, a non-UTF-8 file name, a symlinked directory and untracked files | 12/12 |
| `paths` and the repository argument (41) | `.`, `""`, a subdirectory with and without `/`, `./sub`, a prefix that is not a directory boundary (`su` against `sub/`), a file, duplicates, absolute inside and outside, `..` out and back in, a missing path, `//`, an error after a valid path, `~/sub`, `~`, `~root`, an unknown `~user`, `sub/~`, `/`; symlinks leading out, in and into a loop (`RuntimeError: Symlink loop from …`); a relative, missing or file repository, `//` and `/./` and `..` in it, `~` and `~user`; `max_bytes` −1, 255 and 256; paths with git | 41/41 |
| Budget (21) | `max_bytes` from 256 to 16,384 over 75 files, the exact boundary byte by byte, Markdown after code, 20 and 21 omitted names (the `…`), 120 other files overflowing their 200-byte room, multibyte names and sections | 21/21 |
| Read errors (2) | `PermissionError` from `read_bytes` becomes an error line and the other files are still mapped | 2/2 |
| Real files (28) | 10 rrawrlet modules under 9 KB, 10 F# files under nyrwvy's `src` below 7 KB (2 of them build-generated `AssemblyInfo.fs`) and 8 decision records | 28/28 |
| Random (118) | 50 Markdown files and 50 Python files with random bytes (cut and invalid sequences, NBSP, U+3000, U+2028, `{`, `#`, VT) in headings and signature lines, with `\n`, `\r\n` or `\r`; 18 random trees of names with walk or git | 118/118 |
| **Total** | | **310/310** |

98 of the 118 random cases have a U+FFFD in their expected map, so the decoder's cut-sequence rule was exercised well beyond the hand-written cases. All 310 cases took 19 runs and 12.5 s (5.4 s for the first, which includes the session's first build, then 0.3–0.5 s).

## Bytes

Carrying bytes in a String worked without exception. Every byte offset tree-sitter reports is a code-point position in the latin-1 String, `StringSplitLines` with `"\n\r"` is exactly `bytes.splitlines()`, and the NUL test, the `find` and the slices are single builtins. No case diverged on a byte position.

What the representation leaves to the program is decoding. `decode` is 4 functions and 228 nodes, 13% of the port, and a first draft is easy to get subtly wrong: the second-byte ranges after `E0`, `ED`, `F0` and `F4` decide whether an overlong form or a surrogate is one U+FFFD or several. Python decodes 1.56 MB in 5 ms. The port takes about 0.6 s above the build. It is called only on single lines and on git's output, so the time never mattered here. *Inference:* a `Bytes` type is not what this port lacked. The only operation it would have added is the decoder, and the latin-1 carrier made every other operation exact.

## The decoder's first draft

The decoder was first written the way the Python reads: the fold's state held the output String and appended to it with `StringConcat`, one character at a time. That copies the output on every step:

| Input | `StringConcat` per character | `ListAppendItem`, then one `StringJoin` |
| --- | --- | --- |
| Identity copy of 97,500 code points | 3.01 s | 0.33 s |
| Identity copy of 195,000 code points | `RunTimeout` | 0.22 s |
| Identity copy of 1,560,000 code points | — | 0.58 s |
| `decode` of 97,500 mixed bytes | 2.50 s | 0.36 s |
| `decode` of 195,000 / 390,000 / 1,560,000 mixed bytes | `RunTimeout` | 0.38 / 0.45 / 0.85 s |

`StringCodePoints` alone over 1,560,000 code points took 0.36 s, which is the build. The [language subset](../reference/language-subset.md) already says this: `ListAppendItem` onto the previous list is amortised constant time, and "repeated `StringConcat` copies its left operand each time and stays quadratic." Probe 12 followed that advice from the start. This probe did not, and no case could have shown it, because cases decode single lines. In `repo_map` the quadratic draft would matter for one realistic input. `_signature` decodes from a definition to the end of its line, so a non-ASCII JavaScript file that is minified onto one line but not named `.min.js` decodes the whole line. *Inference, not run:* with the first draft, a 200 KB line of that kind would time out.

The fix took 12 requests and added 2 nodes. The same 310 cases matched after it.

## Syntax trees at the capability boundary

The natural `parse` operation would return the tree as a value of the recursive `Node` type. A run value may have at most 32 composite layers, and every tree level is a Product inside a List, so 16 levels is the deepest a fake can return. This was checked: a 16-level `Node` chain is accepted, and a 17-level chain is a `ProtocolError`, "Run values may have at most 32 nested composite layers." Real trees are deeper. Over rrawrlet's 63 modules the median depth is 18 and the maximum 30, and 46 are deeper than 16. Over the 50 F# files under nyrwvy's `src` the median is 30, 41 are deeper than 16, and one reaches 613.

So `parse` returns preorder text, and the port rebuilds the tree: 5 functions and 126 nodes. The text is also compact enough for the fake budget: the tree text is about 5 times its source (11.8 KB for a 3.5 KB module). Rebuilding and walking a 1 MB file took about 0.7 s. This is a boundary of scripted fakes and `runPure` values only. Inside the program, `Node` values nest as deep as the tree does.

## Scale

Times are `runPure` wall time. Python times are the best of three runs of the real `repo_map` on real directories with the same files, including tree-sitter's parse. The port's times include rebuilding each file's tree text inside a stateless program-defined `Host` (0.4 s of the 1 MB rows), and the build when the entry is not cached. The files are rrawrlet's `model_health.py` (4,145 bytes, 125 lines, 9 definitions), repeated.

| Input | Python | Port |
| --- | --- | --- |
| 100 / 300 / 1,000 files, git mode | — / — / 1.09 s | 0.68 / 1.06 / 1.98 s |
| 100 / 300 / 1,000 files, walk mode | — / — / 1.07 s | 0.79 / 1.00 / 2.10 s |
| 2,000 / 2,500 / 3,000 files, git mode | — / — / 3.36 s | 5.60 s (the session's first run, with its build) / 3.87 s / `RunTimeout` |
| 10,000 files | 10.6–10.8 s | beyond the run limit |
| One file of 265 KB / 1.04 MB | 0.07 / 0.31 s | 0.70 / 1.37 s |
| Tree text only, 1.04 MB of source (6.3 MB of text) | — | 0.64 s |

Per file the port costs about 1.4–1.6 ms above the build and Python about 1.1 ms, so a whole-repository map runs at roughly Python's speed. The binding limit is the 5 s run limit: about 2,500 files of this size fit in one run. Python needs about 10 s for 10,000 files. A large single file is 3 to 4 times slower than Python, and the 1 MB maximum the module accepts takes 1.4 s.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Quadratic `StringConcat` accumulation is the natural first draft** | The decoder written as in Python timed out at 195 KB; the list-and-join form takes 0.85 s for 1.56 MB. The rule is documented, but nothing at authoring time points to it, and the cases could not show it | None queued. Candidates for the owner: amortise `StringConcat` onto the String the previous concat produced, as `ListAppendItem` already is for lists, so the natural form is linear; or name the cost where it would be seen, in the `StringConcat` recipe reference |
| 2 | **UTF-8 decoding with `replace`** | 4 functions and 228 nodes (13%), the most error-prone part of the port; 0.6 s for 1.56 MB where Python takes 5 ms | None queued. Candidate: a `StringDecodeUtf8Lossy(latin1Text)` builtin with Python's maximal-subpart rule. A `Bytes` type would add nothing else this port needed |
| 3 | **Syntax trees cannot be fake values** | The 32-layer run-value bound allows 16 tree levels; most real files are deeper (rrawrlet median 18, nyrwvy F# median 30). The text form cost 5 functions and 126 nodes | None. This is a harness boundary; program values have no such bound |
| 4 | **Fake budgets split the cases** | 65,536 UTF-16 units per fake argument, with each source counted twice (as `readBytes`' answer and as `parse`'s argument key) and its tree once, limit a differential case to about 9 KB of Python source. 256 argument cases per operation split two batches (4 and 16 cases) on the first run. The 1 MB skip could not be tested against Python | None. Harness reach, recorded with probe 13's |
| 5 | **Mutual recursion needs the placeholder route** | `walk`/`walkKid` and `walkDir`/`walkStep`: 2 extra requests | None. Order 27's diagnostic names the route, and it cost nothing else |
| 6 | **Constant sets as literal lists** | About 90 nodes of literals, tested with `ListAny`; 36 comparisons per tree node for `DEFINITIONS` | None. The deferred Map/set; not visible in the times |

Against the deferred candidates: Map was weakly triggered, only as constant sets. StringFold was not needed, because every text pass is a fold over `StringCodePoints`. The build cache (#274) held across batches: after the first run, every batch took 0.3–0.5 s. Lone-surrogate run values (#275) were not needed, because nothing in this module produces a lone surrogate. Neither `Bits32` nor Float64 was needed.

## Reproducing

The scripts are in the project's shared files under `rrawrlet-probe14/`. `mkcases.py` builds the directories and runs the oracle (`oracle.py`, with `treeser.py` serialising the tree), and needs a Python with the tree-sitter packages above and rrawrlet on `PYTHONPATH`. `author.py <snapshot>` authors the first port, `fix_decode.py <snapshot>` applies the decoder follow-up, and `run_cases.py <snapshot> cases.json known.json` runs the cases. `scale.py`, `scale2.py` and `pyscale.py` give the scale rows. The snapshots before and after the follow-up are `p14-first.json` and `p14.json`.
