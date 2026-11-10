// The Zig host module list is kept by hand in three places; they must name the same modules.
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import test from "node:test"

const read = path => readFileSync(new URL(`../../${path}`, import.meta.url), "utf8")
// build_info is generated at build time by build-zig-host.mjs; only test-typed-host lists it statically.
const names = (text, pattern) => [...text.matchAll(pattern)].map(match => match[1]).filter(name => name !== "build_info").sort()

test("build-zig-host, test-typed-host and ZigTools.fs list the same Zig host modules", () => {
  const js = /^\s*\["(\w+)", "[^"]+\.zig"\]/gm
  const build = names(read("scripts/build-zig-host.mjs"), js)
  assert.ok(build.length >= 5)
  assert.deepEqual(names(read("scripts/test-typed-host.mjs"), js), build)
  assert.deepEqual(names(read("tests/Nyrwvy.Core.Tests/ZigTools.fs"), /"(\w+)", \[ "(?:tools|src)"/g), build)
})
