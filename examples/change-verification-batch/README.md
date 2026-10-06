# Practical change-verification trial

**Status:** Experimental

Requirements and the nine sampled / two held-out inputs are fixed in
[`requirements.json`](requirements.json) before candidate authoring. The target
is the existing `config-request-batch/v2` report formatter. One real refactor
flattens its concatenation graph; a separately reviewed report improvement escapes
embedded CR/LF. Negative controls are explicitly synthetic regressions, never
historical bug claims. Existing batch cases continue checking whole-program
reports, retries, expected failures, host exceptions, cancellation and disposal.

The experiment measures observed feedback, missed changes, unknowns, case volume,
manual decisions and wall-clock command/run cost. It does not infer productivity
or whole-program equivalence from sampled agreement.

## Reproduce

From the repository root, after installing the existing locked dependencies and
building the Release AuthoringSession:

```sh
node examples/config-request-batch/run.mjs --v2 /path/to/empty-baseline
node examples/change-verification-batch/run.mjs /path/to/empty-baseline/moved /path/to/empty-trial
node examples/config-request-batch/test-acquisition.mjs /path/to/empty-trial/improved
```

Both commands author/commit graphs only in these output bundles. Negative controls
are rejected in `working/`; ordinary tests of a committed negative control use
`negative-control-only/`. `baseline/`, `refactored/` and `improved/` preserve the real
stages for replay. Every export keeps `Generated.cs` and `symbols.json` in sync
with the graph through the existing emitter; no snapshot or generated C# is edited
by hand. The copied pinned dependency remains byte-identical through real changes.

`requirements.json`, `cases.before.json`, `cases.intended.json`,
`cases.held-out.json` and `cases.host-report.json` are written before proposals.
The native JavaScript expected-string oracle follows the frozen requirement;
it does not reuse graph recipes or learn from candidate outputs. Nine sampled
inputs are reused across stages. Two predeclared held-out inputs challenge the
coverage-gap control. One of the original nine newline inputs also passes through
the real batch pipeline, checking the summary, actual report, disposal and saved
snapshot bytes. The existing five-case saved suite remains unchanged.

`results.json` records verifier reports, stage results, requirements/runner digests,
manual decisions and measured step wall times. `transcript.jsonl` contains ordinary
tool exchanges. Reviews live beside each graph under `reviews/`. Real generated
snapshots and host fixtures are local artifacts, not a new language or framework.
