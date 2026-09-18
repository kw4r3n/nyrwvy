import { tool } from "@opencode-ai/plugin"
import { requestPureControlEvaluation } from "../lib/nyrwvy-pure-control-eval-bridge.ts"

export default tool({
  description: "Send exactly one JSON object to the active pure-expression-control-flow-v1 session. Discover the schema with describeV1.",
  args: {
    request: tool.schema.string().describe("One JSON object for the pure-expression-control-flow-v1 protocol."),
  },
  async execute(args, context) {
    return await requestPureControlEvaluation(args.request, context)
  },
})
