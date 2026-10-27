namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

[<Collection("Session storage faults")>]
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
            Types = [ { Exported = true; TypeParameters = []; Id = moneyId; DisplayName = "Money"; Kind = SemanticTypeKind.Product [ { Id = amountId; DisplayName = "amount"; Type = valueType } ] } ]
            Functions = [ { MustNotWait = false; Exported = true; Id = readId; DisplayName = "read"; Parameters = [ { Id = parameterId; DisplayName = "input"; Type = TypeRef.Named(moneyId, []) } ]
                            TypeParameters = []
                            ResultType = valueType; RequiredEffects = []
                            Body = { Nodes = [ { Id = input; Kind = SemanticNodeKind.ParameterReference parameterId }
                                               { Id = result; Kind = SemanticNodeKind.FieldAccess(input, amountId) } ]; ResultNodeId = result } } ] }
    let private publish (session: AuthoringSession) proposal =
        session.Commit((unwrap proposal).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private create (session: AuthoringSession) name resultType body =
        session.SimulateCreateFunction
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = name
              TypeParameters = None
              Parameters = [ { DisplayName = "input"; Type = TypeRef.Named(moneyId, []) } ]
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
            session.SimulateCreateType { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Local"
                                         Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "value"; Type = intType } ] } |> publish session
            session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Record" } |> publish session
            let effect = session.Declarations().Effects.Head.Id
            session.SimulateCreateCapability
                { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Ledger"
                  Operations = [ { MayWait = false; DisplayName = "record"; Parameters = [ { DisplayName = "value"; Type = intType } ]; ResultType = intType; FailureType = None; EffectId = effect } ] } |> publish session
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
            Assert.False(File.Exists(path + ".dependency.json.previous"))
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            Assert.False(File.Exists(path + ".dependency.json.previous"))
            session.Close()
            (AuthoringSession.openExisting path |> unwrap).Close())

    [<Fact>]
    let ``ordinary by-name and exact-ID protocol repairs validate commit and execute`` () =
        for named in [ true; false ] do
            for paired in [ true; false ] do
                withApp (fun session path _ newPath _ _ relay entry ->
                    let serialize value = JsonSerializer.Serialize value
                    let parse text = use doc = JsonDocument.Parse(text: string) in doc.RootElement.Clone()
                    let request = System.Collections.Generic.Dictionary<string,obj>()
                    request["id"] <- box "wire"
                    request["op"] <- box (if named then "simulateDependencyUpgradeByName" else "simulateDependencyUpgrade")
                    let sourceId = StableId.value (library "old" intType).Id
                    if paired then request["upgrades"] <- box [ {| source = newPath; replaces = sourceId |} ]
                    else request["selection"] <- box {| sources = [ newPath ]; replaces = [ sourceId ] |}
                    let refType = if named then parse """{"kind":"named","name":"Money"}""" else
                                  parse (serialize {| kind = "named"; typeId = StableId.value moneyId |})
                    let resultType = {| kind = "primitive"; primitive = "string" |}
                    let functions =
                        [ relay, (library "old" intType).Functions.Head; entry, relay ]
                        |> List.map (fun (owner, callee) ->
                            if named then
                                box {| ``function`` = owner.DisplayName; parameterTypes = [ {| parameter = "input"; ``type`` = refType |} ]; resultType = resultType
                                       replacement = {| kind = "directCall"; ``function`` = callee.DisplayName; arguments = [ {| parameter = "input"; value = {| kind = "parameter"; name = "input" |} |} ] |} |}
                            else
                                box {| functionId = StableId.value owner.Id; parameterTypes = [ {| parameterId = StableId.value owner.Parameters.Head.Id; ``type`` = refType |} ]; resultType = resultType
                                       replacement = {| kind = "directCall"; functionId = StableId.value callee.Id; arguments = [ {| parameterId = StableId.value callee.Parameters.Head.Id; value = {| kind = "parameter"; parameterId = StableId.value owner.Parameters.Head.Id |} |} ] |} |})
                    request["repairs"] <- box {| functions = functions |}
                    let saved = bytes path
                    let response = AuthoringProtocol.executeLine session (serialize request) |> parse
                    Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
                    Assert.Equal(saved, bytes path)
                    let proposalId = response.GetProperty("result").GetProperty("proposalId").GetString()
                    let commit = AuthoringProtocol.executeLine session (serialize {| id = "commit"; op = "commit"; proposalId = proposalId |}) |> parse
                    Assert.True(commit.GetProperty("ok").GetBoolean(), commit.ToString())
                    let actual = PureRun.run session entry.Id [ PureRunValue.Product(moneyId, [], [ amountId, PureRunValue.String "123" ]) ] (Some(PureRunValue.String "123")) |> unwrap
                    Assert.Equal(Some true, actual.Matches)
                    let updated = session.Declarations().Functions |> List.find (fun f -> f.Id = entry.Id)
                    Assert.Equal<ParameterId list>(entry.Parameters |> List.map (fun p -> p.Id), updated.Parameters |> List.map (fun p -> p.Id)))

    [<Fact>]
    let ``dependency repair protocol rejects ambiguous malformed and incomplete requests without writes`` () =
        withApp (fun session path _ newPath _ _ _ _ ->
            let serialize value = JsonSerializer.Serialize value
            let selection = serialize {| sources = [ newPath ]; replaces = [ StableId.value (library "old" intType).Id ] |}
            let source = serialize newPath
            let target = serialize (StableId.value (library "old" intType).Id)
            let saved = bytes path
            let requests =
                [ $"""{{"id":"bad","op":"simulateDependencyUpgradeByName","repairs":{{}},"selection":{selection},"upgrades":[{{"source":{source},"replaces":{target}}}]}}"""
                  """{"id":"bad","op":"simulateDependencyUpgradeByName","repairs":{}}"""
                  $"""{{"id":"bad","op":"simulateDependencyUpgradeByName","selection":{selection}}}"""
                  $"""{{"id":"bad","op":"simulateDependencyUpgradeByName","selection":{selection},"repairs":{{}},"unknown":true}}"""
                  $"""{{"id":"bad","op":"simulateDependencyUpgradeByName","selection":{selection},"repairs":{{"unknown":[]}}}}"""
                  $"""{{"id":"bad","op":"simulateDependencyUpgradeByName","selection":{selection},"repairs":{{}}}}""" ]
            for request in requests do
                use response = JsonDocument.Parse(AuthoringProtocol.executeLine session request)
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Equal(saved, bytes path))

    [<Fact>]
    let ``sidecar-first failure keeps old files and rollback failure invalidates on next authoritative check`` () =
        withApp (fun session path _ _ change repairs _ _ ->
            let saved = bytes path
            let proposal = session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId) |> unwrap
            let sidecar = path + ".dependency.json"
            SessionStorageFaults.failReplacement <- fun target -> target = sidecar
            try Assert.True(Result.isError(session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)))
            finally SessionStorageFaults.failReplacement <- fun _ -> false
            Assert.Equal(saved, bytes path)
            session.VerifyPersistedSource() |> unwrap |> ignore
            let mutable sidecarAttempts = 0
            SessionStorageFaults.failReplacement <- fun target ->
                if target = sidecar then sidecarAttempts <- sidecarAttempts + 1; sidecarAttempts > 1
                else target = path
            try
                match session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) with
                | Error(AuthoringSessionError.IoFailure message) -> Assert.Contains("Restoring", message)
                | other -> failwithf "%A" other
            finally SessionStorageFaults.failReplacement <- fun _ -> false
            Assert.Equal<byte>(fst saved, File.ReadAllBytes path)
            Assert.True(Result.isError(session.VerifyPersistedSource()))
            match session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) with
            | Error AuthoringSessionError.SessionInvalidated -> ()
            | other -> failwithf "%A" other
            session.Close()
            // The new sidecar sits beside the old snapshot; the kept previous sidecar is named, not restored.
            let previous = sidecar + ".previous"
            Assert.Equal<byte[]>(snd saved, File.ReadAllBytes previous)
            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.DependencyFailure message) -> Assert.Contains(previous, message)
            | Ok reopened -> reopened.Close(); failwith "An inconsistent pair was opened."
            | Error error -> failwithf "Unexpected error for an inconsistent pair: %A" error
            Assert.Equal<byte[]>(snd saved, File.ReadAllBytes previous)
            File.Move(previous, sidecar, true)
            (AuthoringSession.openExisting path |> unwrap).Close())

    [<Fact>]
    let ``new imported identity collision rejects before publishing any local repair`` () =
        withApp (fun session path _ newPath change repairs _ entry ->
            let updated = library "new" stringType
            let collision = { entry with ResultType = stringType
                                         Body = { Nodes = [ { Id = id<GraphNodeTag> "collision"; Kind = SemanticNodeKind.StringLiteral "imported" } ]; ResultNodeId = id<GraphNodeTag> "collision" } }
            File.WriteAllText(newPath, SnapshotCodec.serialize { updated with Functions = collision :: updated.Functions } |> unwrap)
            let saved = bytes path
            Assert.True(Result.isError(session.SimulateDependencyUpgrade(change, repairs, session.SnapshotId)))
            Assert.Equal(saved, bytes path))

    [<Fact>]
    let ``by-name dependency repair retains generic owner and nested recipe type parameter identity`` () =
        withApp (fun session _ _ newPath _ _ relay entry ->
            let genericType = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "T")
            session.SimulateCreateFunction
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "generic"
                  TypeParameters = None
                  Parameters = [ { DisplayName = "input"; Type = TypeRef.Named(moneyId, []) }; { DisplayName = "extra"; Type = genericType } ]
                  ResultType = intType; RequiredEffects = []; Body = ExpressionRecipe.DirectCall(readId, [ parameterId, ExpressionRecipe.ParameterSlot 0 ]) } |> publish session
            let before = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "generic")
            let parse text = use doc = JsonDocument.Parse(text: string) in doc.RootElement.Clone()
            let body callee = {| kind = "directCall"; ``function`` = callee; arguments = [ {| parameter = "input"; value = {| kind = "parameter"; name = "input" |} |} ] |}
            let parameter = {| parameter = "input"; ``type`` = parse """{"kind":"named","name":"Money"}""" |}
            let functions =
                [ box {| ``function`` = relay.DisplayName; parameterTypes = [ parameter ]; resultType = {| kind = "primitive"; primitive = "string" |}; replacement = body "read" |}
                  box {| ``function`` = entry.DisplayName; parameterTypes = [ parameter ]; resultType = {| kind = "primitive"; primitive = "string" |}; replacement = body "relay" |}
                  box {| ``function`` = "generic"; parameterTypes = [ parameter; {| parameter = "extra"; ``type`` = parse """{"kind":"typeParameter","name":"T"}""" |} ]
                         resultType = {| kind = "primitive"; primitive = "string" |}
                         replacement = {| kind = "bind"; value = {| kind = "optionNone"; itemType = {| kind = "typeParameter"; name = "T" |} |}; body = body "read" |} |} ]
            let request =
                JsonSerializer.Serialize
                    {| id = "generic"; op = "simulateDependencyUpgradeByName"
                       upgrades = [ {| source = newPath; replaces = StableId.value (library "old" intType).Id |} ]
                       repairs = {| functions = functions |} |}
            let response = AuthoringProtocol.executeLine session request |> parse
            Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
            session.Commit(response.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let after = session.Declarations().Functions |> List.find (fun f -> f.Id = before.Id)
            Assert.Equal<(TypeParameterId * string) list>(TypeParameters.ofFunction before, TypeParameters.ofFunction after)
            Assert.Contains(ValueGraph.collectNodes after.Body, fun n ->
                match n.Kind with SemanticNodeKind.OptionNone(TypeRef.TypeParameter(t)) -> t = fst (TypeParameters.ofFunction before).Head | _ -> false))
