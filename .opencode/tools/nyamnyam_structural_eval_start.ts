import { tool } from "@opencode-ai/plugin"
import { startStructuralEvaluation } from "../lib/nyamnyam-structural-eval-bridge.ts"

export default tool({
  description: "Start the single harness-configured persistent NyamNyam M2 structural evaluation session. Fixture, task, corpus revision, artifacts, and stale perturbation are harness-owned.",
  args: {},
  async execute(_args, context) {
    return await startStructuralEvaluation(context)
  },
})
