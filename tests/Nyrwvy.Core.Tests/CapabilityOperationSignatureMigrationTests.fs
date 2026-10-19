namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module CapabilityOperationSignatureMigrationTests =
    let private unwrap = function Ok value -> value | Error e -> failwithf "%A" e
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private fixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-operation-migration-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "program.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            let commit (p: AuthoringProposal) = session.Commit(p.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let current () = (session.Status() |> unwrap).SnapshotId
            session.SimulateCreateEffect { ExpectedBaseSnapshotId = current (); DisplayName = "Read" } |> unwrap |> commit
            let effect = session.Declarations().Effects.Head.Id
            session.SimulateCreateCapability
                { ExpectedBaseSnapshotId = current (); DisplayName = "Values"
                  Operations = [ { DisplayName = "read"; Parameters = [ { DisplayName = "input"; Type = intType } ]; ResultType = intType; FailureType = None; EffectId = effect } ] }
            |> unwrap |> commit
            let cap = session.Declarations().Capabilities.Head
            let op = cap.Operations.Head
            let create name parameters result effects body =
                session.SimulateCreateFunction
                    { ExpectedBaseSnapshotId = current (); DisplayName = name; Parameters = parameters
                      TypeParameters = None
                      ResultType = result; RequiredEffects = effects; Body = body }
                |> unwrap |> commit
            create "implementation" [ { DisplayName = "input"; Type = intType } ] intType [] (ExpressionRecipe.ParameterSlot 0)
            let implementation = session.Declarations().Functions.Head
            create "reference" [] (TypeRef.Function([ intType ], intType)) [] (ExpressionRecipe.FunctionReference implementation.Id)
            create "construct" [] (TypeRef.Capability cap.Id) [] (ExpressionRecipe.CapabilityConstruct(cap.Id, [ op.Id, implementation.Id ]))
            create "invoke" [ { DisplayName = "values"; Type = TypeRef.Capability cap.Id }; { DisplayName = "input"; Type = intType } ] intType [ effect ]
                (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, op.Id, [ op.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 1 ]))
            let invoke = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "invoke")
            create "caller" [ { DisplayName = "values"; Type = TypeRef.Capability cap.Id }; { DisplayName = "input"; Type = intType } ] intType [ effect ]
                (ExpressionRecipe.DirectCall(invoke.Id, [ invoke.Parameters[0].Id, ExpressionRecipe.ParameterSlot 0; invoke.Parameters[1].Id, ExpressionRecipe.ParameterSlot 1 ]))
            let caller = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "caller")
            let construct = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "construct")
            create "run" [] intType [ effect ]
                (ExpressionRecipe.DirectCall(caller.Id, [ caller.Parameters[0].Id, ExpressionRecipe.DirectCall(construct.Id, []); caller.Parameters[1].Id, ExpressionRecipe.Int64Literal 7L ]))
            run session path cap op
        finally Directory.Delete(root, true)

    let private request (session: AuthoringSession) (cap: SemanticCapability) (op: SemanticCapabilityOperation) argumentType resultType =
        let functions = session.Declarations().Functions
        let find name = functions |> List.find (fun f -> f.DisplayName = name)
        let invoke = find "invoke"
        let impl = find "implementation"
        let repair (fn: SemanticFunction) body =
            { FunctionId = fn.Id
              ParameterTypes = fn.Parameters |> List.map (fun p -> { ParameterId = p.Id; Type = if p.Type = intType then argumentType else p.Type })
              ResultType = if fn.ResultType = intType then resultType else fn.ResultType
              Replacement = body }
        let value = if argumentType = resultType then ExpressionRecipe.Parameter impl.Parameters.Head.Id else ExpressionRecipe.StringLiteral "result"
        { ExpectedBaseSnapshotId = (session.Status() |> unwrap).SnapshotId
          OperationId = op.Id
          ParameterTypes = [ { ParameterId = op.Parameters.Head.Id; Type = argumentType } ]
          ResultType = resultType
          FunctionRepairs =
            [ repair impl value
              { FunctionId = (find "reference").Id; ParameterTypes = []; ResultType = TypeRef.Function([ argumentType ], resultType); Replacement = ExpressionRecipe.FunctionReference impl.Id }
              repair (find "construct") (ExpressionRecipe.CapabilityConstruct(cap.Id, [ op.Id, impl.Id ]))
              repair invoke (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.Parameter invoke.Parameters[0].Id, op.Id, [ op.Parameters.Head.Id, ExpressionRecipe.Parameter invoke.Parameters[1].Id ]))
              repair (find "caller") (ExpressionRecipe.DirectCall(invoke.Id, [ invoke.Parameters[0].Id, ExpressionRecipe.Parameter (find "caller").Parameters[0].Id; invoke.Parameters[1].Id, ExpressionRecipe.Parameter (find "caller").Parameters[1].Id ]))
              repair (find "run") (ExpressionRecipe.DirectCall((find "caller").Id,
                  [ (find "caller").Parameters[0].Id, ExpressionRecipe.DirectCall((find "construct").Id, [])
                    (find "caller").Parameters[1].Id, (if argumentType = stringType then ExpressionRecipe.StringLiteral "7" else ExpressionRecipe.Int64Literal 7L) ])) ] }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``operation argument and result evolution repairs implementations invokers and callers atomically`` changeArgument =
        fixture (fun session path cap op ->
            let before = session.Declarations()
            let req = request session cap op (if changeArgument then stringType else intType) stringType
            let published = File.ReadAllBytes path
            let proposal = session.SimulateCapabilityOperationSignatureMigration req |> unwrap
            Assert.Equal<byte>(published, File.ReadAllBytes path)
            match proposal.Review with
            | AuthoringProposalReview.CapabilityOperationSignatureMigration review ->
                Assert.Equal(6, review.ResultingChange.BodyRewrites.Length)
                Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
            | _ -> failwith "Wrong review kind"
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let after = session.Declarations()
            Assert.Equal(cap.Id, after.Capabilities.Head.Id)
            Assert.Equal(op.Id, after.Capabilities.Head.Operations.Head.Id)
            Assert.Equal(op.Parameters.Head.Id, after.Capabilities.Head.Operations.Head.Parameters.Head.Id)
            for fn in before.Functions do
                let updated = after.Functions |> List.find (fun f -> f.Id = fn.Id)
                Assert.Equal<ParameterId list>(fn.Parameters |> List.map (fun p -> p.Id), updated.Parameters |> List.map (fun p -> p.Id))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(stringType, reopened.Declarations().Capabilities.Head.Operations.Head.ResultType)
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            Assert.Empty(SnapshotValidator.validate snapshot))

    [<Fact>]
    let ``incomplete mistyped and stale operation repairs retain published bytes`` () =
        fixture (fun session path cap op ->
            let req = request session cap op stringType stringType
            let bytes = File.ReadAllBytes path
            Assert.True(session.SimulateCapabilityOperationSignatureMigration { req with FunctionRepairs = req.FunctionRepairs.Tail } |> Result.isError)
            let bad = { req.FunctionRepairs.Head with Replacement = ExpressionRecipe.Int64Literal 0L }
            Assert.True(session.SimulateCapabilityOperationSignatureMigration { req with FunctionRepairs = bad :: req.FunctionRepairs.Tail } |> Result.isError)
            Assert.True(session.SimulateCapabilityOperationSignatureMigration { req with ParameterTypes = [] } |> Result.isError)
            let first = session.SimulateCapabilityOperationSignatureMigration req |> unwrap
            let second = session.SimulateCapabilityOperationSignatureMigration req |> unwrap
            Assert.Equal<byte>(bytes, File.ReadAllBytes path)
            session.Commit(first.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let latest = File.ReadAllBytes path
            Assert.True(session.Commit(second.ProposalId, CommitPolicy.RejectBlockingObligations) |> Result.isError)
            Assert.True(session.SimulateCapabilityOperationSignatureMigration req |> Result.isError)
            Assert.Equal<byte>(latest, File.ReadAllBytes path))

    [<Fact>]
    let ``imported capability and implementation repairs are refused and saved pins remain usable`` () =
        fixture (fun source path cap op ->
            let consumerPath = Path.Combine(Path.GetDirectoryName path, "consumer.json")
            use consumer = AuthoringSession.createNewWithDependency consumerPath (Some path) |> unwrap
            let bytes = File.ReadAllBytes consumerPath
            let sidecar = File.ReadAllBytes(consumerPath + ".dependency.json")
            let req = request consumer cap op stringType stringType
            let refusal = consumer.SimulateCapabilityOperationSignatureMigration req
            match refusal with
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationModified")
            | other -> failwithf "Imported migration was not refused: %A" other
            Assert.Equal<byte>(bytes, File.ReadAllBytes consumerPath)
            Assert.Equal<byte>(sidecar, File.ReadAllBytes(consumerPath + ".dependency.json"))
            source.Close()
            File.Move(path, path + ".unavailable")
            consumer.Close()
            use reopened = AuthoringSession.openExisting consumerPath |> unwrap
            Assert.Equal(intType, reopened.Declarations().Capabilities.Head.Operations.Head.ResultType))

    [<Fact>]
    let ``unused imported operation cannot migrate even without imported function rewrites`` () =
        fixture (fun source path cap op ->
            let graph = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let library = Path.Combine(Path.GetDirectoryName path, "capability-only.json")
            File.WriteAllText(library, SnapshotCodec.serialize { graph with Functions = [] } |> unwrap)
            let consumerPath = Path.Combine(Path.GetDirectoryName path, "capability-consumer.json")
            use consumer = AuthoringSession.createNewWithDependency consumerPath (Some library) |> unwrap
            let bytes = File.ReadAllBytes consumerPath
            let req: AgentCapabilityOperationSignatureMigrationRequest =
                { ExpectedBaseSnapshotId = (consumer.Status() |> unwrap).SnapshotId
                  OperationId = op.Id
                  ParameterTypes = [ { ParameterId = op.Parameters.Head.Id; Type = stringType } ]
                  ResultType = stringType
                  FunctionRepairs = [] }
            match consumer.SimulateCapabilityOperationSignatureMigration req with
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationModified")
            | other -> failwithf "Imported capability edit accepted: %A" other
            Assert.Equal<byte>(bytes, File.ReadAllBytes consumerPath))

    [<Fact; Trait("Suite", "Extended")>]
    let ``repaired constructed implementation emits and executes through the common runner`` () =
        fixture (fun session path cap op ->
            let proposal = session.SimulateCapabilityOperationSignatureMigration (request session cap op stringType stringType) |> unwrap
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let entry = reopened.Declarations().Functions |> List.find (fun f -> f.DisplayName = "run")
            let result = PureRun.run reopened entry.Id [] (Some(PureRunValue.String "7")) |> unwrap
            Assert.Equal(Some true, result.Matches))

    [<Fact>]
    let ``exact ID wire operation migration publishes an unused local operation`` () =
        fixture (fun source path cap op ->
            let graph = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let target = Path.Combine(Path.GetDirectoryName path, "wire.json")
            File.WriteAllText(target, SnapshotCodec.serialize { graph with Functions = [] } |> unwrap)
            use session = AuthoringSession.openExisting target |> unwrap
            let request =
                $"""{{"id":"signature","op":"simulateCapabilityOperationSignatureMigration","operationId":"{StableId.value op.Id}","parameterTypes":[{{"parameterId":"{StableId.value op.Parameters.Head.Id}","type":{{"kind":"primitive","primitive":"string"}}}}],"resultType":{{"kind":"primitive","primitive":"string"}},"functionRepairs":[],"structuredReview":true}}"""
            use response = System.Text.Json.JsonDocument.Parse(AuthoringProtocol.executeLine session request)
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.Equal("capabilityOperationSignatureMigration", result.GetProperty("review").GetProperty("kind").GetString())
            session.Commit(result.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            Assert.Equal(stringType, session.Declarations().Capabilities.Head.Operations.Head.ResultType))

    [<Fact>]
    let ``unrelated signature edits cannot seed an operation repair set`` () =
        fixture (fun session path cap op ->
            let unrelated = session.SimulateCreateFunction
                                { ExpectedBaseSnapshotId = (session.Status() |> unwrap).SnapshotId
                                  TypeParameters = None
                                  DisplayName = "unrelated"; Parameters = []; ResultType = intType
                                  RequiredEffects = []; Body = ExpressionRecipe.Int64Literal 0L } |> unwrap
            session.Commit(unrelated.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let req = request session cap op stringType stringType
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "unrelated")
            let repair = { FunctionId = fn.Id; ParameterTypes = []; ResultType = stringType; Replacement = ExpressionRecipe.StringLiteral "unrelated" }
            let bytes = File.ReadAllBytes path
            match session.SimulateCapabilityOperationSignatureMigration { req with FunctionRepairs = repair :: req.FunctionRepairs } with
            | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected issues)) ->
                Assert.Contains(issues, fun issue -> issue.Code = "OperationSignatureMigrationRepairMismatch")
            | other -> failwithf "Unrelated signature migration accepted: %A" other
            Assert.Equal<byte>(bytes, File.ReadAllBytes path))

    [<Fact>]
    let ``by name repairs retain existing generic parameter identities`` () =
        fixture (fun source path cap op ->
            let graph = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let target = Path.Combine(Path.GetDirectoryName path, "generic-wire.json")
            File.WriteAllText(target, SnapshotCodec.serialize { graph with Functions = [] } |> unwrap)
            use session = AuthoringSession.openExisting target |> unwrap
            let proposal = session.SimulateCreateFunction
                              { ExpectedBaseSnapshotId = (session.Status() |> unwrap).SnapshotId
                                TypeParameters = None
                                DisplayName = "genericInvoke"
                                Parameters = [ { DisplayName = "values"; Type = TypeRef.Capability cap.Id }
                                               { DisplayName = "extra"; Type = TypeRef.TypeParameter(StableId.createForTesting<TypeParameterTag> "T") } ]
                                ResultType = intType; RequiredEffects = [ op.EffectId ]
                                Body = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, op.Id, [ op.Parameters.Head.Id, ExpressionRecipe.Int64Literal 7L ]) } |> unwrap
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let before = session.Declarations().Functions.Head
            let request = """{"id":"generic","op":"simulateCapabilityOperationSignatureMigrationByName","capability":"Values","operation":"read","parameterTypes":[{"parameter":"input","type":{"kind":"primitive","primitive":"int64"}}],"resultType":{"kind":"primitive","primitive":"string"},"functionRepairs":[{"function":"genericInvoke","parameterTypes":[{"parameter":"values","type":{"kind":"capability","name":"Values"}},{"parameter":"extra","type":{"kind":"typeParameter","name":"T"}}],"resultType":{"kind":"primitive","primitive":"string"},"replacement":{"kind":"bind","value":{"kind":"optionNone","itemType":{"kind":"typeParameter","name":"T"}},"body":{"kind":"capabilityInvoke","capabilityValue":{"kind":"parameter","name":"values"},"capability":"Values","operation":"read","arguments":[{"parameter":"input","value":{"kind":"int64Literal","value":"7"}}]}}}],"structuredReview":true}"""
            use response = System.Text.Json.JsonDocument.Parse(AuthoringProtocol.executeLine session request)
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            session.Commit(response.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let after = session.Declarations().Functions.Head
            Assert.Equal<(TypeParameterId * string) list>(TypeParameters.ofFunction before, TypeParameters.ofFunction after)
            Assert.Contains(ValueGraph.collectNodes after.Body, fun node ->
                match node.Kind with
                | SemanticNodeKind.OptionNone(TypeRef.TypeParameter(id)) -> id = fst (TypeParameters.ofFunction before).Head
                | _ -> false))
