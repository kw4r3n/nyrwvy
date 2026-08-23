# Lightweight implementation-language spike

This is an isolated, first-pass experiment for Nayamnyam's open F# versus C# semantic-core question.

It is deliberately smaller than the full Codex maintenance experiment described in
`docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike.md`.
It checks:

- equivalent stable-ID/domain modeling;
- deterministic semantic operations;
- explicit obligations and no-change transactions;
- .NET 10 compilation with warnings as errors;
- byte-for-byte equality of the canonical scenario output.

The branch is disposable and does not select the production implementation language.
