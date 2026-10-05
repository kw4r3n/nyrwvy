# rrawrlet slice probe 10 — 2026-09-30

**Status:** Complete. A new part of rrawrlet was authored by name only: its OpenCode configuration and model catalog code, `opencode_config.py` without the process-running functions. That covers `load_providers` with `{env:NAME}` resolution, `validate_provider`, `validate_reference`, `validate_selection`, `provider_catalog`, `parse_model_catalog`, `parse_verbose_model_catalog` and `attempt_label`. The port matched the real Python on 317/320 cases on its first run, with no recipe rejections and no port bugs. The 3 misses are known divergences: an integer beyond Int64, which appears in 2 cases, and one `str.isprintable` answer that needs Unicode category data. **Nothing is blocked, and nothing is queued.**

The one measured cost is scanning, not language surface. Written as in Python, `parse_verbose_model_catalog` runs `raw_decode` from each `{`, and each attempt re-reads the rest of the text. That is quadratic, and the port times out at about 200 records. Restructured as one fold that carries the parser, it is linear and read 1,600 records (1.6 MB) within the run limit. Only input with many failing `{` stays quadratic, because a fold cannot stop early.

**Date:** 2026-09-30

Baseline: nyrwvy `d58e709` (main after #262, clean); rrawrlet `ee03576`. The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The oracle ran rrawrlet's real functions with Python 3.11.

The method was probe 9's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests (`simulateCreateTypesByName`, `simulateCreateTypeByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `commit` and `runPureByName`) and no `declarations` call. Scripts, cases and transcripts are kept outside the repository in private files outside the repository (`rrawrlet-probe10/`).

## Why this part

Probe 9 ported the JSON the Leader exchanges with the Team. This part reads JSON the user and OpenCode supply, which exercises what probe 9 left out:

- `json.loads` with an `object_pairs_hook` that rejects duplicate keys;
- `JSONDecoder.raw_decode` from an arbitrary position inside mixed text;
- recursive substitution that reads the environment and stops at the first error;
- dict-order validation with one error message per rule;
- a seen-set of `(provider, model)` tuples;
- Python's `str()` and `repr()` of arbitrary JSON values inside an f-string.

Probe 9's JSON scanner and `json.dumps` were copied into the new recipes and extended.

## What was ported

| Python | Port |
| --- | --- |
| `path.read_text()`, `os.environ` | Capability `Host` with `readText(path): Option<String>` and `env(name): Option<String>`. `None` from `readText` stands for `OSError` and a decoding error |
| `json.loads(..., object_pairs_hook=_object_without_duplicates)` | `loadsStrict`: probe 9's scanner in strict mode. An `ObjF` frame records whether a key repeated, and closing that object enters a terminal `Dup` mode. As in Python, a duplicate raises when its object closes, so it wins over a syntax error that comes later but not over one that comes earlier |
| `JSONDecoder().raw_decode(text, start)` | `rawDecode`: the same scanner in raw mode. The first complete value enters `Done { end }`, and the rest of the text is ignored |
| `resolve` in `load_providers` | `resolve`, `resolveItem` and `resolveMember` over `listEffectFold`. The first error stops the traversal, and each `{env:NAME}` is one `env` call. `envName` is `re.fullmatch(r"\{env:([A-Za-z_][A-Za-z0-9_]*)\}")` written with `StringSpan` |
| `_valid_id`, `validate_*` | `validId` (`str.strip()` whitespace through `StringSpan`/`StringSpanBack`, `Cc` as code point ranges), and folds over dict members that keep the first error |
| `provider_catalog` | `catalogProvider` and `catalogEntry`, with `ListConcat` |
| `parse_model_catalog` | `StringSplitLines` with Python's line breaks, `strip`, `split("/", 1)`, and a `Scan` state whose seen list is checked with `ValueEqual` on a `Key { provider, model }` Product |
| `parse_verbose_model_catalog` | Two versions (see below) |
| `attempt_label` | `attemptLabel`. `KeyError: 'model'` and `KeyError: 'effort'` are `Err` values. `pyStr` and `pyRepr` give Python `str()` and `repr()` of any JSON value, including repr quoting and `\x`/`\u`/`\U` escapes |
| `json.dumps` of each result | probe 9's `dumps` |

The oracle ran the real functions. `load_providers` got a fake `Path` and a logging `os.environ`, and the port had to make the same `readText` and `env` calls, in the same order. Each case prints `OK <json.dumps(result)>` or `ERR <Type>: <message>`; for `attempt_label` it prints `OK <label>`.

Out of scope:

- The process-running functions: `runtime_config`, `runtime_catalog` and `_runtime_output`.
- Byte decoding. As in probe 9, the port receives decoded text.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 11: `Json` and `Member` in one group, `Frame`, `Mode`, `Parser`, `NumParts`, `Raw`, `Key`, `Scan`, `Case`, and `Pass` (restructured version only); 1 effect, 1 capability with 2 operations |
| JSON access and truthiness | 14 / 127 |
| Scanner, with strict and raw modes | 29 / 865 (`step` 143, `numParts` 89) |
| `json.dumps` | 8 / 178 |
| `str()` / `repr()` | 6 / 193 |
| `_valid_id` and the validators | 10 / 321 |
| `provider_catalog` | 4 / 106 |
| `load_providers` | 7 / 177 |
| Model catalogs | 11 / 240 as written; 13 / 444 restructured (`passStep` 182) |
| `attempt_label` | 1 / 127 |
| Case dispatch | 4 / 67 |
| **Total** | **94 / 2,401** as written in 216 requests; **96 / 2,605** restructured in 222 requests |

There were **no recipe rejections**, no `scopeDepth` or type errors, and the differential run found **no port bugs**. Three pairs of functions call each other: `dumps` and `dumpMember`, `pyRepr` and `reprMember`, and `resolve` with its two callbacks. Each cost one placeholder body and one replacement, as in probes 8 and 9.

## Results vs Python

320 cases. A case matches when its output line is equal and the `readText` and `env` calls are the same list.

| Group | What it covers | Result |
| --- | --- | --- |
| `load_providers` (159) | No path, an unreadable file, empty text, a BOM, extra data, trailing commas, `NaN`/`Infinity`, `1e400`, `-0.0`, escapes and astral characters, 60 nesting levels. Duplicate keys at the top, inside, in an array, after a later syntax error, before an earlier one, and spelled with an escape. The document shapes. 31 `{env:…}` values: set, unset, empty, non-ASCII, lower case, digit first, a prefix or suffix, two references, `{file:…}`, in a list, in a key (not resolved), and which error comes first. 52 validation rules, the ID edge cases (NBSP, NEL, U+2028, U+3000, C0 and C1 controls, DEL, ZWSP, astral), and 40 random configurations | 158/159 (1 known) |
| `provider_catalog` (11) | Non-mapping input, empty, variant order with disabled ones, Unicode, 5×4 models | 11/11 |
| `validate_selection` (61) | Each reference and provider error, a missing model, enabled, disabled and missing variants, the order of checks, 40 random selections | 61/61 |
| `parse_verbose_model_catalog` (40) | The rrawrlet test fixture, pretty-printed and compact records, duplicates, the fallback to plain lines, non-string or missing IDs, bad variants. A nested record skipped after its parent succeeds, and found after its parent fails. Braces in a header and in a string, a top-level array, truncated text, duplicate keys (last wins), raw control characters, adjacent records, 40 `{` before a record, 300 nesting levels | 39/40 (1 known) |
| `parse_model_catalog` (18) | Every Python line break, and the characters that are not breaks (`\x1f`). `strip()` with NBSP and U+3000, empty parts, a model containing `/`, spaces inside, duplicates, controls | 18/18 |
| `attempt_label` (31) | The three backends, a missing `model` or `effort`, a falsy `variant` (`""`, `null`, `0`, `False`), `str()` of `None`, bools, floats (`1e+16`, `nan`, `inf`, `-0.0`), lists and dicts. `repr` quoting (`"it's"`, `'say "hi"'`, both quotes, backslashes) and escapes (`\x00`, `\x7f`, `\x85`, `\xa0`, `\xad`, U+3000, U+2028) | 30/31 (1 known) |
| **Total** | | **317/320** |

The 3 known divergences:

- `123456789012345678901` in a configuration, and `12345678901234567890` inside a verbose record, are Python `int`s beyond Int64. The port rejects the text, as in probe 9.
- `repr('​')` and `repr('\U000e0001')`: Python escapes these format characters (`Cf`) because `str.isprintable` is false. The port knows the controls, surrogates, private use and space separators, but not the other `Cf` characters or unassigned code points, so it prints them raw. That would take Unicode category data, and the only consumer is a malformed attempt record.

Batching changed the cost of a run. Probe 9 made one `runPure` per case, about 2.9 s each, mostly the build. Here each `runPure` ran up to 40 cases through a `runCases` entry, with one keyed `Host` fake per batch. All 320 cases took 12 runs and 48–51 s.

## Scale: `parse_verbose_model_catalog`

`opencode models --verbose` prints each model as a `provider/model` line and a pretty-printed JSON object. The scale input used records of about 1 KB each, shaped like a models.dev entry. The real size of the output was not measured; it depends on which providers the user has configured. *Inference:* tens to hundreds of models is plausible.

The text was generated inside the program from a list of IDs, since a `runPure` String holds at most 65,536 UTF-16 code units. Times are `runPure` wall time; about 3.0–3.3 s of each is the build and process start. Python took at most 33 ms for every input below.

| Input | Size | As written | Restructured |
| --- | --- | --- | --- |
| 1 / 10 / 50 records | 1 / 10 / 51 KB | 3.6 / 3.0 / 3.5 s | 3.3 s (1 record) |
| 100 records | 102 KB | 5.0 s | 3.4 s |
| 200 records | 204 KB | `RunTimeout` (5 s limit) | 3.4 s |
| 400 / 800 / 1,600 records | 0.4 / 0.8 / 1.6 MB | `RunTimeout` | 3.5 / 3.7 / 5.1 s |
| `{` repeated, nothing valid | 1,000 / 2,000 / 4,000 | 3.3 / 3.5 / 5.1 s | 3.3 / 3.6 / 4.8 s |

**As written**, `verboseStep` folds over the pieces of `StringSplit(text, "{")`, so it knows where each `{` is. For each `{` at or after the position, it calls `rawDecode(StringSlice(text, start, end))`. The slice, its `StringCodePoints` and the fold are each linear in the rest of the text, and the fold cannot stop at the end of the value. So every record costs the whole remaining text.

**Restructured**, one fold over the code points carries the parser: `Pass { scan, parser, start, idx, stopped }`. A `{` starts a raw parser, `Done` adds the record, and scanning resumes. Only a failed parse needs Python's retry from `start + 1`. The pass then idles to the end of the text, and the outer fold over the `{` pieces starts a new pass there. Output without failures is read once. Every failed `{` still costs the rest of the text, so the adversarial row stays quadratic: 4,000 braces took about 1.5 s above the build, where Python took 11 ms. The restructure cost 1 type, 2 functions and about 200 nodes, and it matched the same 317/320.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Resuming a scan at a position re-reads the rest of the text** | The literal `raw_decode` loop is quadratic and times out at about 200 KB. The restructured single fold reads 1.6 MB, but each failed `{` still costs the rest of the text, because a fold has no early exit and text can only be iterated by materialising `StringCodePoints` of a slice | None queued. Output without failures is linear once restructured. Revisit if a real input with many failed starts appears. An early-exit fold alone would not fix the literal version, because the slice and its code point list are already linear |
| 2 | **`matchUnion` needs every arm** | 130 of 225 arms repeat a sibling arm: 257 nodes, 10% of the port. Most are fixed-result matches over the 14 `Mode` variants (`passStep` 99 nodes, `loadsStrict` 22, `rawDecode` 12, `finish` 11) and "anything but `Str`" matches over `Json` | None queued; probe 7 recorded the same cost as small (about 45 arms). A default arm would trade away exhaustiveness when a variant is added, so it needs a decision first |
| 3 | **No Unicode category data** | 1 known divergence (`Cf` in `repr`). `Cc`, whitespace and line breaks were all fixed sets | None; no consumer |
| 4 | **Python int beyond Int64** | 2 known divergences | None, as in probe 9 |

Map was not needed. The seen check is a `ListAny` with `ValueEqual` over a `List<Key>`, and 1,600 records ran within the limit. Neither were function groups: 3 cycles cost 3 placeholder transactions. Products restated in full again cost helpers: `mk` for the 6-field `Parser`, `moveTo` for `Scan` and `mkPass` for `Pass`. That was recorded after probe 7 and not queued.
