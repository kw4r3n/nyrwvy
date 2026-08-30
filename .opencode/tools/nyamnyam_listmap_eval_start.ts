import { tool } from "@opencode-ai/plugin"
import { startListMapEvaluation } from "../lib/nyamnyam-listmap-eval-bridge.ts"

export default tool({
  description: "Start the single harness-configured persistent NyamNyam M2 ListMap evaluation session. Fixture, task, frozen corpus revision, artifacts, and L10 stale perturbation are harness-owned.",
  args: {},
  async execute(_args, context) {
    return await startListMapEvaluation(context)
  },
})
