import { tool } from "@opencode-ai/plugin"
import { startPureControlEvaluation } from "../lib/nyamnyam-pure-control-eval-bridge.ts"

export default tool({
  description: "Start one persistent NyamNyam scoped pure-control exploratory evaluation session.",
  args: {},
  async execute(_args, context) {
    return await startPureControlEvaluation(context)
  },
})
