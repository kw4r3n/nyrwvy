import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { fileURLToPath } from "node:url"

const repo = fileURLToPath(new URL("../..", import.meta.url))
const host = join(repo, "tools/zig/host/bin/nyrwvy-host")
// Ids are random UUIDv7 and the snapshot orders its entries by id, so ids are blanked and arrays sorted.
// shortcut: this ignores which id refers to which; upgrade to a seeded host (NYRWVY_TEST_ID_SEED) if tracked files are ever regenerated with one.
const canonical = value => Array.isArray(value) ? value.map(canonical).sort((a, b) => JSON.stringify(a) < JSON.stringify(b) ? -1 : 1)
  : value && typeof value === "object" ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key, canonical(item)])) : value
const normalized = text => canonical(JSON.parse(text.replace(/[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}/g, "ID")))

for (const name of ["core", "json"]) {
  test(`libraries/${name}/${name}.json is what author.mjs generates`, { timeout: 300_000 }, t => {
    if (!existsSync(host)) return t.skip("native host not built (npm run build:host)")
    const out = mkdtempSync(join(tmpdir(), `nyr-${name}-fresh-`))
    t.after(() => rmSync(out, { recursive: true, force: true }))
    const run = spawnSync(process.execPath, [join(repo, `libraries/${name}/author.mjs`), host, out], { encoding: "utf8" })
    assert.equal(run.status, 0, run.stderr)
    assert.deepEqual(normalized(readFileSync(join(out, `${name}.json`), "utf8")), normalized(readFileSync(join(repo, `libraries/${name}/${name}.json`), "utf8")),
      `run node libraries/${name}/author.mjs to regenerate ${name}.json`)
  })
}
