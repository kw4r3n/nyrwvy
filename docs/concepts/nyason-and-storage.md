# Nyason and storage direction — 2026-08-26

## Status

Accepted Milestone 1 direction for evaluating Nyason and persistent storage. This does not yet select the permanent authoritative storage medium.

Nyason remains a candidate, but its role is stronger than "an experimental JSON-like alternative".

> **Nyason is the NyamNyam-native semantic encoding candidate and may diverge freely from JSON when doing so improves AI reliability, structural explicitness, canonicalization, or implementation control.**

## Nyason is not required to resemble JSON

The existing v0 prototype uses tagged records, named fields, lists, strings, booleans, and atoms. That historical shape is not a compatibility obligation.

Future Nyason revisions may change syntax substantially if experiments show that a different representation is better for:

- local node classification;
- stable-ID recognition;
- bounded semantic slices;
- fewer malformed edits;
- fewer field-role mistakes;
- lower token/query cost;
- deterministic canonicalization;
- schema evolution;
- recovery/debugging.

Do not preserve JSON-like punctuation, nesting, key/value conventions, or object syntax merely because they are familiar.

JSON compatibility is not a goal.

## Dedicated tooling is a feature, not automatically a cost

Nyason-specific parsers, formatters, validators, viewers, slicers, and migration tools are not inherently a disadvantage.

NyamNyam already has unusual requirements that generic JSON tooling does not know about:

- typed stable-ID domains;
- closed semantic node alternatives;
- canonical nonsemantic ordering;
- semantic versus presentation metadata;
- focused AI slices;
- exact schema identities;
- structured diagnostics tied to semantic roles.

Owning the tooling allows those rules to be implemented directly rather than encoded through conventions layered on a general-purpose format.

The implementation cost still matters, but "requires custom tooling" is not by itself evidence against Nyason.

## AI-first evaluation criteria

Nyason should be judged primarily by whether it helps agents interact with semantic data correctly.

Relevant metrics include:

1. semantic role classification accuracy;
2. wrong-ID-domain rate;
3. malformed structure rate;
4. unrelated edit/churn rate;
5. context required to understand a focused slice;
6. token use for realistic query/edit workflows;
7. repair success after intentionally malformed inputs;
8. ambiguity around nested node/field boundaries;
9. deterministic round-trip and migration behavior.

Raw UTF-8 byte size remains useful operational data, but it is not the primary objective.

The current v0 result being larger than compact JSON is therefore a reason to investigate the representation, not a reason to force Nyason toward JSON syntax or abandon the format.

## Nyason versus JSON

JSON keeps an important role:

- reference/debug baseline;
- external comparison point;
- easy interoperability during development;
- useful control condition for AI experiments.

Nyason is allowed to optimize for a different target.

A future benchmark should compare complete workflows rather than asking only which serialization is shorter.

For example:

```text
semantic query
 -> bounded representation
 -> agent reasoning/edit request
 -> simulation
 -> repair if needed
 -> review
```

The format that produces fewer semantic mistakes at acceptable context/latency cost is more relevant than the one with fewer bytes in an isolated full snapshot.

## Database storage is not the opposite of Nyason

A database and Nyason solve partly different problems.

A likely architecture is:

```text
portable canonical/interchange snapshot
            Nyason
               |
               v
      working/index store
        SQLite / other DB
               |
               v
      query / agent / viewer
```

Under this model:

- Nyason is a deterministic portable representation;
- the database is a query/index/transaction optimization;
- database indexes and derived facts can be rebuilt from authoritative semantic state;
- humans and agents do not need to manipulate DB internals directly.

This arrangement is attractive because the portable representation remains easy to archive, inspect, hash, migrate, and move between environments while the DB handles scale and query efficiency.

## Database authority remains possible if evidence supports it

Do not make "text snapshot must always be authority" a dogma.

If realistic program sizes show that a database provides materially better:

- atomic publication;
- large-graph update cost;
- partial loading;
- recovery;
- schema migration;
- corruption isolation;
- concurrent access;

and a portable export can still be generated deterministically, then making a DB-backed store authoritative is a valid future outcome.

Such a decision must still preserve the existing architectural invariants:

- semantic graph, not DB table shape, defines meaning;
- queries and operations do not depend on storage-specific incidental structure;
- derived indexes are not competing semantic facts;
- canonical export/fingerprint behavior is deterministic;
- storage migration cannot silently guess missing semantics.

## Recommended evaluation sequence

Do not select the permanent storage medium before there is enough scale to test it.

Recommended progression:

1. continue using strict JSON as the baseline codec;
2. actively evolve Nyason as a NyamNyam-native encoding rather than freezing v0 syntax;
3. run AI comprehension/edit benchmarks on focused JSON and Nyason projections;
4. build a simple DB-backed working/index prototype when graph size/query latency justifies it;
5. compare portable-snapshot + derived-DB against DB-authoritative designs;
6. resolve Q-001 from measured reliability, performance, migration, and operational evidence.

## Implications for Nyason v1+

Future Nyason design may explore representations such as:

```text
ListMap(
  id=node:...
  source=node:...
  mapper=function:...
)
```

or flatter role-oriented forms such as:

```text
node node:...
kind ListMap
source node:...
mapper function:...
```

These are examples only. Neither syntax is accepted yet.

The important rule is that Nyason is free to choose whatever grammar makes semantic boundaries most obvious to agents while remaining strictly parseable and canonically renderable.

## Q-001 current direction

Q-001 remains technically open because the permanent authoritative persistence medium is not yet proven.

However, the candidate set should now be understood as:

- **Nyason:** actively developed NyamNyam-native portable semantic encoding candidate;
- **JSON:** strict reference/debug/benchmark codec;
- **database:** likely working/index store and possible future authority if operational evidence supports it;
- **binary formats:** possible transport/storage candidates only if they provide a concrete benefit without weakening recovery and semantic inspection workflows.

The project should not treat JSON compatibility or generic third-party tooling availability as default tie-breakers against Nyason.
