namespace NyamNyam.Core.Tests

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core
open NyamNyam.AgentEval
open Xunit

module AgentEvalProtocolTests =
    let private objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let private arrayValue items: obj = items |> List.toArray |> box

    let private command pairs =
        objectValue pairs |> JsonSerializer.Serialize

    let private createSession snapshot config =
        match Session.create snapshot config with
        | Ok session -> session
        | Error issues -> failwithf "Unexpected evaluation session creation failure: %A" issues

    let private execute (session: Session) payload =
        let response = session.ExecuteLine(payload)
        use document = JsonDocument.Parse(response)
        let root = document.RootElement
        if not (root.GetProperty("ok").GetBoolean()) then
            failwithf "Protocol command failed: %s" response
        response

    let private resultStatus response =
        use document = JsonDocument.Parse(response)
        document.RootElement.GetProperty("result").GetProperty("status").GetString()

    let private proposalHandle response =
        use document = JsonDocument.Parse(response)
        document.RootElement.GetProperty("result").GetProperty("proposalHandle").GetString()

    let private firstDiagnosticCode response =
        use document = JsonDocument.Parse(response)
        let diagnostics =
            document.RootElement
                .GetProperty("result")
                .GetProperty("diagnostics")

        diagnostics.EnumerateArray()
        |> Seq.head
        |> fun diagnostic -> diagnostic.GetProperty("code").GetString()

    let private firstCreatedObligationId response =
        use document = JsonDocument.Parse(response)
        let obligationsCreated =
            document.RootElement
                .GetProperty("result")
                .GetProperty("review")
                .GetProperty("diff")
                .GetProperty("obligationsCreated")

        obligationsCreated.EnumerateArray()
        |> Seq.head
        |> fun obligation -> obligation.GetProperty("obligationId").GetString()

    let private simulateRenameFunction functionId newDisplayName =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "renameFunction"
                          "functionId", box (StableId.value functionId)
                          "newDisplayName", box newDisplayName ] ] ]

    let private commitCommand commandName handle =
        command
            [ "command", box commandName
              "proposalHandle", box handle ]

    [<Fact>]
    let ``evaluation protocol preserves reviewed proposal across rename commit`` () =
        let fixture = MilestoneZeroGraph.create ()
        let session = createSession fixture.Snapshot SessionConfig.defaultValue

        let proposalResponse =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session

        Assert.Equal("proposed", resultStatus proposalResponse)
        let handle = proposalHandle proposalResponse

        let commitResponse = commitCommand "commit" handle |> execute session
        Assert.Equal("committed", resultStatus commitResponse)

        let queryResponse =
            command
                [ "command", box "getFunction"
                  "functionId", box (StableId.value fixture.ValidateFunctionId) ]
            |> execute session

        use queryDocument = JsonDocument.Parse(queryResponse)
        let displayName =
            queryDocument.RootElement
                .GetProperty("result")
                .GetProperty("value")
                .GetProperty("displayName")
                .GetString()
        Assert.Equal("validate_profile", displayName)

    [<Fact>]
    let ``evaluation protocol supports explicit incomplete publication and obligation resolution`` () =
        let fixture = MilestoneZeroGraph.create ()
        let session = createSession fixture.Snapshot SessionConfig.defaultValue

        let addParameterCommand =
            command
                [ "command", box "simulate"
                  "operations",
                    arrayValue
                        [ objectValue
                            [ "kind", box "addFunctionParameter"
                              "functionId", box (StableId.value fixture.ValidateFunctionId)
                              "displayName", box "policy"
                              "type",
                                objectValue
                                    [ "kind", box "named"
                                      "id", box (StableId.value fixture.PolicyTypeId) ]
                              "callSitePlans",
                                arrayValue
                                    [ objectValue
                                        [ "kind", box "leaveMissing"
                                          "callNodeId", box (StableId.value fixture.MainValidateCallNodeId) ] ] ] ] ]

        let addResponse = execute session addParameterCommand
        Assert.Equal("proposed", resultStatus addResponse)
        let addHandle = proposalHandle addResponse
        let obligationId = firstCreatedObligationId addResponse

        let conservativeCommit = commitCommand "commit" addHandle |> execute session
        Assert.Equal("rejected", resultStatus conservativeCommit)
        Assert.Equal("BlockingObligations", firstDiagnosticCode conservativeCommit)

        let incompleteCommit = commitCommand "commitAllowIncomplete" addHandle |> execute session
        Assert.Equal("committed", resultStatus incompleteCommit)

        let resolveCommand =
            command
                [ "command", box "simulate"
                  "operations",
                    arrayValue
                        [ objectValue
                            [ "kind", box "resolveMissingArgument"
                              "obligationId", box obligationId
                              "valueNodeId", box (StableId.value fixture.MainPolicyNodeId) ] ] ]

        let resolveResponse = execute session resolveCommand
        let resolveHandle = proposalHandle resolveResponse
        let resolvedCommit = commitCommand "commit" resolveHandle |> execute session
        Assert.Equal("committed", resultStatus resolvedCommit)

        let obligationsResponse =
            command
                [ "command", box "getOpenObligations"
                  "functionId", box (StableId.value fixture.MainFunctionId) ]
            |> execute session

        use obligationsDocument = JsonDocument.Parse(obligationsResponse)
        let obligations =
            obligationsDocument.RootElement
                .GetProperty("result")
                .GetProperty("value")
        Assert.Equal(0, obligations.GetArrayLength())

    [<Fact>]
    let ``evaluation harness stale perturbation is not an agent command and forces fresh review`` () =
        let fixture = MilestoneZeroGraph.create ()
        let rawUserInput =
            fixture.Snapshot.Types
            |> List.find (fun semanticType -> semanticType.DisplayName = "RawUserInput")

        let config =
            { SessionConfig.defaultValue with
                StalePerturbation =
                    Some
                        { TypeId = rawUserInput.Id
                          NewDisplayName = "RawAccountInput" } }
        let session = createSession fixture.Snapshot config

        let staleProposal =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session
        let staleHandle = proposalHandle staleProposal

        let staleCommit = commitCommand "commit" staleHandle |> execute session
        Assert.Equal("rejected", resultStatus staleCommit)
        Assert.Equal("StaleBaseSnapshot", firstDiagnosticCode staleCommit)

        let freshProposal =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session
        let freshHandle = proposalHandle freshProposal
        let freshCommit = commitCommand "commit" freshHandle |> execute session
        Assert.Equal("committed", resultStatus freshCommit)

        let agentAttempt =
            command
                [ "command", box "harnessAdvance"
                  "typeId", box (StableId.value rawUserInput.Id) ]
        let rejectedProtocolResponse = session.ExecuteLine(agentAttempt)
        use rejectedProtocolDocument = JsonDocument.Parse(rejectedProtocolResponse)
        Assert.False(rejectedProtocolDocument.RootElement.GetProperty("ok").GetBoolean())
        Assert.Equal(
            "ProtocolError",
            rejectedProtocolDocument.RootElement.GetProperty("error").GetProperty("code").GetString()
        )

        let metricsResponse = command [ "command", box "metrics" ] |> execute session
        use metricsDocument = JsonDocument.Parse(metricsResponse)
        let harnessEvents =
            metricsDocument.RootElement
                .GetProperty("result")
                .GetProperty("metrics")
                .GetProperty("harnessEvents")
        Assert.Equal(1, harnessEvents.GetArrayLength())
