namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
open Xunit

module ViewerTests =
    let private unwrap result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Unexpected query error: %A" error

    let private focus (fixture: MilestoneZeroFixture) functionId =
        SemanticQuery.focusFunction fixture.Snapshot functionId
        |> unwrap
        |> ViewerProjection.focus

    let private assertDependencyList (items: string list) (document: string) =
        let expected =
            [ "<ul aria-label=\"value dependencies\">"
              yield! items |> List.map (fun item -> "<li>" + item + "</li>")
              "</ul>" ]
            |> String.concat "\n"
        let lines = document.Split('\n') |> Array.map (fun line -> line.Trim()) |> String.concat "\n"
        Assert.Contains(expected, lines)

    let rec private collectNodes (graph: ViewerGraph): ViewerNode list =
        graph.Nodes
        |> List.collect (fun node ->
            node
            :: (node.Branches
                |> List.collect (fun branch -> collectNodes branch.Graph)))

    let rec private collectBranchRoles (graph: ViewerGraph): ViewerBranchRole list =
        graph.Nodes
        |> List.collect (fun node ->
            node.Branches
            |> List.collect (fun branch ->
                branch.Role :: collectBranchRoles branch.Graph))

    [<Fact>]
    let ``focus projection preserves structured conditional and Result paths`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ValidateFunctionId
        let nodes = collectNodes model.Graph
        let roles = nodes |> List.map (fun node -> node.Role)
        let branches = collectBranchRoles model.Graph

        if not (List.contains (ViewerNodeRole.Operation "Conditional") roles) then
            failwith "Validation focus omitted conditional nodes."
        if not (List.contains (ViewerNodeRole.Operation "ResultOk") roles) then
            failwith "Validation focus omitted Result.Ok construction."
        if not (List.contains (ViewerNodeRole.Operation "ResultErr") roles) then
            failwith "Validation focus omitted Result.Err construction."
        if not (List.contains ViewerBranchRole.WhenTrue branches) then
            failwith "Validation focus omitted the true branch label."
        if not (List.contains ViewerBranchRole.WhenFalse branches) then
            failwith "Validation focus omitted the false branch label."

    [<Fact>]
    let ``report focus exposes capability invocation and exhaustive match paths`` () =
        let fixture = MilestoneZeroGraph.create ()
        let model = focus fixture fixture.ReportFunctionId
        let nodes = collectNodes model.Graph
        let branches = collectBranchRoles model.Graph

        let invokesConsole =
            nodes
            |> List.collect (fun node -> node.Relations)
            |> List.exists (fun relation ->
                relation.Kind = ViewerRelationKind.InvokesCapabilityOperation
                && relation.Target = ViewerTarget.CapabilityOperation fixture.ConsoleWriteLineOperationId)

        if not invokesConsole then
            failwith "Report focus omitted the typed Console capability operation path."
        if not (List.contains ViewerBranchRole.ResultOk branches) then
            failwith "Report focus omitted the Result.Ok match path."
        if not (List.contains ViewerBranchRole.ResultErr branches) then
            failwith "Report focus omitted the Result.Err match path."

        let unionBranches =
            branches
            |> List.choose (function
                | ViewerBranchRole.UnionVariant variantId -> Some variantId
                | _ -> None)
        if unionBranches.Length <> 2 then
            failwithf "Expected two explicit ValidationError variant branches, got %A" unionBranches
