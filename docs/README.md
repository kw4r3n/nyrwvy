# Nyrwvy documentation

**Status:** Current

This directory contains the maintained documentation for the current Nyrwvy design and implementation. Choose a document by the question you are trying to answer rather than by chronology.

## Start here

Pick a document by the question you have.

| Question | Read |
| --- | --- |
| Why does Nyrwvy exist? | [Vision](concepts/vision.md), [AI-first design principles](concepts/ai-first-design-principles.md), [semantic independence](concepts/semantic-independence.md) (proposed) |
| How is the system organized? | [Architecture](developer/architecture.md) |
| What does the language support now? | [Current language subset](reference/language-subset.md), [stable IDs](reference/stable-ids.md) |
| How does an agent use the tools? | [Tool contract](specs/agent-graph-tools.md), [MCP guide](specs/mcp-graph-tools.md), [authoring session protocol](specs/agent-authoring-session.md) |
| What must an implementation preserve? | The [specifications](specs/) — start with [expression authoring](specs/expression-authoring.md) |
| What has been done, and what comes next? | [Post-M2 roadmap](plans/post-m2-roadmap.md) (numbered queue of completed and candidate changes), [general-purpose programming roadmap](plans/general-purpose-programming-roadmap.md) (completed) |
| What did the milestones establish? | [Milestone 1](plans/milestone-1.md), [Milestone 2](plans/milestone-2.md) |
| Why was a direction chosen? | [Decision register](decisions/README.md) |
| What is still unresolved? | [Open questions](decisions/open-questions.md) |
| What evidence backs a claim? | [Verification records](verification/README.md) |
| How much verification does a change need? | [Verification philosophy](developer/verification.md) |
| What is exploratory, not a contract? | [Experiments](experiments/README.md) |
| How is documentation maintained? | [Documentation conventions](developer/documentation.md) |

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

## Historical documentation

The pre-reorganization documentation and other historical material live under [`../docs-archive/`](../docs-archive/README.md). Archive content remains reachable for design history and evidence, but it does not compete with maintained current documentation for authority.
