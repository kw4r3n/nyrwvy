# Named pattern references

- **Status:** Accepted; implemented
- **Date:** 2026-10-24
- **Issue:** none yet

This decision adds a wire-level way to refer to a binding by name. The [expression authoring specification](../specs/expression-authoring.md) stays authoritative for `ExpressionRecipe` and `RecipePatternReference`, which do not change.

## Decision

A recipe on the wire, in both the exact-ID and the name-resolved protocols, may name a binding where a binder opens it and refer to it by that name:

- `bind` takes an optional `name` for its value;
- `matchOption` takes an optional `someName` for the Some value;
- `matchResult` takes optional `okName` and `errName`;
- `{ "kind": "patternReference", "name": "<name>" }` refers to the innermost binding with that name.

Before a recipe is decoded, the session rewrites each named reference into the `scopeDepth` and `slot` it stands for, counting every frame that recipe opens between the reference and its binder: Bind bodies, both MatchOption bodies, both MatchResult bodies, and every MatchUnion arm including `otherwise`. The binder names are then dropped. The decoded `ExpressionRecipe`, its inference and materialization, and the persisted graph are exactly what the equivalent `scopeDepth` recipe produces.

A name resolves only to a binder inside the same recipe. A name that no binder in scope declares, a reference that gives both `name` and `scopeDepth`, and a name on a binder that is not a string are protocol errors. Inner names shadow outer ones. Unnamed `scopeDepth` references keep their meaning, and the two forms may be mixed.

## Reason

Four rounds of an MCP reproduction trial and a fix to the rrawrlet port all had to count `scopeDepth` by hand, down to depth 8 in one function. Values of the same type at different depths, such as plan, report and error strings, are swapped without any type error, so only call-level tests caught the mistakes.

Persisting binding names would need a name on four recipe binder cases and about 160 match sites, plus a stored display name for every binding. The rewrite gives the same authoring benefit inside one recipe with no core change. The frame rules it mirrors are the four binder kinds inference pushes frames for (`ExpressionRecipeInference`), and the decoded recipe is checked by the same inference as before, so a rewrite mistake still surfaces as an ordinary type or scope diagnostic.

## Consequences

- Most deep references in a new function body or a large replacement can be written by name.
- A targeted replacement, an arm plan or a field value recipe that refers to a binding of the existing graph outside the recipe still uses `scopeDepth`. Those bindings have no authored name, and a persisted binding display name for Bind and match values does not exist yet.
- Reviews, inspection and the persisted graph do not show the names; they exist only in the request.

## Revisit conditions

Revisit when a workflow needs to refer by name to a binding outside the recipe, when reviews or inspection should show binding names, or when a new recipe kind opens a frame, which must also be added to the rewrite.
