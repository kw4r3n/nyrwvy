namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module AuthoringDiscoveryTests =
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    [<Fact>]
    let ``declaration discovery keeps function signatures compact while focus retains bodies`` () =
        let root =
            Path.Combine(Path.GetTempPath(), "nyrwvy-authoring-discovery-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")

        try
            use session = AuthoringSession.createNew path |> unwrap "session creation"
            let snapshotId = (session.Status() |> unwrap "session status").SnapshotId
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = snapshotId
                  DisplayName = "increment"
                  Parameters =
                    [ { DisplayName = "value"
                        Type = int64Type } ]
                  ResultType = int64Type
                  RequiredEffects = []
                  Body =
                    ExpressionRecipe.Int64WrappingAdd(
                        ExpressionRecipe.ParameterSlot 0,
                        ExpressionRecipe.Int64Literal 1L
                    ) }

            let proposal = session.SimulateCreateFunction request |> unwrap "function simulation"
            let functionId =
                match proposal.Review with
                | AuthoringProposalReview.CreateFunction review -> review.CreatedFunction.Id
                | other -> failwithf "Expected function creation review, got %A" other

            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)
            |> unwrap "function commit"
            |> ignore

            use declarations =
                AuthoringProtocol.executeLine session """{"id":"d","op":"declarations"}"""
                |> JsonDocument.Parse
            let discoveredFunctions =
                declarations.RootElement.GetProperty("result").GetProperty("functions").EnumerateArray()
                |> Seq.toList
            let discovered = Assert.Single(discoveredFunctions)
            Assert.Equal(StableId.value functionId, discovered.GetProperty("id").GetString())
            Assert.Equal("increment", discovered.GetProperty("displayName").GetString())
            Assert.Single(discovered.GetProperty("parameters").EnumerateArray() |> Seq.toList) |> ignore
            Assert.Equal("int64", discovered.GetProperty("resultType").GetProperty("primitive").GetString())
            Assert.Empty(discovered.GetProperty("requiredEffects").EnumerateArray() |> Seq.toList)
            let mutable discoveredBody = Unchecked.defaultof<JsonElement>
            Assert.False(discovered.TryGetProperty("body", &discoveredBody))

            let focusRequest =
                sprintf """{"id":"f","op":"readFunction","functionId":"%s"}""" (StableId.value functionId)
            use focused = AuthoringProtocol.executeLine session focusRequest |> JsonDocument.Parse
            let focusedFunction = focused.RootElement.GetProperty("result").GetProperty("function")
            let mutable focusedBody = Unchecked.defaultof<JsonElement>
            Assert.True(focusedFunction.TryGetProperty("body", &focusedBody))
            Assert.True(focusedBody.GetProperty("nodes").GetArrayLength() > 0)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
