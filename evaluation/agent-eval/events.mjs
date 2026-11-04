// Summarizes one codex --json event log: tool calls, rejected calls by error code, token usage.
export function summarizeEvents(lines) {
  const events = lines.flatMap(line => { try { return [JSON.parse(line)] } catch { return [] } }) // skips blank and stray non-JSON lines
  const calls = events.filter(e => e.type === "item.completed" && e.item.type === "mcp_tool_call").map(e => e.item)
  const rejections = calls.filter(c => c.status !== "completed").map(c => {
    const text = c.result?.content?.[0]?.text ?? c.error?.message ?? ""
    try { return JSON.parse(text).error?.code ?? "unknown" } catch { return text.slice(0, 60) || "unknown" }
  })
  const usage = events.filter(e => e.type === "turn.completed").map(e => e.usage)
  const sum = key => usage.reduce((total, u) => total + (u[key] ?? 0), 0)
  return { toolCalls: calls.length, rejections, input: sum("input_tokens"), cached: sum("cached_input_tokens"), output: sum("output_tokens") }
}
