import assert from "node:assert/strict"
import { existsSync, readFileSync, readdirSync } from "node:fs"
import { dirname, join, resolve } from "node:path"
import test from "node:test"
import { fileURLToPath } from "node:url"

const root = fileURLToPath(new URL("../../", import.meta.url))

test("production F# sources are owned and compiled by their physical assembly directory", () => {
  for (const name of ["Nyrwvy.Semantics", "Nyrwvy.Execution", "Nyrwvy.Backend.CSharp", "Nyrwvy.Viewer"]) {
    const directory = resolve(root, "src", name)
    const project = readFileSync(join(directory, `${name}.fsproj`), "utf8")
    const items = [...project.matchAll(/<Compile\s+Include="([^"]+)"[^>]*\/>/g)]
    assert.ok(items.length > 0, `${name} must explicitly own its F# compile order`)
    for (const [element, source] of items) {
      assert.doesNotMatch(element, /\bLink=/, `${name}: ${source}`)
      assert.equal(dirname(resolve(directory, source)), directory, `${name}: ${source}`)
      assert.ok(existsSync(join(directory, source)), `${name}: ${source} must exist`)
    }
    const physical = readdirSync(directory).filter((file) => file.endsWith(".fs")).sort()
    assert.deepEqual(items.map(([, source]) => source).sort(), physical, `${name}: unowned or missing source`)
  }
})
