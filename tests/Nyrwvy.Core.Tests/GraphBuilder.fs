namespace Nyrwvy.Core.Tests

open Nyrwvy.Core

/// Literal value-graph construction for tests that used to create programs through the
/// authoring workflows. A body is written as a tree of node kinds; `build` numbers the
/// nodes under a name prefix, children first, and returns the finished graph.
[<RequireQualifiedAccess>]
module GraphBuilder =
    type Tree =
        | Tree of (GraphNodeId list -> SemanticNodeKind) * Tree list

    /// A node with no operands.
    let leaf (kind: SemanticNodeKind) : Tree = Tree((fun _ -> kind), [])

    /// A node whose kind is built from its children's node IDs, in order.
    let node (kindOf: GraphNodeId list -> SemanticNodeKind) (children: Tree list) : Tree =
        Tree(kindOf, children)

    /// A reference to a function parameter.
    let param (parameterId: ParameterId) : Tree =
        leaf (SemanticNodeKind.ParameterReference parameterId)

    let build (nodeId: string -> GraphNodeId) (root: Tree) : ValueGraph =
        let nodes = System.Collections.Generic.List<SemanticNode>()

        let rec go (path: string) (Tree(kindOf, children)) =
            let childIds =
                children |> List.mapi (fun index child -> go $"{path}/{index}" child)

            let id = nodeId path
            nodes.Add({ Id = id; Kind = kindOf childIds })
            id

        let rootId = go "body" root
        { Nodes = List.ofSeq nodes; ResultNodeId = rootId }
