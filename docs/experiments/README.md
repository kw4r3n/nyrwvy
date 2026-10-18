# Experiments

**Status:** Current

This area holds explorations, evaluation protocols, and measurements that are not current contracts. Completed results that support an implemented claim live in [`../verification/`](../verification/README.md).

## Agent evaluation protocols

- Milestone 1: [corpus v1](m1-agent-evaluation-corpus-v1.md), [run template v1](m1-agent-evaluation-run-template-v1.md), [condition v2](m1-agent-evaluation-condition-v2.md)
- Milestone 2: [condition v1](m2-agent-evaluation-condition-v1.md) (invalid for recorded runs), [condition v1.1](m2-agent-evaluation-condition-v1.1.md)
- Frozen corpus inputs: `evaluation/m2-agent-corpus-v1/`, `evaluation/m2-listmap-agent-corpus-v1/` and the unfrozen `evaluation/pure-control-flow-exploratory-v1/`. These corpora, the evaluation runners and their scripts were removed from `main` on 2026-10-18; read and replay them from the freeze tags below or from commit `b3a958a`, the last `main` commit that contained them.
- Canonical freeze tags: [evaluation freeze tags](../operations/evaluation-freeze-tags.md)

## Authoring probes

- [Mini Work Planner authoring probe](mini-work-planner-authoring.md), producing [`mini-work-planner/`](../../examples/mini-work-planner/README.md); a smaller sibling program is [`verification-planner/`](../../examples/verification-planner/README.md)
- [M6 medium-scale authoring pressure probe](m6-medium-scale-authoring-probe.md)

## Representation experiments

- [Nyason v0](nyason-v0.md)
- [Exact-bit representation](bit-representation.md)

## Change verification

- [Semantic change verification experiment](semantic-change-verification.md): computed, revision-bound sequential differential evidence; not a current verification contract.
- [Practical batch change-verification trial](change-verification-practical-trial.md): real formatter refactor/improvement, observed cost and deliberately missed coverage control.
