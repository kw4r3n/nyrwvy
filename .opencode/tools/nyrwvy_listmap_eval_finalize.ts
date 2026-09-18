import { tool } from "@opencode-ai/plugin"
import { finalizeListMapEvaluation } from "../lib/nyrwvy-listmap-eval-bridge.ts"

export default tool({
  description: "Finalize the ListMap evaluation session after its terminal protocol state. Runner finalization durably writes metrics, terminal workspace identity, frozen-oracle summary, and committed-state verification evidence.",
  args: {},
  async execute(_args, context) {
    return await finalizeListMapEvaluation(context)
  },
})
