import test from "node:test"
import assert from "node:assert/strict"
import { summarizeEvents } from "../../evaluation/agent-eval/events.mjs"

test("agent eval counts tool calls, rejection codes and token usage from codex events", () => {
  const call = (status, text) => JSON.stringify({ type: "item.completed", item: { type: "mcp_tool_call", status, result: { content: [{ type: "text", text }] } } })
  const lines = [
    JSON.stringify({ type: "item.completed", item: { type: "agent_message", text: "hi" } }),
    call("completed", JSON.stringify({ ok: true })),
    call("failed", JSON.stringify({ ok: false, error: { code: "ProtocolError", message: "x" } })),
    call("failed", "not json"),
    JSON.stringify({ type: "turn.completed", usage: { input_tokens: 100, cached_input_tokens: 60, output_tokens: 7 } }),
    "",
  ]
  assert.deepEqual(summarizeEvents(lines), { toolCalls: 3, rejections: ["ProtocolError", "not json"], input: 100, cached: 60, output: 7 })
})
