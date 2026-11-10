import test from "node:test"
import assert from "node:assert/strict"
import { execFileSync } from "node:child_process"
import { copyFile, mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"

const verificationScript = new URL("../verify-local-20260825-1427.sh", import.meta.url)

test("clean verification removes only project outputs and preserves dependency executables", { timeout: 10000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "nyrwvy-clean-test-"))
  const repository = join(root, "repository with spaces")
  const commands = join(root, "commands")
  const temporary = join(root, "tmp")
  async function fixture(path, content) {
    const absolute = join(repository, path)
    await mkdir(dirname(absolute), { recursive: true })
    await writeFile(absolute, content)
  }
  try {
    await Promise.all([mkdir(join(repository, "scripts"), { recursive: true }), mkdir(commands), mkdir(temporary)])
    await copyFile(verificationScript, join(repository, "scripts/verify-local-20260825-1427.sh"))
    await writeFile(join(commands, "dotnet"), `#!/bin/sh
if [ "$1" = "--version" ]; then
  echo 10.0.100
fi
`, { mode: 0o755 })
    await writeFile(join(commands, "git"), "#!/bin/sh\nexit 1\n", { mode: 0o755 })

    const projects = ["src/Graph with spaces/Graph.fsproj", "tools/Example/Example.csproj"]
    for (const project of projects) {
      await fixture(project, "<Project />\n")
      await fixture(join(dirname(project), "bin/Release/output.dll"), "generated")
      await fixture(join(dirname(project), "obj/project.assets.json"), "generated")
    }
    const preserved = [
      "tools/nyrwvy-mcp/node_modules/which/bin/node-which",
      ".opencode/node_modules/msgpackr-extract/bin/download-prebuilds.js",
      "node_modules/vendor/bin/tool",
      "node_modules/vendor/obj/cache",
      ".git/fixture/bin/hook",
      "samples/bin/tool",
      "samples/obj/data",
      "bin/local-tool",
      ".claude/worktrees/agent/src/Other/bin/Release/output.dll",
      ".claude/worktrees/agent/src/Other/obj/project.assets.json",
    ]
    await fixture("node_modules/vendor/Vendor.csproj", "<Project />\n")
    await fixture(".claude/worktrees/agent/src/Other/Other.fsproj", "<Project />\n")
    await fixture(".git/fixture/Fixture.fsproj", "<Project />\n")
    for (const path of preserved) await fixture(path, "keep")

    const output = execFileSync("bash", [join(repository, "scripts/verify-local-20260825-1427.sh"), "--clean"], {
      cwd: repository,
      env: { ...process.env, PATH: `${commands}:${process.env.PATH}`, TMPDIR: temporary },
      encoding: "utf8",
      timeout: 8000,
    })
    assert.match(output, /VERIFICATION PASSED/)
    for (const project of projects) {
      for (const directory of ["bin", "obj"]) {
        await assert.rejects(stat(join(repository, dirname(project), directory)), { code: "ENOENT" })
      }
      assert.equal(await readFile(join(repository, project), "utf8"), "<Project />\n")
    }
    for (const path of preserved) assert.equal(await readFile(join(repository, path), "utf8"), "keep", path)
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})
