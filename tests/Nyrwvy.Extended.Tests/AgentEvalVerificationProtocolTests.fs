namespace Nyrwvy.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Nyrwvy.AgentEval
open Nyrwvy.Core
open Xunit

module AgentEvalVerificationProtocolTests =
    let private objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let private arrayValue items: obj = items |> List.toArray |> box

    let private command pairs =
        objectValue pairs |> JsonSerializer.Serialize

    let private execute (session: Session) payload =
        let response = session.ExecuteLine(payload)
        use document = JsonDocument.Parse(response)
        let root = document.RootElement
        if not (root.GetProperty("ok").GetBoolean()) then
            failwithf "Protocol command failed: %s" response
        response

    let private resultElement (response: string) =
        use document = JsonDocument.Parse(response)
        let root = document.RootElement
        if not (root.GetProperty("ok").GetBoolean()) then
            failwithf "Protocol command failed: %s" response
        root.GetProperty("result").Clone()

    let private resultStatus response =
        resultElement response |> fun result -> result.GetProperty("status").GetString()

    let private proposalHandle response =
        resultElement response |> fun result -> result.GetProperty("proposalHandle").GetString()

    let private assertNoVerification (response: string) =
        let result = resultElement response
        let mutable verification = Unchecked.defaultof<JsonElement>
        Assert.False(result.TryGetProperty("verification", &verification))

    let private renameFunction functionId displayName =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "renameFunction"
                          "functionId", box (StableId.value functionId)
                          "newDisplayName", box displayName ] ] ]

    let private addM1Context (fixture: MilestoneOneFixture) =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "addFunctionParameter"
                          "functionId", box (StableId.value fixture.ReportResultsFunctionId)
                          "displayName", box "context"
                          "type", objectValue [ "kind", box "primitive"; "name", box "Unit" ]
                          "callSitePlans",
                            arrayValue
                                [ objectValue
                                    [ "kind", box "bindExistingValue"
                                      "callNodeId", box (StableId.value fixture.MainReportResultsCallNodeId)
                                      "valueNodeId", box (StableId.value fixture.MainUnitNodeId) ] ] ] ] ]

    let private addM0Incomplete (fixture: MilestoneZeroFixture) =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "addFunctionParameter"
                          "functionId", box (StableId.value fixture.ValidateFunctionId)
                          "displayName", box "policy"
                          "type", objectValue [ "kind", box "named"; "id", box (StableId.value fixture.PolicyTypeId) ]
                          "callSitePlans",
                            arrayValue
                                [ objectValue
                                    [ "kind", box "leaveMissing"
                                      "callNodeId", box (StableId.value fixture.MainValidateCallNodeId) ] ] ] ] ]

    let private commit commandName handle =
        command
            [ "command", box commandName
              "proposalHandle", box handle ]

    let private createSession snapshot config artifactDirectory =
        match Session.createWithArtifactDirectory snapshot config artifactDirectory with
        | Ok session -> session
        | Error error -> failwithf "Unexpected verification session creation failure: %A" error

    let private temporaryDirectory label =
        Path.Combine(Path.GetTempPath(), "nyrwvy-agent-verification-" + label + "-" + Guid.NewGuid().ToString("N"))

    let private assertPrivateArtifactTree (root: string) =
        if not (OperatingSystem.IsWindows()) then
            let privateDirectory =
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            let privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
            Assert.Equal(privateDirectory, File.GetUnixFileMode(root))

            for path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories) do
                if (File.GetAttributes(path) &&& FileAttributes.Directory) = FileAttributes.Directory then
                    Assert.Equal(privateDirectory, File.GetUnixFileMode(path))
                else
                    Assert.Equal(privateFile, File.GetUnixFileMode(path))

    let private verificationReport (session: Session) =
        session.VerificationReports
        |> List.tryLast
        |> Option.defaultWith (fun () -> failwith "Missing verification report.")

    let private assertPassedEvidence (session: Session) (response: string) =
        let result = resultElement response
        let verification = result.GetProperty("verification")
        Assert.Equal("terminal", verification.GetProperty("state").GetString())
        Assert.Equal("passed", verification.GetProperty("status").GetString())
        Assert.True(verification.GetProperty("evidenceDurable").GetBoolean())
        Assert.Equal(
            verification.GetProperty("publishedSnapshotId").GetString(),
            verification.GetProperty("typedSemanticSourceSnapshotId").GetString()
        )
        Assert.Equal(
            verification.GetProperty("publishedSnapshotId").GetString(),
            verification.GetProperty("executionSourceSnapshotId").GetString()
        )

        let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
        Assert.False(response.Contains(paths.DirectoryPath, StringComparison.Ordinal))
        Assert.False(response.Contains("evidencePath", StringComparison.Ordinal))
        Assert.False(response.Contains("runDirectory", StringComparison.Ordinal))
        Assert.False(response.Contains("artifacts", StringComparison.Ordinal))
        Assert.False(response.Contains("Generated.cs", StringComparison.Ordinal))
        Assert.False(response.Contains("Program.cs", StringComparison.Ordinal))

        let report = verificationReport session
        let evidencePath = report.EvidencePath
        Assert.True(File.Exists(evidencePath))
        let evidenceDirectory = Path.GetDirectoryName(evidencePath)
        Assert.Empty(Directory.GetFiles(evidenceDirectory, "verification.json.tmp-*"))
        let evidenceFiles = Directory.GetFiles(paths.DirectoryPath, "verification.json", SearchOption.AllDirectories)
        Assert.Single(evidenceFiles) |> ignore
        Assert.Equal(Path.GetFullPath(evidencePath), Path.GetFullPath(evidenceFiles[0]))

        use evidenceDocument = JsonDocument.Parse(File.ReadAllText(evidencePath))
        let evidence = evidenceDocument.RootElement
        Assert.Equal("terminal", evidence.GetProperty("state").GetString())
        Assert.Equal("passed", evidence.GetProperty("status").GetString())
        Assert.True(evidence.GetProperty("evidenceDurable").GetBoolean())
        Assert.Equal(
            verification.GetProperty("publishedSnapshotId").GetString(),
            evidence.GetProperty("publishedSnapshot").GetProperty("currentSnapshotId").GetString()
        )
        Assert.Equal(
            verification.GetProperty("publishedSnapshotId").GetString(),
            evidence.GetProperty("receipt").GetProperty("proposedSnapshotId").GetString()
        )

        let stages = evidence.GetProperty("stages").EnumerateArray() |> Seq.toList
        for stage in stages do
            if stage.GetProperty("name").GetString() <> "fixtureHarness" then
                Assert.Equal("passed", stage.GetProperty("status").GetString())

        evidencePath

    let private assertFailedEvidence (session: Session) (response: string) =
        let result = resultElement response
        Assert.Equal("committed", result.GetProperty("status").GetString())
        let verification = result.GetProperty("verification")
        Assert.Equal("terminal", verification.GetProperty("state").GetString())
        Assert.Equal("failed", verification.GetProperty("status").GetString())
        Assert.True(verification.GetProperty("evidenceDurable").GetBoolean())

        let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
        Assert.False(response.Contains(paths.DirectoryPath, StringComparison.Ordinal))
        Assert.False(response.Contains("evidencePath", StringComparison.Ordinal))
        Assert.False(response.Contains("runDirectory", StringComparison.Ordinal))
        Assert.False(response.Contains("artifacts", StringComparison.Ordinal))
        Assert.False(response.Contains("Generated.cs", StringComparison.Ordinal))
        Assert.False(response.Contains("Program.cs", StringComparison.Ordinal))

        let evidencePath = (verificationReport session).EvidencePath
        Assert.True(File.Exists(evidencePath))
        let evidenceDirectory = Path.GetDirectoryName(evidencePath)
        Assert.Empty(Directory.GetFiles(evidenceDirectory, "verification.json.tmp-*"))
        use evidenceDocument = JsonDocument.Parse(File.ReadAllText(evidencePath))
        let evidence = evidenceDocument.RootElement
        Assert.Equal("terminal", evidence.GetProperty("state").GetString())
        Assert.Equal("failed", evidence.GetProperty("status").GetString())
        Assert.True(evidence.GetProperty("evidenceDurable").GetBoolean())
        Assert.Equal("committed", evidence.GetProperty("commit").GetProperty("status").GetString())

        let compileStage =
            evidence.GetProperty("stages").EnumerateArray()
            |> Seq.find (fun stage -> stage.GetProperty("name").GetString() = "compile")
        Assert.Equal("failed", compileStage.GetProperty("status").GetString())

    [<Fact>]
    let ``committed M0 protocol state is lowered compiled executed and durably evidenced`` () =
        let fixture = MilestoneZeroGraph.create ()
        let artifactDirectory = temporaryDirectory "m0"
        Directory.CreateDirectory(artifactDirectory) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    VerificationFixture = Some VerificationFixture.MilestoneZero }
            let session = createSession fixture.Snapshot config artifactDirectory
            let proposal = renameFunction fixture.ValidateFunctionId "validate_profile" |> execute session
            let commitResponse = commit "commit" (proposalHandle proposal) |> execute session

            Assert.Equal("committed", resultStatus commitResponse)
            assertPassedEvidence session commitResponse |> ignore
            assertPrivateArtifactTree artifactDirectory
        finally
            if Directory.Exists(artifactDirectory) then
                Directory.Delete(artifactDirectory, true)

    [<Fact>]
    let ``committed M1 protocol state uses the exact published workspace for verification`` () =
        let fixture = MilestoneOneGraph.create ()
        let artifactDirectory = temporaryDirectory "m1"
        Directory.CreateDirectory(artifactDirectory) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    VerificationFixture = Some VerificationFixture.MilestoneOne }
            let session = createSession fixture.Snapshot config artifactDirectory
            let proposal = addM1Context fixture |> execute session
            let commitResponse = commit "commit" (proposalHandle proposal) |> execute session

            Assert.Equal("committed", resultStatus commitResponse)
            assertPassedEvidence session commitResponse |> ignore
            assertPrivateArtifactTree artifactDirectory
        finally
            if Directory.Exists(artifactDirectory) then
                Directory.Delete(artifactDirectory, true)

    [<Fact>]
    let ``verification failure leaves the commit result explicit and persists failed evidence`` () =
        let fixture = MilestoneZeroGraph.create ()
        let artifactDirectory = temporaryDirectory "failure"
        Directory.CreateDirectory(artifactDirectory) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    VerificationFixture = Some VerificationFixture.MilestoneOne }
            let session = createSession fixture.Snapshot config artifactDirectory
            let proposal = renameFunction fixture.ValidateFunctionId "validate_profile" |> execute session
            let commitResponse = commit "commit" (proposalHandle proposal) |> execute session

            assertFailedEvidence session commitResponse
        finally
            if Directory.Exists(artifactDirectory) then
                Directory.Delete(artifactDirectory, true)

    [<Fact>]
    let ``uncommitted and incomplete protocol states do not run verification`` () =
        let fixture = MilestoneZeroGraph.create ()
        let artifactDirectory = temporaryDirectory "skipped"
        Directory.CreateDirectory(artifactDirectory) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    VerificationFixture = Some VerificationFixture.MilestoneZero }
            let session = createSession fixture.Snapshot config artifactDirectory

            let uncommitted = renameFunction fixture.ValidateFunctionId "validate_profile" |> execute session
            assertNoVerification uncommitted
            Assert.Empty(Directory.GetFiles(artifactDirectory, "verification.json", SearchOption.AllDirectories))

            let attemptedVerification = session.ExecuteLine(command [ "command", box "verify" ])
            use attemptedDocument = JsonDocument.Parse(attemptedVerification)
            Assert.False(attemptedDocument.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("ProtocolError", attemptedDocument.RootElement.GetProperty("error").GetProperty("code").GetString())

            let incomplete = addM0Incomplete fixture |> execute session
            let incompleteCommit = commit "commitAllowIncomplete" (proposalHandle incomplete) |> execute session
            Assert.Equal("committed", resultStatus incompleteCommit)
            assertNoVerification incompleteCommit
            Assert.Empty(Directory.GetFiles(artifactDirectory, "verification.json", SearchOption.AllDirectories))
        finally
            if Directory.Exists(artifactDirectory) then
                Directory.Delete(artifactDirectory, true)

    [<Fact>]
    let ``verification output comparison only normalizes CRLF`` () =
        Assert.True(Verification.matchesExpectedOutput "first\nsecond\n" "first\r\nsecond\r\n")
        Assert.False(Verification.matchesExpectedOutput "first\nsecond\n" " first\nsecond\n")
        Assert.False(Verification.matchesExpectedOutput "first\nsecond\n" "first\nsecond\n\n")
        Assert.False(Verification.matchesExpectedOutput "first\nsecond\n" "first\nsecond")
        Assert.False(Verification.matchesExpectedOutput "first\nsecond\n" "first\nsecond \n")
