import { tool } from "@opencode-ai/plugin"
import { openAuthoring, requestAuthoring, closeAuthoring } from "../lib/nyrwvy-authoring.ts"

const object = tool.schema.record(tool.schema.string(), tool.schema.unknown())
const typeRef = object.describe('TypeRef: {kind:"primitive",primitive:"bool"|"int64"|"float64"|"string"|"unit"}, {kind:"named",name:"Type"}, or builtinGeneric with generic and arguments.')
const recipe = object.describe("Structured expression recipe, not source code. Named references use parameter.name, fieldAccess.type/field, directCall.function and arguments[].parameter, unionConstruct.type/variant. See docs/specs/agent-graph-tools.md. Use decimal strings for exact Int64 literals.")

export const open = tool({
  description: "Create or reopen one persistent Nyrwvy graph. Requires the Release AuthoringSession build. No evaluation agent or per-program helper required.",
  args: { mode: tool.schema.enum(["create", "open"]), snapshot: tool.schema.string() },
  execute: (args, context) => openAuthoring(args.mode, args.snapshot, context),
})

export const inspect = tool({
  description: "Inspect status, declarations, or one function's typed graph, literal values, branch scopes and callers. Pass exactly one function name or functionId for the function view.",
  args: {
    view: tool.schema.enum(["status", "declarations", "function"]),
    function: tool.schema.string().optional(), functionId: tool.schema.string().optional(),
  },
  execute(args, context) {
    if (args.view !== "function") {
      if (args.function !== undefined || args.functionId !== undefined) throw new Error("Function selectors require view=function.")
      return requestAuthoring({ op: args.view }, context)
    }
    return requestAuthoring({ op: "readFunction", ...(args.function === undefined ? {} : { function: args.function }),
      ...(args.functionId === undefined ? {} : { functionId: args.functionId }) }, context)
  },
})

export const create_type = tool({
  description: "Propose a Product or Union declaration. Resolve named type references inside the session; review the result before committing.",
  args: { displayName: tool.schema.string(), typeKind: object.describe('Product: {kind:"product",fields:[{displayName,type}]}. Union: {kind:"union",variants:[{displayName,fields:[]}]}') },
  execute: (args, context) => requestAuthoring({ op: "simulateCreateTypeByName", ...args }, context),
})

export const create_function = tool({
  description: "Propose a typed function directly in the semantic graph. Body parameter references use names, not caller-allocated IDs.",
  args: {
    displayName: tool.schema.string(),
    parameters: tool.schema.array(tool.schema.object({ displayName: tool.schema.string(), type: typeRef })),
    resultType: typeRef, requiredEffects: tool.schema.array(tool.schema.string()), body: recipe,
  },
  execute: (args, context) => requestAuthoring({ op: "simulateCreateFunctionByName", ...args }, context),
})

export const replace = tool({
  description: "Propose a targeted graph edit, preserving unrelated nodes. Use a targetNodeId from inspect; omit it only for intentional whole-body replacement. Review before commit.",
  args: { function: tool.schema.string(), targetNodeId: tool.schema.string().optional(), replacement: recipe },
  execute: (args, context) => requestAuthoring({
    op: args.targetNodeId === undefined ? "simulateReplaceBodyByName" : "simulateReplaceTargetByName", ...args,
  }, context),
})

export const commit = tool({
  description: "Publish exactly the reviewed proposalId. Does not regenerate or retry stale proposals. Reinspect and resimulate after a stale rejection.",
  args: { proposalId: tool.schema.string() },
  execute: (args, context) => requestAuthoring({ op: "commit", ...args }, context),
})

export const reject = tool({
  description: "Discard an uncommitted proposal without changing the persistent graph.",
  args: { proposalId: tool.schema.string() },
  execute: (args, context) => requestAuthoring({ op: "reject", ...args }, context),
})

export const request = tool({
  description: "Call any maintained AuthoringSession operation using a structured object. Includes exact-ID editing, rename, effects/capabilities, atomic migrations, readiness and emission. A request id is supplied by the bridge; simulations never auto-commit.",
  args: { op: tool.schema.string(), arguments: object },
  execute(args, context) {
    if ("op" in args.arguments || "id" in args.arguments) throw new Error("Pass op separately; request ids are tool-owned.")
    return requestAuthoring({ ...args.arguments, op: args.op }, context)
  },
})

export const close = tool({
  description: "Close the current authoring session. Committed graph state persists; pending proposals are discarded.",
  args: {}, execute: (_args, context) => closeAuthoring(context),
})
