namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ProductFieldEvolutionTests =
    let private stable<'tag> name = StableId.createForTesting<'tag> ("product-evolution-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private productId = stable<TypeTag> "product"
    let private originalFieldId = stable<FieldTag> "original"
    let private directId = stable<FunctionTag> "direct"
    let private nestedId = stable<FunctionTag> "nested"
    let private directProductId = stable<GraphNodeTag> "direct-product"
    let private trueProductId = stable<GraphNodeTag> "true-product"
    let private falseProductId = stable<GraphNodeTag> "false-product"
    let private directValueId = stable<GraphNodeTag> "direct-input"
    let private trueValueId = stable<GraphNodeTag> "true-input"
    let private falseValueId = stable<GraphNodeTag> "false-value"

    let private fixture () =
        let product: SemanticType =
            { Exported = true; TypeParameters = []; Id = productId
              DisplayName = "Record"
              Kind = SemanticTypeKind.Product
                [ { Id = originalFieldId; DisplayName = "original"; Type = stringType } ] }
        let resultType = TypeRef.Named(product.Id, [])
        let directParameter: SemanticParameter =
            { Id = stable<ParameterTag> "direct-parameter"; DisplayName = "input"; Type = stringType }
        let direct: SemanticFunction =
            { Exported = true; Id = directId
              DisplayName = "direct"
              Parameters = [ directParameter ]
              ResultType = resultType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = directValueId; Kind = SemanticNodeKind.ParameterReference directParameter.Id }
                      { Id = directProductId
                        Kind = SemanticNodeKind.ProductConstruct(productId, [],
                            [ { TargetFieldId = originalFieldId; ValueNodeId = directValueId } ]) } ]
                  ResultNodeId = directProductId } }
        let nestedParameter: SemanticParameter =
            { Id = stable<ParameterTag> "nested-parameter"; DisplayName = "input"; Type = stringType }
        let trueBranch: ValueGraph =
            { Nodes =
                [ { Id = trueValueId; Kind = SemanticNodeKind.ParameterReference nestedParameter.Id }
                  { Id = trueProductId
                    Kind = SemanticNodeKind.ProductConstruct(productId, [],
                        [ { TargetFieldId = originalFieldId; ValueNodeId = trueValueId } ]) } ]
              ResultNodeId = trueProductId }
        let falseBranch: ValueGraph =
            { Nodes =
                [ { Id = falseValueId; Kind = SemanticNodeKind.StringLiteral "other" }
                  { Id = falseProductId
                    Kind = SemanticNodeKind.ProductConstruct(productId, [],
                        [ { TargetFieldId = originalFieldId; ValueNodeId = falseValueId } ]) } ]
              ResultNodeId = falseProductId }
        let conditionId = stable<GraphNodeTag> "condition"
        let resultId = stable<GraphNodeTag> "result"
        let nested: SemanticFunction =
            { Exported = true; Id = nestedId
              DisplayName = "nested"
              Parameters = [ nestedParameter ]
              ResultType = resultType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = conditionId; Kind = SemanticNodeKind.BoolLiteral true }
                      { Id = resultId; Kind = SemanticNodeKind.Conditional(conditionId, trueBranch, falseBranch) } ]
                  ResultNodeId = resultId } }
        { Snapshot.empty (stable<SnapshotTag> "snapshot") with
            Types = [ product ]
            Functions = [ direct; nested ] }

    let private intent plans: AgentAddProductFieldIntent =
        { TargetTypeId = productId
          NewFieldDisplayName = "added"
          NewFieldType = stringType
          ConstructionPlans = plans }

    let private plans =
        [ { ConstructNodeId = directProductId; ValueNodeId = directValueId }
          { ConstructNodeId = trueProductId; ValueNodeId = trueValueId }
          { ConstructNodeId = falseProductId; ValueNodeId = falseValueId } ]

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok result -> result
        | Error errors -> failwithf "Invalid fixture: %A" errors

    let private simulate ws action =
        match AgentWorkflow.simulate ws [ action ] with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected simulation error: %A" error

    let private published ws proposal =
        match AgentWorkflow.commit ws proposal with
        | AgentCommitResult.Committed(next, _) -> next
        | AgentCommitResult.Rejected errors -> failwithf "Unexpected commit failure: %A" errors

    let private newField (snapshot: Snapshot) =
        match (List.exactlyOne snapshot.Types).Kind with
        | SemanticTypeKind.Product fields -> fields |> List.find (fun field -> field.DisplayName = "added")
        | _ -> failwith "Expected Product."

    [<Fact>]
    let ``Product field and all nested constructors publish atomically without changing existing identity`` () =
        let initial = fixture ()
        let ws = workspace initial
        let proposal = simulate ws (AgentOperationIntent.AddProductField(intent plans))
        let review = AgentChangeProposal.review proposal
        let added = List.exactlyOne review.ResultingChange.Diff.ProductFieldsAdded
        Assert.Equal(productId, added.OwnerTypeId)
        Assert.Equal<string list>([ "direct"; "nested" ], review.ResultingChange.FunctionFocus |> List.map (fun focus -> focus.Function.DisplayName) |> List.sort)
        Assert.Equal<TypeId list>([ productId ], review.ResultingChange.AffectedTypeIds)
        Assert.Equal(3, review.ResultingChange.Diff.ProductFieldsBound.Length)
        let updated = published ws proposal
        let snapshot = AgentWorkspace.snapshotForStructuralEditing updated
        Assert.Empty(SnapshotValidator.validate snapshot)
        Assert.Equal(productId, (List.exactlyOne snapshot.Types).Id)
        Assert.Equal(added.AddedFieldId, (newField snapshot).Id)
        Assert.Equal<FunctionId list>([ directId; nestedId ], snapshot.Functions |> List.map (fun fn -> fn.Id))
        for expectedNodeId in [ directProductId; trueProductId; falseProductId ] do
            let node =
                snapshot.Functions
                |> List.collect (fun fn -> ValueGraph.collectNodes fn.Body)
                |> List.find (fun node -> node.Id = expectedNodeId)
            match node.Kind with
            | SemanticNodeKind.ProductConstruct(typeId, _, fields) ->
                Assert.Equal(productId, typeId)
                Assert.Equal<Set<FieldId>>(Set.ofList [ originalFieldId; added.AddedFieldId ], fields |> List.map (fun field -> field.TargetFieldId) |> Set.ofList)
            | _ -> failwith "Product construct identity changed."
        let json = AgentWorkspace.serializeCurrentSnapshot updated |> function
            | Ok text -> text
            | Error error -> failwithf "Serialization failed: %A" error
        let reopened = SnapshotCodec.deserialize json |> function
            | Ok value -> value
            | Error error -> failwithf "Reopen failed: %A" error
        Assert.Equal(added.AddedFieldId, (newField reopened).Id)
        let execution =
            match TypedSemanticIr.project reopened with
            | Ok typed -> ExecutionIr.lower typed
            | Error error -> failwithf "Projection failed: %A" error
        match execution with
        | Ok program ->
            match CSharpBackend.emit program CSharpBackendProfile.empty with
            | Ok artifact -> Assert.Contains(CSharpBackendNames.fieldName added.AddedFieldId, artifact.Source)
            | Error error -> failwithf "Backend emission failed: %A" error
        | Error error -> failwithf "Lowering failed: %A" error

    [<Fact>]
    let ``removing an unreferenced Product field repairs nested constructions and preserves identities`` () =
        let initial = fixture ()
        let first = workspace initial
        let added = published first (simulate first (AgentOperationIntent.AddProductField(intent plans)))
        let addedSnapshot = AgentWorkspace.snapshotForStructuralEditing added
        let fieldId = (newField addedSnapshot).Id
        let proposal = simulate added (AgentOperationIntent.RemoveProductField fieldId)
        let review = AgentChangeProposal.review proposal
        let removed = List.exactlyOne review.ResultingChange.Diff.ProductFieldsRemoved
        Assert.Equal(fieldId, removed.RemovedFieldId)
        Assert.Equal<GraphNodeId list>(plans |> List.map (fun plan -> plan.ConstructNodeId) |> List.sortBy StableId.value,
            removed.AffectedConstructNodeIds)
        Assert.Equal<TypeId list>([ productId ], review.ResultingChange.AffectedTypeIds)
        Assert.Equal(2, review.ResultingChange.FunctionFocus.Length)
        let updated = published added proposal
        let snapshot = AgentWorkspace.snapshotForStructuralEditing updated
        Assert.Empty(SnapshotValidator.validate snapshot)
        Assert.Equal<SemanticType list>(initial.Types, snapshot.Types)
        Assert.Equal<SemanticFunction list>(initial.Functions, snapshot.Functions)
        let json = AgentWorkspace.serializeCurrentSnapshot updated |> function
            | Ok text -> text
            | Error error -> failwithf "Serialization failed: %A" error
        match SnapshotCodec.deserialize json with
        | Ok reopened ->
            Assert.Equal<SemanticType list>(initial.Types, reopened.Types)
            Assert.Equal<FunctionId list>(initial.Functions |> List.map (fun fn -> fn.Id),
                reopened.Functions |> List.map (fun fn -> fn.Id))
            for fn in reopened.Functions do
                let before = initial.Functions |> List.find (fun item -> item.Id = fn.Id)
                Assert.Equal<GraphNodeId list>(
                    ValueGraph.collectNodes before.Body |> List.map (fun node -> node.Id) |> List.sortBy StableId.value,
                    ValueGraph.collectNodes fn.Body |> List.map (fun node -> node.Id) |> List.sortBy StableId.value)
        | Error error -> failwithf "Reopen failed: %A" error

    [<Fact>]
    let ``Product field deletion rejects live accesses and invalid field identities`` () =
        let initial = fixture ()
        let productInput: SemanticParameter =
            { Id = stable<ParameterTag> "access-parameter"; DisplayName = "value"; Type = TypeRef.Named(productId, []) }
        let sourceId = stable<GraphNodeTag> "access-source"
        let accessId = stable<GraphNodeTag> "access"
        let accessor: SemanticFunction =
            { Exported = true; Id = stable<FunctionTag> "accessor"
              DisplayName = "accessor"
              Parameters = [ productInput ]
              ResultType = stringType
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = sourceId; Kind = SemanticNodeKind.ParameterReference productInput.Id }
                      { Id = accessId; Kind = SemanticNodeKind.FieldAccess(sourceId, originalFieldId) } ]
                  ResultNodeId = accessId } }
        let ws = workspace { initial with Functions = accessor :: initial.Functions }
        match AgentWorkflow.simulate ws [ AgentOperationIntent.RemoveProductField originalFieldId ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("ProductFieldStillReferenced", diagnostic.Code)
        | other -> failwithf "Referenced field was removed: %A" other
        match AgentWorkflow.simulate ws [ AgentOperationIntent.RemoveProductField(stable<FieldTag> "missing") ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal("TargetNotFound", diagnostic.Code)
        | other -> failwithf "Missing field was removed: %A" other
        Assert.Equal(initial.Id, AgentWorkspace.currentSnapshotId ws)

    [<Fact>]
    let ``Product field removal does not discard potentially effectful field evaluation`` () =
        let initial = fixture ()
        let effectId = stable<EffectTag> "observable"
        let withEffects =
            { initial with
                Effects = [ { Exported = true; Id = effectId; DisplayName = "observable" } ]
                Functions =
                    initial.Functions
                    |> List.map (fun fn ->
                        if fn.Id = directId then { fn with RequiredEffects = [ effectId ] } else fn) }
        let ws = workspace withEffects
        match AgentWorkflow.simulate ws [ AgentOperationIntent.RemoveProductField originalFieldId ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("ProductFieldMayDiscardEffects", diagnostic.Code)
        | other -> failwithf "Effectful field evaluation was discarded: %A" other
        let fieldId = stable<FieldTag> "new-and-removed"
        let request: TransactionRequest =
            { Id = StableIdAllocator.newTransactionId ()
              BaseSnapshotId = initial.Id
              ProposedSnapshotId = StableIdAllocator.newSnapshotId ()
              Operations =
                [ SemanticOperation.AddProductField
                    { TargetTypeId = productId
                      NewFieldId = fieldId
                      NewFieldDisplayName = "temporary"
                      NewFieldType = stringType
                      ConstructionPlans = plans }
                  SemanticOperation.RemoveProductField fieldId ] }
        match Transaction.simulate initial request with
        | SimulationResult.Rejected [ diagnostic ] -> Assert.Equal("ProductFieldCreatedInTransaction", diagnostic.Code)
        | other -> failwithf "Add-remove no-op transaction was accepted: %A" other

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated C# executes reviewed Product field evolution`` () =
        let initial = fixture ()
        let ws = workspace initial
        let proposal = simulate ws (AgentOperationIntent.AddProductField(intent plans))
        let committed = published ws proposal
        let snapshot = AgentWorkspace.snapshotForStructuralEditing committed
        let typed =
            match TypedSemanticIr.project snapshot with
            | Ok value -> value
            | Error error -> failwithf "Projection failed: %A" error
        let execution =
            match ExecutionIr.lower typed with
            | Ok value -> value
            | Error error -> failwithf "Lowering failed: %A" error
        let artifact =
            match CSharpBackend.emit execution CSharpBackendProfile.empty with
            | Ok value -> value
            | Error error -> failwithf "Emission failed: %A" error
        let temp = Path.Combine(Path.GetTempPath(), "nyrwvy-product-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(temp) |> ignore
        try
            let field = CSharpBackendNames.fieldName (newField snapshot).Id
            let direct = CSharpBackendNames.functionName directId
            let nested = CSharpBackendNames.functionName nestedId
            File.WriteAllText(Path.Combine(temp, "Generated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            File.WriteAllText(Path.Combine(temp, "Generated.cs"), artifact.Source)
            let program =
                $"""using System;
using Nyrwvy.Generated;
Console.WriteLine(NyFunctions.{direct}("first").{field});
Console.WriteLine(NyFunctions.{nested}("second").{field});
"""
            File.WriteAllText(Path.Combine(temp, "Program.cs"), program)
            let run args =
                let start = ProcessStartInfo("dotnet")
                start.WorkingDirectory <- temp
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                for arg in args do start.ArgumentList.Add(arg)
                use child = Process.Start(start)
                let output = child.StandardOutput.ReadToEndAsync()
                let errors = child.StandardError.ReadToEndAsync()
                child.WaitForExit()
                if child.ExitCode <> 0 then failwithf "dotnet %A failed: %s\n%s" args output.Result errors.Result
                output.Result
            run [ "build"; "Generated.csproj"; "-v:q" ] |> ignore
            let output = run [ Path.Combine(temp, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("first\nsecond", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists temp then Directory.Delete(temp, true)

    [<Fact>]
    let ``Product evolution rejects missing duplicate and mistyped repair plans before publication`` () =
        let ws = fixture () |> workspace
        let reject expected intentValue =
            match AgentWorkflow.simulate ws [ AgentOperationIntent.AddProductField intentValue ] with
            | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal(expected, diagnostic.Code)
            | other -> failwithf "Expected %s, got %A" expected other
        reject "ProductFieldPlanMismatch" (intent (List.tail plans))
        reject "ProductFieldPlanMismatch" (intent (plans @ [ List.head plans ]))
        let wrong =
            plans |> List.map (fun plan ->
                if plan.ConstructNodeId = trueProductId then
                    { plan with ValueNodeId = stable<GraphNodeTag> "condition" }
                else plan)
        reject "ProductFieldValueTypeMismatch" (intent wrong)
        let wrongScope =
            plans |> List.map (fun plan ->
                if plan.ConstructNodeId = trueProductId then
                    { plan with ValueNodeId = falseValueId }
                else plan)
        match AgentWorkflow.simulate ws [ AgentOperationIntent.AddProductField(intent wrongScope) ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal("InvalidProposedSnapshot", diagnostic.Code)
        | other -> failwithf "Sibling-branch value escaped lexical scope: %A" other
        reject "DuplicateFieldDisplayName" { intent plans with NewFieldDisplayName = "original" }
        Assert.Equal((fixture ()).Id, AgentWorkspace.currentSnapshotId ws)

    [<Fact>]
    let ``unreferenced function deletion reviews removed identities and rejects stale publication`` () =
        let initial = fixture ()
        let ws = workspace initial
        let proposal = simulate ws (AgentOperationIntent.DeleteFunction directId)
        let review = AgentChangeProposal.review proposal
        let removed = List.exactlyOne review.ResultingChange.Diff.FunctionsRemoved
        Assert.Equal(directId, removed.FunctionId)
        Assert.Contains(directProductId, removed.RemovedNodeIds)
        Assert.Empty(review.ResultingChange.AffectedFunctionIds)
        let updated = published ws proposal
        let snapshot = AgentWorkspace.snapshotForStructuralEditing updated
        Assert.Equal<FunctionId list>([ nestedId ], snapshot.Functions |> List.map (fun fn -> fn.Id))
        Assert.Empty(SnapshotValidator.validate snapshot)
        let json = AgentWorkspace.serializeCurrentSnapshot updated |> function
            | Ok text -> text
            | Error error -> failwithf "Serialization failed: %A" error
        match SnapshotCodec.deserialize json with
        | Ok reopened -> Assert.Equal<FunctionId list>([ nestedId ], reopened.Functions |> List.map (fun fn -> fn.Id))
        | Error error -> failwithf "Reopen failed: %A" error
        let rename = simulate ws (AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function nestedId, "renamed"))
        let advanced = published ws rename
        match AgentWorkflow.commit advanced proposal with
        | AgentCommitResult.Rejected [ diagnostic ] -> Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
        | other -> failwithf "Stale deletion was accepted: %A" other

    [<Fact>]
    let ``function deletion refuses live caller references`` () =
        let initial = fixture ()
        let direct = initial.Functions |> List.find (fun fn -> fn.Id = directId)
        let nested = initial.Functions |> List.find (fun fn -> fn.Id = nestedId)
        let parameterNodeId = stable<GraphNodeTag> "caller-input"
        let callNodeId = stable<GraphNodeTag> "caller"
        let referenced =
            { initial with
                Functions =
                    [ direct
                      { nested with
                          Body =
                            { Nodes =
                                [ { Id = parameterNodeId; Kind = SemanticNodeKind.ParameterReference nested.Parameters.Head.Id }
                                  { Id = callNodeId
                                    Kind = SemanticNodeKind.DirectCall(directId,
                                        [ { TargetParameterId = direct.Parameters.Head.Id; ValueNodeId = parameterNodeId } ]) } ]
                              ResultNodeId = callNodeId } } ] }
        let ws = workspace referenced
        match AgentWorkflow.simulate ws [ AgentOperationIntent.DeleteFunction directId ] with
        | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal("FunctionStillReferenced", diagnostic.Code)
        | other -> failwithf "Referenced function deletion was accepted: %A" other
        Assert.Equal(referenced.Id, AgentWorkspace.currentSnapshotId ws)

    [<Fact>]
    let ``authoring JSONL reviews and commits Product repair plans using stable IDs`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-product-field-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let initial = fixture ()
            let source = SnapshotCodec.serialize initial |> function
                | Ok text -> text
                | Error error -> failwithf "Fixture serialization failed: %A" error
            File.WriteAllText(path, source)
            use session = AuthoringSession.openExisting path |> function
                | Ok opened -> opened
                | Error error -> failwithf "Open failed: %A" error
            let repairs =
                plans
                |> List.map (fun plan ->
                    $"""{{"constructNodeId":"{StableId.value plan.ConstructNodeId}","valueNodeId":"{StableId.value plan.ValueNodeId}"}}""")
                |> String.concat ","
            let command =
                $"""{{"id":"repair","op":"simulateOperations","operations":[{{"kind":"addProductField","typeId":"{StableId.value productId}","displayName":"added","type":{{"kind":"primitive","primitive":"string"}},"constructions":[{repairs}]}}]}}"""
            use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            let proposalId = result.GetProperty("proposalId").GetString()
            let diff = result.GetProperty("review").GetProperty("diff")
            Assert.Equal(3, diff.GetProperty("productFieldsBound").GetArrayLength())
            Assert.Equal(1, diff.GetProperty("productFieldsAdded").GetArrayLength())
            use committed =
                AuthoringProtocol.executeLine session ($"""{{"id":"commit","op":"commit","proposalId":"{proposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            session.Close()
            use reopened = AuthoringSession.openExisting path |> function
                | Ok opened -> opened
                | Error error -> failwithf "Reopen failed: %A" error
            let stored =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok value -> value
                | Error error -> failwithf "Stored graph invalid: %A" error
            Assert.Equal("added", (newField stored).DisplayName)
            let deleteRequest =
                $"""{{"id":"delete","op":"simulateOperations","operations":[{{"kind":"deleteFunction","functionId":"{StableId.value directId}"}}]}}"""
            use deletedReview = AuthoringProtocol.executeLine reopened deleteRequest |> JsonDocument.Parse
            Assert.True(deletedReview.RootElement.GetProperty("ok").GetBoolean(), deletedReview.RootElement.ToString())
            let deletionResult = deletedReview.RootElement.GetProperty("result")
            Assert.Equal(1, deletionResult.GetProperty("review").GetProperty("diff").GetProperty("functionsRemoved").GetArrayLength())
            let deletionProposalId = deletionResult.GetProperty("proposalId").GetString()
            use deletedCommit =
                AuthoringProtocol.executeLine reopened ($"""{{"id":"commit","op":"commit","proposalId":"{deletionProposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(deletedCommit.RootElement.GetProperty("ok").GetBoolean(), deletedCommit.RootElement.ToString())
            let fieldId = (newField stored).Id
            let removeRequest =
                $"""{{"id":"remove","op":"simulateOperations","operations":[{{"kind":"removeProductField","fieldId":"{StableId.value fieldId}"}}]}}"""
            use removalReview = AuthoringProtocol.executeLine reopened removeRequest |> JsonDocument.Parse
            Assert.True(removalReview.RootElement.GetProperty("ok").GetBoolean(), removalReview.RootElement.ToString())
            let removalResult = removalReview.RootElement.GetProperty("result")
            Assert.Equal(1, removalResult.GetProperty("review").GetProperty("diff").GetProperty("productFieldsRemoved").GetArrayLength())
            let removalProposalId = removalResult.GetProperty("proposalId").GetString()
            use removalCommit =
                AuthoringProtocol.executeLine reopened ($"""{{"id":"commit","op":"commit","proposalId":"{removalProposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(removalCommit.RootElement.GetProperty("ok").GetBoolean(), removalCommit.RootElement.ToString())
            reopened.Close()
            let afterDelete =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok value -> value
                | Error error -> failwithf "Deleted graph invalid: %A" error
            Assert.Equal<FunctionId list>([ nestedId ], afterDelete.Functions |> List.map (fun fn -> fn.Id))
            Assert.Equal<SemanticType list>(initial.Types, afterDelete.Types)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
