namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Collections.Generic
open Nyrwvy.Core

/// A bounded, readable path from the result node through operands and nested bodies.
module internal WireOutline =
    open WireJson

    let focus (declarations: AuthoringDeclarations) (value: FunctionFocusView) =
        let fn = value.Function
        let names = Dictionary<string, string>()
        let add id name = names[StableId.value id] <- name
        for parameter in fn.Parameters do add parameter.Id parameter.DisplayName
        for functionEntity in declarations.Functions do
            add functionEntity.Id functionEntity.DisplayName
            for parameter in functionEntity.Parameters do add parameter.Id parameter.DisplayName
        for semanticType in declarations.Types do
            add semanticType.Id semanticType.DisplayName
            let fields, variants =
                match semanticType.Kind with
                | SemanticTypeKind.Product fields -> fields, []
                | SemanticTypeKind.Union variants -> [], variants
            for field in fields do add field.Id field.DisplayName
            for variant in variants do
                add variant.Id variant.DisplayName
                for field in variant.Fields do add field.Id field.DisplayName
        for capability in declarations.Capabilities do
            add capability.Id capability.DisplayName
            for operation in capability.Operations do add operation.Id operation.DisplayName
        let named id =
            let key = StableId.value id
            match names.TryGetValue key with
            | true, name -> name
            | _ -> key
        let literal (text: string) =
            let shown = if text.Length > 64 then text.Substring(0, 64) + "…" else text
            serialize (runString shown)
        let operands (node: SemanticNode) scopes =
            match node.Kind with
            | SemanticNodeKind.ParameterReference id -> [ named id ]
            | SemanticNodeKind.PatternReference id ->
                let binding = scopes |> List.tryPick (fun (depth, bindings: PatternBinding list) ->
                    bindings |> List.tryFind (fun binding -> binding.Id = id) |> Option.map (fun binding -> depth, binding))
                match binding with
                | Some(depth, binding) -> [ "scopeDepth=" + string depth; binding.DisplayName |> Option.defaultValue "value" ]
                | None -> [ StableId.value id ]
            | SemanticNodeKind.StringLiteral text -> [ literal text ]
            | SemanticNodeKind.Int64Literal number -> [ string number ]
            | SemanticNodeKind.BoolLiteral value -> [ string value |> fun text -> text.ToLowerInvariant() ]
            | SemanticNodeKind.Float64Literal bits -> [ Float64Bits.toHex bits ]
            | SemanticNodeKind.FieldAccess(_, id) -> [ named id ]
            | SemanticNodeKind.UnionConstruct(id, _, _) -> [ named id ]
            | SemanticNodeKind.ProductConstruct(id, _, _) | SemanticNodeKind.ProductUpdate(id, _, _) -> [ named id ]
            | SemanticNodeKind.CapabilityInvoke(_, id, _) -> [ named id ]
            | SemanticNodeKind.Bind(_, arm) -> [ arm.ValueBinding.DisplayName |> Option.defaultValue "value" ]
            | SemanticNodeKind.MatchOption(_, arm, _) ->
                arm.ValueBinding.DisplayName |> Option.toList
            | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                [ okArm.ValueBinding.DisplayName; errArm.ErrorBinding.DisplayName ] |> List.choose id
            | SemanticNodeKind.MatchUnion(_, arms) ->
                arms |> List.collect UnionMatchArm.canonicalVariantIds |> List.map named
            | _ -> SemanticNodeShape.functionReferences node |> List.map (fun reference -> named reference.FunctionId)

        let allNodes = ValueGraph.collectNodes fn.Body |> List.map (fun node -> node.Id, node) |> Map.ofList
        let seen = HashSet<GraphNodeId>()
        let lines = ResizeArray<string>()
        let mutable truncated = false
        let result = WireEncodeCommon.compactFocus value :?> IDictionary<string, obj>
        let maxBytes = 16384
        let update () =
            result["outline"] <- lines |> Seq.map box |> Seq.toList |> arrayValue
            result["outlineTruncated"] <- box truncated
            result["outlineMaxBytes"] <- box maxBytes
        update ()
        let addLine line =
            if not truncated then
                lines.Add line
                update ()
                if byteCount result > maxBytes then
                    lines.RemoveAt(lines.Count - 1)
                    truncated <- true
                    update ()
        let rec visit depth scopes id =
            if not truncated then
                if seen.Add id then
                    let node = Map.find id allNodes
                    let kind =
                        match node.Kind with
                        | SemanticNodeKind.ParameterReference _ -> "parameter"
                        | _ ->
                            let name = SemanticNodeKind.name node.Kind
                            name.Substring(0, 1).ToLowerInvariant() + name.Substring(1)
                    let detail = operands node scopes
                    addLine (String.replicate (min depth 64 * 2) " " + kind + " " + StableId.value id +
                             (if List.isEmpty detail then "" else " " + String.concat " " detail))
                    for child in SemanticNodeShape.directValueReferences node do visit (depth + 1) scopes child
                    for scope in SemanticNodeShape.nestedScopes node do
                        let bindings = scope.Bindings |> List.map NestedBinding.pattern
                        let nested = if List.isEmpty bindings then scopes else (0, bindings) :: (scopes |> List.map (fun (depth, bindings) -> depth + 1, bindings))
                        visit (depth + 1) nested scope.Graph.ResultNodeId
                else addLine (String.replicate (min depth 64 * 2) " " + "ref " + StableId.value id)
        visit 0 [] fn.Body.ResultNodeId
        update ()
        result :> obj
