namespace NyamNyam.Core.Tests

open Microsoft.FSharp.Reflection
open NyamNyam.Core
open Xunit

module AgentStructuralContractTests =
    type private Fixture =
        { Snapshot: Snapshot
          TargetFunctionId: FunctionId
          TargetParameterId: ParameterId
          TargetResultNodeId: GraphNodeId
          CallerFunctionId: FunctionId
          UnrelatedFunctionId: FunctionId
          NonFloatFunctionId: FunctionId
          NonFloatResultNodeId: GraphNodeId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-structural-contract-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-structural-contract-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-structural-contract-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-structural-contract-" + name)
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let private createFixture (): Fixture =
        let targetParameter: SemanticParameter =
            { Id = parameterId "target-value"
              DisplayName = "value"
              Type = float64Type }

        // Deliberately keep direct body-node order different from stable-ID order so
        // the review-ordering contract is exercised independently of storage order.
        let oldZ = nodeId "target-z"
        let oldA = nodeId "target-a"
        let oldM = nodeId "target-m"
        let target: SemanticFunction =
            { Id = functionId "target"
              DisplayName = "target"
              Parameters = [ targetParameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = oldZ; Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0) }
                      { Id = oldM; Kind = SemanticNodeKind.Float64Add(oldZ, oldA) }
                      { Id = oldA; Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 2.0) } ]
                  ResultNodeId = oldM } }

        let callerValueId = nodeId "caller-value"
        let callerCallId = nodeId "caller-call"
        let caller: SemanticFunction =
            { Id = functionId "caller"
              DisplayName = "caller"
              Parameters = []
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = callerValueId; Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 4.0) }
                      { Id = callerCallId
                        Kind =
                            SemanticNodeKind.DirectCall(
                                target.Id,
                                [ { TargetParameterId = targetParameter.Id
                                    ValueNodeId = callerValueId } ]
                            ) } ]
                  ResultNodeId = callerCallId } }

        let unrelatedResultId = nodeId "unrelated-result"
        let unrelated: SemanticFunction =
            { Id = functionId "unrelated"
              DisplayName = "unrelated"
              Parameters = []
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = unrelatedResultId; Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 99.0) } ]
                  ResultNodeId = unrelatedResultId } }

        let nonFloatResultId = nodeId "non-float-result"
        let nonFloat: SemanticFunction =
            { Id = functionId "non-float"
              DisplayName = "non_float"
              Parameters = []
              ResultType = int64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = nonFloatResultId; Kind = SemanticNodeKind.Int64Literal 1L } ]
                  ResultNodeId = nonFloatResultId } }

        let snapshot: Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ target; caller; unrelated; nonFloat ]
              Obligations = [] }

        Assert.Empty(SnapshotValidator.validate snapshot)

        { Snapshot = snapshot
          TargetFunctionId = target.Id
          TargetParameterId = targetParameter.Id
          TargetResultNodeId = target.Body.ResultNodeId
          CallerFunctionId = caller.Id
          UnrelatedFunctionId = unrelated.Id
          NonFloatFunctionId = nonFloat.Id
          NonFloatResultNodeId = nonFloatResultId }

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request fixture recipe: AgentReplaceFunctionBodyWithFloat64Recipe =
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = fixture.TargetResultNodeId
          Replacement = recipe }

    let private simulate workspace structuralRequest =
        match AgentStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected structural simulation failure: %A" error

    let private commit workspace proposal =
        match AgentStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Committed(next, publication) -> next, publication
        | AgentStructuralCommitResult.Rejected diagnostics -> failwithf "Unexpected structural commit rejection: %A" diagnostics

    let private assertRejectedCode expected result =
        match result with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected %s, got %A" expected other

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let rec private deepRecipe depth =
        if depth <= 1 then
            Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)
        else
            Float64ExpressionRecipe.Add(
                Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0),
                deepRecipe (depth - 1)
            )

    let rec private balancedLeaves leaves =
        if leaves <= 1 then
            Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)
        else
            let leftLeaves = leaves / 2
            let rightLeaves = leaves - leftLeaves
            Float64ExpressionRecipe.Add(balancedLeaves leftLeaves, balancedLeaves rightLeaves)

    [<Fact>]
    let ``structural preflight rejects unknown and non-Float64 targets`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        let unknownRequest: AgentReplaceFunctionBodyWithFloat64Recipe =
            { TargetFunctionId = functionId "missing"
              ExpectedBaseSnapshotId = fixture.Snapshot.Id
              ExpectedCurrentResultNodeId = nodeId "missing-result"
              Replacement = Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0) }

        AgentStructuralWorkflow.simulate workspace unknownRequest
        |> assertRejectedCode "StructuralTargetNotFound"

        let nonFloatRequest: AgentReplaceFunctionBodyWithFloat64Recipe =
            { TargetFunctionId = fixture.NonFloatFunctionId
              ExpectedBaseSnapshotId = fixture.Snapshot.Id
              ExpectedCurrentResultNodeId = fixture.NonFloatResultNodeId
              Replacement = Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0) }

        AgentStructuralWorkflow.simulate workspace nonFloatRequest
        |> assertRejectedCode "StructuralTargetResultTypeMismatch"

    [<Fact>]
    let ``structural recipe accepts exact practical bounds and rejects first representable overflow`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        // Depth 32 materializes 63 nodes and is valid. Depth 33 materializes 65
        // nodes too, but depth is intentionally diagnosed first.
        AgentStructuralWorkflow.simulate workspace (request fixture (deepRecipe 32))
        |> function
            | Ok _ -> ()
            | result -> failwithf "Expected depth-32 recipe to succeed, got %A" result

        AgentStructuralWorkflow.simulate workspace (request fixture (deepRecipe 33))
        |> assertRejectedCode "StructuralRecipeDepthLimitExceeded"

        // A full binary expression always has an odd node count. Therefore 63 is
        // the largest representable count <= 64, and 65 is the first overflow.
        AgentStructuralWorkflow.simulate workspace (request fixture (balancedLeaves 32))
        |> function
            | Ok _ -> ()
            | result -> failwithf "Expected 63-node recipe to succeed, got %A" result

        AgentStructuralWorkflow.simulate workspace (request fixture (balancedLeaves 33))
        |> assertRejectedCode "StructuralRecipeNodeLimitExceeded"

    [<Fact>]
    let ``structural literal bits survive materialization publication JSON and Nyason`` () =
        let fixture = createFixture ()
        let cases =
            [ "finite", Float64Bits.create 0x3ff8000000000000UL
              "negative-zero", Float64Bits.create 0x8000000000000000UL
              "infinity", Float64Bits.create 0x7ff0000000000000UL
              "canonical-nan", Float64Bits.create 0x7ff0000000000001UL ]

        for label, bits in cases do
            let workspace = createWorkspace fixture.Snapshot
            let proposal =
                request fixture (Float64ExpressionRecipe.Literal bits)
                |> simulate workspace
            let review = AgentStructuralChangeProposal.review proposal
            let added = List.exactlyOne review.ResultingChange.AddedNodes
            Assert.Equal(Some(Float64Bits.toHex bits), added.Float64BitsHex)

            let committed, _ = commit workspace proposal
            let committedSnapshot = AgentWorkspace.snapshotForStructuralEditing committed
            let json = SnapshotCodec.serialize committedSnapshot |> unwrap (label + " JSON serialize")
            let jsonRestored = SnapshotCodec.deserialize json |> unwrap (label + " JSON deserialize")
            let nyason = NyasonCodec.serialize committedSnapshot |> unwrap (label + " Nyason serialize")
            let nyasonRestored = NyasonCodec.deserialize nyason |> unwrap (label + " Nyason deserialize")

            let assertLiteral expected (snapshot: Snapshot) =
                let target = snapshot.Functions |> List.find (fun fn -> fn.Id = fixture.TargetFunctionId)
                match List.exactlyOne target.Body.Nodes with
                | { Kind = SemanticNodeKind.Float64Literal actual } -> Assert.Equal(expected, actual)
                | node -> failwithf "%s restored the wrong node: %A" label node

            assertLiteral bits committedSnapshot
            assertLiteral bits jsonRestored
            assertLiteral bits nyasonRestored

            let snapshotFingerprint = SnapshotFingerprint.compute committedSnapshot |> unwrap (label + " snapshot fingerprint")
            let semanticFingerprint = SemanticFingerprint.compute committedSnapshot |> unwrap (label + " semantic fingerprint")
            Assert.Equal(snapshotFingerprint, SnapshotFingerprint.compute jsonRestored |> unwrap (label + " JSON snapshot fingerprint"))
            Assert.Equal(snapshotFingerprint, SnapshotFingerprint.compute nyasonRestored |> unwrap (label + " Nyason snapshot fingerprint"))
            Assert.Equal(semanticFingerprint, SemanticFingerprint.compute jsonRestored |> unwrap (label + " JSON semantic fingerprint"))
            Assert.Equal(semanticFingerprint, SemanticFingerprint.compute nyasonRestored |> unwrap (label + " Nyason semantic fingerprint"))

    [<Fact>]
    let ``structural replacement preserves callers declarations unrelated facts and deterministic review ordering`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let recipe =
            Float64ExpressionRecipe.Add(
                Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 10.0),
                Float64ExpressionRecipe.Add(
                    Float64ExpressionRecipe.Parameter fixture.TargetParameterId,
                    Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 20.0)
                )
            )

        let proposal = simulate workspace (request fixture recipe)
        let review = AgentStructuralChangeProposal.review proposal

        let expectedRemoved =
            fixture.Snapshot.Functions
            |> List.find (fun fn -> fn.Id = fixture.TargetFunctionId)
            |> fun fn -> ValueGraph.collectNodes fn.Body
            |> List.map (fun node -> node.Id)
            |> List.sortBy StableId.value

        let actualRemoved = review.ResultingChange.RemovedNodes |> List.map (fun node -> node.NodeId)
        if actualRemoved <> expectedRemoved then
            failwithf "Removed-node summaries are not stable-ID ordered. Expected %A, got %A" expectedRemoved actualRemoved

        let addedRoles = review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        let expectedRoles = [ "Float64Literal"; "ParameterReference"; "Float64Literal"; "Float64Add"; "Float64Add" ]
        if addedRoles <> expectedRoles then
            failwithf "Added-node summaries lost materialization order. Expected %A, got %A" expectedRoles addedRoles

        let committed, _ = commit workspace proposal
        let after = AgentWorkspace.snapshotForStructuralEditing committed
        let before = fixture.Snapshot

        if before.Types <> after.Types
           || before.Effects <> after.Effects
           || before.Capabilities <> after.Capabilities
           || before.Obligations <> after.Obligations then
            failwith "Structural replacement changed unrelated declaration or obligation collections."

        let findFunction id snapshot = snapshot.Functions |> List.find (fun fn -> fn.Id = id)
        let beforeCaller = findFunction fixture.CallerFunctionId before
        let afterCaller = findFunction fixture.CallerFunctionId after
        let beforeUnrelated = findFunction fixture.UnrelatedFunctionId before
        let afterUnrelated = findFunction fixture.UnrelatedFunctionId after
        let beforeNonFloat = findFunction fixture.NonFloatFunctionId before
        let afterNonFloat = findFunction fixture.NonFloatFunctionId after

        if beforeCaller <> afterCaller then failwith "Caller function changed during target-body replacement."
        if beforeUnrelated <> afterUnrelated then failwith "Unrelated Float64 function changed during target-body replacement."
        if beforeNonFloat <> afterNonFloat then failwith "Unrelated Int64 function changed during target-body replacement."

        let beforeTarget = findFunction fixture.TargetFunctionId before
        let afterTarget = findFunction fixture.TargetFunctionId after
        Assert.Equal(beforeTarget.Id, afterTarget.Id)
        Assert.Equal(beforeTarget.DisplayName, afterTarget.DisplayName)
        if beforeTarget.Parameters <> afterTarget.Parameters then failwith "Target parameter identity/order changed."
        Assert.Equal(beforeTarget.ResultType, afterTarget.ResultType)
        if beforeTarget.RequiredEffects <> afterTarget.RequiredEffects then failwith "Target effect requirements changed."
        Assert.NotEqual(beforeTarget.Body.ResultNodeId, afterTarget.Body.ResultNodeId)

    [<Fact>]
    let ``structural request type exposes only guarded target facts and the closed recipe`` () =
        let fields =
            FSharpType.GetRecordFields(typeof<AgentReplaceFunctionBodyWithFloat64Recipe>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList

        let names = fields |> List.map fst
        let expectedNames = [ "TargetFunctionId"; "ExpectedBaseSnapshotId"; "ExpectedCurrentResultNodeId"; "Replacement" ]
        if names <> expectedNames then
            failwithf "Structural request authority surface changed. Expected %A, got %A" expectedNames names

        let forbiddenPayloadTypes =
            [ typeof<Snapshot>; typeof<ValueGraph>; typeof<SemanticNode>; typeof<SemanticNodeKind> ]
        if fields |> List.exists (fun (_, fieldType) -> forbiddenPayloadTypes |> List.exists (fun forbidden -> forbidden = fieldType)) then
            failwithf "Structural request exposes a raw semantic payload: %A" fields

        let cases = FSharpType.GetUnionCases(typeof<Float64ExpressionRecipe>)
        let caseNames = cases |> Array.map (fun case -> case.Name) |> Array.toList
        if caseNames <> [ "Parameter"; "Literal"; "Add" ] then
            failwithf "Float64 recipe authority surface changed: %A" caseNames

        let parameterCase = cases |> Array.find (fun case -> case.Name = "Parameter")
        let literalCase = cases |> Array.find (fun case -> case.Name = "Literal")
        let addCase = cases |> Array.find (fun case -> case.Name = "Add")
        let parameterFields = parameterCase.GetFields() |> Array.map (fun field -> field.PropertyType) |> Array.toList
        let literalFields = literalCase.GetFields() |> Array.map (fun field -> field.PropertyType) |> Array.toList
        let addFields = addCase.GetFields() |> Array.map (fun field -> field.PropertyType) |> Array.toList

        if parameterFields <> [ typeof<ParameterId> ] then failwithf "Parameter recipe payload changed: %A" parameterFields
        if literalFields <> [ typeof<Float64Bits> ] then failwithf "Literal recipe payload changed: %A" literalFields
        if addFields <> [ typeof<Float64ExpressionRecipe>; typeof<Float64ExpressionRecipe> ] then
            failwithf "Add recipe payload changed: %A" addFields
