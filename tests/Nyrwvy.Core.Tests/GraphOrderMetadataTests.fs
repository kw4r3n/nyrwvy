namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module GraphOrderMetadataTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("order-metadata-" + name)
    let private effectId = id<EffectTag> "effect"

    let private callee effects: SemanticFunction =
        let nodeId = id<GraphNodeTag> "callee-result"
        { Id = id<FunctionTag> "callee"; DisplayName = "callee"; Exported = true
          TypeParameters = []; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Unit
          RequiredEffects = effects
          Body = { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ]; ResultNodeId = nodeId } }

    let private caller name: SemanticFunction =
        let first = id<GraphNodeTag> (name + "-first")
        let second = id<GraphNodeTag> (name + "-second")
        { Id = id<FunctionTag> name; DisplayName = name; Exported = true
          TypeParameters = []; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Unit
          RequiredEffects = [ effectId ]
          Body =
            { Nodes =
                [ { Id = first; Kind = SemanticNodeKind.DirectCall(id<FunctionTag> "callee", []) }
                  { Id = second; Kind = SemanticNodeKind.DirectCall(id<FunctionTag> "callee", []) } ]
              ResultNodeId = second } }

    let private snapshot effects =
        { Snapshot.empty (id<SnapshotTag> "snapshot") with
            Effects = [ { Id = effectId; DisplayName = "effect"; Exported = true } ]
            Functions = [ callee effects; caller "caller" ] }

    let private conflicts baseline proposed =
        GraphOrder.newEffectOrderConflicts baseline proposed |> List.map (fun (fn, _, _) -> fn.Id)

    [<Fact>]
    let ``metadata edits preserve admission of pre-existing unordered effects`` () =
        let baseline = snapshot [ effectId ]
        Assert.Empty(SnapshotValidator.validate baseline)
        let caller = baseline.Functions[1]
        Assert.True(GraphOrder.tryEffectOrderConflict (GraphOrder.semanticObservableFunctions baseline.Functions) caller |> Option.isSome)
        let proposed =
            { baseline with
                Functions = baseline.Functions |> List.map (fun fn -> { fn with DisplayName = fn.DisplayName + "_renamed"; Exported = false }) }
        Assert.Empty(conflicts baseline proposed)
        Assert.Empty(SnapshotValidator.validateProposal baseline proposed)

    [<Fact>]
    let ``body edits still reject an unordered observable pair`` () =
        let baseline = snapshot [ effectId ]
        let proposed =
            { baseline with
                Functions = baseline.Functions |> List.map (fun fn ->
                    if fn.Id <> id<FunctionTag> "caller" then fn
                    else
                        { fn with Body = { fn.Body with Nodes = { Id = id<GraphNodeTag> "extra"; Kind = SemanticNodeKind.UnitLiteral } :: fn.Body.Nodes } }) }
        Assert.Equal<FunctionId list>([ id<FunctionTag> "caller" ], conflicts baseline proposed)
        Assert.Contains(SnapshotValidator.validateProposal baseline proposed, fun issue -> issue.Code = "AmbiguousEffectOrder")

    [<Fact>]
    let ``callee effect changes still detect new ambiguity in an unchanged caller`` () =
        let baseline = snapshot []
        let proposed =
            { baseline with
                Functions = baseline.Functions |> List.map (fun fn ->
                    if fn.Id = id<FunctionTag> "callee" then { fn with RequiredEffects = [ effectId ] } else fn) }
        Assert.Equal<FunctionId list>([ id<FunctionTag> "caller" ], conflicts baseline proposed)
        Assert.Contains(SnapshotValidator.validateProposal baseline proposed, fun issue -> issue.Code = "AmbiguousEffectOrder")

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``new function membership or identity still detects conflicts`` replaceIdentity =
        let baseline = snapshot [ effectId ]
        let added = caller "added"
        let proposed =
            { baseline with
                Functions =
                    if replaceIdentity then [ baseline.Functions.Head; { baseline.Functions[1] with Id = added.Id } ]
                    else baseline.Functions @ [ added ] }
        Assert.Equal<FunctionId list>([ added.Id ], conflicts baseline proposed)

    [<Fact>]
    let ``equal cloned bodies and reordered functions retain existing admission`` () =
        let baseline = snapshot [ effectId ]
        let proposed =
            { baseline with
                Functions = baseline.Functions |> List.rev |> List.map (fun fn ->
                    let cloned: ValueGraph = { Nodes = List.map (fun node -> node) fn.Body.Nodes; ResultNodeId = fn.Body.ResultNodeId }
                    Assert.False(obj.ReferenceEquals(fn.Body, cloned))
                    { fn with Body = cloned }) }
        Assert.Empty(conflicts baseline proposed)
        Assert.Empty(SnapshotValidator.validateProposal baseline proposed)

    [<Fact>]
    let ``duplicate function identities retain the existing last-wins comparison`` () =
        let baseline = snapshot [ effectId ]
        let duplicate =
            { baseline.Functions[1] with
                Body = baseline.Functions.Head.Body
                RequiredEffects = [] }
        let invalid = { baseline with Functions = baseline.Functions @ [ duplicate ] }
        Assert.Equal<FunctionId list>([ id<FunctionTag> "caller" ], conflicts invalid invalid)
