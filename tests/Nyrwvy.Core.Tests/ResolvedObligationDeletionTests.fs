namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ResolvedObligationDeletionTests =
    type private Side =
        { Callee: SemanticFunction; Caller: SemanticFunction; Call: GraphNodeId
          Value: GraphNodeId; Parameter: ParameterId; Obligation: ObligationId }

    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private side name =
        let calleeId, callerId = StableIdAllocator.newFunctionId(), StableIdAllocator.newFunctionId()
        let unitId, valueId, callId = StableIdAllocator.newGraphNodeId(), StableIdAllocator.newGraphNodeId(), StableIdAllocator.newGraphNodeId()
        let unitType = TypeRef.Primitive PrimitiveType.Unit
        { Callee = { MustNotWait = false; Exported = true; Id=calleeId; DisplayName=name+"Callee"; Parameters=[]; ResultType=unitType; RequiredEffects=[]
                     TypeParameters = []
                     Body={Nodes=[{Id=unitId;Kind=SemanticNodeKind.UnitLiteral}];ResultNodeId=unitId} }
          Caller = { MustNotWait = false; Exported = true; Id=callerId; DisplayName=name+"Caller"; Parameters=[]; ResultType=unitType; RequiredEffects=[]
                     TypeParameters = []
                     Body={Nodes=[{Id=valueId;Kind=SemanticNodeKind.Int64Literal 1L};{Id=callId;Kind=SemanticNodeKind.DirectCall(calleeId,[])}];ResultNodeId=callId} }
          Call=callId; Value=valueId; Parameter=StableIdAllocator.newParameterId(); Obligation=StableIdAllocator.newObligationId() }

    let private request (snapshot: Snapshot) operations : TransactionRequest =
        { Id=StableIdAllocator.newTransactionId(); BaseSnapshotId=snapshot.Id
          ProposedSnapshotId=StableIdAllocator.newSnapshotId(); Operations=operations }

    let private simulate snapshot operations =
        match Transaction.simulate snapshot (request snapshot operations) with
        | SimulationResult.Proposed value -> value
        | SimulationResult.Rejected diagnostics -> failwithf "%A" diagnostics

    let private fixture () =
        let left, right = side "left", side "right"
        let initial = { Snapshot.empty (StableIdAllocator.newSnapshotId()) with Functions=[left.Callee;left.Caller;right.Callee;right.Caller] }
        let add side = SemanticOperation.AddFunctionParameter {TargetFunctionId=side.Callee.Id;NewParameterId=side.Parameter;NewParameterDisplayName="input";NewParameterType=TypeRef.Primitive PrimitiveType.Int64;CallSitePlans=[LeaveMissing(side.Call,side.Obligation)]}
        let resolve side = SemanticOperation.ResolveMissingArgument {TargetObligationId=side.Obligation;ResolutionValueNodeId=side.Value}
        let opened = (simulate initial [add left;add right]).ProposedSnapshot
        let leftResolved = (simulate opened [resolve left]).ProposedSnapshot
        let resolved = (simulate leftResolved [resolve right]).ProposedSnapshot
        left, right, opened, leftResolved, resolved

    [<Fact>]
    let ``reviewed deletion retires exactly the removed body's resolved records and preserves history`` () =
        let left, right, _, _, before = fixture ()
        let source = SnapshotCodec.serialize before |> unwrap
        let workspace = SemanticWorkspace.create before |> unwrap
        let change = request before [SemanticOperation.DeleteFunction left.Caller.Id;SemanticOperation.DeleteFunction left.Callee.Id]
        let preview =
            match SemanticWorkspace.simulate workspace change with
            | WorkspaceSimulationResult.Preview value -> value
            | other -> failwithf "%A" other
        let removed = preview.Simulation.Diff.FunctionsRemoved |> List.find (fun item -> item.FunctionId=left.Caller.Id)
        Assert.Equal<MissingArgumentObligation list>(before.Obligations |> List.filter (fun item -> item.Id=left.Obligation), removed.RetiredObligations)
        Assert.Empty((preview.Simulation.Diff.FunctionsRemoved |> List.find (fun item -> item.FunctionId=left.Callee.Id)).RetiredObligations)
        let after = preview.Simulation.ProposedSnapshot
        Assert.Equal<MissingArgumentObligation list>(before.Obligations |> List.filter (fun item -> item.Id=right.Obligation),after.Obligations)
        Assert.Equal<SemanticFunction list>(before.Functions |> List.filter (fun item -> item.Id=right.Caller.Id || item.Id=right.Callee.Id),after.Functions)
        Assert.Empty(SnapshotValidator.validate after)
        Assert.Equal(Ok SemanticReadiness.Complete,SnapshotReadiness.evaluate after)
        match SemanticWorkspace.commitReviewed workspace change preview.Receipt with
        | WorkspaceCommitResult.Committed(next,_) ->
            Assert.Equal(before, List.head (SemanticWorkspace.previousSnapshots next))
            let current = SemanticWorkspace.currentSnapshot next
            let encoded=SnapshotCodec.serialize current |> unwrap
            Assert.Equal(encoded,encoded |> SnapshotCodec.deserialize |> unwrap |> SnapshotCodec.serialize |> unwrap)
        | other -> failwithf "%A" other
        Assert.Equal(source,SnapshotCodec.serialize before |> unwrap)
        // The receipt is bound to the exact ordered request, so it never authorizes a reordered one.
        let reversed = { change with Operations=List.rev change.Operations }
        match SemanticWorkspace.commitReviewed workspace reversed preview.Receipt with
        | WorkspaceCommitResult.Rejected diagnostics -> Assert.Contains(diagnostics,fun item -> item.Code="PreviewReceiptRequestMismatch")
        | other -> failwithf "%A" other

    [<Fact>]
    let ``open obligations on a deleted body block the whole batch while unrelated open work survives`` () =
        let left, right, opened, leftResolved, _ = fixture ()
        let before = SnapshotCodec.serialize opened |> unwrap
        match Transaction.simulate opened (request opened [SemanticOperation.DeleteFunction left.Caller.Id]) with
        | SimulationResult.Rejected diagnostics ->
            let item = Assert.Single diagnostics
            Assert.Equal("FunctionHasObligations",item.Code)
            Assert.DoesNotContain("or remove",item.Message)
        | other -> failwithf "%A" other
        Assert.Equal(before,SnapshotCodec.serialize opened |> unwrap)
        let mixed =
            simulate leftResolved
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId=left.Callee.Id; NewParameterId=StableIdAllocator.newParameterId()
                      NewParameterDisplayName="extra"; NewParameterType=TypeRef.Primitive PrimitiveType.Int64
                      CallSitePlans=[LeaveMissing(left.Call,StableIdAllocator.newObligationId())] } ]
            |> fun item -> item.ProposedSnapshot
        let mixedBytes=SnapshotCodec.serialize mixed |> unwrap
        match Transaction.simulate mixed (request mixed [SemanticOperation.DeleteFunction left.Caller.Id]) with
        | SimulationResult.Rejected diagnostics -> Assert.Contains(diagnostics,fun item -> item.Code="FunctionHasObligations")
        | other -> failwithf "%A" other
        Assert.Equal(mixedBytes,SnapshotCodec.serialize mixed |> unwrap)
        let after = (simulate leftResolved [SemanticOperation.DeleteFunction left.Caller.Id]).ProposedSnapshot
        Assert.Equal<ObligationId list>([right.Obligation],after.Obligations |> List.map (fun item -> item.Id))
        Assert.Equal(ObligationStatus.Open,after.Obligations.Head.Status)
        Assert.Empty(SnapshotValidator.validate after)
        // Resolving the specific open choice before deletion is valid in one ordered transaction.
        let combined = simulate opened [SemanticOperation.ResolveMissingArgument {TargetObligationId=left.Obligation;ResolutionValueNodeId=left.Value};SemanticOperation.DeleteFunction left.Caller.Id]
        let retired = (Assert.Single combined.Diff.FunctionsRemoved).RetiredObligations |> Assert.Single
        Assert.Equal(ObligationStatus.Resolved {TransactionId=combined.TransactionId;ValueNodeId=left.Value},retired.Status)

    [<Theory; InlineData(true); InlineData(false)>]
    let ``session deletion reviews expose retirement before commit and reopen without dangling obligations`` named =
        let left, right, _, _, before = fixture ()
        let root = Path.Combine(Path.GetTempPath(),"nyrwvy-obligation-retirement-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path=Path.Combine(root,"workspace.json")
        let old=before.Obligations |> List.find (fun item -> item.Id=left.Obligation)
        try
            File.WriteAllText(path,SnapshotCodec.serialize before |> unwrap)
            use session=AuthoringSession.openExisting path |> unwrap
            let operation = if named then $""""op":"simulateOperationsByName","operations":[{{"kind":"deleteFunction","function":"leftCaller"}},{{"kind":"deleteFunction","function":"leftCallee"}}]""" else $""""op":"simulateOperations","operations":[{{"kind":"deleteFunction","functionId":"{StableId.value left.Caller.Id}"}},{{"kind":"deleteFunction","functionId":"{StableId.value left.Callee.Id}"}}]"""
            let raw=AuthoringProtocol.executeLine session $"""{{"id":"delete",{operation}}}"""
            use response=JsonDocument.Parse raw
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(),raw)
            let result=response.RootElement.GetProperty("result")
            let removed=result.GetProperty("review").GetProperty("diff").GetProperty("functionsRemoved").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("functionId").GetString()=StableId.value left.Caller.Id)
            let record=(removed.GetProperty("retiredObligations"))[0]
            Assert.Equal(StableId.value old.Id,record.GetProperty("obligationId").GetString())
            Assert.Equal(StableId.value old.OriginTransactionId,record.GetProperty("originTransactionId").GetString())
            Assert.Equal(StableId.value old.CallNodeId,record.GetProperty("callNodeId").GetString())
            Assert.Equal(StableId.value old.ParameterId,record.GetProperty("parameterId").GetString())
            match old.Status with
            | ObligationStatus.Resolved resolution ->
                Assert.Equal("resolved",record.GetProperty("status").GetProperty("kind").GetString())
                Assert.Equal(StableId.value resolution.TransactionId,record.GetProperty("status").GetProperty("transactionId").GetString())
                Assert.Equal(StableId.value resolution.ValueNodeId,record.GetProperty("status").GetProperty("valueNodeId").GetString())
            | _ -> failwith "Expected resolved fixture"
            let id=result.GetProperty("proposalId").GetString()
            let view=session.PendingProposalView id |> unwrap
            let _,artifact=ViewerFile.writeProposalReview path None view (fun target -> session.FocusFunction target |> Result.toOption) |> unwrap
            let content=ViewerRendering.page artifact.Content
            Assert.Contains("Retired resolved obligation",content)
            for expected in [old.Id |> StableId.value;old.OriginTransactionId |> StableId.value;old.CallNodeId |> StableId.value;old.ParameterId |> StableId.value] do Assert.Contains(expected,content)
            Assert.Equal(SnapshotCodec.serialize before |> unwrap,File.ReadAllText path)
            session.Commit(id,CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.Close()
            use reopened=AuthoringSession.openExisting path |> unwrap
            let stored=File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            Assert.Equal<ObligationId list>([right.Obligation],stored.Obligations |> List.map (fun item -> item.Id))
            Assert.Equal<FunctionId list>([right.Callee.Id;right.Caller.Id] |> List.sort, reopened.Declarations().Functions |> List.map (fun item -> item.Id) |> List.sort)
            Assert.Empty(SnapshotValidator.validate stored)
        finally
            Directory.Delete(root,true)
