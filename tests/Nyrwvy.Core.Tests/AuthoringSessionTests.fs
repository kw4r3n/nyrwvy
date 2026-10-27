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

    let private assertLockReleased path message =
        try
            use _handle = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            Assert.True(true, message)
        with :? IOException -> Assert.True(false, message)

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
        { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (status session).SnapshotId
          TypeParameters = None
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
            | Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot issues)) ->
                Assert.Contains(issues, fun issue -> issue.Code = "ImportedDeclarationModified")
            | other -> failwithf "Imported declaration edit reached review: %A" other
            Assert.Equal(original, File.ReadAllText(plannerPath))
            reopened.Close()

            let manifestPath = plannerPath + ".dependency.json"
            let manifest = File.ReadAllText(manifestPath)
            File.Delete(manifestPath)
            let sourceBeforeMissingOpen = File.ReadAllBytes plannerPath
            match AuthoringSession.openExisting plannerPath with
            | Error(AuthoringSessionError.DependencyFailure message) ->
                Assert.Contains(manifestPath, message)
                Assert.Contains("Restore the exact sidecar", message)
            | Ok session -> session.Close(); failwith "Removed dependency manifest was accepted."
            | Error error -> failwithf "Unexpected missing-manifest error: %A" error
            Assert.Equal<byte>(sourceBeforeMissingOpen, File.ReadAllBytes plannerPath)
            Assert.False(File.Exists manifestPath, "Failed open must not guess or recreate the lost selection.")
            assertLockReleased plannerPath "Failed open must release the workspace lock."
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
            // The selected content is now retained in the sidecar, not a mutable
            // source path. An unrelated upstream commit cannot invalidate it.
            commit plannerBeforePolicyUpdate pending |> ignore
            plannerBeforePolicyUpdate.Close()
            use afterPolicyUpdate = openSession plannerPath
            Assert.DoesNotContain(afterPolicyUpdate.Declarations().Functions, fun item -> item.DisplayName = "changed")
            Assert.Contains(afterPolicyUpdate.Declarations().Functions, fun item -> item.DisplayName = "pending")
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
            match session.Reject second.ProposalId with
            | Ok receipt ->
                Assert.Equal(second.ProposalId, StableId.value receipt.TransactionId)
                Assert.Equal(afterFirst.SnapshotId, receipt.RetainedSnapshotId)
            | Error error -> failwithf "Could not discard stale proposal: %A" error
            match session.Reject second.ProposalId with
            | Error(AuthoringSessionError.ProposalNotFound id) -> Assert.Equal(second.ProposalId, id)
            | other -> failwithf "Rejected proposal remained registered: %A" other
            Assert.Equal(afterFirst.SnapshotId, (status session).SnapshotId)
            Assert.Equal(persistedAfterFirst, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``a snapshot path that is a symbolic link is refused for writing`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        let link = Path.Combine(root, "alias.json")
        try
            (createSession path).Close()
            File.CreateSymbolicLink(link, path) |> ignore
            // Without the refusal the link gets a lock of its own beside a session holding the target.
            use _session = AuthoringSession.openExisting path |> function Ok value -> value | Error error -> failwithf "%A" error
            match AuthoringSession.openExisting link with
            | Error(AuthoringSessionError.IoFailure message) -> Assert.Contains("symbolic link", message)
            | result -> failwithf "Expected the link to be refused, got %A" result
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
    let ``close releases the lock and a failed open releases it too`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        let lockPath = path + ".lock"
        try
            let session = createSession path
            Assert.True(File.Exists lockPath)
            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.LockUnavailable _) -> ()
            | result -> failwithf "Expected the second writer to be rejected, got %A" result
            session.Close()
            assertLockReleased path "Close must release the workspace lock."
            File.WriteAllText(path, "not a snapshot")
            match AuthoringSession.openExisting path with
            | Error(AuthoringSessionError.SnapshotCodecFailure _) -> ()
            | result -> failwithf "Expected a codec failure, got %A" result
            assertLockReleased path "Failed open must release the workspace lock."
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``startup failures are one worded stderr line with exit status 2`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        let run mode =
            use child = startTool mode path
            child.StandardInput.Close()
            let stderr = child.StandardError.ReadToEnd()
            child.WaitForExit()
            child.ExitCode, stderr.TrimEnd()
        try
            let missingCode, missing = run "open"
            Assert.Equal(2, missingCode)
            Assert.StartsWith($"SnapshotFileNotFound: No snapshot file at {Path.GetFullPath path}.", missing)
            Assert.Contains("create", missing)
            Assert.DoesNotContain("\n", missing)
            File.WriteAllText(path, """{"x":1}""")
            let shapeCode, shape = run "open"
            Assert.Equal(2, shapeCode)
            Assert.StartsWith("SnapshotCodecFailure: The snapshot does not have the expected shape: ", shape)
            Assert.DoesNotContain("InvalidShape", shape)
            let existsCode, exists = run "create"
            Assert.Equal(2, existsCode)
            Assert.StartsWith("SnapshotFileAlreadyExists: ", exists)
            Assert.Contains("open", exists)
            assertLockReleased path "Failed open must release the workspace lock."
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``JSONL rejects duplicate properties without publishing or consuming the reviewed proposal`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let persistedBefore = File.ReadAllText(path)
            let proposal =
                creationRequest session "value" [] int64Type (ExpressionRecipe.Int64Literal 1L)
                |> simulateCreation session
            // Taken with the proposal pending: a rejected line must not change the pending count either.
            let before = status session
            let requests =
                [ """{"id":"first","id":"second","op":"status"}"""
                  sprintf """{"id":"commit","op":"reject","op":"commit","proposalId":"%s"}""" proposal.ProposalId
                  sprintf """{"id":"commit","op":"commit","proposalId":"missing","proposalId":"%s"}""" proposal.ProposalId
                  """{"id":"nested","op":"simulateCreateFunctionByName","exported":true,"displayName":"nested","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"boolLiteral","value":true,"value":false}}"""
                  """{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Record","typeKind":{"kind":"product","fields":[{"displayName":"first","displayName":"second","type":{"kind":"primitive","primitive":"bool"}}]}}""" ]

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
    let ``JSONL reports wrongly typed recipe values as protocol errors`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let before = status session
            let creation (body: string) =
                sprintf """{"id":"typed","op":"simulateCreateFunctionByName","exported":true,"displayName":"typed","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":%s}""" body
            let requests =
                [ creation """{"kind":"parameterSlot","index":"1"}"""
                  creation "5"
                  creation """{"kind":"boolAnd","left":5,"right":{"kind":"boolLiteral","value":true}}""" ]

            for request in requests do
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
                let error = response.RootElement.GetProperty("error")
                Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
                Assert.Equal(before, status session)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``JSONL committed twenty level conditional reopens with identical fingerprints`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let rec body depth =
                if depth = 0 then """{"kind":"boolLiteral","value":true}"""
                else
                    sprintf """{"kind":"conditional","condition":{"kind":"boolLiteral","value":true},"whenTrue":%s,"whenFalse":{"kind":"boolLiteral","value":false}}""" (body (depth - 1))
            let request =
                sprintf """{"id":"deep","op":"simulateCreateFunctionByName","exported":true,"displayName":"deep","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":%s}""" (body 20)
            use proposed =
                JsonDocument.Parse(AuthoringProtocol.executeLine session request, JsonDocumentOptions(MaxDepth = 256))
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use committed =
                AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
                |> requireOk
            let before = status session
            let functionBefore = List.exactlyOne (session.Declarations().Functions)
            Assert.Equal(61, ValueGraph.collectNodes functionBefore.Body |> List.length)
            session.Close()
            use reopened = openSession path
            Assert.Equal(before.SnapshotId, (status reopened).SnapshotId)
            Assert.Equal(before.SnapshotFingerprint, (status reopened).SnapshotFingerprint)
            Assert.Equal(before.SemanticFingerprint, (status reopened).SemanticFingerprint)
            let functionAfter = List.exactlyOne (reopened.Declarations().Functions)
            Assert.Equal(functionBefore.Id, functionAfter.Id)
            Assert.Equal(functionBefore.Body.ResultNodeId, functionAfter.Body.ResultNodeId)
            let nodeIds (fn: SemanticFunction) =
                ValueGraph.collectNodes fn.Body |> List.map (fun node -> node.Id) |> Set.ofList
            Assert.Equal<Set<GraphNodeId>>(nodeIds functionBefore, nodeIds functionAfter)
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``JSONL authors ordinal StringEqual with typed operands and reopens stable node`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let request =
                """{"id":"same","op":"simulateCreateFunctionByName","exported":true,"displayName":"same","parameters":[{"displayName":"left","type":{"kind":"primitive","primitive":"string"}},{"displayName":"right","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"stringEqual","left":{"kind":"parameter","name":"left"},"right":{"kind":"parameter","name":"right"}}}"""
            use proposed = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use committed =
                AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
                |> JsonDocument.Parse
            Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            session.Close()
            use reopened = openSession path
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> function
                | Ok value -> value
                | Error error -> failwithf "Reopen failed: %A" error
            let authored = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = "same")
            Assert.Contains(authored.Body.Nodes, fun node ->
                match node.Kind with
                | SemanticNodeKind.StringEqual _ -> true
                | _ -> false)
            Assert.Empty(SnapshotValidator.validate snapshot)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``JSONL name-resolved fold recipe publishes and reopens with callback identity`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let send text =
                let document = AuthoringProtocol.executeLine session text |> JsonDocument.Parse
                if not (document.RootElement.GetProperty("ok").GetBoolean()) then
                    failwithf "Fold request failed: %s" (document.RootElement.ToString())
                document
            let commitProposal proposalId =
                use _response = send (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
                ()
            let proposalId =
                use response = send """{"id":"folder","op":"simulateCreateFunctionByName","exported":true,"displayName":"sum_step","parameters":[{"displayName":"acc","type":{"kind":"primitive","primitive":"int64"}},{"displayName":"item","type":{"kind":"primitive","primitive":"int64"}}],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"int64WrappingAdd","left":{"kind":"parameter","name":"acc"},"right":{"kind":"parameter","name":"item"}}}"""
                response.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            commitProposal proposalId
            let proposalId =
                use response = send """{"id":"fold","op":"simulateCreateFunctionByName","exported":true,"displayName":"sum","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"listFold","source":{"kind":"listConstruct","itemType":{"kind":"primitive","primitive":"int64"},"items":[{"kind":"int64Literal","value":1},{"kind":"int64Literal","value":2}]},"seed":{"kind":"int64Literal","value":0},"function":"sum_step"}}"""
                response.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            commitProposal proposalId
            session.Close()
            use reopened = openSession path
            let snapshot = reopened.Declarations()
            Assert.Contains(snapshot.Functions, fun functionEntity -> functionEntity.DisplayName = "sum")
            match reopened.ProjectExecution() with
            | Ok program -> Assert.Equal(2, program.Functions.Length)
            | Error error -> failwithf "Fold did not lower after reopen: %A" error
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
                        """{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"ChangeRequest","typeKind":{"kind":"product","fields":[{"displayName":"agentInteraction","type":{"kind":"primitive","primitive":"bool"}},{"displayName":"environmentDependent","type":{"kind":"primitive","primitive":"bool"}}]}}"""
                    |> requireOk
                document.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            commitResponse typeProposalId

            let functionProposalId =
                use document =
                    exchange
                        tool
                        """{"id":"function","op":"simulateCreateFunctionByName","exported":true,"displayName":"requires_live","parameters":[{"displayName":"request","type":{"kind":"named","name":"ChangeRequest"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"conditional","condition":{"kind":"fieldAccess","source":{"kind":"parameter","name":"request"},"type":"ChangeRequest","field":"agentInteraction"},"whenTrue":{"kind":"boolLiteral","value":true},"whenFalse":{"kind":"fieldAccess","source":{"kind":"parameter","name":"request"},"type":"ChangeRequest","field":"environmentDependent"}}}"""
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
                        """{"id":"create","op":"simulateCreateFunction","exported":true,"displayName":"seven","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"int64Literal","value":7}}"""
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

    let private rejectedDiagnosticCode (session: AuthoringSession) (request: string) =
        use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
        let root = response.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.GetRawText())
        let error = root.GetProperty("error")
        Assert.Equal("SimulationRejected", error.GetProperty("code").GetString())
        error.GetProperty("details").GetProperty("diagnostics").[0].GetProperty("code").GetString()

    let private simulatedProposalId (session: AuthoringSession) (request: string) =
        use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
        let root = response.RootElement
        Assert.True(root.GetProperty("ok").GetBoolean(), root.GetRawText())
        root.GetProperty("result").GetProperty("proposalId").GetString()

    [<Fact>]
    let ``JSONL simulate requests reject a stale expectedSnapshotId and accept the inspected one`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session = createSession path
            let inspected = StableId.value session.SnapshotId
            let proposal =
                creationRequest session "value" [] int64Type (ExpressionRecipe.Int64Literal 1L)
                |> simulateCreation session
            commit session proposal |> ignore
            let current = StableId.value session.SnapshotId
            let resultNodeId = StableId.value (createdFunction proposal).Body.ResultNodeId
            let before = status session

            let replace extra =
                $"""{{"id":"replace","op":"simulateReplaceBodyByName","function":"value","replacement":{{"kind":"int64Literal","value":2}}{extra}}}"""
            let snapshot value = $""","expectedSnapshotId":"{value}" """

            // Another writer advanced the workspace after the inspection: every workflow reports its own stale-base code.
            Assert.Equal("StructuralExpectedBaseMismatch", rejectedDiagnosticCode session (replace (snapshot inspected)))
            Assert.Equal(
                "EffectDeclarationExpectedBaseMismatch",
                rejectedDiagnosticCode session $"""{{"id":"effect","op":"simulateCreateEffect","exported":true,"displayName":"Clock"{snapshot inspected}}}""")
            Assert.Equal(
                "TypeDeclarationExpectedBaseMismatch",
                rejectedDiagnosticCode
                    session
                    $"""{{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Flag","typeKind":{{"kind":"product","fields":[]}}{snapshot inspected}}}""")
            Assert.Equal(
                "StaleBaseSnapshot",
                rejectedDiagnosticCode
                    session
                    $"""{{"id":"delete","op":"simulateOperationsByName","operations":[{{"kind":"deleteFunction","function":"value"}}]{snapshot inspected}}}""")
            let otherNode = StableId.value (StableIdAllocator.newGraphNodeId ())
            Assert.Equal(
                "StructuralExpectedResultMismatch",
                rejectedDiagnosticCode session (replace $""","expectedResultNodeId":"{otherNode}" """))
            Assert.Equal(before, status session)

            // The inspected snapshot and result node are current, so the request is simulated as before.
            simulatedProposalId session (replace ($"""{snapshot current},"expectedResultNodeId":"{resultNodeId}" """))
            |> ignore
            simulatedProposalId
                session
                $"""{{"id":"delete","op":"simulateOperationsByName","operations":[{{"kind":"deleteFunction","function":"value"}}]{snapshot current}}}"""
            |> ignore

            // Omitting both keeps simulating against the current snapshot.
            let proposalId = simulatedProposalId session (replace "")
            match session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) with
            | Ok _ -> ()
            | Error error -> failwithf "Unexpected commit failure: %A" error
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``a locked open removes this snapshot's interrupted temporaries and no one else's`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            (createSession path).Close()
            let guid () = Guid.NewGuid().ToString("N")
            let stale = [ $".workspace.json.{guid ()}.tmp"; $".workspace.json.dependency.json.{guid ()}.tmp" ] |> List.map (fun name -> Path.Combine(root, name))
            let foreign = Path.Combine(root, $".workspace.json.other.json.{guid ()}.tmp")
            for file in foreign :: stale do File.WriteAllText(file, "partial")
            (AuthoringSession.openExisting path |> function Ok session -> session | Error error -> failwithf "%A" error).Close()
            for file in stale do Assert.False(File.Exists file, file)
            Assert.True(File.Exists foreign)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
