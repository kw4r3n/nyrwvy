import test from "node:test"
import assert from "node:assert/strict"
import { mkdtemp, rm, symlink } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join } from "node:path"
import { randomUUID } from "node:crypto"
import { openAuthoring, requestAuthoring, closeAuthoring } from "../../tools/nyrwvy-agent/authoring.ts"

// Fast-contract coverage for three safety refusals that used to be exercised only by the
// extended F# parity suite: the pending-proposal cap, a snapshot opened for writing through
// a symbolic link, and a symbolic-link session lock. If any of them silently stopped
// refusing, a routine run should say so the same day.
const union = (displayName, variants) => ({ op: "simulateCreateTypeByName", displayName,
  typeKind: { kind: "union", variants: variants.map(displayName => ({ displayName, fields: [] })) } })

async function request(context, payload) {
  const reply = JSON.parse(await requestAuthoring(payload, context))
  assert.equal(reply.ok, true, JSON.stringify(reply.error))
  return reply.result
}

test("the pending proposal limit refuses another simulation until one is rejected", { timeout: 30000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-pending-limit-"))
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const snapshot = join(directory, "session.json")
  process.env.NYRWVY_TEST_PENDING_LIMIT = "2"
  try {
    await openAuthoring("create", snapshot, context)
    const first = await request(context, union("First", ["A"]))
    await request(context, union("Second", ["B"]))
    const refused = JSON.parse(await requestAuthoring(union("Third", ["C"]), context))
    assert.equal(refused.ok, false)
    assert.equal(refused.error.code, "SessionError")
    assert.match(refused.error.message, /already holds 2 pending proposals/)
    await request(context, { op: "reject", proposalId: first.proposalId })
    const third = await request(context, union("Third", ["C"]))
    assert.ok(third.proposalId)
    await closeAuthoring(context)
  } finally {
    delete process.env.NYRWVY_TEST_PENDING_LIMIT
    await rm(directory, { recursive: true, force: true })
  }
})

test("a snapshot or session lock reached through a symbolic link is refused for writing", { timeout: 30000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), "nyrwvy-link-refusal-"))
  const context = { sessionID: randomUUID(), worktree: process.cwd() }
  const target = join(directory, "real.json")
  const link = join(directory, "link.json")
  try {
    await openAuthoring("create", target, context)
    await closeAuthoring(context)
    await symlink(target, link)
    await assert.rejects(openAuthoring("open", link, context), error =>
      error.message.startsWith(`AuthoringSession could not start: IoFailure: The snapshot path ${link} is a symbolic link`))
    // A clean close leaves the lock file behind; replace it with a link and the open of the
    // real snapshot must refuse on the lock instead.
    await rm(`${target}.lock`)
    await symlink(target, `${target}.lock`)
    await assert.rejects(openAuthoring("open", target, context), error =>
      error.message.startsWith(`AuthoringSession could not start: IoFailure: The session lock path ${target}.lock is a symbolic link`))
  } finally {
    await rm(directory, { recursive: true, force: true })
  }
})
