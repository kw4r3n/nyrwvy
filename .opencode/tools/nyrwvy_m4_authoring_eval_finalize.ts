import { tool } from "@opencode-ai/plugin"
import { finalizeM4AuthoringEvaluation } from "../lib/nyrwvy-m4-authoring-eval-bridge.ts"

export default tool({
  description: "Finalize the active Nyrwvy Milestone 4 maintained-authoring exploratory session.",
  args: {},
  async execute(_args, context) {
    return await finalizeM4AuthoringEvaluation(context)
  },
})
