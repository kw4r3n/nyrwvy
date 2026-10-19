namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure values of recursive named types, bounded by the value layer limit instead of the type.
module PureRunRecursiveTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("run-recursive-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listType item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionType item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private nodeId = id<TypeTag> "node"
    let private treeId = id<TypeTag> "tree"
    let private valueId = id<FieldTag> "value"
    let private nextId = id<FieldTag> "next"
    let private leafValueId = id<FieldTag> "leaf-value"
    let private childrenId = id<FieldTag> "children"
    let private leafId = id<VariantTag> "leaf"
    let private branchId = id<VariantTag> "branch"
    let private nodeType = TypeRef.Named(nodeId, [])
    let private treeType = TypeRef.Named(treeId, [])
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected error: %A" error

    let private withSession run =
        let types: SemanticType list =
            [ { Exported = true; TypeParameters = []; Id = nodeId; DisplayName = "Node"
                Kind = SemanticTypeKind.Product
                    [ { Id = valueId; DisplayName = "value"; Type = intType }
                      { Id = nextId; DisplayName = "next"; Type = optionType nodeType } ] }
              { Exported = true; TypeParameters = []; Id = treeId; DisplayName = "Tree"
                Kind = SemanticTypeKind.Union
                    [ { Id = leafId; DisplayName = "Leaf"; Fields = [ { Id = leafValueId; DisplayName = "value"; Type = intType } ] }
                      { Id = branchId; DisplayName = "Branch"; Fields = [ { Id = childrenId; DisplayName = "children"; Type = listType treeType } ] } ] } ]
        let create ws name parameterType resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  DisplayName = name
                  Parameters = [ { DisplayName = "input"; Type = parameterType } ]
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "initial") with Types = types } |> unwrap
        let input = ExpressionRecipe.ParameterSlot 0
        let ws = create ws "node" nodeType nodeType input
        let ws = create ws "tree" treeType treeType input
        let ws = create ws "push" nodeType nodeType
                    (ExpressionRecipe.ProductConstruct(nodeId, [], [ valueId, ExpressionRecipe.Int64Literal 0L; nextId, ExpressionRecipe.OptionSome input ]))
        let ws = create ws "branch" treeType treeType
                    (ExpressionRecipe.UnionConstruct(branchId, [], [ childrenId, ExpressionRecipe.ListConstruct(treeType, [ input; input ]) ]))
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-recursive-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap)
            use session = AuthoringSession.openExisting path |> unwrap
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            run session path functionId
        finally Directory.Delete(root, true)

    /// A chain of `count` Nodes; each Node adds a Product and an Option layer. Fields are in canonical ID order.
    let rec private chain count =
        let next = if count = 1 then None else Some(chain (count - 1))
        PureRunValue.Product(nodeId, [], [ nextId, PureRunValue.OptionValue(nodeType, next); valueId, PureRunValue.Int64(int64 count) ])

    let private leaf value = PureRunValue.Union(treeId, [], leafId, [ leafValueId, PureRunValue.Int64 value ])
    let private branch children = PureRunValue.Union(treeId, [], branchId, [ childrenId, PureRunValue.ListValue(treeType, children) ])

    let rec private chainWire count =
        let next =
            if count = 1 then """{"kind":"option","itemType":{"kind":"named","typeId":"run-recursive-node"},"hasValue":false}"""
            else $"""{{"kind":"option","itemType":{{"kind":"named","typeId":"run-recursive-node"}},"hasValue":true,"value":{chainWire (count - 1)}}}"""
        $"""{{"kind":"product","typeId":"run-recursive-node","fields":[{{"fieldId":"run-recursive-next","value":{next}}},{{"fieldId":"run-recursive-value","value":{{"kind":"int64","value":"{count}"}}}}]}}"""

    [<Fact>]
    let ``recursive values are bounded by composite layers before execution`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let reject name arguments code =
                match PureRun.run session (functionId name) arguments None with
                | Error(PureRunError.Unsupported(actual, _)) -> Assert.Equal(code, actual)
                | other -> failwithf "Invalid recursive value accepted for %s: %A" name other
            // 17 Nodes need 34 layers.
            reject "node" [ chain 17 ] "RunArgumentTooLarge"
            reject "node" [ PureRunValue.Product(nodeId, [], [ valueId, PureRunValue.Int64 1L; nextId, PureRunValue.OptionValue(treeType, None) ]) ] "RunProductShapeMismatch"
            reject "tree" [ branch [ chain 1 ] ] "RunContainerTypeMismatch"
            let request = $"""{{"id":"deep","op":"runPure","functionId":"{StableId.value (functionId "node")}","arguments":[{chainWire 17}]}}"""
            use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("ProtocolError", response.RootElement.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(before, File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure executes recursive Product and Union values up to the layer limit`` () =
        withSession (fun session path functionId ->
            let before = File.ReadAllText path
            let roundtrip name value =
                let result = PureRun.run session (functionId name) [ value ] (Some value) |> unwrap
                Assert.Equal(Some true, result.Matches)
                Assert.Equal(value, result.Actual)
            roundtrip "node" (chain 1)
            roundtrip "node" (chain 16)
            let tree = branch [ leaf 1L; branch []; branch [ leaf Int64.MinValue; branch [ leaf 3L ] ] ]
            roundtrip "tree" tree
            let pushed = PureRun.run session (functionId "push") [ chain 3 ] None |> unwrap
            Assert.Equal(PureRunValue.Product(nodeId, [], [ nextId, PureRunValue.OptionValue(nodeType, Some(chain 3)); valueId, PureRunValue.Int64 0L ]), pushed.Actual)
            let doubled = PureRun.run session (functionId "branch") [ tree ] (Some(branch [ tree; leaf 0L ])) |> unwrap
            Assert.Equal(Some false, doubled.Matches)
            Assert.Equal(branch [ tree; tree ], doubled.Actual)
            // A result deeper than 32 layers is refused while encoding, not truncated.
            match PureRun.run session (functionId "push") [ chain 16 ] None with
            | Error(PureRunError.Unsupported(code, message)) ->
                Assert.Equal("RunProcessFailure", code)
                Assert.Contains("32 composite layers", message)
            | other -> failwithf "Over-deep recursive result accepted: %A" other
            let request = $"""{{"id":"wire","op":"runPure","functionId":"{StableId.value (functionId "node")}","arguments":[{chainWire 16}],"expected":{chainWire 16}}}"""
            use response = JsonDocument.Parse(AuthoringProtocol.executeLine session request, JsonDocumentOptions(MaxDepth = 256))
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("matches").GetBoolean())
            // The reported value adds display names next to the IDs and is accepted back as an exact-ID value.
            Assert.Equal("Node", result.GetProperty("actual").GetProperty("type").GetString())
            let echo = $"""{{"id":"echo","op":"runPure","functionId":"{StableId.value (functionId "node")}","arguments":[{chainWire 16}],"expected":{result.GetProperty("actual").GetRawText()}}}"""
            use echoed = JsonDocument.Parse(AuthoringProtocol.executeLine session echo, JsonDocumentOptions(MaxDepth = 256))
            Assert.True(echoed.RootElement.GetProperty("result").GetProperty("matches").GetBoolean(), echoed.RootElement.ToString())
            Assert.Equal(before, File.ReadAllText path))
