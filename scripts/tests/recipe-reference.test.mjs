import assert from "node:assert/strict"
import test from "node:test"
import { recipeKinds, recipeReference } from "../../tools/nyrwvy-agent/recipe-reference.mjs"

// Loading the module is the main check: a quoting slip in a note breaks every graph tool that imports it.
test("every recipe kind has a reference entry, and self calls document keepUnlisted", () => {
  for (const kind of recipeKinds) assert.equal(recipeReference({ kind }).format, "nameResolvedExpressionRecipe", kind)
  const directCall = recipeReference({ kind: "directCall" })
  assert.ok("keepUnlisted" in directCall.shape)
  assert.match(directCall.notes, /keepUnlisted/)
  assert.match(recipeReference({ kind: "listLastIndexOf" }).notes, /last item/)
})
