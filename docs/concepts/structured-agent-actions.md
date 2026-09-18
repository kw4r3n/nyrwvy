# Structured action spaces and Nyrwvy

**Status:** Current reference context. Sources checked 2026-09-19.

## Intended boundary

Nyrwvy's current write-side goal is to give an AI enough tools to inspect and edit the authoritative typed semantic graph. It is not to build a natural-language compiler, an autonomous planning system, or a new human-written source language. The agent still chooses the change; the toolchain resolves references, validates explicit operations, presents their consequences, and publishes the reviewed graph.

Humans need understandable projections of structure and change. Those projections can be graphical and need not resemble a programming language. Improving them remains useful, but does not block the current minimum AI-authoring toolset.

## Reference research

### CODESTRUCT: the closest action-interface reference

Kim et al., **CODESTRUCT: Code Agents over Structured Action Spaces**, ACL 2026, July, pp. 13290–13306.

- [ACL Anthology and publication metadata](https://aclanthology.org/2026.acl-long.607/)
- [Full paper, arXiv version](https://arxiv.org/html/2604.05407v1)
- [Author implementation](https://github.com/amazon-science/CodeStruct)

CODESTRUCT exposes `readCode` and `editCode` around named AST entities instead of requiring agents to locate and reproduce arbitrary text spans. Its evaluations report improved task success and lower token consumption for most tested models. This supports investigating the action interface, not only the model, as a source of avoidable editing work.

The distinction matters: AST targeting does not mean the agent exclusively constructs semantic graphs. Replacement content can still be source text, and syntax validation is not Nyrwvy's typed semantic validation. The reported experiments do not establish that Nyrwvy, arbitrary graph mutation, or every structured interface is more efficient.

### Code Graph Model: a representation reference, not the same editing mechanism

**Code Graph Model (CGM): A Graph-Integrated Large Language Model for Repository-Level Software Engineering Tasks**, NeurIPS 2025.

- [Conference paper](https://proceedings.neurips.cc/paper_files/paper/2025/hash/178ae4ba29022eb7bf509c2e27bc8ab8-Abstract-Conference.html)

CGM integrates code-graph structure and dependencies into the model's repository-level processing. It is relevant to making relationships explicit, but is not evidence that an agent was given Nyrwvy-style direct graph-editing tools. Do not merge the representation hypothesis and the action-interface hypothesis into one result.

### Geometric AST-edit experiment: supplementary evidence

Jack Foxabbott, **AST Edits: The Code Editing Format Nobody Uses**, Geometric, 2026-04-02.

- [Technical experiment and results](https://geometricagi.github.io/2026/04/02/ast-edits.html)

This author-run experiment compares seven edit formats across four models on 29 Python editing tasks. AST-targeted edits perform well in that setup, but the workload is small and this is a technical report, not a peer-reviewed general superiority claim. Like CODESTRUCT, its AST targeting still permits replacement source text.

## What Nyrwvy takes from this

Use named declarations and exact stable identities to avoid incidental text-location work. Return literal values, branch structure and references as data, not strings the agent must parse back into a graph. Make focused changes available without forcing whole-body reconstruction. Keep query, simulation, review and publication explicit.

Names are optional selectors, not replacement identities. Stable IDs remain useful for unambiguous targeting, repeated names and identity continuity. The goal is not to hide every ID or to make payloads resemble human prose; it is to remove per-task host programs and unnecessary reconstruction work.

These sources motivate the design direction. They do not define Nyrwvy semantics, require another model benchmark for each adapter change, or prove an efficiency gain for the toolset introduced here. See [Vision](vision.md), [verification policy](../developer/verification.md), and the [minimum graph-tool contract](../specs/agent-graph-tools.md).
