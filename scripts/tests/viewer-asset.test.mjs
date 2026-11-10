import assert from "node:assert/strict"
import { createHash } from "node:crypto"
import { readFile } from "node:fs/promises"
import test from "node:test"

// The viewer page's Content-Security-Policy runs only the script whose hash it names, so an edit to
// the script that leaves the hash behind renders every page blank.
test("the viewer asset's CSP names the hash of its own script", async () => {
  const page = await readFile(new URL("../../src/Nyrwvy.Viewer/viewer.html", import.meta.url), "utf8")
  const scripts = [...page.matchAll(/<script>([\s\S]*?)<\/script>/g)]
  assert.equal(scripts.length, 1, "one executable script")
  const hash = createHash("sha256").update(scripts[0][1], "utf8").digest("base64")
  assert.match(page, new RegExp(`script-src 'sha256-${hash.replace(/[+/]/g, "\\$&")}'`))
})
