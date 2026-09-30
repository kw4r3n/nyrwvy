# rrawrlet slice probe 16 — 2026-10-01

**Status:** Complete. A part of rrawrlet that no earlier probe had touched was authored by name only: `results.py`'s `application_status` and `apply_result`, with every `checkout.py` helper they call (`normalize_exclusions`, `excluded_path`, `repository_marker`, `classify_source`, `check_source_index`, `file_state`, `symlink_state` with its overlay of pending changes, and `preserve_file_bytes`). Git, the file system and rrawrlet's record reader are a 16-operation `Host` capability; everything above them is in the port, including SHA-256 of file contents, UTF-8 decoding with Python's error text, `repr()` of file names and the receipt's JSON. The port matched the real Python on **518/518 cases on its first run**, including all 11,901 host calls in order. There are no known divergences inside the scope below. **Nothing is blocked.**

One cost limits reach. There is no Map, so the natural port compares the two trees by looking each name up in both lists. That is quadratic: comparing trees of 16,000 files exceeds the 5 s run limit, and an apply that changes every file does so at 12,800 files. Python's dicts do the same in milliseconds. A Map-free rewrite (sort both trees, then one merge-join fold) took 7 requests and 327 nodes, matched the same 518 cases, and handles 64,000 files in 1.5 s. It is not an algorithm the Python contains. Map, deferred since probe 13, is the candidate this evidence supports; it is not queued.

**Date:** 2026-10-01

