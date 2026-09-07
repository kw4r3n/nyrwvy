import { tool } from "@opencode-ai/plugin"
import { finalizePureControlEvaluation } from "../lib/nyamnyam-pure-control-eval-bridge.ts"

export default tool({
  description: "Finalize the active scoped pure-control exploratory evaluation session.",
  args: {},
  async execute(_args, context) {
    return await finalizePureControlEvaluation(context)
  },
})
