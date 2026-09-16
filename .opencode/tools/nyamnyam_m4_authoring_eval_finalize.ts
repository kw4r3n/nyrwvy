import { tool } from "@opencode-ai/plugin"
import { finalizeM4AuthoringEvaluation } from "../lib/nyamnyam-m4-authoring-eval-bridge.ts"

export default tool({
  description: "Finalize the active NyamNyam Milestone 4 maintained-authoring exploratory session.",
  args: {},
  async execute(_args, context) {
    return await finalizeM4AuthoringEvaluation(context)
  },
})
