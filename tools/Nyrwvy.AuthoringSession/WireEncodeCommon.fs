namespace Nyrwvy.Tools.AuthoringSession

open Nyrwvy.Core

module internal WireEncodeCommon =
    open WireJson

    let private optional value encode =
        match value with
        | Some item -> encode item
        | None -> null

    let rec typeRef = function
        | TypeRef.Primitive primitive ->
            let name =
                match primitive with
                | PrimitiveType.String -> "string"
                | PrimitiveType.Bool -> "bool"
                | PrimitiveType.Unit -> "unit"
                | PrimitiveType.Int64 -> "int64"
                | PrimitiveType.Float64 -> "float64"
            objectValue [ "kind", box "primitive"; "primitive", box name ]
        | TypeRef.Function(arguments, result) ->
            objectValue [ "kind", box "function"; "parameters", arguments |> List.map typeRef |> arrayValue; "resultType", typeRef result ]
        | TypeRef.Named typeId ->
            objectValue [ "kind", box "named"; "typeId", box (StableId.value typeId) ]
        | TypeRef.Capability capabilityId ->
            objectValue [ "kind", box "capability"; "capabilityId", box (StableId.value capabilityId) ]
        | TypeRef.TypeParameter(typeParameterId, displayName) ->
            objectValue [ "kind", box "typeParameter"; "typeParameterId", box (StableId.value typeParameterId); "displayName", box displayName ]
        | TypeRef.BuiltinGeneric(generic, arguments) ->
            let name =
                match generic with
                | BuiltinGenericType.Option -> "option"
                | BuiltinGenericType.Result -> "result"
                | BuiltinGenericType.List -> "list"
                | BuiltinGenericType.Task -> "task"
                | BuiltinGenericType.TaskScope -> "taskScope"
            objectValue
                [ "kind", box "builtinGeneric"
                  "generic", box name
                  "arguments", arguments |> List.map typeRef |> arrayValue ]

    let private parameter (value: SemanticParameter) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRef value.Type ]

    let private functionDeclaration (value: SemanticFunction) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue ]

    let functionEntity (value: SemanticFunction) =
        let nodes =
            ValueGraph.collectNodes value.Body
            |> List.map (fun node ->
                objectValue
                    [ "id", box (StableId.value node.Id)
                      "kind", box (sprintf "%A" node.Kind) ])
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue
              "body",
                objectValue
                    [ "nodes", arrayValue nodes
                      "resultNodeId", box (StableId.value value.Body.ResultNodeId) ] ]

    /// Compact review form of a function: the full signature with display names and the
    /// body's result node and node count, without the per-node graph.
    let functionSummary (value: SemanticFunction) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue
              "body",
                objectValue
                    [ "resultNodeId", box (StableId.value value.Body.ResultNodeId)
                      "nodeCount", box (ValueGraph.collectNodes value.Body |> List.length) ] ]

    let private field (value: SemanticField) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRef value.Type ]

    let semanticType (value: SemanticType) =
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
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "typeKind", kind ]

    let declarations (value: AuthoringDeclarations) =
        let effects =
            value.Effects
            |> List.map (fun effect ->
                objectValue
                    [ "id", box (StableId.value effect.Id)
                      "displayName", box effect.DisplayName ])
        let capabilities =
            value.Capabilities
            |> List.map (fun capability ->
                let operations =
                    capability.Operations
                    |> List.map (fun operation ->
                        let parameters =
                            operation.Parameters
                            |> List.map (fun parameter ->
                                objectValue
                                    [ "id", box (StableId.value parameter.Id)
                                      "displayName", box parameter.DisplayName
                                      "type", typeRef parameter.Type ])
                        objectValue
                            [ "id", box (StableId.value operation.Id)
                              "displayName", box operation.DisplayName
                              "parameters", arrayValue parameters
                              "resultType", typeRef operation.ResultType
                              "failureType", optional operation.FailureType typeRef
                              "effectId", box (StableId.value operation.EffectId) ])
                objectValue
                    [ "id", box (StableId.value capability.Id)
                      "displayName", box capability.DisplayName
                      "operations", arrayValue operations ])
        objectValue
            [ "functions", value.Functions |> List.map functionDeclaration |> arrayValue
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
                        [ "role", box (sprintf "%A" dependency.Role)
                          "nodeId", box (StableId.value dependency.NodeId) ])
                |> arrayValue
              "parameterId", optional value.ParameterId (StableId.value >> box)
              "functionId", optional value.FunctionId (StableId.value >> box)
              "float64Bits", optional value.Float64BitsHex box ]

    let private callSite (value: QueryCallSite) =
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

    let private focusFields encodeFunction includeNodeTypes (value: FunctionFocusView) =
        [ yield "snapshotId", box (StableId.value value.SnapshotId)
          yield "function", encodeFunction value.Function
          yield "bodyEffectRequirements", bodyEffectRequirements value.BodyEffectRequirements
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
