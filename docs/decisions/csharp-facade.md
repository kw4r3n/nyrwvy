# Readable C# names for exported declarations

- **Status:** Accepted 2026-10-26, after kw4r3n/nyrwvy#494 and snap's `nyrwvy-facade` branch replaced its hand-written alias layer
- **Date:** 2026-10-26
- **Issue:** none

This decision gives a C# host readable names for a program's exported declarations without changing the generated source. `emitCSharp` can write a second file, the facade, beside the program. Generated identifiers stay derived from stable IDs, as the [C# backend spec](../specs/csharp-backend.md) requires; only the facade depends on display names.

## Decision

**The facade.** `emitCSharp` takes an optional `facade` path (a `.cs` file, confined like `output`). The facade covers the snapshot's own exported functions, types and capabilities, plus every type and capability their signatures and fields reach, including imported ones. It contains:

- `global using` aliases: a non-generic type by its display name, each variant of a non-generic Union as `{Union}{Variant}`, and each capability by its display name.
- In `public static class NyFacade` (namespace `Nyrwvy.Generated`; hosts add `using static Nyrwvy.Generated.NyFacade;`):
  - C# 14 extension properties that read each field by its display name. They work in property patterns, so `decision.action is KeyActionPane { cmd: var c }` matches a Union without a hand-written record.
  - A constructor per Product (`New{Type}`) and per variant (`New{Union}{Variant}`) with display-name parameters. Generated constructors take `f_<hex>` parameters in field-ID order, not declaration order (snap's `AgentStep`, declared `state, alert`, is constructed as `alert, state`, both Int64), so building by position silently swaps values of one type. The facade passes generated parameters by name.
  - A static method per exported function, forwarding by generated parameter name.
- Per capability, `abstract class {Capability}Base` with readable abstract methods; its explicit interface members forward the generated operation names, so a host implements a capability without hex names.

Signatures in the facade use the aliases. A generic type has no alias, because C# cannot alias an open generic type; its extension properties and constructors are generic methods, and a pattern that needs its type writes the generated name (`NyV_…<long> { item: var x }`, checked by a prototype).

**Names.** The facade's shared names (aliases, base classes, constructors and functions) must be distinct, and every name must be a valid C# identifier that does not start with `Ny` followed by an uppercase letter or `_` (generated and runtime names) or with `__` (the facade's receiver). A field may not be named like a record member (`Equals`, `ToString`, …). A violation rejects the whole request with `FacadeNameConflict` or `FacadeNameInvalid` and a `problems` list naming each display name and its declarations; nothing is written. Duplicate display names elsewhere stay allowed: identity is the stable ID, and only names a host sees must be unique.

**Binding.** The facade is bound to the snapshot it was emitted from, as the [standard host runner](standard-host-runner.md) is. Renaming a declaration renames its facade name, and a host that uses the old name stops compiling; the host regenerates the facade with the program.

**Re-export.** There is none. A dependency's function reaches the facade through a local exported function that calls it. A dependency's type reaches it when an exported signature or field uses it.

## Reason

Every C# host so far rebuilt this layer by hand: snap's `extract.sh` and `Nyr.cs` (6 KB of aliases, tuple wrappers and a hand-written `KeyAct` record mirroring a Union), `examples/mini-work-planner/materialize.py`, `examples/config-request-batch` (`writeHost` substitutes generated names into a template), and the [prompt-desk notes](../plans/prompt-desk-port-improvements.md) (P1). Each new Product or Union cost another hand mapping, which pushed hosts toward Int64 codes (`AgentNext`'s state) and parallel lists (`PickPane`'s `cxs`, `cys`) instead of declared types.

**Rejected: readable generated identifiers.** Naming generated types by display name would break the backend rule that identifiers are injective and independent of mutable names, and every rename would change the program's own source.

**Rejected: wrapper types that convert.** A readable record converted to and from the generated one copies the value on every call; snap calls `layoutFrame` every frame.

Aliases and extension members add names without new types, so values pass through unchanged. Measured in snap's 213x60 synthetic frame: `layoutFrame` median 0.126–0.130 ms through the facade against 0.128–0.130 ms calling the generated function directly, in the same process.

## Consequences

- snap's `nyrwvy-facade` branch deletes `extract.sh` and `Nyr.cs`; its panes, key decisions, agent states and self-test use facade names only, and `--self-test` passes. It has not been run in a real session.
- The facade requires C# 14 (.NET 10) in the host.
- A host that still wants its own wrapper can keep it; emitting without `facade` is unchanged.
- `CSharpFacadeTests` compiles a host that builds, reads, matches, implements and calls (including a generic function) through the facade with warnings as errors, re-emits over an existing facade, and checks that a conflict writes no file.

## Open questions

- **Open:** two dependencies whose reachable types share a display name cannot be fixed by the dependent, which cannot rename imports. The facade rejects them; an emit-time public-name override is the candidate if it happens.
- **Open:** aliases for closed generic instantiations used in signatures (`Box<long>`) were not added; no host needs one yet.
- **Open:** a review `Warning` for duplicate display names in general ([failure policy](../specs/failure-obligation-policy.md#severity)) is the owner's stated direction and is not part of this decision.

## Revisit conditions

Revisit when a host hits the two-dependency conflict, needs a generic type by name, or needs the facade for a backend other than C#.
