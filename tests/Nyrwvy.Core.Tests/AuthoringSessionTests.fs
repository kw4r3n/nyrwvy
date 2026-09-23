namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module AuthoringSessionTests =
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyrwvy-authoring-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(path) |> ignore
        path

    let private status (session: AuthoringSession) =
        match session.Status() with
        | Ok value -> value
        | Error error -> failwithf "Unexpected authoring-session status failure: %A" error

    let private createSession path =
        match AuthoringSession.createNew path with
        | Ok value -> value
        | Error error -> failwithf "Unexpected authoring-session creation failure: %A" error

    let private openSession path =
        match AuthoringSession.openExisting path with
        | Ok value -> value
        | Error error -> failwithf "Unexpected authoring-session open failure: %A" error

    let private parameter displayName typeRef: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
        { DisplayName = displayName
          Type = typeRef }

    let private creationRequest
        (session: AuthoringSession)
        displayName
        parameters
        resultType
        body
        : AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = (status session).SnapshotId
          DisplayName = displayName
          Parameters = parameters
          ResultType = resultType
          RequiredEffects = []
          Body = body }

    let private simulateCreation
        (session: AuthoringSession)
        (request: AgentExpressionStructuralWorkflow.FunctionCreation.Request)
        : AuthoringProposal =
        match session.SimulateCreateFunction request with
        | Ok value -> value
        | Error error -> failwithf "Unexpected creation simulation failure: %A" error

    let private createdFunction (proposal: AuthoringProposal): SemanticFunction =
        match proposal.Review with
        | AuthoringProposalReview.CreateFunction review -> review.CreatedFunction
        | other -> failwithf "Expected function-creation review, got %A" other

    let private commit
        (session: AuthoringSession)
        (proposal: AuthoringProposal)
        : AuthoringCommitReceipt =
        match session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) with
        | Ok value -> value
        | Error error -> failwithf "Unexpected authoring commit failure: %A" error

    let private functionById functionId (session: AuthoringSession) =
        session.Declarations().Functions
        |> List.find (fun functionEntity -> functionEntity.Id = functionId)

    [<Fact>]
    let ``pinned dependency permits reviewed foreign calls without persisting imported declarations`` () =
        let root = tempDirectory ()
        let policyPath = Path.Combine(root, "policy.json")
        let plannerPath = Path.Combine(root, "planner.json")
        try
            let policyId, policyParameterId =
                use policy = createSession policyPath
                let proposal =
                    creationRequest policy "policy" [ parameter "input" int64Type ] int64Type (ExpressionRecipe.ParameterSlot 0)
                    |> simulateCreation policy
                let created = createdFunction proposal
                commit policy proposal |> ignore
                created.Id, created.Parameters.Head.Id

            let imported =
                match SnapshotCodec.deserialize (File.ReadAllText(policyPath)) with
                | Ok snapshot -> snapshot
                | Error error -> failwithf "Invalid policy fixture: %A" error
            let collision = { Snapshot.empty (StableIdAllocator.newSnapshotId ()) with Functions = imported.Functions }
            match SnapshotCodec.serializeResolved imported collision with
            | Error(SnapshotCodecError.InvalidSnapshot issues) ->
                Assert.Contains(issues, fun issue -> issue.Code = "DuplicateFunctionId")
            | other -> failwithf "Imported identity collision accepted: %A" other

            let plannerId =
                use planner =
                    match AuthoringSession.createNewWithDependency plannerPath (Some policyPath) with
                    | Ok value -> value
                    | Error error -> failwithf "Dependency creation failed: %A" error
                Assert.Contains(planner.Declarations().Functions, fun item -> item.Id = policyId)
                let proposal =
                    creationRequest planner "plan" [ parameter "input" int64Type ] int64Type
                        (ExpressionRecipe.DirectCall(policyId, [ policyParameterId, ExpressionRecipe.ParameterSlot 0 ]))
                    |> simulateCreation planner
                let id = (createdFunction proposal).Id
                commit planner proposal |> ignore
                id

            let localSource = File.ReadAllText(plannerPath)
            Assert.Contains(StableId.value policyId, localSource) // A reference, not a copied declaration.
            use stored = JsonDocument.Parse(localSource)
            Assert.Equal(1, stored.RootElement.GetProperty("functions").GetArrayLength())
            match SnapshotCodec.deserialize localSource with
            | Error(SnapshotCodecError.InvalidShape _) -> ()
            | other -> failwithf "Standalone local snapshot unexpectedly validated: %A" other
            use reopened = openSession plannerPath
            Assert.Contains(reopened.Declarations().Functions, fun item -> item.Id = policyId)
            Assert.Contains(reopened.Declarations().Functions, fun item -> item.Id = plannerId)
            match reopened.ProjectExecution() with
            | Ok program -> Assert.Equal(2, program.Functions.Length)
            | Error error -> failwithf "Dependency did not lower: %A" error
            let original = File.ReadAllText(plannerPath)
            match reopened.SimulateOperations([ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function policyId, "hijacked") ]) with
            | Error error -> failwithf "Unexpected simulation error: %A" error
            | Ok proposal ->
                match reopened.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) with
                | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                    Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationModified")
                | other -> failwithf "Imported declaration edit was published: %A" other
            Assert.Equal(original, File.ReadAllText(plannerPath))
            reopened.Close()

            let manifestPath = plannerPath + ".dependency.json"
            let manifest = File.ReadAllText(manifestPath)
            File.Delete(manifestPath)
            match AuthoringSession.openExisting plannerPath with
            | Error(AuthoringSessionError.SnapshotCodecFailure _) -> ()
            | Ok session -> session.Close(); failwith "Removed dependency manifest was accepted."
            | Error error -> failwithf "Unexpected missing-manifest error: %A" error
            File.WriteAllText(manifestPath, manifest)

            use plannerBeforePolicyUpdate = openSession plannerPath
            let pending =
                creationRequest plannerBeforePolicyUpdate "pending" [] int64Type (ExpressionRecipe.Int64Literal 0L)
                |> simulateCreation plannerBeforePolicyUpdate
            use policy = openSession policyPath
            let change =
                creationRequest policy "changed" [] int64Type (ExpressionRecipe.Int64Literal 0L)
                |> simulateCreation policy
            commit policy change |> ignore
            match plannerBeforePolicyUpdate.Commit(pending.ProposalId, CommitPolicy.RejectBlockingObligations) with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | other -> failwithf "Commit against changed dependency succeeded: %A" other
            plannerBeforePolicyUpdate.Close()
            match AuthoringSession.openExisting plannerPath with
            | Error(AuthoringSessionError.DependencyFailure _) -> ()
            | Ok session -> session.Close(); failwith "Changed dependency was accepted."
            | Error error -> failwithf "Unexpected dependency failure: %A" error
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``durable session creates helper and caller reopens identities and preserves unrelated IDs on nested replacement`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            Assert.Empty(session.Declarations().Functions)

            let helperProposal =
                creationRequest
                    session
                    "increment"
                    [ parameter "value" int64Type ]
                    int64Type
                    (ExpressionRecipe.Int64WrappingAdd(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 1L))
                |> simulateCreation session
            let helperId = (createdFunction helperProposal).Id
            commit session helperProposal |> ignore
            let helper = functionById helperId session

            let callerProposal =
                creationRequest
                    session
                    "call_increment"
                    [ parameter "input" int64Type ]
                    int64Type
                    (ExpressionRecipe.Int64WrappingAdd(
                        ExpressionRecipe.DirectCall(
                            helper.Id,
                            [ helper.Parameters.Head.Id, ExpressionRecipe.ParameterSlot 0 ]
                        ),
                        ExpressionRecipe.Int64Literal 2L
                    ))
                |> simulateCreation session
            let callerId = (createdFunction callerProposal).Id
            commit session callerProposal |> ignore

            let beforeClose = status session
            session.Close()

            use reopened = openSession path
            let reopenedStatus = status reopened
            Assert.Equal(beforeClose.SnapshotId, reopenedStatus.SnapshotId)
            Assert.Equal(beforeClose.SnapshotFingerprint, reopenedStatus.SnapshotFingerprint)
            Assert.Equal(beforeClose.SemanticFingerprint, reopenedStatus.SemanticFingerprint)
            Assert.Equal<FunctionId list>(
                [ helperId; callerId ] |> List.sortBy StableId.value,
                reopened.Declarations().Functions |> List.map (fun item -> item.Id) |> List.sortBy StableId.value
            )

            let caller = functionById callerId reopened
            let rootNodeId = caller.Body.ResultNodeId
            let nodes = ValueGraph.collectNodes caller.Body
            let callNodeId =
                nodes
                |> List.find (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.DirectCall _ -> true
                    | _ -> false)
                |> fun node -> node.Id
            let literalNodeId =
                nodes
                |> List.find (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.Int64Literal 2L -> true
                    | _ -> false)
                |> fun node -> node.Id

            let replacement: AgentExpressionStructuralWorkflow.Targeted.Request =
                { TargetFunctionId = callerId
                  ExpectedBaseSnapshotId = reopenedStatus.SnapshotId
                  TargetNodeId = literalNodeId
                  Replacement = ExpressionRecipe.Int64Literal 3L }
            let proposal =
                match reopened.SimulateReplaceTarget replacement with
                | Ok value -> value
                | Error error -> failwithf "Unexpected targeted replacement simulation failure: %A" error
            commit reopened proposal |> ignore

            let changed = functionById callerId reopened
            Assert.Equal(rootNodeId, changed.Body.ResultNodeId)
            Assert.Contains(ValueGraph.collectNodes changed.Body, fun node -> node.Id = callNodeId)
            Assert.Contains(
                ValueGraph.collectNodes changed.Body,
                fun node ->
                    match node.Kind with
                    | SemanticNodeKind.Int64Literal 3L -> true
                    | _ -> false
            )

            match reopened.ProjectExecution() with
            | Error error -> failwithf "Committed workspace did not lower: %A" error
            | Ok execution -> Assert.Equal((status reopened).SnapshotId, execution.SourceSnapshotId)

            match reopened.EmitCSharp CSharpBackendProfile.empty with
            | Error error -> failwithf "Committed workspace did not emit C#: %A" error
            | Ok artifact -> Assert.Contains("NyFunctions", artifact.Source, StringComparison.Ordinal)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``invalid rejected and stale proposals never advance durable authoritative state`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let created =
                creationRequest session "value" [] int64Type (ExpressionRecipe.Int64Literal 1L)
                |> simulateCreation session
            let functionId = (createdFunction created).Id
            commit session created |> ignore

            let baseStatus = status session
            let targetId = (functionById functionId session).Body.ResultNodeId
            let persistedAtBase = File.ReadAllText(path)

            let invalid: AgentExpressionStructuralWorkflow.Targeted.Request =
                { TargetFunctionId = functionId
                  ExpectedBaseSnapshotId = baseStatus.SnapshotId
                  TargetNodeId = targetId
                  Replacement = ExpressionRecipe.BoolLiteral false }
            match session.SimulateReplaceTarget invalid with
            | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected _)) -> ()
            | result -> failwithf "Expected invalid structural simulation rejection, got %A" result
            Assert.Equal(baseStatus.SnapshotId, (status session).SnapshotId)
            Assert.Equal(persistedAtBase, File.ReadAllText(path))

            let valid replacementValue =
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = functionId
                      ExpectedBaseSnapshotId = (status session).SnapshotId
                      TargetNodeId = targetId
                      Replacement = ExpressionRecipe.Int64Literal replacementValue }
                match session.SimulateReplaceTarget request with
                | Ok proposal -> proposal
                | Error error -> failwithf "Unexpected targeted proposal failure: %A" error

            let rejected = valid 2L
            match session.Reject rejected.ProposalId with
            | Ok receipt -> Assert.Equal(baseStatus.SnapshotId, receipt.RetainedSnapshotId)
            | Error error -> failwithf "Unexpected explicit rejection failure: %A" error
            Assert.Equal(baseStatus.SnapshotId, (status session).SnapshotId)
            Assert.Equal(persistedAtBase, File.ReadAllText(path))

            let first = valid 3L
            let second = valid 4L
            commit session first |> ignore
            let afterFirst = status session
            let persistedAfterFirst = File.ReadAllText(path)

            match session.Commit(second.ProposalId, CommitPolicy.RejectBlockingObligations) with
            | Error(AuthoringSessionError.CommitRejected diagnostics) ->
                Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Code = "StaleBaseSnapshot")
            | result -> failwithf "Expected stale proposal rejection, got %A" result
            Assert.Equal(afterFirst.SnapshotId, (status session).SnapshotId)
            Assert.Equal(persistedAfterFirst, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``session enforces one writer and does not publish after persistence failure or external modification`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let created =
                creationRequest session "value" [] int64Type (ExpressionRecipe.Int64Literal 1L)
                |> simulateCreation session
            let functionId = (createdFunction created).Id
            commit session created |> ignore

            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.LockUnavailable lockedPath) -> Assert.Equal(Path.GetFullPath(path), lockedPath)
            | result -> failwithf "Expected the second writer to be rejected, got %A" result

            let renameProposal =
                match
                    session.SimulateOperations
                        [ AgentOperationIntent.RenameDisplayName(
                            AgentRenameTarget.Function functionId,
                            "value_renamed"
                          ) ]
                with
                | Ok value -> value
                | Error error -> failwithf "Unexpected rename simulation failure: %A" error

            let before = status session
            let sourceBefore = File.ReadAllText(path)
            let blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
            let writeFailure =
                try session.Commit(renameProposal.ProposalId, CommitPolicy.RejectBlockingObligations)
                finally blocker.Dispose()
            match writeFailure with
            | Error(AuthoringSessionError.IoFailure _) -> ()
            | result -> failwithf "Expected persistence I/O failure, got %A" result
            Assert.Equal(before.SnapshotId, (status session).SnapshotId)
            Assert.Equal(sourceBefore, File.ReadAllText(path))

            let externallyChanged = sourceBefore + Environment.NewLine
            File.WriteAllText(path, externallyChanged)
            match session.Commit(renameProposal.ProposalId, CommitPolicy.RejectBlockingObligations) with
            | Error AuthoringSessionError.ExternalModification -> ()
            | result -> failwithf "Expected external-modification rejection, got %A" result
            Assert.Equal(before.SnapshotId, (status session).SnapshotId)
            Assert.Equal(externallyChanged, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private repositoryRoot () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName
            elif isNull directory.Parent then failwith "Could not locate repository root."
            else find directory.Parent
        find (DirectoryInfo(Environment.CurrentDirectory))

    let private startTool (mode: string) (path: string): Process =
        let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
        let toolPath =
            Path.Combine(
                repositoryRoot (),
                "tools",
                "Nyrwvy.AuthoringSession",
                "bin",
                configuration,
                "net10.0",
                "Nyrwvy.AuthoringSession.dll"
            )
        if not (File.Exists toolPath) then failwithf "Authoring-session executable was not built: %s" toolPath
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.ArgumentList.Add(toolPath)
        startInfo.ArgumentList.Add(mode)
        startInfo.ArgumentList.Add("--snapshot")
        startInfo.ArgumentList.Add(path)
        let childProcess = Process.Start(startInfo)
        if isNull childProcess then failwith "Failed to start authoring-session process."
        childProcess

    let private exchange (childProcess: Process) (payload: string): string =
        childProcess.StandardInput.WriteLine(payload)
        childProcess.StandardInput.Flush()
        let response = childProcess.StandardOutput.ReadLine()
        if isNull response then failwithf "Authoring-session process ended early: %s" (childProcess.StandardError.ReadToEnd())
        response

    let private finish (childProcess: Process) =
        childProcess.StandardInput.Close()
        let stderr = childProcess.StandardError.ReadToEndAsync()
        childProcess.WaitForExit()
        let errorText = stderr.Result
        Assert.True(childProcess.ExitCode = 0, $"Authoring-session process failed: {errorText}")
        childProcess.Dispose()

    let private requireOk (response: string): JsonDocument =
        let document = JsonDocument.Parse(response)
        if not (document.RootElement.GetProperty("ok").GetBoolean()) then
            let rendered = document.RootElement.GetRawText()
            document.Dispose()
            failwithf "JSONL request failed: %s" rendered
        document

    [<Fact>]
    let ``JSONL rejects duplicate properties without publishing or consuming the reviewed proposal`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let before = status session
            let persistedBefore = File.ReadAllText(path)
            let proposal =
                creationRequest session "value" [] int64Type (ExpressionRecipe.Int64Literal 1L)
                |> simulateCreation session
            let requests =
                [ """{"id":"first","id":"second","op":"status"}"""
                  sprintf """{"id":"commit","op":"reject","op":"commit","proposalId":"%s"}""" proposal.ProposalId
                  sprintf """{"id":"commit","op":"commit","proposalId":"missing","proposalId":"%s"}""" proposal.ProposalId
                  """{"id":"nested","op":"simulateCreateFunctionByName","displayName":"nested","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"boolLiteral","value":true,"value":false}}"""
                  """{"id":"type","op":"simulateCreateTypeByName","displayName":"Record","typeKind":{"kind":"product","fields":[{"displayName":"first","displayName":"second","type":{"kind":"primitive","primitive":"bool"}}]}}""" ]

            for request in requests do
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
                let error = response.RootElement.GetProperty("error")
                Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
                Assert.Contains("duplicate", error.GetProperty("message").GetString())
                Assert.Equal(before, status session)
                Assert.Equal(persistedBefore, File.ReadAllText(path))

            commit session proposal |> ignore
            Assert.Single(session.Declarations().Functions) |> ignore
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``JSONL name-resolved authoring creates and replaces without caller-managed semantic IDs`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            let tool = startTool "create" path

            let commitResponse proposalId =
                exchange tool (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
                |> requireOk
                |> fun document -> document.Dispose()

            let typeProposalId =
                use document =
                    exchange
                        tool
                        """{"id":"type","op":"simulateCreateTypeByName","displayName":"ChangeRequest","typeKind":{"kind":"product","fields":[{"displayName":"agentInteraction","type":{"kind":"primitive","primitive":"bool"}},{"displayName":"environmentDependent","type":{"kind":"primitive","primitive":"bool"}}]}}"""
                    |> requireOk
                document.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            commitResponse typeProposalId

            let functionProposalId =
                use document =
                    exchange
                        tool
                        """{"id":"function","op":"simulateCreateFunctionByName","displayName":"requires_live","parameters":[{"displayName":"request","type":{"kind":"named","name":"ChangeRequest"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"conditional","condition":{"kind":"fieldAccess","source":{"kind":"parameter","name":"request"},"type":"ChangeRequest","field":"agentInteraction"},"whenTrue":{"kind":"boolLiteral","value":true},"whenFalse":{"kind":"fieldAccess","source":{"kind":"parameter","name":"request"},"type":"ChangeRequest","field":"environmentDependent"}}}"""
                    |> requireOk
                document.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            commitResponse functionProposalId

            let replacementProposalId =
                use document =
                    exchange
                        tool
                        """{"id":"replace","op":"simulateReplaceBodyByName","function":"requires_live","replacement":{"kind":"boolLiteral","value":false}}"""
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let proposed = result.GetProperty("review").GetProperty("proposedFunction")
                Assert.Equal("requires_live", proposed.GetProperty("displayName").GetString())
                result.GetProperty("proposalId").GetString()
            commitResponse replacementProposalId

            use declarations = exchange tool """{"id":"decls","op":"declarations"}""" |> requireOk
            let functions =
                declarations.RootElement.GetProperty("result").GetProperty("functions").EnumerateArray()
                |> Seq.toList
            Assert.Single(functions) |> ignore
            Assert.Equal("requires_live", functions.Head.GetProperty("displayName").GetString())

            finish tool
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``JSONL process publication reopens with the same semantic identity and fingerprints`` abruptTermination =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            let first = startTool "create" path
            let proposalId, functionId, resultNodeId =
                use document =
                    exchange
                        first
                        """{"id":"create","op":"simulateCreateFunction","displayName":"seven","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"int64Literal","value":7}}"""
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let review = result.GetProperty("review")
                let reviewProperties =
                    review.EnumerateObject()
                    |> Seq.map (fun property -> property.Name)
                    |> Set.ofSeq
                Assert.False(Set.contains "materializedBody" reviewProperties)
                Assert.False(Set.contains "proposedFocus" reviewProperties)
                let createdFunction = review.GetProperty("createdFunction")
                result.GetProperty("proposalId").GetString(),
                createdFunction.GetProperty("id").GetString(),
                createdFunction.GetProperty("body").GetProperty("resultNodeId").GetString()

            exchange first (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
            |> requireOk
            |> fun document -> document.Dispose()

            let replacementProposalId =
                use document =
                    exchange
                        first
                        (sprintf
                            """{"id":"replace","op":"simulateReplaceBody","functionId":"%s","expectedCurrentResultNodeId":"%s","replacement":{"kind":"int64Literal","value":8}}"""
                            functionId
                            resultNodeId)
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let review = result.GetProperty("review")
                let reviewProperties =
                    review.EnumerateObject()
                    |> Seq.map (fun property -> property.Name)
                    |> Set.ofSeq
                Assert.False(Set.contains "baseFocus" reviewProperties)
                Assert.False(Set.contains "proposedFocus" reviewProperties)
                Assert.True(Set.contains "proposedFunction" reviewProperties)
                let diff = review.GetProperty("diff")
                let diffProperties =
                    diff.EnumerateObject()
                    |> Seq.map (fun property -> property.Name)
                    |> Set.ofSeq
                Assert.True(Set.contains "removedNodeIds" diffProperties)
                Assert.True(Set.contains "addedNodeIds" diffProperties)
                Assert.False(Set.contains "removedNodes" diffProperties)
                Assert.False(Set.contains "addedNodes" diffProperties)
                Assert.Equal<string>(functionId, review.GetProperty("proposedFunction").GetProperty("id").GetString())
                result.GetProperty("proposalId").GetString()

            exchange first (sprintf """{"id":"replace-commit","op":"commit","proposalId":"%s"}""" replacementProposalId)
            |> requireOk
            |> fun document -> document.Dispose()

            let snapshotId, snapshotFingerprint, semanticFingerprint =
                use document = exchange first """{"id":"status","op":"status"}""" |> requireOk
                let result = document.RootElement.GetProperty("result")
                result.GetProperty("snapshotId").GetString(),
                result.GetProperty("snapshotFingerprint").GetString(),
                result.GetProperty("semanticFingerprint").GetString()
            if abruptTermination then
                first.Kill(true)
                Assert.True(first.WaitForExit(5000), "The authoring process did not terminate within five seconds.")
                first.Dispose()
            else
                finish first

            let second = startTool "open" path
            use reopenedStatus = exchange second """{"id":"status","op":"status"}""" |> requireOk
            let result = reopenedStatus.RootElement.GetProperty("result")
            Assert.Equal<string>(snapshotId, result.GetProperty("snapshotId").GetString())
            Assert.Equal<string>(snapshotFingerprint, result.GetProperty("snapshotFingerprint").GetString())
            Assert.Equal<string>(semanticFingerprint, result.GetProperty("semanticFingerprint").GetString())

            use declarations = exchange second """{"id":"decls","op":"declarations"}""" |> requireOk
            let functions = declarations.RootElement.GetProperty("result").GetProperty("functions").EnumerateArray() |> Seq.toList
            Assert.Single(functions) |> ignore
            Assert.Equal<string>(functionId, functions.Head.GetProperty("id").GetString())
            finish second
        finally
            if Directory.Exists root then Directory.Delete(root, true)
