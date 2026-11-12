import test from "node:test"
import assert from "node:assert/strict"
import { spawnSync } from "node:child_process"

// The model-cooldown example (examples/model-cooldown): rrawrlet's cooldown update ported to a graph
// program over the host library's file, clock and lock primitives, run on the Zig backend (about
// 30 s) and on the C# backend (about 4 s, needs dotnet).
const skip = process.platform !== "linux" ? "the harness links a Linux .so with -rpath and uses the real OS" : false
const tail = text => text.split("\n").slice(-40).join("\n")
const run = script => {
  const check = spawnSync(process.execPath, [script], { cwd: process.cwd(), encoding: "utf8", timeout: 280_000 })
  assert.equal(check.status, 0, tail(check.stdout + check.stderr))
}

test("the model cooldown example runs on Zig", { timeout: 300000, skip }, () => run("examples/model-cooldown/run-zig.mjs"))

test("the model cooldown example runs on C#", { timeout: 300000, skip }, () => run("examples/model-cooldown/run.mjs"))
