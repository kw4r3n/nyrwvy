# RAST provenance decision — 2026-08-26

## Status

Accepted. This closes the need for a NyamNyam-specific RAST provenance framework.

## Decision

NyamNyam will **not** introduce dedicated semantic metadata, schema fields, or a repository-wide provenance system merely to track conceptual ancestry from the earlier RAST project.

The two projects have diverged substantially in architecture and goals. RAST remains useful historical context, but it is no longer treated as a migration source whose conceptual lineage must be recorded systematically.

## Normal source attribution still applies

If future work directly copies or substantially adapts concrete material from RAST, use ordinary development provenance appropriate to the material, for example:

- commit message attribution;
- source/test comment with repository path and revision;
- pull-request note;
- license/copyright notice where legally required.

Examples that may justify explicit attribution:

- a test case copied with only mechanical adaptation;
- a validator algorithm substantially ported from one RAST implementation;
- a block of source code reused directly;
- documentation text copied substantially verbatim.

## What does not require provenance tracking

Do not add provenance records merely because an idea was discussed during the RAST era.

Examples:

- graph-first concepts that have since been redesigned independently;
- explicit Result/Option use;
- conservative validation principles;
- backend-neutral IR ideas;
- effect/capability concepts that now have different representations;
- generic historical design influence.

These are part of the project's design history, not semantic or implementation dependencies.

## Architectural consequence

RAST remains external to NyamNyam.

- no runtime dependency;
- no semantic identity relationship;
- no requirement that NyamNyam preserve RAST compatibility;
- no provenance fields in Snapshot, Typed Semantic IR, Execution IR, Nyason, or agent-facing operations;
- no requirement to maintain source-revision maps as NyamNyam evolves.

A future importer, if ever built, is an explicit compatibility/tooling feature and may carry import-specific provenance for operational reasons. That does not revive a general RAST provenance requirement.

## Q-013 resolution

Q-013 is resolved for the current architecture:

> Use normal source attribution only when concrete material is directly copied or substantially adapted. Do not create a special RAST provenance convention for conceptual ancestry.
