import { tool } from "@opencode-ai/plugin"
import { finalizeStructuralEvaluation } from "../lib/nyrwvy-structural-eval-bridge.ts"

export default tool({
  description: "Finalize the structural evaluation session after its terminal protocol state. Runner finalization durably writes metrics, terminal workspace identity, and committed-state verification evidence.",
  args: {},
  async execute(_args, context) {
    return await finalizeStructuralEvaluation(context)
  },
})
