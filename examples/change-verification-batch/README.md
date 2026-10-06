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
