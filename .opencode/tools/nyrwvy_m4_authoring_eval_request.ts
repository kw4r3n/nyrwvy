import { tool } from "@opencode-ai/plugin"
import { requestM4AuthoringEvaluation } from "../lib/nyrwvy-m4-authoring-eval-bridge.ts"

export default tool({
  description: "Send exactly one maintained Nyrwvy AuthoringSession JSON request to the active M4 exploratory session.",
  args: {
    request: tool.schema.string().describe("One JSON object for the maintained Nyrwvy AuthoringSession protocol."),
  },
  async execute(args, context) {
    return await requestM4AuthoringEvaluation(args.request, context)
  },
})
