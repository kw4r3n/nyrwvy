# NyamNyam documentation

**Status:** Current

This directory contains the maintained documentation for the current NyamNyam design and implementation. Choose a document by the question you are trying to answer rather than by chronology.

## Start here

- **Why does NyamNyam exist?** Read [Vision](concepts/vision.md) and the [AI-first semantic design principles](concepts/ai-first-design-principles.md).
- **How is the system organized?** Read [Architecture](developer/architecture.md).
- **How much verification does a change need?** Read [Verification philosophy](developer/verification.md).
- **What does the implemented language and agent surface support now?** Read the [current language subset](reference/language-subset.md) and [stable-ID reference](reference/stable-ids.md).
- **Why was a direction chosen?** Use the [decision register](decisions/README.md).
- **What remains unresolved?** Read [Open questions](decisions/open-questions.md).
- **What must an implementation preserve?** Use the maintained [specifications](specs/), including [expression structural authoring](specs/expression-authoring.md), [atomic product/union declaration authoring](specs/type-declaration-authoring.md), [atomic effect declaration authoring](specs/effect-declaration-authoring.md), the [snapshot-backed agent authoring session](specs/agent-authoring-session.md), and the narrower [Float64 bounded structural editing contract](specs/bounded-structural-editing.md).
- **What was completed in Milestone 1?** Read the completed [Milestone 1 record](plans/milestone-1.md) and its linked verification evidence.
- **What did Milestone 2 establish?** Read the [Milestone 2 record](plans/milestone-2.md) and its controlled structural-editing evidence.
- **What evidence supports an implemented claim?** Use [verification records](verification/).
- **What is exploratory rather than a current contract?** Use [experiments](experiments/).
- **How should documentation itself be maintained?** Read [Documentation conventions](developer/documentation.md).

## Maintained areas

| Area | Purpose |
| --- | --- |
| [`concepts/`](concepts/) | Mental models, goals, and design principles. |
| [`reference/`](reference/) | Exact current facts and supported behavior. |
| [`developer/`](developer/) | Architecture, verification policy, maintenance, and contributor/reviewer guidance. |
| [`decisions/`](decisions/) | Accepted rationale and bounded unresolved questions. |
| [`specs/`](specs/) | Normative contracts the implementation must satisfy. |
| [`plans/`](plans/) | Active or proposed implementation/evaluation plans and maintained milestone boundaries. |
| [`verification/`](verification/) | Recorded evidence from completed implementation/verification work. |
| [`experiments/`](experiments/) | Ideas and measurements that are not current contracts. |

There is no empty `guides/` or user-specific documentation tree yet. Those should be introduced only when there is stable workflow content that actually needs them.

## Historical documentation

The pre-reorganization documentation and other historical material live under [`../docs-archive/`](../docs-archive/README.md). Archive content remains reachable for design history and evidence, but it does not compete with maintained current documentation for authority.
