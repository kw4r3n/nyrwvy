namespace NyamNyam.AgentEval.ListMapStructural

open NyamNyam.Core

[<RequireQualifiedAccess>]
module ListMapStructuralOracle =
    let verifyExpectedMaterialization
        (fixture: ListMapStructuralHarnessFixtureBinding)
        (publication: PublishedStructuralTransaction)
        : Result<unit, string> =
        let diff = publication.Diff
        if diff.OwnerFunctionId <> fixture.TargetFunctionId then
            Error "Published ListMap diff targets the wrong function."
        elif diff.AddedNodes.Length <> 2 then
            Error "Published ListMap diff does not contain exactly two materialized nodes."
        else
            let sourceNode = diff.AddedNodes[0]
            let mapNode = diff.AddedNodes[1]
            let removedIds = diff.RemovedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
            let addedIds = diff.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
            if sourceNode.Role <> "ParameterReference" then
                Error "First ListMap materialized node is not ParameterReference."
            elif sourceNode.ParameterId <> Some fixture.SourceListParameterId then
                Error "Materialized ParameterReference does not use the frozen source ParameterId."
            elif mapNode.Role <> "ListMap" then
                Error "Second ListMap materialized node is not ListMap."
            elif mapNode.FunctionId <> Some fixture.CompatibleMapperFunctionId then
                Error "Materialized ListMap does not use the frozen mapper FunctionId."
            elif mapNode.Dependencies <> [ sourceNode.NodeId ] then
                Error "Materialized ListMap dependency does not point exactly to the fresh source node."
            elif diff.NewResultNodeId <> mapNode.NodeId then
                Error "Published ListMap result does not point to the materialized ListMap node."
            elif not (Set.intersect removedIds addedIds |> Set.isEmpty) then
                Error "Published ListMap materialization reused a removed production GraphNodeId."
            else
                Ok ()
