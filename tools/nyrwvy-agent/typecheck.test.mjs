import test from "node:test"
import assert from "node:assert/strict"
import { readdirSync, readFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import { join, relative } from "node:path"
import ts from "typescript"

const root = fileURLToPath(new URL("../../", import.meta.url))
function config(name) {
  const path = join(root, name)
  const source = ts.readConfigFile(path, ts.sys.readFile)
  assert.equal(source.error, undefined)
  const parsed = ts.parseJsonConfigFileContent(source.config, ts.sys, root, undefined, path)
  assert.deepEqual(parsed.errors, [])
  return parsed
}
function sources(directory) {
  return readdirSync(join(root, directory), { withFileTypes: true }).flatMap(entry => {
    if (entry.name === "node_modules") return []
    const path = join(directory, entry.name)
    return entry.isDirectory() ? sources(path) : [path]
  })
}

test("adapter configs cover maintained TypeScript and shared/MCP JavaScript without Bun ambient leakage", () => {
  const adapters = config("tsconfig.json")
  const bun = config("tsconfig.bun.json")
  const adapterFiles = new Set(adapters.fileNames.map(path => relative(root, path)))
  const bunFiles = new Set(bun.fileNames.map(path => relative(root, path)))
  const maintained = [".opencode", "tools/nyrwvy-agent", "tools/nyrwvy-mcp"].flatMap(sources)
  for (const path of maintained.filter(path => path.endsWith(".ts"))) {
    const isBunTest = path.endsWith(".test.ts")
    assert.ok((isBunTest ? bunFiles : adapterFiles).has(path), `${path} must be typechecked`)
    if (isBunTest) assert.equal(adapterFiles.has(path), false, path)
  }
  for (const path of ["tools/nyrwvy-agent/graph-tools.mjs", "tools/nyrwvy-agent/recipe-reference.mjs", "tools/nyrwvy-mcp/server.mjs"]) {
    assert.ok(adapterFiles.has(path), `${path} implementation must be checked, not a declaration stub`)
  }
  for (const parsed of [adapters, bun]) {
    for (const option of ["strict", "noEmit", "allowJs", "checkJs", "allowImportingTsExtensions", "erasableSyntaxOnly", "verbatimModuleSyntax"]) {
      assert.equal(parsed.options[option], true, option)
    }
    assert.notEqual(parsed.options.skipLibCheck, true)
    assert.notEqual(parsed.options.noCheck, true)
    assert.ok(!parsed.options.lib.some(lib => lib.includes("dom")))
  }
  assert.deepEqual(adapters.options.types, ["node"])
  assert.deepEqual(bun.options.types, ["node", "bun-types/test"])
  const resolved = ts.resolveModuleName("@opencode-ai/plugin", join(root, ".opencode/tools/nyrwvy.ts"), adapters.options, ts.sys)
  assert.equal(resolved.resolvedModule.resolvedFileName, join(root, "node_modules/@opencode-ai/plugin/dist/index.d.ts"))
})

test("the compiler diagnoses implementation mistakes and schema/handler mismatches in both hosts", { timeout: 60000 }, () => {
  const parsed = config("tsconfig.json")
  const replacements = new Map([
    [join(root, "tools/nyrwvy-agent/graph-tools.mjs"), [
      ["snapshot: schema.string()", "snapshot: schema.array(schema.string())"],
      ["openAuthoring(args.mode, args.snapshot, context, args.workspace, args.dependency)", "openAuthoring(args.mode, 42, context, args.workspace, args.dependency)"],
    ]],
    [join(root, "tools/nyrwvy-agent/recipe-reference.mjs"), [['binary("int64WrappingAdd")', "binary(42)"]]],
    [join(root, "tools/nyrwvy-mcp/server.mjs"), [['content: [{ type: "text", text }]', 'content: [{ type: "invalid-content-kind", text }]']]],
    [join(root, "tools/nyrwvy-agent/authoring.ts"), [["import { randomUUID }", "Bun.version;\nimport { randomUUID }"]]],
  ])
  const overrides = new Map()
  for (const [path, edits] of replacements) {
    let text = readFileSync(path, "utf8")
    for (const [before, after] of edits) {
      assert.ok(text.includes(before), `Update the diagnostic probe for ${relative(root, path)}`)
      text = text.replace(before, after)
    }
    overrides.set(path, text)
  }
  const host = ts.createCompilerHost(parsed.options)
  const readFile = host.readFile.bind(host)
  host.readFile = path => overrides.get(path) ?? readFile(path)
  const program = ts.createProgram(parsed.fileNames, parsed.options, host)
  const diagnostics = ts.getPreEmitDiagnostics(program)
  const errors = diagnostics.filter(diagnostic => diagnostic.category === ts.DiagnosticCategory.Error)
  for (const path of replacements.keys()) {
    assert.ok(errors.some(error => error.file?.fileName === path), `Expected a real implementation diagnostic in ${relative(root, path)}`)
  }
  const openCode = errors.filter(error => error.file?.fileName === join(root, ".opencode/tools/nyrwvy.ts"))
  assert.ok(openCode.some(error => ts.flattenDiagnosticMessageText(error.messageText, "\n").includes("snapshot")), "OpenCode must reject a mismatched handler")
  const mcp = errors.filter(error => error.file?.fileName === join(root, "tools/nyrwvy-mcp/server.mjs"))
  assert.ok(mcp.some(error => ts.flattenDiagnosticMessageText(error.messageText, "\n").includes("snapshot")),
      `MCP must reject a mismatched handler: ${mcp.map(error => ts.flattenDiagnosticMessageText(error.messageText, "\n")).join("\n")}`)
})
