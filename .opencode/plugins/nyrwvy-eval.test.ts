import { expect, test } from "bun:test"
import { spawn } from "node:child_process"
import { randomUUID } from "node:crypto"
import { mkdtemp, rm, stat, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { isAbsolute, join, relative, sep } from "node:path"
import {
  appendResponseChunk,
  createLiveSession,
  ensurePrivateDirectory,
  ensurePrivateFile,
  finalizeRunnerProcess,
  metadataArguments,
  resolveArtifactRoot,
  validateRequestLine,
} from "./nyrwvy-eval"

test("response chunks retain completed lines until a waiter consumes them", () => {
  let state = appendResponseChunk({ buffered: "", completedLines: [] }, "first\nsecond")
  expect(state).toEqual({ buffered: "second", completedLines: ["first"] })

  state = appendResponseChunk(state, "\nthird\npartial")
  expect(state).toEqual({ buffered: "partial", completedLines: ["first", "second", "third"] })
})

test("request validation rejects line smuggling and non-object JSON", () => {
  expect(validateRequestLine('{"command":"state"}')).toBe('{"command":"state"}')
  expect(() => validateRequestLine('{"command":"state"}\n{"command":"metrics"}')).toThrow()
  expect(() => validateRequestLine('{"command":"state"}\r')).toThrow()
  expect(() => validateRequestLine("[]")).toThrow()
  expect(() => validateRequestLine('{"command":"state"} {"command":"metrics"}')).toThrow()
})

test("artifact root rejects relative and worktree-contained harness configuration", () => {
  const worktree = join(tmpdir(), `nyrwvy-eval-worktree-${randomUUID()}`)
  const original = process.env.NYRWVY_EVAL_ARTIFACT_ROOT

  try {
    process.env.NYRWVY_EVAL_ARTIFACT_ROOT = "relative-artifacts"
    expect(() => resolveArtifactRoot(worktree)).toThrow("absolute path")

    process.env.NYRWVY_EVAL_ARTIFACT_ROOT = join(worktree, "artifacts")
    expect(() => resolveArtifactRoot(worktree)).toThrow("outside the evaluation worktree")

    const outside = join(tmpdir(), `nyrwvy-eval-artifacts-${randomUUID()}`)
    process.env.NYRWVY_EVAL_ARTIFACT_ROOT = outside
    expect(resolveArtifactRoot(worktree)).toBe(outside)
  } finally {
    if (original === undefined) delete process.env.NYRWVY_EVAL_ARTIFACT_ROOT
    else process.env.NYRWVY_EVAL_ARTIFACT_ROOT = original
  }
})

test("default artifact root is absolute and outside the worktree", () => {
  const original = process.env.NYRWVY_EVAL_ARTIFACT_ROOT
  const worktree = join(tmpdir(), `nyrwvy-eval-default-worktree-${randomUUID()}`)

  try {
    delete process.env.NYRWVY_EVAL_ARTIFACT_ROOT
    const root = resolveArtifactRoot(worktree)
    const relativeRoot = relative(worktree, root)
    const contained =
      relativeRoot === "" ||
      (relativeRoot !== ".." && !relativeRoot.startsWith(`..${sep}`) && !isAbsolute(relativeRoot))

    expect(isAbsolute(root)).toBe(true)
    expect(contained).toBe(false)
  } finally {
    if (original === undefined) delete process.env.NYRWVY_EVAL_ARTIFACT_ROOT
    else process.env.NYRWVY_EVAL_ARTIFACT_ROOT = original
  }
})

test("adapter forwards the harness OpenCode version as private run metadata", () => {
  const original = process.env.NYRWVY_EVAL_OPENCODE_VERSION

  try {
    process.env.NYRWVY_EVAL_OPENCODE_VERSION = "1.2.3"
    expect(metadataArguments("metadata-test")).toContainEqual("--opencode-version")
    expect(metadataArguments("metadata-test")).toContainEqual("1.2.3")
  } finally {
    if (original === undefined) delete process.env.NYRWVY_EVAL_OPENCODE_VERSION
    else process.env.NYRWVY_EVAL_OPENCODE_VERSION = original
  }
})

test("artifact helpers apply private modes on Unix", async () => {
  if (process.platform === "win32") return

  const root = await mkdtemp(join(tmpdir(), "nyrwvy-eval-permissions-"))
  try {
    await ensurePrivateDirectory(root)
    const runDirectory = join(root, "run")
    await ensurePrivateDirectory(runDirectory)
    const fixturePath = join(runDirectory, "fixture.json")
    await writeFile(fixturePath, "{}")
    await ensurePrivateFile(fixturePath)

    expect((await stat(root)).mode & 0o777).toBe(0o700)
    expect((await stat(runDirectory)).mode & 0o777).toBe(0o700)
    expect((await stat(fixturePath)).mode & 0o777).toBe(0o600)
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test("adapter reports failed runner finalization without exposing diagnostics", async () => {
  const artifactDirectory = "/private/harness/evaluation-runs/secret-run"
  const child = spawn(process.execPath, ["-e", 'process.stderr.write("private finalization details"); process.exit(17)'], {
    stdio: ["pipe", "pipe", "pipe"],
  })
  const session = createLiveSession(child, artifactDirectory, "finalization-test")

  let failure: unknown
  try {
    await finalizeRunnerProcess(session)
  } catch (error) {
    failure = error
  }

  expect(failure).toBeInstanceOf(Error)
  expect((failure as Error).message).toContain("session finalization failed")
  expect((failure as Error).message).not.toContain(artifactDirectory)
})
