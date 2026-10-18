namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module AuthoringParameterMigrationSessionTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool

    let private unwrap label = function
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private currentSnapshotId (session: AuthoringSession) =
        (session.Status() |> unwrap "status").SnapshotId

    let private commit (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)
        |> unwrap "commit"
        |> ignore

    let private parameter displayName typeRef: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
        { DisplayName = displayName
          Type = typeRef }

    let private createFunction session displayName parameters resultType body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = currentSnapshotId session
              DisplayName = displayName
              Parameters = parameters
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        session.SimulateCreateFunction request
        |> unwrap (displayName + " simulation")
        |> commit session
        session.Declarations().Functions
        |> List.find (fun item -> item.DisplayName = displayName)

    let private requireOk (response: string): JsonDocument =
        let document = JsonDocument.Parse(response)
        if not (document.RootElement.GetProperty("ok").GetBoolean()) then
            let rendered = document.RootElement.GetRawText()
            document.Dispose()
            failwithf "JSONL request failed: %s" rendered
        document

    [<Fact>]
    let ``maintained protocol migrates parameter type atomically and reopens stable identities`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-m5-migration-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")

        try
            use session = AuthoringSession.createNew path |> unwrap "session creation"

            let typeRequest: AgentCreateTypeDeclarationRequest =
                { TypeParameters = []; ExpectedBaseSnapshotId = currentSnapshotId session
                  DisplayName = "ValidationRequest"
                  Kind =
                    AgentTypeDeclarationKindIntent.Product
                        [ { DisplayName = "email"; Type = stringType } ] }
            session.SimulateCreateType typeRequest
            |> unwrap "request type simulation"
            |> commit session

            let requestType =
                session.Declarations().Types
                |> List.find (fun item -> item.DisplayName = "ValidationRequest")
            let emailField =
                match requestType.Kind with
                | SemanticTypeKind.Product [ field ] -> field
                | other -> failwithf "Unexpected request type: %A" other

            let classify =
                createFunction
                    session
                    "classify"
                    [ parameter "input" stringType ]
                    boolType
                    (ExpressionRecipe.StringContains(
                        ExpressionRecipe.ParameterSlot 0,
                        ExpressionRecipe.StringLiteral "@"
                    ))
            let classifyInput = List.exactlyOne classify.Parameters

            let processFunction =
                createFunction
                    session
                    "process"
                    [ parameter "input" stringType ]
                    boolType
                    (ExpressionRecipe.DirectCall(
                        classify.Id,
                        [ classifyInput.Id, ExpressionRecipe.ParameterSlot 0 ]
                    ))
            let processInput = List.exactlyOne processFunction.Parameters

            let migration =
                $"""{{"id":"migrate","op":"simulateParameterTypeMigration","targetFunctionId":"{StableId.value classify.Id}","targetParameterId":"{StableId.value classifyInput.Id}","newParameterType":{{"kind":"named","typeId":"{StableId.value requestType.Id}"}},"targetReplacement":{{"kind":"stringContains","value":{{"kind":"fieldAccess","source":{{"kind":"parameter","parameterId":"{StableId.value classifyInput.Id}"}},"fieldId":"{StableId.value emailField.Id}"}},"substring":{{"kind":"stringLiteral","value":"@"}}}},"callerRewrites":[{{"callerFunctionId":"{StableId.value processFunction.Id}","replacement":{{"kind":"directCall","functionId":"{StableId.value classify.Id}","arguments":[{{"parameterId":"{StableId.value classifyInput.Id}","value":{{"kind":"productConstruct","typeId":"{StableId.value requestType.Id}","fields":[{{"fieldId":"{StableId.value emailField.Id}","value":{{"kind":"parameter","parameterId":"{StableId.value processInput.Id}"}}}}]}}}}]}}}}]}}"""

            let proposalId =
                use document = AuthoringProtocol.executeLine session migration |> requireOk
                let result = document.RootElement.GetProperty("result")
                let review = result.GetProperty("review")
                Assert.Equal("parameterTypeMigration", review.GetProperty("kind").GetString())
                Assert.Equal("semanticallyReady", review.GetProperty("readiness").GetProperty("kind").GetString())
                let change = review.GetProperty("diff").GetProperty("parameterChange")
                Assert.Equal(StableId.value classify.Id, change.GetProperty("functionId").GetString())
                Assert.Equal(StableId.value classifyInput.Id, change.GetProperty("parameterId").GetString())
                result.GetProperty("proposalId").GetString()

            use commitDocument =
                AuthoringProtocol.executeLine
                    session
                    ($"{{\"id\":\"commit\",\"op\":\"commit\",\"proposalId\":\"{proposalId}\"}}")
                |> requireOk
            let publishedSnapshotId =
                commitDocument.RootElement.GetProperty("result").GetProperty("publishedSnapshotId").GetString()

            let migratedClassify =
                session.Declarations().Functions
                |> List.find (fun item -> item.Id = classify.Id)
            Assert.Equal(classifyInput.Id, migratedClassify.Parameters.Head.Id)
            Assert.Equal(TypeRef.Named(requestType.Id, []), migratedClassify.Parameters.Head.Type)

            let migratedProcess =
                session.Declarations().Functions
                |> List.find (fun item -> item.Id = processFunction.Id)
            Assert.Equal(processInput.Id, migratedProcess.Parameters.Head.Id)
            Assert.Equal(stringType, migratedProcess.Parameters.Head.Type)

            session.Close()

            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            Assert.Equal(publishedSnapshotId, StableId.value (currentSnapshotId reopened))
            let reopenedClassify =
                reopened.Declarations().Functions
                |> List.find (fun item -> item.Id = classify.Id)
            Assert.Equal(classifyInput.Id, reopenedClassify.Parameters.Head.Id)
            Assert.Equal(TypeRef.Named(requestType.Id, []), reopenedClassify.Parameters.Head.Type)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
