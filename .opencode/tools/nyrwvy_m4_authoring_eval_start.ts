import { tool } from "@opencode-ai/plugin"
import { startM4AuthoringEvaluation } from "../lib/nyrwvy-m4-authoring-eval-bridge.ts"

export default tool({
  description: "Start one fresh Nyrwvy Milestone 4 maintained-authoring exploratory session.",
  args: {},
  async execute(_args, context) {
    return await startM4AuthoringEvaluation(context)
  },
})
