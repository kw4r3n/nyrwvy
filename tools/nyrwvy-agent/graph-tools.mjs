import { openAuthoring, requestAuthoring, closeAuthoring } from "./authoring.ts"
import { recipeKinds, recipeReference } from "./recipe-reference.mjs"

/** @typedef {{ _zod: { output: unknown; input: unknown } }} SchemaBase */

/**
 * Keep the host's native schema family S: Zod 4.1 and 4.3 have incompatible
 * internal version types. Only the fluent operations used below are required;
 * no schema instances are converted or asserted to belong to another version.
 * @template {SchemaBase} S
 * @template T
 * @typedef {S & {
 *   _zod: { output: T; input: T },
 *   describe(description: string): GraphSchema<S, T>,
 *   optional(): GraphSchema<S, T | undefined> & { _zod: { optin: "optional"; optout: "optional" } }
 * }} GraphSchema
 */

/**
 * The bounded builder surface used by these tools. object constructs only a
 * function parameter descriptor; it is not a general-purpose schema adapter.
 * @template {SchemaBase} S
 * @typedef {{
 *   string(): GraphSchema<S, string>,
 *   unknown(): GraphSchema<S, unknown>,
 *   enum<const Values extends readonly string[]>(values: Values): GraphSchema<S, Values[number]>,
 *   record(key: GraphSchema<S, string>, value: GraphSchema<S, unknown>): GraphSchema<S, Record<string, unknown>>,
 *   array<T>(item: GraphSchema<S, T>): GraphSchema<S, T[]>,
 *   object(fields: { displayName: GraphSchema<S, string>; type: GraphSchema<S, Record<string, unknown>> }): GraphSchema<S, { displayName: string; type: Record<string, unknown> }>
 * }} GraphSchemaBuilder
 */

/**
 * @template {Record<string, SchemaBase>} Args
 * @typedef {{
 *   description: string,
 *   args: Args,
 *   execute: (args: import("zod/v4/core").$InferObjectOutput<NoInfer<Args>, {}>, context: import("./authoring.ts").AuthoringContext) => Promise<string>
 * }} GraphTool
 */

/**
 * Preserve each handler's input type and infer its context. Both hosts check
 * the input against their native schema at registration, after S is concrete.
 * @template {Record<string, SchemaBase>} Args
 * @template Input
 * @param {{ description: string; args: Args; execute(args: Input, context: import("./authoring.ts").AuthoringContext): Promise<string> }} definition
 */
function defineTool(definition) { return definition }

/**
 * Shared tool contract. The host supplies its Zod-compatible schema builder.
 * @template {SchemaBase} S
 * @param {GraphSchemaBuilder<S>} schema
 */
