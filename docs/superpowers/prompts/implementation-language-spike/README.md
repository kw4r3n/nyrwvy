# Implementation Language Spike Setup Prompts

These prompts divide the F# versus C# implementation-language spike into four isolated Codex setup sessions.

Canonical plans:

- [`../../plans/2026-07-29-fsharp-csharp-implementation-language-spike.md`](../../plans/2026-07-29-fsharp-csharp-implementation-language-spike.md)
- [`../../plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md`](../../plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md)

## Order

1. `01-shared-infrastructure.md`
2. `02-fsharp-baseline.md`
3. `03-csharp-baseline.md`
4. `04-integration-automation.md`

The prompts are setup instructions, not scored-trial prompts. Scored trials receive only the frozen public experiment bundle created by the implementation.

## Isolation rules

- Run each prompt in a fresh Codex session.
- Record the prompt SHA-256, base commit, model, and Codex CLI version.
- Session 2 must not inspect the C# implementation.
- Session 3 must not inspect the F# implementation.
- Session 4 may inspect both baselines only for contract equivalence and integration.
- Do not run calibration or scored trials during setup sessions.
- Do not weaken the accepted contract, hidden evaluator, strict compiler settings, or scoring policy.

## Suggested execution

Render a session prompt using the planned helper:

```bash
python experiments/implementation-language-spike/scripts/render_setup_prompt.py \
  --session 01-shared-infrastructure \
  --output /tmp/nayamnyam-language-spike-session-01
```

Then start a fresh Codex session in the repository at the recorded base commit and provide the rendered `prompt.md`.

Each session should commit only its assigned scope and finish with the checks named in its prompt.