import { tool } from "@opencode-ai/plugin"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

const definitions = createGraphTools(tool.schema)
export const open = tool(definitions.open)
export const inspect = tool(definitions.inspect)
export const recipe_reference = tool(definitions.recipe_reference)
export const create_type = tool(definitions.create_type)
export const create_function = tool(definitions.create_function)
export const replace = tool(definitions.replace)
export const commit = tool(definitions.commit)
export const reject = tool(definitions.reject)
export const request = tool(definitions.request)
export const close = tool(definitions.close)