export function createGraphTools(schema) {
  const object = schema.record(schema.string(), schema.unknown())
  const typeRef = object.describe('TypeRef: {kind:"primitive",primitive:"bool"|"int64"|"float64"|"bits32"|"string"|"unit"}, {kind:"named",name:"Type"}, {kind:"capability",name:"Capability"}, builtinGeneric with generic option/result/list and arguments, {kind:"function",parameters:[<typeRef>,...],resultType:<typeRef>} for pure capture-free function values, or {kind:"typeParameter",name:"T"} in the parameter types of a new function (makes it generic; the same name means the same parameter, and callers get type arguments by argument matching).')
  const recipe = object.describe("Name-resolved ExpressionRecipe. Use nyrwvy_recipe_reference when the required recipe shape is unfamiliar. Use decimal strings for exact Int64 literals.")
  const reviewDetail = "The review is compact by default: proposalId, receipt fingerprints, readiness, the diff, the function signature with its resultNodeId and nodeCount, effects, aborts, callers, callees and open obligations, without the per-node graph. Diff node summaries show literal values and referenced names, and a targeted diff shows previousTarget and proposedTarget. Pass detail \"full\" only when the proposed graph itself must be read before commit (for migrations it adds rewrittenFunctions); after commit, inspect the function."
  const detail = schema.enum(/** @type {const} */ (["compact", "full"])).describe('Review size: "compact" (default) or "full" (adds the proposed per-node graph, or rewrittenFunctions for migrations).').optional()
  return {
    open: defineTool({
      description: "Create or reopen a persistent graph. Create with dependency (or several dependencies) stores each exact complete content; identical shared declarations are imported once and any other identity collision is rejected. Mode upgrade replaces a dependency with a new version only after request op previewDependencyUpgrade reported it compatible: pass dependency (new version), expectedFingerprint (preview newFingerprint) and, for several dependencies, replaces (old SnapshotId). Mode pin migrates/repairs an existing pin using the same selected version only; optional dependency supplies a relocated original, never an upgrade. Relative paths use workspace or the current project. Open resumes the stored selection.",
      args: { mode: schema.enum(["create", "open", "pin", "upgrade"]), snapshot: schema.string(), workspace: schema.string().optional(), dependency: schema.string().optional(),
        dependencies: schema.array(schema.string()).optional(), expectedFingerprint: schema.string().optional(), replaces: schema.string().optional() },
      /** @param {{ mode: "create" | "open" | "pin" | "upgrade"; snapshot: string; workspace?: string; dependency?: string; dependencies?: string[]; expectedFingerprint?: string; replaces?: string }} args */
      execute: (args, context) => openAuthoring(args.mode, args.snapshot, context, args.workspace, args.dependency, args.dependencies,
        args.mode === "upgrade" ? { expectedFingerprint: args.expectedFingerprint ?? "", replaces: args.replaces } : undefined),
    }),
    inspect: defineTool({
      description: "Inspect status, declarations, or one function's typed graph, literal values, branch scopes, callers, and declared versus derived body effect requirements with semantic evidence. Pass exactly one function name or functionId for the function view.",
      args: { view: schema.enum(["status", "declarations", "function"]), function: schema.string().optional(), functionId: schema.string().optional() },
      /** @param {{ view: "status" | "declarations" | "function"; function?: string; functionId?: string }} args */
      execute(args, context) {
        if (args.view !== "function") {
          if (args.function !== undefined || args.functionId !== undefined) throw new Error("Function selectors require view=function.")
          return requestAuthoring({ op: args.view }, context)
        }
        return requestAuthoring({ op: "readFunction", ...(args.function === undefined ? {} : { function: args.function }),
          ...(args.functionId === undefined ? {} : { functionId: args.functionId }) }, context)
      },
    }),
    recipe_reference: defineTool({
      description: "Read the maintained name-resolved ExpressionRecipe wire shapes used by create_function and replace. No snapshot or authoring session is required. Omit kind for a compact categorized index.",
      args: { kind: schema.enum(recipeKinds).optional() },
      /** @param {{ kind?: import("./recipe-reference.mjs").RecipeKind }} args */
      execute: async args => JSON.stringify(recipeReference(args.kind)),
    }),
    create_type: defineTool({
      description: "Propose a Product or Union declaration. Resolve named type references inside the session; review the result before committing. A field type may name the type itself (a recursive type such as Tree with children: List<Tree>); for types that name each other, use request op simulateCreateTypesByName with types: [{displayName, typeKind}].",
      args: { displayName: schema.string(), typeKind: object.describe('Product: {kind:"product",fields:[{displayName,type}]}. Union: {kind:"union",variants:[{displayName,fields:[]}]}') },
      /** @param {{ displayName: string; typeKind: Record<string, unknown> }} args */
      execute: (args, context) => requestAuthoring({ op: "simulateCreateTypeByName", ...args }, context),
    }),
    create_function: defineTool({
      description: "Propose a typed function directly in the semantic graph. Body is a name-resolved ExpressionRecipe; use nyrwvy_recipe_reference when its shape is unfamiliar. Parameter references use names, not caller-allocated IDs. " + reviewDetail,
      args: { displayName: schema.string(), parameters: schema.array(schema.object({ displayName: schema.string(), type: typeRef })),
        resultType: typeRef, requiredEffects: schema.array(schema.string()), body: recipe, detail },
      /** @param {{ displayName: string; parameters: { displayName: string; type: Record<string, unknown> }[]; resultType: Record<string, unknown>; requiredEffects: string[]; body: Record<string, unknown>; detail?: "compact" | "full" }} args */
      execute: (args, context) => requestAuthoring({ op: "simulateCreateFunctionByName", ...args }, context),
    }),
    replace: defineTool({
      description: "Propose a targeted graph edit, preserving unrelated nodes. replacement is a name-resolved ExpressionRecipe; use nyrwvy_recipe_reference when its shape is unfamiliar. Use a targetNodeId from inspect; omit it only for intentional whole-body replacement. Review before commit. " + reviewDetail,
      args: { function: schema.string(), targetNodeId: schema.string().optional(), replacement: recipe, detail },
      /** @param {{ function: string; targetNodeId?: string; replacement: Record<string, unknown>; detail?: "compact" | "full" }} args */
      execute: (args, context) => requestAuthoring({ op: args.targetNodeId === undefined ? "simulateReplaceBodyByName" : "simulateReplaceTargetByName", ...args }, context),
    }),
    commit: defineTool({
      description: "Publish exactly the reviewed proposalId. Does not regenerate or retry stale proposals. Reinspect and resimulate after a stale rejection.",
      args: { proposalId: schema.string() },
      /** @param {{ proposalId: string }} args */
      execute: (args, context) => requestAuthoring({ op: "commit", ...args }, context),
    }),
    reject: defineTool({
      description: "Discard an uncommitted proposal without changing the persistent graph.",
      args: { proposalId: schema.string() },
      /** @param {{ proposalId: string }} args */
      execute: (args, context) => requestAuthoring({ op: "reject", ...args }, context),
    }),
    request: defineTool({
      description: "Call any maintained AuthoringSession operation using a structured object. Includes exact-ID editing, rename, effects/capabilities (simulateCreateCapabilityByName takes name-resolved typeRefs and effect names), deleteCapabilityOperation for unused operations, simulateOperationsByName for name-resolved deletions (operations: [{kind:'deleteFunction', function} | {kind:'deleteType', type} | {kind:'deleteEffect', effect} | {kind:'deleteCapability', capability} | {kind:'deleteCapabilityOperation', capability, operation}]; a function's own self-calls never block its deletion), atomic migrations (parameter type, result type, Product field type, Union variant like an existing one), readiness, emission, and explicit runPure verification of committed functions (capability parameters bound to scripted fakeCapability responses (or cases: [{arguments: [{capabilityParameterId, value}], responses}] for independent queues matching all argument values exactly; unmatched calls report RunBindingNoMatch) or hostCapability implementations from explicit hostSources, which run arbitrary C# and fail with RunHostSourcesDisabled unless the operator started the server with host sources enabled (open reports hostSourcesAllowed); ParallelAll host bindings require supportsConcurrentCalls: true) with Unit/scalars (Float64 as {kind:'float64',value: JSON number or 'NaN'|'Infinity'|'-Infinity'|'-0'}, nested containers only, matched bitwise; Bits32 as {kind:'bits32',value: unsigned 0..4294967295 as a JSON integer or decimal string, printed as a decimal string}, nested containers only) and bounded nesting (including recursive named types) through List/Option, Product/Union fields and Result payloads; mismatches and runtime failures report semantic paths and function frames. runPureByName takes function by name and name-selected values: product/union {type, variant, fields:[{field, value}]}, fakeCapability/hostCapability capability, operations operation, case arguments parameter, and name-resolved itemType/okType/errorType. Run output of both forms keeps every ID and adds display names beside them (function, type, variant, field; calls: parameter, capability, operation, argument parameter). Every simulate* reply is compact by default (no per-node graph; pass detail: \"full\" to include it). A request id is supplied by the bridge; simulations never auto-commit.",
      args: { op: schema.string(), arguments: object },
      /** @param {{ op: string; arguments: Record<string, unknown> }} args */
      execute(args, context) {
        if ("op" in args.arguments || "id" in args.arguments) throw new Error("Pass op separately; request ids are tool-owned.")
        return requestAuthoring({ ...args.arguments, op: args.op }, context)
      },
    }),
    close: defineTool({
      description: "Close the current authoring session. Committed graph state persists; pending proposals are discarded.",
      args: {},
      /** @param {Record<string, never>} _args */
      execute: (_args, context) => closeAuthoring(context),
    }),
  }
}
