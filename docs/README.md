# Nyrwvy documentation

**Status:** Current

This directory contains the maintained documentation for the current Nyrwvy design and implementation. Choose a document by the question you are trying to answer rather than by chronology.

## Start here

- **Why does Nyrwvy exist?** Read [Vision](concepts/vision.md) and the [AI-first semantic design principles](concepts/ai-first-design-principles.md). [Semantic independence](concepts/semantic-independence.md) (proposed) separates Nyrwvy's meaning from backend choices.
- **How is the system organized?** Read [Architecture](developer/architecture.md).
- **How much verification does a change need?** Read [Verification philosophy](developer/verification.md).
- **What does the implemented language and agent surface support now?** Read the [current language subset](reference/language-subset.md) and [stable-ID reference](reference/stable-ids.md).
- **What happened after Milestone 2, and what should trigger the next milestone?** Read the [post-M2 roadmap](plans/post-m2-roadmap.md).
- **Which general-purpose language and tool capabilities should be implemented next?** Read the [immediate execution queue](plans/post-m2-roadmap.md#immediate-execution-queue), which targets the blockers from the latest rrawrlet probe and the gaps in [Semantic independence](concepts/semantic-independence.md). The completed [general-purpose programming roadmap](plans/general-purpose-programming-roadmap.md) records what G1–G4 and T1–T3 established, using rrawrlet as a requirements probe rather than a builtin feature set.
- **Why was a direction chosen?** Use the [decision register](decisions/README.md).
- **What remains unresolved?** Read [Open questions](decisions/open-questions.md).
- **What must an implementation preserve?** Use the maintained [specifications](specs/), including [expression structural authoring](specs/expression-authoring.md), [atomic product/union declaration authoring](specs/type-declaration-authoring.md), [atomic effect declaration authoring](specs/effect-declaration-authoring.md), [atomic capability/operation declaration authoring](specs/capability-declaration-authoring.md), the [snapshot-backed agent authoring session](specs/agent-authoring-session.md), and the narrower [Float64 bounded structural editing contract](specs/bounded-structural-editing.md).
- **What was completed in Milestone 1?** Read the completed [Milestone 1 record](plans/milestone-1.md) and its linked verification evidence.
- **What did Milestone 2 establish?** Read the [Milestone 2 record](plans/milestone-2.md) and its controlled structural-editing evidence.
- **What evidence supports an implemented claim?** Use the [verification record index](verification/README.md), including the Codex CLI MCP probe: [initial approval blocker](verification/codex-cli-mcp-maintenance-probe.md) and [qualified maintenance success](verification/codex-cli-mcp-maintenance-probe-run-2.md).
- **What is exploratory rather than a current contract?** Use the [experiment index](experiments/README.md).
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
| [`verification/`](verification/README.md) | Recorded evidence from completed implementation/verification work. |
| [`experiments/`](experiments/README.md) | Ideas and measurements that are not current contracts. |
| [`operations/`](operations/) | Maintained operational procedures and canonical names, such as [evaluation freeze tags](operations/evaluation-freeze-tags.md). |

There is no empty `guides/` or user-specific documentation tree yet. Those should be introduced only when there is stable workflow content that actually needs them.

## Historical documentation

The pre-reorganization documentation and other historical material live under [`../docs-archive/`](../docs-archive/README.md). Archive content remains reachable for design history and evidence, but it does not compete with maintained current documentation for authority.
