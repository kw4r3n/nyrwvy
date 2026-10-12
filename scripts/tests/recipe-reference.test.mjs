import assert from "node:assert/strict"
import test from "node:test"
import { recipeCategories, recipeKinds, recipeReference } from "../../tools/nyrwvy-agent/recipe-reference.mjs"
import { fixedSignatures } from "../../tools/nyrwvy-agent/recipe-signatures.generated.mjs"

// Loading the module is the main check: a quoting slip in a note breaks every graph tool that imports it.
test("every recipe kind has a reference entry, and self calls document keepUnlisted", () => {
  for (const kind of recipeKinds) assert.equal(recipeReference({ kind }).format, "nameResolvedExpressionRecipe", kind)
  const directCall = recipeReference({ kind: "directCall" })
  assert.ok("keepUnlisted" in directCall.shape)
  assert.match(directCall.notes, /keepUnlisted/)
  assert.match(recipeReference({ kind: "listLastIndexOf" }).notes, /last item/)
})

// The F# table is the source of every fixed signature, so each one must reach exactly one category.
test("every generated fixed signature is listed under one category", () => {
  const listed = recipeCategories.flatMap(category => Object.entries(recipeReference({ category }).signatures))
  for (const [kind, signature] of Object.entries(fixedSignatures)) {
    assert.deepEqual(listed.filter(([name]) => name === kind), [[kind, signature]], kind)
  }
})

test("patternReference guidance counts empty frames without guessing the intended binding", () => {
  for (const entry of [
    recipeReference({ kind: "patternReference" }),
    recipeReference({ kinds: ["patternReference", "matchOption"] }).entries[0],
  ]) {
    assert.match(entry.notes, /including empty MatchOption None/)
    assert.match(entry.notes, /conditional branches do not add a frame/)
    assert.match(entry.notes, /Bind value or match input is outside/)
    assert.match(entry.notes, /several frames can bind value with the same type/)
    assert.match(entry.notes, /depth 1 is right, and depth 2 is left/)
    assert.doesNotMatch(entry.notes, /innermost is usually meant/)
  }
})
