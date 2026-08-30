import { expect, test } from "bun:test"
import { randomUUID } from "node:crypto"
import { chmod, mkdtemp, rm, stat, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { isAbsolute, join, relative, sep } from "node:path"
import {
  appendResponseChunk,
  ensurePrivateDirectory,
  ensurePrivateFile,
  frozenInputDiffArguments,
  frozenListMapInputPaths,
  metadataArguments,
  resolveArtifactRoot,
  stalePerturbationArguments,
  validateRequestLine,
} from "../lib/nyamnyam-listmap-eval-bridge.ts"

test("direct ListMap tool modules load", async () => {
  const start = await import("../tools/nyamnyam_listmap_eval_start.ts")
  const request = await import("../tools/nyamnyam_listmap_eval_request.ts")
  const finalize = await import("../tools/nyamnyam_listmap_eval_finalize.ts")
  expect(start.default).toBeDefined()
  expect(request.default).toBeDefined()
  expect(finalize.default).toBeDefined()
})

test("ListMap bridge retains complete response lines", () => {
  let state = appendResponseChunk({ buffered: "", completedLines: [] }, "first\nsecond")
  expect(state).toEqual({ buffered: "second", completedLines: ["first"] })
  state = appendResponseChunk(state, "\nthird\npartial")
  expect(state).toEqual({ buffered: "partial", completedLines: ["first", "second", "third"] })
})

test("ListMap bridge request validation rejects line smuggling and non-object JSON", () => {
  expect(validateRequestLine('{"command":"describeV1"}')).toBe('{"command":"describeV1"}')
  expect(() => validateRequestLine('{"command":"stateV1"}\n{"command":"metricsV1"}')).toThrow()
  expect(() => validateRequestLine("[]")).toThrow()
  expect(() => validateRequestLine(" ")).toThrow()
})

test("ListMap artifact root must remain outside worktree", () => {
  const original = process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT
  const worktree = join(tmpdir(), `nyamnyam-listmap-worktree-${randomUUID()}`)
  try {
    process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT = "relative-artifacts"
    expect(() => resolveArtifactRoot(worktree)).toThrow("absolute path")

    process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT = join(worktree, "artifacts")
    expect(() => resolveArtifactRoot(worktree)).toThrow("outside the evaluation worktree")

    const outside = join(tmpdir(), `nyamnyam-listmap-artifacts-${randomUUID()}`)
    process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT = outside
    const resolved = resolveArtifactRoot(worktree)
    const relativeRoot = relative(worktree, resolved)
    expect(isAbsolute(resolved)).toBe(true)
    expect(relativeRoot === "" || (relativeRoot !== ".." && !relativeRoot.startsWith(`..${sep}`) && !isAbsolute(relativeRoot))).toBe(false)
  } finally {
    if (original === undefined) delete process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT
    else process.env.NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT = original
  }
})

test("ListMap bridge pins corpus oracle protocol and implementation inputs to frozen revision", () => {
  expect(frozenListMapInputPaths).toEqual([
    "evaluation/m2-listmap-agent-corpus-v1",
    "tests/NyamNyam.Core.Tests/M2ListMapStructuralEvaluationCorpus.fs",
    "tests/NyamNyam.Core.Tests/M2ListMapStructuralEvaluationCorpusTests.fs",
    "tools/NyamNyam.EvaluationFixtureExport/Program.fs",
    "tools/NyamNyam.EvaluationFixtureExport/NyamNyam.EvaluationFixtureExport.fsproj",
    "src/NyamNyam.Core/AgentListMapStructuralWorkflow.fs",
    "tools/NyamNyam.AgentEval.ListMapStructural/Protocol.fs",
  ])
  expect(frozenInputDiffArguments()).toEqual([
    "diff",
    "--exit-code",
    "a2ffb5615d37d44bbd910c4f47c36136c31cdce4",
    "--",
    ...frozenListMapInputPaths,
  ])
})

test("ListMap bridge uses harness-only stale perturbation only for L10", () => {
  expect(stalePerturbationArguments("M2-L1")).toEqual([])
  expect(stalePerturbationArguments("M2-L10")).toEqual(["--stale-perturbation", "m2-l10"])
})

test("ListMap bridge forwards operator-fixed model and condition metadata", () => {
  const names = [
    "NYAMNYAM_LISTMAP_EVAL_MODEL",
    "NYAMNYAM_LISTMAP_EVAL_PROVIDER_RUNTIME",
    "NYAMNYAM_LISTMAP_EVAL_REASONING_CONFIG",
    "NYAMNYAM_LISTMAP_EVAL_OPENCODE_VERSION",
    "NYAMNYAM_LISTMAP_EVAL_PROMPT_REVISION",
    "NYAMNYAM_LISTMAP_EVAL_TOOL_REVISION",
  ] as const
  const previous = Object.fromEntries(names.map((name) => [name, process.env[name]]))
  try {
    process.env.NYAMNYAM_LISTMAP_EVAL_MODEL = "openai/gpt-5.6-terra"
    process.env.NYAMNYAM_LISTMAP_EVAL_PROVIDER_RUNTIME = "opencode"
    process.env.NYAMNYAM_LISTMAP_EVAL_REASONING_CONFIG = "high"
    process.env.NYAMNYAM_LISTMAP_EVAL_OPENCODE_VERSION = "test-opencode"
    process.env.NYAMNYAM_LISTMAP_EVAL_PROMPT_REVISION = "m2-listmap-corpus-v1-seed-v1"
    process.env.NYAMNYAM_LISTMAP_EVAL_TOOL_REVISION = "tool-v1"
    const args = metadataArguments()
    expect(args).toContainEqual("--model")
    expect(args).toContainEqual("openai/gpt-5.6-terra")
    expect(args).toContainEqual("--provider-runtime")
    expect(args).toContainEqual("opencode")
    expect(args).toContainEqual("--reasoning-config")
    expect(args).toContainEqual("high")
    expect(args).toContainEqual("--opencode-version")
    expect(args).toContainEqual("test-opencode")
    expect(args).toContainEqual("--prompt-revision")
    expect(args).toContainEqual("m2-listmap-corpus-v1-seed-v1")
    expect(args).toContainEqual("--tool-revision")
    expect(args).toContainEqual("tool-v1")
  } finally {
    for (const name of names) {
      const value = previous[name]
      if (value === undefined) delete process.env[name]
      else process.env[name] = value
    }
  }
})

test("ListMap bridge private mode helpers enforce Unix permissions", async () => {
  if (process.platform === "win32") return
  const root = await mkdtemp(join(tmpdir(), "nyamnyam-listmap-permissions-"))
  try {
    await ensurePrivateDirectory(root)
    const directory = join(root, "run")
    await ensurePrivateDirectory(directory)
    const fixture = join(directory, "fixture.json")
    await writeFile(fixture, "{}")
    await ensurePrivateFile(fixture)
    expect((await stat(root)).mode & 0o777).toBe(0o700)
    expect((await stat(directory)).mode & 0o777).toBe(0o700)
    expect((await stat(fixture)).mode & 0o777).toBe(0o600)
  } finally {
    await chmod(root, 0o700).catch(() => undefined)
    await rm(root, { recursive: true, force: true })
  }
})
