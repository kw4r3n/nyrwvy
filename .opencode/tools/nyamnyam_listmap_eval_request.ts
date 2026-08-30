import { tool } from "@opencode-ai/plugin"
import { requestListMapEvaluation } from "../lib/nyamnyam-listmap-eval-bridge.ts"

export default tool({
  description: "Send exactly one JSON object to the active m2-listmap-structural-v1 session. Discover the exact schema with describeV1; this bridge adds no mutation authority.",
  args: {
    request: tool.schema.string().describe("One JSON object for the frozen m2-listmap-structural-v1 protocol."),
  },
  async execute(args, context) {
    return await requestListMapEvaluation(args.request, context)
  },
})
