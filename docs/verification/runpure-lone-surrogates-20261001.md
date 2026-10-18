# Lone surrogates in runPure String values — 2026-10-01

**Status:** Implemented and verified. Baseline: [runPure arguments as input and the build cache](runpure-build-cache-20261001.md).

A String in the language is any sequence of UTF-16 code units, but a `runPure` String value had to be well-formed UTF-16. The rrawrlet probes therefore escaped lone surrogates themselves: the harness sent U+10FFFE and four hex digits, and the port unescaped them (3 functions and 53 nodes in [probe 12](rrawrlet-slice-probes-summary.md#probe-12), 5 functions and 76 nodes in [probe 13](rrawrlet-slice-probes-summary.md#probe-13), in both directions). `runPure` now carries any sequence of code units, using JSON's own `\uD800` escapes on the wire. There is no new sentinel.

## Where lone surrogates were rejected or changed

| Step | Before | Now |
| --- | --- | --- |
| Reading the request | `JsonElement.GetString` throws for an escape like `\uD800` that does not form a pair, so the request failed as a `SessionError` | `WireInput.runText` reads such a string by unescaping its raw JSON text. The document parser has already validated the escapes. |
| Checking arguments and expectations | `RunInvalidString` for any String that was not well-formed | No check. Length and null limits are unchanged. |
| Passing arguments to the child | Base64 of UTF-8, which cannot hold a lone surrogate | Since the build cache change, input tokens carry the UTF-16 code units, so nothing changes here |
| Encoding results in the child | `CheckedString` counted strict UTF-8 bytes and threw `EncoderFallbackException`, which surfaced as a runtime failure. `JsonSerializer` would also have replaced a lone surrogate with U+FFFD. | `CheckedString` writes a text token (`s` and the Base64 of the code units). The exception message is sent as a token too. |
| Reading the child's result | Rejected Strings that were not well-formed (`RunInvalidResult`) | Decodes the tokens |
| Writing the response | `JsonSerializer` replaces a lone surrogate with `�` | `WireJson.runString` boxes a String with a lone surrogate as `LooseText`. Its converter writes the other characters as the serializer does, and each lone surrogate as `\uXXXX`. A well-formed String is still the plain string. |

The response rule applies to every run-value String: `actual`, `expected`, `mismatch`, `calls`, echoed fake bindings, and the `message` of a runtime failure (for example a fake `throw` message). Well-formed Strings are encoded exactly as before: they take the same serializer path, and a test compares the output with `JsonSerializer.Serialize`.

## Wire rule

- A request may spell a lone surrogate as `\uD800`-style escapes, in either case. Standard JSON parsers, JavaScript's among them, read this as the code unit.
- A response writes each lone surrogate as `\uXXXX` in uppercase. A surrogate pair stays a pair: the serializer writes it as two escapes, as before.
- Equality is ordinal over code units. `\uD800` and `\uDC00` are different values, and a lone high surrogate never matches a pair.
- A client whose JSON reader insists on well-formed UTF-16, such as .NET's `JsonElement.GetString`, must read these strings another way.

## Verification

- `PureRunLoneSurrogateTests` (the wire test is fast; the run test is Extended):
  - Test Strings: a lone high surrogate, a lone low one, a reversed pair, a surrogate at the start, one at the end, and a mix with astral characters, commas, quotes and a backslash. A well-formed control String and lowercase escapes are included.
  - Wire: each String is read from and written to JSON and read back unchanged, and well-formed Strings serialize byte for byte as before.
  - Arguments, results and expectations: equal lone surrogates match, and different ones report a mismatch.
  - Results the program builds: two halves joined by `StringConcat` form the pair, and a reversed join stays two lone surrogates.
  - Nested values: a `List` of Products with String, `List<String>`, `Option<String>` and `Result<String, Int64>` fields.
  - Fakes: keyed cases with each String as the key and a returned value, the recorded call arguments, a lone high half that matches no case, and a thrown message.
  - Through `AuthoringProtocol.executeLine`: `runPure` and `runPureByName` requests with `\uD800`-style escapes return the same escapes in `actual`, the failure `message` and `calls`.
- Existing tests that asserted `RunInvalidString` for a lone surrogate no longer apply, so those cases were removed:
  - `PureRunTests`: two cases.
  - `PureRunProductTests`, `PureRunUnionTests`, `PureRunNestedTests`, `PureRunInnerNestingTests`, `PureRunBindingTests`: one case each.
  
  The oversized-String cases beside them are unchanged.
- `./scripts/test-fast.fish` and `./scripts/test-extended.fish` pass.
