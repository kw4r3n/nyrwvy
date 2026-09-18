# Nyason v0 experiment

**Status:** Experimental

Nyason v0 is an implemented and mechanically verified prototype of a Nyrwvy-specific semantic encoding. It is useful evidence, but it is not the canonical persistence contract and its v0 syntax is not a compatibility obligation.

The prototype explored:

- tagged records with named semantic fields;
- typed stable-ID domains visible in serialized values;
- closed parsing and early structural rejection;
- deterministic canonical encoding;
- local semantic self-description for retrieved slices;
- comparison with strict JSON as the reference/debug baseline.

The experiment established that a dedicated encoding can preserve Nyrwvy-specific semantic roles directly, but it did not establish that the v0 surface syntax is better for agent workflows or should become permanent storage authority.

Current Nyason/storage direction is maintained separately in [Nyason and storage direction](../concepts/nyason-and-storage.md). Future Nyason revisions may diverge substantially from v0 when experiments support a more reliable representation.

## Historical record

The complete dated v0 design/evidence note is preserved in the [pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/nyason-v0-20260825.md), together with the [Phase G implementation record](../../docs-archive/2026-08-pre-reorg/rebuild-phase-g-nyason-20260825.md).