Baseline: nyrwvy `084890c` (main after #277, clean); rrawrlet `4276240` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real functions on Python 3.11.15 against real Git 2.43.0 repositories. rrawrlet runs Git inside `bwrap`, which this container does not have, so the oracle passed `command=("/usr/bin/git",)` to `checkout_git`, the documented bypass that `Team` uses. The sandbox limits what Git may write, not what it answers.

The method was probe 14's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests (`simulateCreateTypesByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `commit` and `runPureByName`), no `declarations` call, and `runPure` batches through a `runCases` entry. Probe 11's session client and recipe helpers were reused unchanged. SHA-256 follows the structure of the [Bits32 verification](bits32-20261001.md)'s port, re-authored here over latin-1 bytes instead of UTF-8 text. The harness, cases, snapshots and transcripts are in the project's shared files (`rrawrlet-probe16/`) and are not committed.

## Why this part

`results.py` is the step that writes an agent's result into the user's own source tree, so most of its code is refusal logic. It exercises what probes 4 to 15 left out:

- **Two trees compared by name.** `tree_entries` parses `git ls-tree -r -z` into a dict per commit, and `changed_files` takes the sorted union of both key sets where the entries differ. Every changed name is then looked up again in both dicts, and pending links go into a dict keyed by path.
- **Symlink resolution by hand.** `symlink_state` with pending changes walks a link target component by component, follows up to 40 links from either the pending changes or the disk, never follows the old source below a replaced path, and refuses a result that would leave the source. `file_state` refuses a symlinked or repository ancestor without following it.
- **Content identity.** A source file's state is its mode and the SHA-256 of its bytes, compared with the state of the recorded base blob, so an edit made since `prepare` is refused.
- **Loosely typed records.** `run.json`, `summary.json` and `apply.json` are JSON. The code relies on `isinstance`, truthiness (`if preparation`, `summary.get("stop_reason")`), `is not False`, `type(x) is int` and dict equality.

The other untouched modules were less new: `verify.py` and `review.py` are thin callers of `results.py` and a model backend, `web_live_requests.py` validates JSON much as probe 15's `mcp.py` does, and `codex.py` is process plumbing. Probe 15 took `mcp.py` in parallel.

## What was ported

| Python | Port |
| --- | --- |
| `load_run` (records.py), `read_record` | `Host.loadRun(value): Result<Run, String>` with `Run {path, metadata: J, summary: J, finished}`, and `readRecord(path): Result<J, String>`. `J` is a JSON union (`Null`, `Bool`, `Int`, `Float` as its `repr`, `Str`, `List`, `Obj` of `Mem {key, value}`). Errors carry Python's `TypeName: message` |
| `checkout_git` | `git(cwd, argv, input, scope): Result<String, String>`: argv NUL-joined, stdin and stdout as latin-1 Strings, `scope` naming `writable`, `writable_paths` and `ok_codes`. A failure is `RuntimeError: <stderr>` |
| `Path.resolve`, `lstat`, `is_dir`, `is_file`, `is_symlink`; `os.path.lexists`, `os.readlink`; reading a file | `resolve`, `lstat` (`Missing`, `Link`, `Dir`, `Reg`, `RegX` or `Other`), `isDir`, `isFile`, `isSymlink`, `lexists`, `readlink`, `readBytes` |
| `TemporaryDirectory`, `mkdir`, `write_text`, `write_json` | `tempDir(prefix)`, `cleanupTemp`, `mkdirs`, `writeText` and `writeRecord`, all returning Unit. The port writes the receipt's text itself (`json.dumps(indent=2, ensure_ascii=False)`), and cleans the temporary directory on the error path as `with` does |
| `PurePosixPath` | Paths are Strings in `str(Path(…))` form: `pnorm` (a `//` root kept only when no third `/` follows), `pjoin`, `pparent`, `pparts`, `parents`, `inParents`, `relTo`, `relPath`, `pname` |
| `hashlib.sha256(data).hexdigest()` | SHA-256 over the code points of a latin-1 String, with `Bits32` |
| `bytes.decode()`, `os.fsdecode` | One decoder with two modes. Strict mode stops at the first bad sequence (a `Result`-returning `ListEffectFold` folder) and reports `UnicodeDecodeError: 'utf-8' codec can't decode byte 0xff in position 3: invalid start byte`, `bytes in position 0-1: invalid continuation byte` or `unexpected end of data`, with Python's maximal-subpart ranges. Escape mode turns each byte of a bad sequence into a lone surrogate U+DC80–U+DCFF, as `surrogateescape` does |
| `repr(str)` | Quote choice, `\\`, `\n`, `\r`, `\t`, then `\xNN`, `\uNNNN` or `\UNNNNNNNN` for code points `str.isprintable()` rejects: controls, format characters, private use, surrogates, separators other than space, from a table of 19 ranges |
| dicts keyed by name and by path | `List<Entry>` and `List<Change>` searched with `ListFilter` then `ListItemAt 0`; `sorted(set(…))` as `ListSortBy` and dropping adjacent duplicates; `old.get(n) != new.get(n)` as `ValueEqual` of two `Option<Entry>` |
| `any(… for …)` over effectful checks | A fixed set is a `BoolOr` chain, whose right operand runs only when needed. Over a list (`repository_marker` of every parent) it is a `ListEffectFold` whose folder returns `Err` at the first hit, which stops the fold |
| the `while pending:` loop of `symlink_state` | `slLoop` and `slFollow`, mutually recursive over `SL {resolved, pending, followed, affected}`; `Err` carries the state when `os.readlink` fails or the 41st link is followed, because the `except` clause needs `affected` at that moment |
| `part.lower() == ".git"` | `StringToLower` equals `".git"` and the part has no U+0130. `StringToLower` maps `İ` to `i`, and Python to `i̇`, so `.gİt` would otherwise be refused as Git metadata where Python accepts it |
| truthiness, `is not False`, `type(v) is int`, dict `==` | `truthy(J)` (with `0.0` and `-0.0` false); `ValueEqual` against `Some(Bool false)`; the `Int` variant; `objEq`: an object with as many members as the expected one and each expected member present (keys are distinct) |

Case outputs are one String: `pending`, `applied`, `no_changes` or `unavailable` for `application_status`, and for `apply_result` the returned dict (`source`, `base`, `result`, `dry_run` and the file list) or `TypeName: message`.

Out of scope: the Git sandbox itself; `repr` of unassigned code points, which needs the Unicode database (63 of 216 random `repr` samples in the unit check hit one and were excluded); integers beyond Int64 in records; and races between checks and writes, which a replayed host cannot produce.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 14 in three requests (`J` and `Mem` as one recursive group; `Kind`, `FState`, `Change`, `Entry`, `Run`, `Saved`, `Case`, `Sha`, `Rnd`, `Dec`; then `SL` and `Prep2`); 1 effect; `Host` with 16 operations |
| String and list helpers | 11 / 78 |
| `repr` | 6 / 153 |
| Paths | 11 / 203 |
| JSON values, receipt equality and `json.dumps` of a String | 16 / 200 |
| UTF-8 decoding, strict and `surrogateescape` | 8 / 429 |
| SHA-256 | 19 / 483 |
| `normalize_exclusions`, `excluded_path` | 4 / 104 |
| `repository_marker`, `classify_source` | 4 / 172 |
| `check_source_index` | 4 / 92 |
| `symlink_state` | 4 / 296 (`symlinkState` 127, `slLoop` 93) |
| `file_state`, `current_symlink_target` | 5 / 208 |
| `saved_result`, `tree_entries`, `changed_files` and the list lookups | 13 / 269 |
| `prepared_source`, `apply_receipt`, `application_status` | 7 / 220 |
| `apply_result`, `preserve_file_bytes` | 12 / 568 (`applyResult` 110, `applyIn` 79, `changeStep` 75) |
| Case dispatch | 2 / 24 |
| **Total** | **126 / 3,499** in 132 requests |

The helpers below `normalize_exclusions` (1,546 nodes, 44%) are library behaviour: `pathlib`, `hashlib`, the UTF-8 codec, `repr` and `json`. They were checked on their own against Python before the cases were built (paths, 216 `repr` strings, 315 byte strings in both decode modes, SHA-256 at the padding edges and on 1,000 random bytes). That check found **2 port bugs**: `pjoin` joined onto `/` as `//name`, which `PurePosixPath` keeps as a distinct root, and the SHA-256 padding length was taken before the `0x80` byte instead of after it. Both were fixed before the case run.

There was **1 recipe rejection**: a `ResultOk` in `checkSourceIndex` named `Bool` as its error type where the branch's other side was `Result<Bool, String>` (`PureExpressionBranchTypeMismatch`, with the path to the node). The mutual recursion `slLoop`/`slFollow` took 1 more request through the placeholder route. The authoring script itself failed four times in Python before the clean run: unbalanced parentheses twice, a callback created before the function that uses it (`Unknown function 'escByte'`), and a stale session handle. The 132 requests above are the clean run.

## Results vs Python

518 cases. Each is a real prepared run under `/tmp/rs16/<id>`: a source (a Git repository, or a plain directory with exclusions), a prepared repository with base and result commits, and a run directory with `run.json` and `summary.json`, then any edit the case makes. The oracle ran the real function once and recorded every host call. A case matches when its output is equal and the port made the same host calls with the same arguments in the same order: 11,901 calls, including 5,795 Git commands.

| Group | What it covers | Result |
| --- | --- | --- |
| Status and records (95) | `pending`, `applied` after a real apply, `no_changes`; the receipt with `version` `True`, `2`, `"1"`, `1.0`, an extra or missing key, its keys reordered, another repository, a list, invalid JSON and a directory; an unfinished run, a missing run, no or a non-string repository, `preparation` `null`, `{}` (so `base` comes from the metadata), `{}` with a valid `base`, and a string; base commits short, uppercase, an integer, absent from the repository and SHA-256 length; result equal to base, result and base swapped; a failed, missing or duplicated Leader; `stop_reason` `"user"`, `""`, `0`, `false`, `[]` and `0.0`; `worktree_dirty` `true`, `null`, `0` and missing; the source missing, an integer, equal to the repository, or reached through `..`; the recorded repository with `/` or `/.`; `source_kind` `svn`, `null` and missing. Each as `application_status` and as a dry-run `apply_result` | 95/95 |
| Files (30) | Changes, additions in new directories, deletions of whole directories, mode `100644` ↔ `100755`, binary and empty files, Japanese and accented names, spaces, quotes, tab, backslash, newline and DEL in names. Each as status, dry run and real apply | 30/30 |
| Symlinks (78) | Added, retargeted, deleted, becoming a file and the reverse; escapes by `../..`, `..` from a subdirectory, `.`, an absolute target, a dangling target; loops of 2 and of 1; chains through pending links, a chain that leaves through `d/..`, a link whose resolution passes a retargeted directory link; 40 and 43 links in a chain (the limit); a non-UTF-8 target; `d/`, `d//t.txt` and `./d/./t.txt`; unchanged links re-checked because the patch is not empty, one replaced by an absolute link in the source and one whose directory the result deletes | 78/78 |
| Source drift and ancestors (63) | An edit, a mode change, a deletion, an untracked addition, a link in place of a file, a link that now escapes or is absolute, an unrelated edit, a directory in place of an added file, a FIFO; an ancestor that is a link, a file or missing; nested repositories (a `.git` directory, a `.git` file, and a bare layout with and without `refs`); a component `.gİt`; non-UTF-8 names in the result tree (`UnicodeDecodeError` with Python's text) | 63/63 |
| Git sources (24) | A submodule in the result tree (`Unsupported result file mode: 160000`), a skip-worktree flag, `core.sparseCheckout` true and false, a submodule in the source index, a source that is a subdirectory, a linked worktree, a separate Git directory | 24/24 |
| Directory sources (78) | Exclusions touched directly and through an ancestor, none; `exclude` as a string, `null`, an object, with `../x`, `/x`, `""`, `.`, `5`, a NUL, duplicates and `a//b`, `a/../b`, and `../it's` (a `repr` with double quotes); the source deleted, made a Git repository with and without a commit, given an empty `.git`, placed inside a repository, an empty `.git` or a bare layout, renamed `x.GIT`, and reached through a symlink; 60-file changes | 78/78 |
| Random (150) | Random trees of files, executables, links and random bytes, random changes and deletions, drift in 30% of cases, Git or directory sources, status, dry run or apply | 150/150 |
| **Total** | | **518/518** |

The 518 cases took 40 runs and 29.0 s (7.9 s for the first, which includes the session's first build, then 0.4–0.9 s). The first attempt at batching failed two batches with a `ProtocolError`, "Run values may contain at most 8192 value nodes." That is a harness limit no earlier probe reached: each `loadRun` answer is a JSON tree of about 130 value nodes. Bounding batches by value nodes as well as by UTF-16 units fixed it. No case output changed.

## Dicts, and the two trees

Every dict in this module became a list searched by `ListFilter`. For the run records, the receipt and the pending links that costs nothing measurable. For the trees it is quadratic. `changed_files` looks each name of the union up in both trees, and `apply_result` looks each changed name up in both again, so an apply over n files does about 4n² name comparisons.

Times are `runPure` wall time on a stateless program-defined `Host` whose run value names the shape: `n` files `d<i/100>/f<i>.txt`, of which every k-th changes. Git, `lstat` and reads are answered from the path, so no disk is involved; each changed file is still hashed twice in the port.

| Input | Natural port | Merge-join port |
| --- | --- | --- |
| Tree comparison only, 4,000 / 8,000 / 16,000 files | 0.84 / 2.65 s / `RunTimeout` | 0.85 / 0.43 / 0.53 s |
| Tree comparison only, 32,000 / 64,000 files | `RunTimeout` | 0.83 / 1.26 s |
| Apply, every file changed, 1,600 / 3,200 / 6,400 | 0.97 / 1.89 / 3.48 s | 1.05 / 1.19 / 1.48 s |
| Apply, every file changed, 12,800 | `RunTimeout` | 1.90 s |
| Apply, every 10th file changed, 4,000 / 8,000 / 16,000 / 32,000 | 1.10 / 2.46 s / `RunTimeout` / `RunTimeout` | 0.64 / 0.76 / 1.08 / 1.55 s |
| Apply, every 100th file changed, 64,000 | — | 1.54 s |

Times include the per-run build and process start (0.4–0.5 s for a trivial entry). A repository of 16,000 tracked files is ordinary, and the natural port cannot compare it even when only a few files changed.

The merge-join version sorts each tree once by name with `ListSortBy` and walks both in one `ListFold` whose accumulator holds two positions. Each step compares the heads and emits a pair `{name, old, new}` when they differ, and `changeStep` then takes the pair instead of looking the name up. That is 2 types, 5 new functions (`mergeStep` 116 nodes, `changeStepP` 76) and a replaced `applyResult`, 327 nodes in 7 requests. It matched the same 518 cases. It depends on the order `ListSortBy` gives with `StringLessThan` being the order of `sorted()`, which the language subset guarantees. But it is a different algorithm from the Python one, and a reader has to check that the fold visits every name.

Python does the whole apply in 0.16 s of its own logic for 1,600 changed files. That was measured by replaying the recorded Git answers in process. With real Git it takes 12.1 s, because it starts two `git cat-file` processes per file. So in rrawrlet the dict is not the bottleneck; in the port it is the only one.

## Library behaviour

44% of the nodes reproduce Python library behaviour. The UTF-8 decoder is the largest part (429 nodes). Probe 14 wrote a lossy one with `errors="replace"` in 228 nodes. This module needs two other modes: strict decoding, whose error text reaches the user (a non-UTF-8 name in the result tree), and `surrogateescape`, for link targets. The three differ only in what happens to a bad sequence, and the sequence boundaries (Python's maximal subpart after `E0`, `ED`, `F0` and `F4`) are the hard part of all three. *Inference:* one decoding builtin with the error mode as a parameter would have replaced both probes' decoders.

SHA-256 with `Bits32` was direct to write. It hashes 1.48 MB in 1.17 s including the build, so hashing is not what limits an apply. `repr` needs to know which code points are printable. A table of 19 ranges covers every assigned code point that `str.isprintable()` rejects, but not unassigned ones, which Python also escapes. That needs the Unicode database, as probe 11's `str.isspace` table did.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **No Map: comparing two trees is quadratic** | The natural port times out comparing 16,000-file trees and applying 12,800 changed files. The Map-free merge join took 7 requests and 327 nodes, and is an algorithm the Python does not contain | None queued. Candidate: the deferred `Map<String, V>` (construction from a list of pairs, lookup, keys in order). Probe 13 found Map "weakly triggered" (a naive fold lookup reached the 5 s limit at about 40,000 refs); this is the first probe where the natural port stops at an ordinary input size |
| 2 | **UTF-8 decoding, again** | 8 functions and 429 nodes (12%) for strict decoding with Python's error text and `surrogateescape`, after probe 14's 228 for `replace` | None queued. Candidate: probe 14's decode builtin, with the error mode (`strict`, `replace`, `surrogateescape`) as a parameter and a `Result` for `strict` |
| 3 | **Run values of at most 8,192 nodes** | JSON records of about 130 nodes each split the batches | None. Harness reach, like the fake budgets recorded since probe 10 |
| 4 | **`repr` needs Unicode categories** | 6 functions and 153 nodes; unassigned code points are out of scope | None. The same gap as probe 11's `isspace` table |
| 5 | **`StringToLower` is not Python's `lower()` for `İ`** | `.gİt` would have been refused as Git metadata; one extra `StringContains` guards it | None. Documented; the case confirms the guard |
| 6 | **Effectful `any` and mutual recursion** | `repository_marker` over the parents as an `Err`-stopping fold; `slLoop`/`slFollow` through the placeholder route (1 extra request) | None. Both routes are documented and cost little |

Against the other candidates: no loop was deep enough to need probe 15's tail calls (the symlink walk recurses once per path component and link, at most a few hundred times). `Float64ToInt64` was not needed. The records' only floats are timings the module never reads, carried as their `repr`. Lone-surrogate run values (#275) carried the non-UTF-8 link target and names in both directions. The build cache (#274) held: every batch after the first took 0.4–0.9 s.

## Reproducing

The scripts are in the project's shared files under `rrawrlet-probe16/`. `base.py` authors the types, the `Host` and the library helpers, and `author.py <snapshot>` authors the rest on top of it. `test_base.py` checks the helpers against Python. `fixture.py` builds prepared runs, `oracle.py` records the real functions' host calls, and `mkcases.py` builds the 518 cases (as root, because some cases need a FIFO). `run_cases.py <snapshot> cases.json` runs them. `scale.py` gives the natural port's scale rows. `fix_merge.py <snapshot>` applies the merge join, and `pyscale.py` gives the Python rows. The snapshots are `p16.json` and, after the merge join, `p16m.json`.
