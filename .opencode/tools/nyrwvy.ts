import { tool } from "@opencode-ai/plugin"
import { createGraphTools } from "../../tools/nyrwvy-agent/graph-tools.mjs"

type OpenCodeSchema = ReturnType<typeof tool>["args"][string]
const definitions = createGraphTools<OpenCodeSchema>(tool.schema)
export const open = tool(definitions.open)
export const inspect = tool(definitions.inspect)
export const view = tool(definitions.view)
export const recipe_reference = tool(definitions.recipe_reference)
export const create_type = tool(definitions.create_type)
export const create_function = tool(definitions.create_function)
export const replace = tool(definitions.replace)
export const upgrade_dependencies = tool(definitions.upgrade_dependencies)
export const commit = tool(definitions.commit)
export const reject = tool(definitions.reject)
export const request = tool(definitions.request)
export const close = tool(definitions.close)
