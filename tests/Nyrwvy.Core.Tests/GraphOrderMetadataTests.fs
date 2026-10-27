namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module GraphOrderMetadataTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("order-metadata-" + name)
    let private effectId = id<EffectTag> "effect"

    let private callee effects: SemanticFunction =
        let nodeId = id<GraphNodeTag> "callee-result"
        { MustNotWait = false; Id = id<FunctionTag> "callee"; DisplayName = "callee"; Exported = true
          TypeParameters = []; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Unit
          RequiredEffects = effects
          Body = { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.UnitLiteral } ]; ResultNodeId = nodeId } }

    let private caller name: SemanticFunction =
        let first = id<GraphNodeTag> (name + "-first")
        let second = id<GraphNodeTag> (name + "-second")
        { MustNotWait = false; Id = id<FunctionTag> name; DisplayName = name; Exported = true
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

    [<Fact>]
    let ``full validation rejects unordered calls to an observable callee without any abort`` () =
        let issues = SnapshotValidator.validate (snapshot [ effectId ])
        let issue = Assert.Single(issues)
        Assert.Equal("AmbiguousEffectOrder", issue.Code)
        Assert.Contains("(DirectCall to 'callee')", issue.Message)
        Assert.Contains("Sequence them with Bind", issue.Message)

    [<Fact>]
    let ``unordered calls to a callee that observes nothing stay valid`` () =
        Assert.Empty(SnapshotValidator.validate (snapshot []))
