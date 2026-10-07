namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module DependencyLocalRepairTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("dependency-repair-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private moneyId = id<TypeTag> "money"
    let private amountId = id<FieldTag> "amount"
    let private readId = id<FunctionTag> "read"
    let private parameterId = id<ParameterTag> "input"
    let private unwrap = function Ok v -> v | Error e -> failwithf "%A" e
    let private library version valueType =
        let input, result = id<GraphNodeTag> "input", id<GraphNodeTag> "read"
        { Snapshot.empty (id<SnapshotTag> version) with
            Types = [ { Id = moneyId; DisplayName = "Money"; Kind = SemanticTypeKind.Product [ { Id = amountId; DisplayName = "amount"; Type = valueType } ] } ]
            Functions = [ { Id = readId; DisplayName = "read"; Parameters = [ { Id = parameterId; DisplayName = "input"; Type = TypeRef.Named moneyId } ]
                            ResultType = valueType; RequiredEffects = []
                            Body = { Nodes = [ { Id = input; Kind = SemanticNodeKind.ParameterReference parameterId }
                                               { Id = result; Kind = SemanticNodeKind.FieldAccess(input, amountId) } ]; ResultNodeId = result } } ] }
    let private publish (session: AuthoringSession) proposal =
        session.Commit((unwrap proposal).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private create (session: AuthoringSession) name resultType body =
        session.SimulateCreateFunction
            { ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = name
              Parameters = [ { DisplayName = "input"; Type = TypeRef.Named moneyId } ]
              ResultType = resultType; RequiredEffects = []; Body = body } |> publish session
        session.Declarations().Functions |> List.find (fun f -> f.DisplayName = name)
    let private call (fn: SemanticFunction) = ExpressionRecipe.DirectCall(fn.Id, [ fn.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ])
    let private repair (fn: SemanticFunction) body =
        { FunctionId = fn.Id; ParameterTypes = fn.Parameters |> List.map (fun p -> { ParameterId = p.Id; Type = p.Type })
          ResultType = stringType; Replacement = body }
    let private withApp run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-local-repair-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let before = library "old" intType
        let after = library "new" stringType
        let oldPath, newPath, appPath = Path.Combine(root, "old.json"), Path.Combine(root, "new.json"), Path.Combine(root, "app.json")
        File.WriteAllText(oldPath, SnapshotCodec.serialize before |> unwrap)
        File.WriteAllText(newPath, SnapshotCodec.serialize after |> unwrap)
        let session = AuthoringSession.createNewWithDependencies appPath [ oldPath ] |> unwrap
        try
            let relay = create session "relay" intType (call before.Functions.Head)
            let entry = create session "entry" intType (call relay)
            let change = AuthoringDependencyChangeRequest.Selection { Sources = [ newPath ]; Replaces = [ StableId.value before.Id ] }
            let repairs = { Fields = []; Operations = []; Functions = [ repair relay (ExpressionRecipe.DirectCall(readId, [ parameterId, ExpressionRecipe.Parameter relay.Parameters.Head.Id ])); repair entry (ExpressionRecipe.DirectCall(relay.Id, [ relay.Parameters.Head.Id, ExpressionRecipe.Parameter entry.Parameters.Head.Id ])) ] }
            run session appPath oldPath newPath change repairs relay entry
        finally
            session.Close()
            Directory.Delete(root, true)
    let private bytes path = File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")

    [<Fact>]
    let ``dependency schema and local transitive signatures repair as one reviewed proposal`` () =
        withApp (fun session path oldPath newPath change repairs relay entry ->
            let saved = bytes path
            let proposal = session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId) |> unwrap
            Assert.Equal(saved, bytes path)
            let view = session.PendingProposalView proposal.ProposalId |> unwrap
            Assert.True(view.ProposedSnapshot.IsSome)
            Assert.Equal<FunctionId list>([ relay.Id; entry.Id ], view.FunctionIds)
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let functions = session.Declarations().Functions
            for fn in [ relay; entry ] do
                let updated = functions |> List.find (fun f -> f.Id = fn.Id)
                Assert.Equal(stringType, updated.ResultType)
                Assert.Equal<ParameterId list>(fn.Parameters |> List.map (fun p -> p.Id), updated.Parameters |> List.map (fun p -> p.Id))
            // The updated import is still read-only after this in-session commit.
            Assert.True(Result.isError(session.SimulateReplaceBody { ExpectedBaseSnapshotId = session.SnapshotId; TargetFunctionId = readId; ExpectedCurrentResultNodeId = (functions |> List.find (fun f -> f.Id = readId)).Body.ResultNodeId; Replacement = ExpressionRecipe.StringLiteral "forbidden" }))
            session.Close()
            File.Delete oldPath
            File.Delete newPath
            let reopened = AuthoringSession.openExisting path |> unwrap
            try Assert.Equal(stringType, (reopened.Declarations().Functions |> List.find (fun f -> f.Id = entry.Id)).ResultType)
            finally reopened.Close())

    [<Fact>]
    let ``partial duplicate imported and incorrect parameter repairs reject without writes`` () =
        withApp (fun session path _ _ change repairs _ _ ->
            let saved = bytes path
            let invalid =
                [ { repairs with Functions = repairs.Functions |> List.take 1 }
                  { repairs with Functions = repairs.Functions @ repairs.Functions }
                  { repairs with Functions = repair (library "new" stringType).Functions.Head (ExpressionRecipe.StringLiteral "bad") :: repairs.Functions }
                  { repairs with Functions = repairs.Functions |> List.map (fun r -> { r with ParameterTypes = [] }) } ]
            for candidate in invalid do
                Assert.True(Result.isError(session.SimulateDependencyUpgrade(change, candidate, session.SnapshotId)))
                Assert.Equal(saved, bytes path))

    [<Fact>]
    let ``local commit invalidates dependency repair while unchanged reviewed proposal commits once`` () =
        withApp (fun session path _ _ change repairs relay _ ->
            let proposal = session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId) |> unwrap
            session.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function relay.Id, "relayRenamed") ] |> publish session
            let saved = bytes path
            Assert.True(Result.isError(session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)))
            Assert.Equal(saved, bytes path)
            let fresh = session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId) |> unwrap
            session.Commit(fresh.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            Assert.True(Result.isError(session.Commit(fresh.ProposalId, CommitPolicy.RejectBlockingObligations))))

    [<Fact>]
    let ``changed selected source refuses reviewed repair without writes`` () =
        withApp (fun session path _ newPath change repairs _ _ ->
            let proposal = session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId) |> unwrap
            File.WriteAllText(newPath, SnapshotCodec.serialize (library "newer" stringType) |> unwrap)
            let saved = bytes path
            Assert.True(Result.isError(session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)))
            Assert.Equal(saved, bytes path))

    [<Fact>]
    let ``local field and operation signature repairs retain field operation and parameter identities`` () =
        withApp (fun session path _ _ change repairs _ _ ->
            session.SimulateCreateType { ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Local"
                                         Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "value"; Type = intType } ] } |> publish session
            session.SimulateCreateEffect { ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Record" } |> publish session
            let effect = session.Declarations().Effects.Head.Id
            session.SimulateCreateCapability
                { ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Ledger"
                  Operations = [ { DisplayName = "record"; Parameters = [ { DisplayName = "value"; Type = intType } ]; ResultType = intType; FailureType = None; EffectId = effect } ] } |> publish session
            let before = session.Declarations()
            let owner = before.Types |> List.find (fun t -> t.DisplayName = "Local")
            let field = match owner.Kind with SemanticTypeKind.Product fields -> fields.Head | _ -> failwith "product"
            let operation = before.Capabilities.Head.Operations.Head
            let complete =
                { repairs with Fields = [ { TypeId = owner.Id; FieldId = field.Id; NewType = stringType } ]
                               Operations = [ { OperationId = operation.Id; ParameterTypes = [ { ParameterId = operation.Parameters.Head.Id; Type = stringType } ]; ResultType = stringType } ] }
            let proposal = session.SimulateDependencyUpgrade(change, complete, session.SnapshotId) |> unwrap
            let saved = bytes path
            let invalid = { complete with Fields = [ { TypeId = moneyId; FieldId = amountId; NewType = intType } ] }
            Assert.True(Result.isError(session.SimulateDependencyUpgrade(change, invalid, session.SnapshotId)))
            Assert.Equal(saved, bytes path)
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let after = session.Declarations()
            let updated = after.Types |> List.find (fun t -> t.Id = owner.Id)
            let updatedField = match updated.Kind with SemanticTypeKind.Product fields -> fields.Head | _ -> failwith "product"
            Assert.Equal(field.Id, updatedField.Id)
            Assert.Equal(stringType, updatedField.Type)
            let updatedOperation = after.Capabilities.Head.Operations.Head
            Assert.Equal(operation.Id, updatedOperation.Id)
            Assert.Equal(operation.Parameters.Head.Id, updatedOperation.Parameters.Head.Id)
            Assert.Equal(stringType, updatedOperation.Parameters.Head.Type)
            Assert.Equal(stringType, updatedOperation.ResultType))

    [<Fact>]
    let ``graph-write failure restores old pin and proposal can retry and reopen`` () =
        withApp (fun session path _ _ change repairs _ _ ->
            let saved = bytes path
            let proposal = session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId) |> unwrap
            SessionStorageFaults.failReplacement <- fun target -> target = path
            try Assert.True(Result.isError(session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)))
            finally SessionStorageFaults.failReplacement <- fun _ -> false
            Assert.Equal(saved, bytes path)
            let probe = session.Status() |> unwrap
            Assert.Equal(probe.SnapshotId, (session.PendingProposalView proposal.ProposalId |> unwrap).BaseSnapshotId)
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.Close()
            (AuthoringSession.openExisting path |> unwrap).Close())
