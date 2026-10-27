namespace Nyrwvy.Tools.AuthoringSession

open Nyrwvy.Core
open System

module internal WireEncodeCommon =
    open WireJson

    let private optional value encode =
        match value with
        | Some item -> encode item
        | None -> null

    /// A type in wire `TypeRef` notation. A type parameter occurrence carries its ID and, when
    /// `names` (the declaring function's or type's list) knows it, its `displayName`.
    let rec typeRefIn (names: Map<TypeParameterId, string>) (value: TypeRef): obj =
        let typeRef = typeRefIn names
        match value with
        | TypeRef.Primitive primitive ->
            let name =
                match primitive with
                | PrimitiveType.String -> "string"
                | PrimitiveType.Bool -> "bool"
                | PrimitiveType.Unit -> "unit"
                | PrimitiveType.Int64 -> "int64"
                | PrimitiveType.Float64 -> "float64"
                | PrimitiveType.Bits32 -> "bits32"
                | PrimitiveType.Bytes -> "bytes"
            objectValue [ "kind", box "primitive"; "primitive", box name ]
        | TypeRef.Function(arguments, result) ->
            objectValue [ "kind", box "function"; "parameters", arguments |> List.map typeRef |> arrayValue; "resultType", typeRef result ]
        | TypeRef.Named(typeId, []) ->
            objectValue [ "kind", box "named"; "typeId", box (StableId.value typeId) ]
        | TypeRef.Named(typeId, arguments) ->
            objectValue [ "kind", box "named"; "typeId", box (StableId.value typeId); "arguments", arguments |> List.map typeRef |> arrayValue ]
        | TypeRef.Capability capabilityId ->
            objectValue [ "kind", box "capability"; "capabilityId", box (StableId.value capabilityId) ]
        | TypeRef.TypeParameter typeParameterId ->
            objectValue
                ([ "kind", box "typeParameter"; "typeParameterId", box (StableId.value typeParameterId) ]
                 @ (match Map.tryFind typeParameterId names with
                    | Some displayName -> [ "displayName", box displayName ]
                    | None -> []))
        | TypeRef.BuiltinGeneric(generic, arguments) ->
            let name =
                match generic with
                | BuiltinGenericType.Option -> "option"
                | BuiltinGenericType.Result -> "result"
                | BuiltinGenericType.Map -> "map"
                | BuiltinGenericType.List -> "list"
                | BuiltinGenericType.Set -> "set"
                | BuiltinGenericType.OneShot -> "oneShot"
                | BuiltinGenericType.Task -> "task"
                | BuiltinGenericType.Exception -> "exception"
                | BuiltinGenericType.TaskScope -> "taskScope"
            objectValue
                [ "kind", box "builtinGeneric"
                  "generic", box name
                  "arguments", arguments |> List.map typeRef |> arrayValue ]

    /// A type outside any declaration (run values, migration diffs): type parameters carry their ID only.
    let typeRef (value: TypeRef): obj = typeRefIn Map.empty value

    /// The display names of a function's own type parameters.
    let functionTypeParameterNames (value: SemanticFunction) = Map.ofList value.TypeParameters

    /// Declared type parameters, in order, only for a generic declaration, so other output is unchanged.
    let private typeParameters (value: (TypeParameterId * string) list) =
        if List.isEmpty value then []
        else
            [ "typeParameters",
              value
              |> List.map (fun (id, name) -> objectValue [ "typeParameterId", box (StableId.value id); "displayName", box name ])
              |> arrayValue ]

    let private parameter names (value: SemanticParameter) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRefIn names value.Type ]

    /// A function's signature fields: `id`, `displayName`, `typeParameters` (generic functions only),
    /// `parameters`, `resultType`, `requiredEffects`.
    let private signatureFields (value: SemanticFunction) =
        let names = functionTypeParameterNames value
        [ "id", box (StableId.value value.Id)
          "displayName", box value.DisplayName
          "exported", box value.Exported ]
        @ (if value.MustNotWait then [ "mustNotWait", box true ] else [])
        @ typeParameters value.TypeParameters
        @ [ "parameters", value.Parameters |> List.map (parameter names) |> arrayValue
            "resultType", typeRefIn names value.ResultType
            "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue ]

    let private functionDeclaration (value: SemanticFunction) = objectValue (signatureFields value)

    let functionEntity (value: SemanticFunction) =
        let nodes =
            ValueGraph.collectNodes value.Body
            |> List.map (fun node ->
                objectValue
                    [ "id", box (StableId.value node.Id)
                      "kind", runString (sprintf "%A" node.Kind) ])
        objectValue
            (signatureFields value
             @ [ "body",
                 objectValue
                     [ "nodes", arrayValue nodes
                       "resultNodeId", box (StableId.value value.Body.ResultNodeId) ] ])

    /// Compact review form of a function: the full signature with display names and the
    /// body's result node and node count, without the per-node graph.
    let functionSummary (value: SemanticFunction) =
        objectValue
            (signatureFields value
             @ [ "body",
                 objectValue
                     [ "resultNodeId", box (StableId.value value.Body.ResultNodeId)
                       "nodeCount", box (ValueGraph.collectNodes value.Body |> List.length) ] ])

    /// One Product or variant field, as `declarations` and the reviews show it; `names` are the
    /// owning type's type parameters.
    let fieldIn (names: Map<TypeParameterId, string>) (value: SemanticField) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRefIn names value.Type ]

    /// A field shown without its owning type; a type parameter occurrence carries its ID only.
    let field (value: SemanticField) = fieldIn Map.empty value

    let semanticType (value: SemanticType) =
        let value = Snapshot.canonicalTypeMembers value
        let field = fieldIn (Map.ofList value.TypeParameters)
        let kind =
            match value.Kind with
            | SemanticTypeKind.Product fields ->
                objectValue [ "kind", box "product"; "fields", fields |> List.map field |> arrayValue ]
            | SemanticTypeKind.Union variants ->
                objectValue
                    [ "kind", box "union"
                      "variants",
                        variants
                        |> List.map (fun variant ->
                            objectValue
                                [ "id", box (StableId.value variant.Id)
                                  "displayName", box variant.DisplayName
                                  "fields", variant.Fields |> List.map field |> arrayValue ])
                        |> arrayValue ]
        objectValue
            ([ "id", box (StableId.value value.Id)
               "displayName", box value.DisplayName
               "exported", box value.Exported ]
             @ typeParameters value.TypeParameters
             @ [ "typeKind", kind ])

    /// One Capability operation with its parameters, as `declarations` and the reviews show it.
    let capabilityOperation (operation: SemanticCapabilityOperation) =
        let parameters =
            operation.Parameters
            |> List.map (fun parameter ->
                objectValue
                    [ "id", box (StableId.value parameter.Id)
                      "displayName", box parameter.DisplayName
                      "type", typeRef parameter.Type ])
        objectValue
            ([ "id", box (StableId.value operation.Id)
               "displayName", box operation.DisplayName
               "parameters", arrayValue parameters
               "resultType", typeRef operation.ResultType
               "failureType", optional operation.FailureType typeRef
               "effectId", box (StableId.value operation.EffectId) ]
             @ (if operation.MayWait then [ "mayWait", box true ] else []))

    let capabilityEntity (value: SemanticCapability) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "exported", box value.Exported
              if value.TaskBound then "taskBound", box true
              "operations", (Snapshot.canonicalCapabilityMembers value).Operations |> List.map capabilityOperation |> arrayValue ]

    let declarations (value: AuthoringDeclarations) =
        let effects =
            value.Effects
            |> List.map (fun effect ->
                objectValue
                    [ "id", box (StableId.value effect.Id)
                      "displayName", box effect.DisplayName
                      "exported", box effect.Exported ])
        let capabilities = value.Capabilities |> List.map capabilityEntity
        objectValue
            [ "provenance", value.Provenance |> List.map (fun p ->
                  objectValue [ "kind", box p.Kind; "id", box p.Id; "ownership", box (if p.IsLocal then "local" else "imported")
                                "sources", p.Sources |> List.map (fun source -> objectValue [ "snapshotId", box source.SnapshotId; "fingerprint", box source.Fingerprint ]) |> arrayValue ]) |> arrayValue
              "functions", value.Functions |> List.map functionDeclaration |> arrayValue
              "types", value.Types |> List.map semanticType |> arrayValue
              "effects", arrayValue effects
              "capabilities", arrayValue capabilities ]

    let readiness = function
        | TransactionReadiness.SemanticallyReady -> objectValue [ "kind", box "semanticallyReady" ]
        | TransactionReadiness.Incomplete obligationIds ->
            objectValue
                [ "kind", box "incomplete"
                  "obligationIds", obligationIds |> List.map (StableId.value >> box) |> arrayValue ]

    let receipt (value: TransactionPreviewReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
              "proposedSnapshotId", box (StableId.value value.ProposedSnapshotId)
              "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
              "proposedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint) ]

    /// String literals longer than this many UTF-16 code units are shown as a prefix in node
    /// summaries, with `truncated` and the full `length`.
    let private summaryStringLimit = 256

    let private nodeLiteral = function
        | StructuralNodeLiteral.Bool value -> objectValue [ "kind", box "bool"; "value", box value ]
        | StructuralNodeLiteral.Int64 value -> objectValue [ "kind", box "int64"; "value", box (value.ToString(Globalization.CultureInfo.InvariantCulture)) ]
        | StructuralNodeLiteral.String value when value.Length <= summaryStringLimit ->
            objectValue [ "kind", box "string"; "value", runString value ]
        | StructuralNodeLiteral.String value ->
            // Never split a surrogate pair at the cut.
            let cut = if Char.IsHighSurrogate value[summaryStringLimit - 1] && Char.IsLowSurrogate value[summaryStringLimit] then summaryStringLimit - 1 else summaryStringLimit
            objectValue [ "kind", box "string"; "value", runString (value.Substring(0, cut)); "truncated", box true; "length", box value.Length ]

    /// Value-dependency roles are fieldless or carry one index/ID; the case text is their wire name.
    let dependencyRole (role: ValueDependencyRole) = sprintf "%A" role

    let nodeSummary (value: StructuralNodeSummary) =
        objectValue
            [ "nodeId", box (StableId.value value.NodeId)
              "role", box value.Role
              "type", optional value.Type typeRef
              "dependencies", value.Dependencies |> List.map (StableId.value >> box) |> arrayValue
              "valueDependencies",
                value.ValueDependencies
                |> List.map (fun dependency ->
                    objectValue
                        [ "role", box (dependencyRole dependency.Role)
                          "nodeId", box (StableId.value dependency.NodeId) ])
                |> arrayValue
              "parameterId", optional value.ParameterId (StableId.value >> box)
              "functionId", optional value.FunctionId (StableId.value >> box)
              "float64Bits", optional value.Float64BitsHex box
              "literal", optional value.Literal nodeLiteral
              "referenceDisplayName", optional value.ReferenceDisplayName box ]

    type internal BindingFrame =
        { OwnerNodeId: GraphNodeId
          Role: NestedGraphRole
          Bindings: NestedBinding list }

    /// Every node of a body with the binding frames enclosing it, innermost first: the order a
    /// recipe's scopeDepth counts. Branches and Bool operands open no frame; OptionNone opens an
    /// empty one, as TargetedReplacementGraph does.
    let bindingFrames (body: ValueGraph) =
        let rec walk frames (graph: ValueGraph) =
            graph.Nodes
            |> List.collect (fun node ->
                (node, frames)
                :: (SemanticNodeShape.nestedScopes node
                    |> List.collect (fun scope ->
                        match scope.Role with
                        | NestedGraphRole.WhenTrue
                        | NestedGraphRole.WhenFalse
                        | NestedGraphRole.BoolAndRight
                        | NestedGraphRole.BoolOrRight -> walk frames scope.Graph
                        | role -> walk ({ OwnerNodeId = node.Id; Role = role; Bindings = scope.Bindings } :: frames) scope.Graph)))
        walk [] body

    /// A frame without its bindings; names come from `variantNames`/`fieldNames` when they are known.
    let private frameFields (variantNames: Map<VariantId, string>) depth (frame: BindingFrame) =
        let role, variants =
            match frame.Role with
            | NestedGraphRole.UnionArm ids -> "unionArm", ids
            | NestedGraphRole.BindBody -> "bind", []
            | NestedGraphRole.OptionSome -> "optionSome", []
            | NestedGraphRole.OptionNone -> "optionNone", []
            | NestedGraphRole.ResultOk -> "resultOk", []
            | NestedGraphRole.ResultErr -> "resultErr", []
            | other -> sprintf "%A" other, []
        [ "scopeDepth", box depth
          "ownerNodeId", box (StableId.value frame.OwnerNodeId)
          "role", box role ]
        @ (if variants.IsEmpty then []
           else
               [ "variantIds", variants |> List.map (StableId.value >> box) |> arrayValue ]
               @ (match variants |> List.map (fun item -> Map.tryFind item variantNames) with
                  | names when List.forall Option.isSome names -> [ "variants", names |> List.map (Option.get >> box) |> arrayValue ]
                  | _ -> []))

    let private bindingSlot (fieldNames: Map<FieldId, string>) names (binding: NestedBinding) =
        let pattern = NestedBinding.pattern binding
        let slot =
            match binding with
            | NestedBinding.Field(fieldId, _) ->
                [ "slot", box "field"; "fieldId", box (StableId.value fieldId) ]
                @ (Map.tryFind fieldId fieldNames |> Option.map (fun name -> [ "field", box name ]) |> Option.defaultValue [])
            | NestedBinding.Value _ -> [ "slot", box "value" ]
            | NestedBinding.Error _ -> [ "slot", box "error" ]
        objectValue
            (slot
             @ [ "bindingId", box (StableId.value pattern.Id); "type", typeRefIn names pattern.Type ]
             @ (pattern.DisplayName |> Option.map (fun name -> [ "displayName", box name ]) |> Option.defaultValue []))

    /// The frames visible at one node, innermost first, as a recipe's patternReference counts them.
    let bindingScopes variantNames fieldNames (fn: SemanticFunction) (frames: BindingFrame list) =
        let names = functionTypeParameterNames fn
        frames
        |> List.mapi (fun depth frame ->
            objectValue (frameFields variantNames depth frame @ [ "bindings", frame.Bindings |> List.map (bindingSlot fieldNames names) |> arrayValue ]))
        |> arrayValue

    /// The binding each pattern reference among `nodeIds` reads, with its scopeDepth from that node,
    /// so a review shows which match arm a replacement's reference resolved to.
    let patternReferences (fn: SemanticFunction) (nodeIds: Set<GraphNodeId>) =
        let names = functionTypeParameterNames fn
        bindingFrames fn.Body
        |> List.choose (fun (node, frames) ->
            match node.Kind with
            | SemanticNodeKind.PatternReference bindingId when nodeIds.Contains node.Id ->
                frames
                |> List.indexed
                |> List.tryPick (fun (depth, frame) ->
                    frame.Bindings
                    |> List.tryFind (fun binding -> (NestedBinding.pattern binding).Id = bindingId)
                    |> Option.map (fun binding ->
                        objectValue
                            ([ "nodeId", box (StableId.value node.Id) ]
                             @ frameFields Map.empty depth frame
                             @ [ "binding", bindingSlot Map.empty names binding ])))
            | _ -> None)
        |> arrayValue

    /// String, Int64 and Bool literals of a body, for checking conditions without the full graph.
    let literals (body: ValueGraph) =
        ValueGraph.collectNodes body
        |> List.choose (fun node ->
            match node.Kind with
            | SemanticNodeKind.StringLiteral value -> Some(StructuralNodeLiteral.String value)
            | SemanticNodeKind.Int64Literal value -> Some(StructuralNodeLiteral.Int64 value)
            | SemanticNodeKind.BoolLiteral value -> Some(StructuralNodeLiteral.Bool value)
            | _ -> None
            |> Option.map (fun literal -> objectValue [ "nodeId", box (StableId.value node.Id); "literal", nodeLiteral literal ]))
        |> arrayValue

    let callSite (value: QueryCallSite) =
        objectValue
            [ "callerFunctionId", box (StableId.value value.CallerFunctionId)
              "callNodeId", box (StableId.value value.CallNodeId)
              "calleeFunctionId", box (StableId.value value.CalleeFunctionId) ]

    let bodyEffectRequirements (value: BodyEffectRequirements) =
        objectValue
            [ "effectIds", value.EffectIds |> List.map (StableId.value >> box) |> arrayValue
              "evidence", value.Evidence |> List.map (fun item ->
                  let source =
                      match item.Source with
                      | EffectRequirementSource.FunctionContract id -> objectValue [ "kind", box "functionContract"; "functionId", box (StableId.value id) ]
                      | EffectRequirementSource.CapabilityOperation id -> objectValue [ "kind", box "capabilityOperation"; "operationId", box (StableId.value id) ]
                  objectValue [ "effectId", box (StableId.value item.EffectId); "nodeId", box (StableId.value item.NodeId); "source", source ]) |> arrayValue
              "unresolvedFunctionIds", value.UnresolvedFunctionIds |> List.map (StableId.value >> box) |> arrayValue
              "unresolvedOperationIds", value.UnresolvedOperationIds |> List.map (StableId.value >> box) |> arrayValue ]

    let aborts (values: QueryFunctionAbort list) =
        values |> List.map (fun item ->
            objectValue
                [ "ownerFunctionId", box (StableId.value item.OwnerFunctionId)
                  "nodeId", box (StableId.value item.NodeId)
                  "errorNodeId", box (StableId.value item.ErrorNodeId)
                  "errorType", optional item.ErrorType typeRef
                  "enclosingControlNodeIds", item.EnclosingControlNodeIds |> List.map (StableId.value >> box) |> arrayValue
                  "dependentContinuationNodeIds", item.DependentContinuationNodeIds |> List.map (StableId.value >> box) |> arrayValue ]) |> arrayValue

    /// `"mayWait": true` and the `waitPath` when the function may wait; nothing otherwise.
    let waitFields (value: FunctionFocusView) =
        match value.WaitPath with
        | Some path -> [ "mayWait", box true; "waitPath", box path ]
        | None -> []

    let private focusFields encodeFunction includeNodeTypes (value: FunctionFocusView) =
        let typeRef = typeRefIn (functionTypeParameterNames value.Function)
        [ yield "snapshotId", box (StableId.value value.SnapshotId)
          yield "function", encodeFunction value.Function
          yield "bodyEffectRequirements", bodyEffectRequirements value.BodyEffectRequirements
          yield! waitFields value
          yield "aborts", aborts value.Aborts
          if includeNodeTypes then
              yield "nodeTypes",
                    value.NodeTypes
                    |> List.map (fun item ->
                        objectValue
                            [ "nodeId", box (StableId.value item.NodeId)
                              "type", optional item.Type typeRef ])
                    |> arrayValue
          yield "callers", value.Callers |> List.map callSite |> arrayValue
          yield "callees", value.Callees |> List.map callSite |> arrayValue
          yield "openObligations",
                value.OpenObligations
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", box (StableId.value item.ObligationId)
                          "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.ParameterId) ])
                |> arrayValue ]

    let focusWith encodeFunction (value: FunctionFocusView) =
        objectValue (focusFields encodeFunction true value)

    /// Compact review focus: every impact fact (effects, aborts, callers, callees, open
    /// obligations) with the function summarized; per-node graph and node types are omitted.
    let compactFocus (value: FunctionFocusView) =
        objectValue (focusFields functionSummary false value)

    let focus value = focusWith functionEntity value
