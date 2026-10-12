# Nested pattern-reference authoring guidance

Base: `2a56a5af59d69d013587522c705041c195e1dad5`.

The bounded-worker-stream monitor exposed two different mistakes: omitting the
empty Option None frame, and choosing a valid slot from the wrong producer.
Existing scope diagnostics already list visible frames, and the matchOption
reference already mentions the None frame. This change extends those existing
mechanisms; it adds no new recipe, resolver, or lexical-scope rule.

## Before and after

Inside monitor's nested left Result Ok, right Result Ok, then collector Option
None arms, the frames are:

- depth 0: collector None, no bindings
- depth 1: right producer result, Value
- depth 2: left producer result, Value
- depth 3: the outer Bind result

The old attempted mapping `leftAlive = value@1, rightAlive = value@0` fails
because None has no Value slot. The correct mapping remains
`leftAlive = value@2, rightAlive = value@1`.
Swapping these to 1/2 is syntactically and type-valid but reads the wrong
producers. The diagnostic must not recommend the innermost match.

Previously, an invalid reference with several matching slots said
“the innermost is usually meant.” It now says “choose the frame that binds the
intended value” and reminds the author that empty frames count. The
patternReference lookup now explains this exact example, empty Union arms,
conditionals, and the distinction between inputs and bodies.

The executable regression in
`examples/bounded-worker-stream/source.test.mjs` inspects the actual authored
monitor and asserts its producer-to-depth mapping. It also demonstrates that
the old 1/0 mapping is unbound, while swapped 1/2 passes slot validation but
does not satisfy the intended mapping. No runtime or example authoring code changes.

## Verification

Cloud Node v24.19.0:

- Baseline red: the new reference-guidance test fails against unchanged main.
- Patched: recipe-reference and bounded-worker-stream source tests, 13 passed.
- `git diff --check`: passed.
Local verification applied candidate v0 cleanly to the base above, with no
source repairs. Tested tree: `b05a396ab38e7a3120d454b199b7a426ac948407`.

- Baseline red: the new F# fact compiled and failed once as expected.
- ScopeDepthDiagnosticsTests: 8 passed.
- Fast Core suite: 854 passed.
- Ordinary Node checks: 33 passed.
- Bounded-worker-stream checks: 16 passed.
- Recipe-reference checks: 3 passed.
- Patched runs: zero failures, skips, or timeouts.
- Full Extended suite: not run.

Local evidence archive: `nyrwvy-nested-reference-guidance-2a56a5af-evidence.tar.gz`.
SHA-256: `d86ca0cf9e83c47c0841cf2ab74f3088c89a69f6c9186e696a69288319cc7c5e`.

This final revision updates only this verification record after the tested v0
candidate. Production and test source bytes are unchanged. No provider calls,
push, PR, or merge.

The F# regression exercises the nested three-frame shape at negative,
out-of-range, and empty-slot depths; missing Error slots have no misleading
candidate hint. Both valid Value depths remain accepted. Existing exact
diagnostic assertions protect single-candidate and other selector behavior.
