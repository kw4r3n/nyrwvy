namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Structural policies exercised without running a compiler. Actual authoring/execution lives
/// in PureRunEntryProjectionTests; these remain in the routine fast graph.
module EntrySelectionFastTests =
    let private id<'Tag> value = StableId.createForTesting<'Tag> value
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private fn name nodes: SemanticFunction =
        { Id = id name; DisplayName = name; Parameters = []; ResultType = unitType; RequiredEffects = []
          TypeParameters = []
          Body = { Nodes = nodes; ResultNodeId = nodes.Head.Id } }
    let private node name kind: SemanticNode = { Id = id name; Kind = kind }
    let private executionUnit name =
        let unitNode = { OriginNodeId = id (name + "-unit"); Completion = GraphCompletion.Normal unitType; Kind = TypedSemanticNodeKind.UnitLiteral }
        let typed: TypedSemanticProgram =
            { SourceSnapshotId = id "snapshot"; Types = []; Effects = []; Capabilities = []
              Functions = [ { Id = id name; Parameters = []; ResultType = unitType; RequiredEffects = []
                              TypeParameters = []
                              Body = { Nodes = [ unitNode ]; ResultNodeId = unitNode.OriginNodeId } } ] }
        match ExecutionIr.lower typed with
        | Ok program -> program.Functions.Head
        | Error error -> failwithf "Unit lowering failed: %A" error

    [<Fact>]
    let ``entry selection keeps each reference role branches cycles and reachable obligations`` () =
        let entry = id<FunctionTag> "entry"
        let value = id<GraphNodeTag> "input"
        let targets = [ "direct"; "value"; "pureCallback"; "effectCallback"; "worker"; "scopeBody"; "resourceBody"; "implementation" ]
        let referenced =
            [ node "direct-call" (SemanticNodeKind.DirectCall(id "direct", []))
              node "function-value" (SemanticNodeKind.FunctionReference(id "value"))
              node "pure-callback" (SemanticNodeKind.ListMap(value, id "pureCallback", []))
              node "effect-callback" (SemanticNodeKind.ListEffectFold(value, value, id "effectCallback", []))
              node "worker" (SemanticNodeKind.TaskStart(value, id "worker", [], value))
              node "scope" (SemanticNodeKind.TaskScope(id "scopeBody", []))
              // Release is a CapabilityOperationId, not a function reference.
              node "resource" (SemanticNodeKind.UseResource(value, value, id "release-operation", id "resourceBody", []))
              node "implementation" (SemanticNodeKind.CapabilityConstruct(id "capability", [ id "operation", id "implementation" ])) ]
        let branch: ValueGraph = { Nodes = referenced; ResultNodeId = referenced.Head.Id }
        let literal = node "literal" SemanticNodeKind.UnitLiteral
        let root = fn "entry" [ node "branch" (SemanticNodeKind.Conditional(value, { Nodes = [ literal ]; ResultNodeId = literal.Id }, branch)) ]
        let functions = root :: (targets |> List.map (fun name -> fn name [ node (name + "-cycle") (SemanticNodeKind.DirectCall(entry, [])) ]))
        let unrelated = fn "unrelated" [ node "unrelated-call" (SemanticNodeKind.DirectCall(id "missing", [])) ]
        let obligation name call: MissingArgumentObligation =
            { Id = id name; OriginTransactionId = id "transaction"; CallNodeId = id call; ParameterId = id "parameter"; Status = ObligationStatus.Open }
        let snapshot: Snapshot =
            { Id = id "snapshot"; ParentId = Some(id "parent"); Types = []; Effects = [ { Id = id "effect"; DisplayName = "effect" } ]
              Capabilities = []; Functions = functions @ [ unrelated ]
              Obligations = [ obligation "reachable" "direct-call"; obligation "unreachable" "unrelated-call" ] }
        let selected = ExecutionCallGraph.forSnapshotEntry snapshot entry
        Assert.Equal<Set<FunctionId>>(Set.ofList (entry :: (targets |> List.map id)), selected.Functions |> List.map (fun f -> f.Id) |> Set.ofList)
        Assert.Equal<ObligationId list>([ id "reachable" ], selected.Obligations |> List.map (fun o -> o.Id))
        Assert.Equal(snapshot.Id, selected.Id)
        Assert.Equal(snapshot.ParentId, selected.ParentId)
        Assert.Equal<SemanticEffect list>(snapshot.Effects, selected.Effects)
        Assert.Equal<SemanticType list>(snapshot.Types, selected.Types)
        Assert.Equal<SemanticCapability list>(snapshot.Capabilities, selected.Capabilities)
        let absent = ExecutionCallGraph.forSnapshotEntry snapshot (id "absent")
        Assert.Empty(absent.Functions)
        Assert.Empty(absent.Obligations)
        Assert.Equal(snapshot.Id, absent.Id)

    [<Theory>]
    [<InlineData("CancellationRequested")>]
    [<InlineData("ParallelAll")>]
    [<InlineData("TaskScope")>]
    [<InlineData("TaskStart")>]
    [<InlineData("TaskWait")>]
    [<InlineData("TaskCancel")>]
    [<InlineData("OneShotCreate")>]
    [<InlineData("OneShotComplete")>]
    [<InlineData("OneShotWait")>]
    let ``concurrent verification remains unknown only when the runtime operation is reachable`` kind =
        let worker = id<FunctionTag> "worker"
        let operand = (executionUnit "operand").Blocks.Head.Instructions.Head.Result.Id
        let operation =
            match kind with
            | "CancellationRequested" -> ExecutionInstructionKind.CancellationRequested
            | "ParallelAll" -> ExecutionInstructionKind.ParallelAll(operand, worker, [])
            | "TaskScope" -> ExecutionInstructionKind.TaskScope(worker, [])
            | "TaskStart" -> ExecutionInstructionKind.TaskStart(operand, worker, [], operand)
            | "TaskWait" -> ExecutionInstructionKind.TaskWait(operand, TaskWaitMode.Any, None)
            | "TaskCancel" -> ExecutionInstructionKind.TaskCancel operand
            | "OneShotCreate" -> ExecutionInstructionKind.OneShotCreate(operand, unitType)
            | "OneShotComplete" -> ExecutionInstructionKind.OneShotComplete(operand, operand)
            | "OneShotWait" -> ExecutionInstructionKind.OneShotWait(operand, None)
            | _ -> failwith kind
        let functionOf name instruction: ExecutionFunction =
            let nodeId = id<GraphNodeTag> (name + "-node")
            let lowered = executionUnit name
            let block = lowered.Blocks.Head
            let value = block.Instructions.Head.Result
            { lowered with Blocks = [ { block with Instructions = [ { Result = value; OriginNodeId = nodeId; Kind = instruction } ] } ] }
        let taskType: ExecutionType =
            { TypeParameters = []; Id = id "unused-task-type"; Kind = ExecutionTypeKind.Product [ { Id = id "unused-field"; Type = TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ unitType ]) } ] }
        let program: ExecutionProgram =
            { SourceSnapshotId = id "snapshot"; Types = [ taskType ]; Effects = []; Capabilities = []
              Functions = [ functionOf "pure" ExecutionInstructionKind.UnitLiteral
                            functionOf "entry" (ExecutionInstructionKind.DirectCall(worker, []))
                            functionOf "worker" operation ] }
        let observed: Result<PureRunResult, PureRunError> =
            Ok { SnapshotId = program.SourceSnapshotId; FunctionId = id "entry"; Actual = PureRunValue.Unit
                 Expected = None; Matches = None; Mismatch = None; Calls = [] }
        let snapshot =
            { Snapshot.empty (id "snapshot") with
                Functions = [ fn "pure" [ node "pure-unit" SemanticNodeKind.UnitLiteral ]
                              fn "entry" [ node "entry-call" (SemanticNodeKind.DirectCall(worker, [])) ]
                              fn "worker" [ node "worker-unit" SemanticNodeKind.UnitLiteral ] ] }
        let classify entry =
            // Match ChangeVerification's Project path: select semantically before lowering,
            // then inspect that conservative projection without another execution-only prune.
            let ids = (ExecutionCallGraph.forSnapshotEntry snapshot (id entry)).Functions |> List.map (fun fn -> fn.Id) |> Set.ofList
            let selected = { program with Functions = program.Functions |> List.filter (fun fn -> Set.contains fn.SemanticFunctionId ids) }
            Assert.Equal<ExecutionType list>(program.Types, selected.Types)
            ChangeVerification.classify (ExecutionCallGraph.usesConcurrentOperations selected) None observed observed
        Assert.Equal("unknown", classify "entry")
        Assert.Equal("tested-agreement", classify "pure")
