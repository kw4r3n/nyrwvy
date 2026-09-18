import { tool } from "@opencode-ai/plugin"
import { restartM4AuthoringEvaluation } from "../lib/nyrwvy-m4-authoring-eval-bridge.ts"

export default tool({
  description: "Close and reopen the active maintained Nyrwvy AuthoringSession from its persisted snapshot.",
  args: {},
  async execute(_args, context) {
    return await restartM4AuthoringEvaluation(context)
  },
})
