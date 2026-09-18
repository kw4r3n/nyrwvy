namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module TypedSemanticIrTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error

    let rec private collectTypedNodes (graph: TypedSemanticGraph): TypedSemanticNode list =
        graph.Nodes
        |> List.collect (fun node ->
            let nested =
                match node.Kind with
                | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                    collectTypedNodes whenTrue @ collectTypedNodes whenFalse
                | TypedSemanticNodeKind.MatchUnion(_, arms) ->
                    arms |> List.collect (fun arm -> collectTypedNodes arm.Body)
                | TypedSemanticNodeKind.Bind(_, body) -> collectTypedNodes body.Body
                | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                    collectTypedNodes someArm.Body @ collectTypedNodes noneBody
                | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
                    collectTypedNodes okArm.Body @ collectTypedNodes errArm.Body
                | TypedSemanticNodeKind.ParameterReference _
                | TypedSemanticNodeKind.PatternReference _
                | TypedSemanticNodeKind.StringLiteral _
                | TypedSemanticNodeKind.BoolLiteral _
                | TypedSemanticNodeKind.UnitLiteral
                | TypedSemanticNodeKind.Int64Literal _
                | TypedSemanticNodeKind.Float64Literal _
                | TypedSemanticNodeKind.StringIsEmpty _
                | TypedSemanticNodeKind.StringContains _
                | TypedSemanticNodeKind.Int64WrappingAdd _
                | TypedSemanticNodeKind.Int64WrappingSubtract _
                | TypedSemanticNodeKind.Int64WrappingMultiply _
                | TypedSemanticNodeKind.Int64Equal _
                | TypedSemanticNodeKind.Int64LessThan _
                | TypedSemanticNodeKind.Int64LessThanOrEqual _
                | TypedSemanticNodeKind.Int64GreaterThan _
                | TypedSemanticNodeKind.Int64GreaterThanOrEqual _
                | TypedSemanticNodeKind.Float64Add _
                | TypedSemanticNodeKind.Float64Subtract _
                | TypedSemanticNodeKind.Float64Multiply _
                | TypedSemanticNodeKind.Float64Divide _
                | TypedSemanticNodeKind.Float64Equal _
                | TypedSemanticNodeKind.Float64LessThan _
                | TypedSemanticNodeKind.Float64IsNaN _
                | TypedSemanticNodeKind.Float64IsFinite _
                | TypedSemanticNodeKind.Float64IsInfinite _
                | TypedSemanticNodeKind.ListEmpty
                | TypedSemanticNodeKind.ListConstruct _
                | TypedSemanticNodeKind.ListLength _
                | TypedSemanticNodeKind.ListAppendItem _
                | TypedSemanticNodeKind.ListConcat _
                | TypedSemanticNodeKind.ListMap _
                | TypedSemanticNodeKind.ListFilter _
                | TypedSemanticNodeKind.ListAny _
                | TypedSemanticNodeKind.ListAll _
                | TypedSemanticNodeKind.DirectCall _
                | TypedSemanticNodeKind.CapabilityInvoke _
                | TypedSemanticNodeKind.ProductConstruct _
                | TypedSemanticNodeKind.FieldAccess _
                | TypedSemanticNodeKind.UnionConstruct _
                | TypedSemanticNodeKind.OptionNone
                | TypedSemanticNodeKind.OptionSome _
                | TypedSemanticNodeKind.ResultOk _
                | TypedSemanticNodeKind.ResultErr _ -> []

            node :: nested)

    [<Fact>]
    let ``semantic facts derive a type for every representative M0 node`` () =
        let fixture = MilestoneZeroGraph.create ()

        for functionEntity in fixture.Snapshot.Functions do
            let facts = SemanticFacts.nodeTypes fixture.Snapshot functionEntity
            let expectedIds =
                ValueGraph.collectNodes functionEntity.Body
                |> List.map (fun node -> node.Id)
                |> Set.ofList
            let typedIds =
                facts
                |> Map.toList
                |> List.choose (fun (nodeId, nodeType) -> Option.map (fun _ -> nodeId) nodeType)
                |> Set.ofList

            if expectedIds <> typedIds then
                failwithf "Semantic facts did not type every node. Expected %A, got %A" expectedIds typedIds

    [<Fact>]
    let ``typed semantic projection preserves stable identity and graph provenance`` () =
        let fixture = MilestoneZeroGraph.create ()
        let ir = TypedSemanticIr.project fixture.Snapshot |> unwrap

        Assert.Equal(fixture.Snapshot.Id, ir.SourceSnapshotId)

        let functionIds = ir.Functions |> List.map (fun functionEntity -> functionEntity.Id) |> Set.ofList
        Assert.Contains(fixture.ValidateFunctionId, functionIds)
        Assert.Contains(fixture.ReportFunctionId, functionIds)
        Assert.Contains(fixture.MainFunctionId, functionIds)

        let main = ir.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let mainNodes = collectTypedNodes main.Body
        Assert.Contains(mainNodes, fun node -> node.OriginNodeId = fixture.MainValidateCallNodeId)

    [<Fact>]
    let ``typed semantic nodes carry complete types while normalized constructors drop semantic type hints`` () =
        let fixture = MilestoneZeroGraph.create ()
        let ir = TypedSemanticIr.project fixture.Snapshot |> unwrap
        let nodes = ir.Functions |> List.collect (fun functionEntity -> collectTypedNodes functionEntity.Body)

        let resultConstructors =
            nodes
            |> List.choose (fun node ->
                match node.Kind with
                | TypedSemanticNodeKind.ResultOk valueNodeId -> Some(node, valueNodeId)
                | TypedSemanticNodeKind.ResultErr errorNodeId -> Some(node, errorNodeId)
                | _ -> None)

        Assert.NotEmpty(resultConstructors)

        for node, _ in resultConstructors do
            match node.Type with
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; _ ]) -> ()
            | other -> failwithf "Expected a complete Result type on normalized IR node, got %A" other

        let optionNoneNodes =
            nodes
            |> List.filter (fun node -> node.Kind = TypedSemanticNodeKind.OptionNone)

        for node in optionNoneNodes do
            match node.Type with
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ _ ]) -> ()
            | other -> failwithf "Expected a complete Option type on normalized IR node, got %A" other

    [<Fact>]
    let ``typed semantic projection rejects snapshots with open obligations`` () =
        let fixture = MilestoneZeroGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-ir-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-ir-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-ir-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-ir-incomplete"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let incomplete =
            match Transaction.simulate fixture.Snapshot request with
            | Proposed simulation -> simulation.ProposedSnapshot
            | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

        match TypedSemanticIr.project incomplete with
        | Error(TypedSemanticIrError.IncompleteSnapshot [ actual ]) -> Assert.Equal(obligationId, actual)
        | result -> failwithf "Expected IncompleteSnapshot, got %A" result

    [<Fact>]
    let ``typed semantic projection canonicalizes nonsemantic collection order`` () =
        let fixture = MilestoneZeroGraph.create ()
        let reorderFunction (functionEntity: SemanticFunction) =
            { functionEntity with
                Body = { functionEntity.Body with Nodes = List.rev functionEntity.Body.Nodes } }

        let reordered =
            { fixture.Snapshot with
                Types = List.rev fixture.Snapshot.Types
                Effects = List.rev fixture.Snapshot.Effects
                Capabilities = List.rev fixture.Snapshot.Capabilities
                Functions = fixture.Snapshot.Functions |> List.rev |> List.map reorderFunction }

        let expected = TypedSemanticIr.project fixture.Snapshot |> unwrap
        let actual = TypedSemanticIr.project reordered |> unwrap
        Assert.Equal(expected, actual)

    [<Fact>]
    let ``typed semantic projection retains explicit effect and capability contracts`` () =
        let fixture = MilestoneZeroGraph.create ()
        let ir = TypedSemanticIr.project fixture.Snapshot |> unwrap

        Assert.Contains(fixture.ConsoleEffectId, ir.Effects)
        let console = ir.Capabilities |> List.find (fun capability -> capability.Id = fixture.ConsoleCapabilityId)
        let writeLine =
            console.Operations
            |> List.find (fun operation -> operation.Id = fixture.ConsoleWriteLineOperationId)

        Assert.Equal(fixture.ConsoleEffectId, writeLine.EffectId)

        let report = ir.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.ReportFunctionId)
        Assert.Contains(fixture.ConsoleEffectId, report.RequiredEffects)
