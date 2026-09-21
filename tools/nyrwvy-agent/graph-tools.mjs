import { openAuthoring, requestAuthoring, closeAuthoring } from "./authoring.ts"
import { recipeKinds, recipeReference } from "./recipe-reference.mjs"

/** Shared tool contract. The host supplies its Zod-compatible schema builder. */
export function createGraphTools(schema) {
  const object = schema.record(schema.string(), schema.unknown())
  const typeRef = object.describe('TypeRef: {kind:"primitive",primitive:"bool"|"int64"|"float64"|"string"|"unit"}, {kind:"named",name:"Type"}, {kind:"capability",name:"Capability"}, or builtinGeneric with generic option/result/list and arguments.')
  const recipe = object.describe("Name-resolved ExpressionRecipe. Use nyrwvy_recipe_reference when the required recipe shape is unfamiliar. Use decimal strings for exact Int64 literals.")
  return {
    open: {
      description: "Create or reopen one persistent Nyrwvy graph. Relative snapshots default to the client/MCP process current project. Pass workspace only when the host does not launch the server in that project.",
      args: { mode: schema.enum(["create", "open"]), snapshot: schema.string(), workspace: schema.string().optional() },
      execute: (args, context) => openAuthoring(args.mode, args.snapshot, context, args.workspace),
    },
    inspect: {
      description: "Inspect status, declarations, or one function's typed graph, literal values, branch scopes and callers. Pass exactly one function name or functionId for the function view.",
      args: { view: schema.enum(["status", "declarations", "function"]), function: schema.string().optional(), functionId: schema.string().optional() },
      execute(args, context) {
        if (args.view !== "function") {
          if (args.function !== undefined || args.functionId !== undefined) throw new Error("Function selectors require view=function.")
          return requestAuthoring({ op: args.view }, context)
        }
        return requestAuthoring({ op: "readFunction", ...(args.function === undefined ? {} : { function: args.function }),
          ...(args.functionId === undefined ? {} : { functionId: args.functionId }) }, context)
      },
    },
    recipe_reference: {
      description: "Read the maintained name-resolved ExpressionRecipe wire shapes used by create_function and replace. No snapshot or authoring session is required. Omit kind for a compact categorized index.",
      args: { kind: schema.enum(recipeKinds).optional() },
      execute: args => JSON.stringify(recipeReference(args.kind)),
    },
    create_type: {
      description: "Propose a Product or Union declaration. Resolve named type references inside the session; review the result before committing.",
      args: { displayName: schema.string(), typeKind: object.describe('Product: {kind:"product",fields:[{displayName,type}]}. Union: {kind:"union",variants:[{displayName,fields:[]}]}') },
      execute: (args, context) => requestAuthoring({ op: "simulateCreateTypeByName", ...args }, context),
    },
    create_function: {
      description: "Propose a typed function directly in the semantic graph. Body is a name-resolved ExpressionRecipe; use nyrwvy_recipe_reference when its shape is unfamiliar. Parameter references use names, not caller-allocated IDs.",
      args: { displayName: schema.string(), parameters: schema.array(schema.object({ displayName: schema.string(), type: typeRef })),
        resultType: typeRef, requiredEffects: schema.array(schema.string()), body: recipe },
      execute: (args, context) => requestAuthoring({ op: "simulateCreateFunctionByName", ...args }, context),
    },
    replace: {
      description: "Propose a targeted graph edit, preserving unrelated nodes. replacement is a name-resolved ExpressionRecipe; use nyrwvy_recipe_reference when its shape is unfamiliar. Use a targetNodeId from inspect; omit it only for intentional whole-body replacement. Review before commit.",
      args: { function: schema.string(), targetNodeId: schema.string().optional(), replacement: recipe },
      execute: (args, context) => requestAuthoring({ op: args.targetNodeId === undefined ? "simulateReplaceBodyByName" : "simulateReplaceTargetByName", ...args }, context),
    },
    commit: {
      description: "Publish exactly the reviewed proposalId. Does not regenerate or retry stale proposals. Reinspect and resimulate after a stale rejection.",
      args: { proposalId: schema.string() },
      execute: (args, context) => requestAuthoring({ op: "commit", ...args }, context),
    },
    reject: {
      description: "Discard an uncommitted proposal without changing the persistent graph.",
      args: { proposalId: schema.string() },
      execute: (args, context) => requestAuthoring({ op: "reject", ...args }, context),
    },
    request: {
      description: "Call any maintained AuthoringSession operation using a structured object. Includes exact-ID editing, rename, effects/capabilities, atomic migrations, readiness and emission. A request id is supplied by the bridge; simulations never auto-commit.",
      args: { op: schema.string(), arguments: object },
      execute(args, context) {
        if ("op" in args.arguments || "id" in args.arguments) throw new Error("Pass op separately; request ids are tool-owned.")
        return requestAuthoring({ ...args.arguments, op: args.op }, context)
      },
    },
    close: {
      description: "Close the current authoring session. Committed graph state persists; pending proposals are discarded.",
      args: {}, execute: (_args, context) => closeAuthoring(context),
    },
  }
}
