import { expect, test } from "bun:test"
import { randomUUID } from "node:crypto"
import { join } from "node:path"
import { tmpdir } from "node:os"
import {
  appendResponseChunk,
  compactArguments,
  frozenRevisionDiffArguments,
  validateExternalArtifactRoot,
  validateJsonObjectLine,
} from "../lib/evaluation-jsonl-runtime.ts"

test("shared JSONL runtime retains complete response lines", () => {
  let state = appendResponseChunk({ buffered: "", completedLines: [] }, "first\nsecond")
  expect(state).toEqual({ buffered: "second", completedLines: ["first"] })
  state = appendResponseChunk(state, "\nthird\npartial")
  expect(state).toEqual({ buffered: "partial", completedLines: ["first", "second", "third"] })
})

test("shared JSONL validation rejects line smuggling and non-object JSON", () => {
  expect(validateJsonObjectLine('{"command":"describeV1"}', "request")).toBe('{"command":"describeV1"}')
  expect(() => validateJsonObjectLine('{"command":"stateV1"}\n{"command":"metricsV1"}', "request")).toThrow()
  expect(() => validateJsonObjectLine("[]", "request")).toThrow()
  expect(() => validateJsonObjectLine(" ", "request")).toThrow()
})

test("shared artifact-root validation rejects worktree containment", () => {
  const worktree = join(tmpdir(), `nyamnyam-shared-eval-worktree-${randomUUID()}`)
  const messages = {
    rootMustBeAbsolute: "root absolute",
    worktreeMustBeAbsolute: "worktree absolute",
    rootMustBeOutsideWorktree: "outside worktree",
  }
  expect(() => validateExternalArtifactRoot("relative", worktree, messages)).toThrow("root absolute")
  expect(() => validateExternalArtifactRoot(join(worktree, "artifacts"), worktree, messages)).toThrow("outside worktree")
})

test("shared option and frozen-diff argument helpers preserve order", () => {
  expect(compactArguments([["--model", "m"], ["--unset", undefined], ["--variant", "high"]])).toEqual([
    "--model", "m", "--variant", "high",
  ])
  expect(frozenRevisionDiffArguments("abc", ["one", "two"])).toEqual([
    "diff", "--exit-code", "abc", "--", "one", "two",
  ])
})
