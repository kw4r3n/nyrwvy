namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.Globalization
open System.IO
open Nyrwvy.Core
open Xunit

type private Int64Fixture =
    { Snapshot: Snapshot
      AddFunctionId: FunctionId
      SubtractFunctionId: FunctionId
      MultiplyFunctionId: FunctionId
      EqualFunctionId: FunctionId
      LessFunctionId: FunctionId
      LessOrEqualFunctionId: FunctionId
      GreaterFunctionId: FunctionId
      GreaterOrEqualFunctionId: FunctionId
      AddResultNodeId: GraphNodeId
      AddLeftNodeId: GraphNodeId
      AddRightNodeId: GraphNodeId }

module Int64Tests =
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-int64-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-int64-" + name)
    let private snapshotId = StableId.createForTesting<SnapshotTag> "snapshot-int64"
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool

    let private binaryFunction
        name
        resultType
        leftValue
        rightValue
        kind
        : SemanticFunction * GraphNodeId * GraphNodeId * GraphNodeId =
        let leftId = nodeId (name + "-left")
        let rightId = nodeId (name + "-right")
        let resultId = nodeId (name + "-result")
        let left = { Id = leftId; Kind = SemanticNodeKind.Int64Literal leftValue }
        let right = { Id = rightId; Kind = SemanticNodeKind.Int64Literal rightValue }
        let result = { Id = resultId; Kind = kind leftId rightId }
        { MustNotWait = false; Exported = true; Id = functionId name
          TypeParameters = []
          DisplayName = name
          Parameters = []
          ResultType = resultType
          RequiredEffects = []
          Body =
            { Nodes = [ left; right; result ]
              ResultNodeId = resultId } },
        leftId,
        rightId,
        resultId

    let private createFixture () =
        let add, addLeft, addRight, addResult =
            binaryFunction
                "wrapping-add"
                int64Type
                Int64.MaxValue
                1L
                (fun left right -> SemanticNodeKind.Int64WrappingAdd(left, right))
        let subtract, _, _, _ =
            binaryFunction
                "wrapping-subtract"
                int64Type
                Int64.MinValue
                1L
                (fun left right -> SemanticNodeKind.Int64WrappingSubtract(left, right))
        let multiply, _, _, _ =
            binaryFunction
                "wrapping-multiply"
                int64Type
                Int64.MaxValue
                2L
                (fun left right -> SemanticNodeKind.Int64WrappingMultiply(left, right))
        let equal, _, _, _ =
            binaryFunction
                "equal"
                boolType
                3L
                3L
                (fun left right -> SemanticNodeKind.Int64Equal(left, right))
        let less, _, _, _ =
            binaryFunction
                "less-than"
                boolType
                2L
                3L
                (fun left right -> SemanticNodeKind.Int64LessThan(left, right))
        let lessOrEqual, _, _, _ =
            binaryFunction
                "less-than-or-equal"
                boolType
                3L
                3L
                (fun left right -> SemanticNodeKind.Int64LessThanOrEqual(left, right))
        let greater, _, _, _ =
            binaryFunction
                "greater-than"
                boolType
                4L
                3L
                (fun left right -> SemanticNodeKind.Int64GreaterThan(left, right))
        let greaterOrEqual, _, _, _ =
            binaryFunction
                "greater-than-or-equal"
                boolType
                3L
                3L
                (fun left right -> SemanticNodeKind.Int64GreaterThanOrEqual(left, right))

        { Snapshot =
            { Id = snapshotId
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ add; subtract; multiply; equal; less; lessOrEqual; greater; greaterOrEqual ]
              Obligations = [] }
          AddFunctionId = add.Id
          SubtractFunctionId = subtract.Id
          MultiplyFunctionId = multiply.Id
          EqualFunctionId = equal.Id
          LessFunctionId = less.Id
          LessOrEqualFunctionId = lessOrEqual.Id
          GreaterFunctionId = greater.Id
          GreaterOrEqualFunctionId = greaterOrEqual.Id
          AddResultNodeId = addResult
          AddLeftNodeId = addLeft
          AddRightNodeId = addRight }

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private requireMatch label predicate items =
        if not (items |> List.exists predicate) then
            failwithf "Expected %s, got %A" label items

    let private requireValue label value items =
        if not (items |> List.contains value) then
            failwithf "Expected %s value %A, got %A" label value items

    let private lower fixture =
        let typed = TypedSemanticIr.project fixture.Snapshot |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        typed, execution

    [<Fact>]
    let ``Int64 operations reject non-Int64 operands`` () =
        let fixture = createFixture ()
        let changedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.AddFunctionId then
                    functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node ->
                            if node.Id = fixture.AddRightNodeId then
                                { node with Kind = SemanticNodeKind.StringLiteral "not-an-int64" }
                            else node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        let invalid = { fixture.Snapshot with Functions = changedFunctions }
        let issues = SnapshotValidator.validate invalid
        requireMatch "Int64OperandTypeMismatch diagnostic" (fun issue -> issue.Code = "Int64OperandTypeMismatch") issues

    [<Fact>]
    let ``JSON preserves full-range Int64 literals and node kinds`` () =
        let fixture = createFixture ()
        let json = SnapshotCodec.serializeCanonical fixture.Snapshot |> unwrap "JSON serialization"
        let fromJson = SnapshotCodec.deserialize (SnapshotCodec.serialize fixture.Snapshot |> unwrap "JSON serialization") |> unwrap "JSON deserialization"

        let literals snapshot =
            snapshot.Functions
            |> List.collect (fun functionEntity -> ValueGraph.collectNodes functionEntity.Body)
            |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.Int64Literal value -> Some value
                | _ -> None)

        for restored in [ fromJson ] do
            let values = literals restored
            requireValue "Int64 minimum" Int64.MinValue values
            requireValue "Int64 maximum" Int64.MaxValue values
            let add = restored.Functions |> List.find (fun functionEntity -> functionEntity.Id = fixture.AddFunctionId)
            let result = add.Body.Nodes |> List.find (fun node -> node.Id = fixture.AddResultNodeId)
            match result.Kind with
            | SemanticNodeKind.Int64WrappingAdd _ -> ()
            | other -> failwithf "Expected Int64WrappingAdd after round-trip, got %A" other

        Assert.Contains("\"schemaVersion\":9", json)

    [<Fact>]
    let ``Typed and Execution IR preserve every initial Int64 semantic role`` () =
        let fixture = createFixture ()
        let typed, execution = lower fixture

        let typedKinds = typed.Functions |> List.collect (fun functionEntity -> functionEntity.Body.Nodes) |> List.map (fun node -> node.Kind)
        requireMatch "Typed Int64WrappingAdd" (function TypedSemanticNodeKind.Int64WrappingAdd _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64WrappingSubtract" (function TypedSemanticNodeKind.Int64WrappingSubtract _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64WrappingMultiply" (function TypedSemanticNodeKind.Int64WrappingMultiply _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64Equal" (function TypedSemanticNodeKind.Int64Equal _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64LessThan" (function TypedSemanticNodeKind.Int64LessThan _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64LessThanOrEqual" (function TypedSemanticNodeKind.Int64LessThanOrEqual _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64GreaterThan" (function TypedSemanticNodeKind.Int64GreaterThan _ -> true | _ -> false) typedKinds
        requireMatch "Typed Int64GreaterThanOrEqual" (function TypedSemanticNodeKind.Int64GreaterThanOrEqual _ -> true | _ -> false) typedKinds

        let executionKinds =
            execution.Functions
            |> List.collect (fun functionEntity -> functionEntity.Blocks)
            |> List.collect (fun block -> block.Instructions)
            |> List.map (fun instruction -> instruction.Kind)
        requireMatch "Execution Int64WrappingAdd" (function ExecutionInstructionKind.Int64WrappingAdd _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64WrappingSubtract" (function ExecutionInstructionKind.Int64WrappingSubtract _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64WrappingMultiply" (function ExecutionInstructionKind.Int64WrappingMultiply _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64Equal" (function ExecutionInstructionKind.Int64Equal _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64LessThan" (function ExecutionInstructionKind.Int64LessThan _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64LessThanOrEqual" (function ExecutionInstructionKind.Int64LessThanOrEqual _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64GreaterThan" (function ExecutionInstructionKind.Int64GreaterThan _ -> true | _ -> false) executionKinds
        requireMatch "Execution Int64GreaterThanOrEqual" (function ExecutionInstructionKind.Int64GreaterThanOrEqual _ -> true | _ -> false) executionKinds

        let typedJson = TypedSemanticIrSerializer.serialize typed
        let executionJson = ExecutionIrSerializer.serialize execution
        Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", typedJson)
        Assert.Contains("\"primitiveInt64\"", typedJson)
        Assert.Contains("\"int64WrappingAdd\"", executionJson)

    [<Fact>]
    let ``Int64 semantic changes participate in semantic fingerprints`` () =
        let fixture = createFixture ()
        let original = SemanticFingerprint.compute fixture.Snapshot |> unwrap "original semantic fingerprint"
        let changedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> fixture.AddFunctionId then functionEntity
                else
                    let nodes =
                        functionEntity.Body.Nodes
                        |> List.map (fun node ->
                            if node.Id = fixture.AddLeftNodeId then
                                { node with Kind = SemanticNodeKind.Int64Literal(Int64.MaxValue - 1L) }
                            else node)
                    { functionEntity with Body = { functionEntity.Body with Nodes = nodes } })
        let changed = { fixture.Snapshot with Functions = changedFunctions }
        let changedFingerprint = SemanticFingerprint.compute changed |> unwrap "changed semantic fingerprint"
        Assert.NotEqual(original, changedFingerprint)
