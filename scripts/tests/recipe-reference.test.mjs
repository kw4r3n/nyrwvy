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
