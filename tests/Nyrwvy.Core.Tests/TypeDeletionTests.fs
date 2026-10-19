namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module TypeDeletionTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("type-deletion-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private removedTypeId = id<TypeTag> "obsolete"
    let private retainedTypeId = id<TypeTag> "retained"
    let private fieldId = id<FieldTag> "obsolete-field"
    let private obsoleteFunctionId = id<FunctionTag> "obsolete-function"
    let private retainedFunctionId = id<FunctionTag> "retained-function"

    let private fixture () =
        let obsolete: SemanticType =
            { Exported = true; TypeParameters = []; Id = removedTypeId
              DisplayName = "Obsolete"
              Kind = SemanticTypeKind.Product [ { Id = fieldId; DisplayName = "value"; Type = stringType } ] }
        let retained: SemanticType =
            { Exported = true; TypeParameters = []; Id = retainedTypeId
              DisplayName = "Retained"
              Kind = SemanticTypeKind.Product [] }
        let parameter: SemanticParameter =
            { Id = id<ParameterTag> "argument"; DisplayName = "value"; Type = stringType }
        let inputId = id<GraphNodeTag> "input"
        let constructId = id<GraphNodeTag> "construct"
        let obsoleteFn: SemanticFunction =
            { Exported = true; Id = obsoleteFunctionId
              TypeParameters = []
              DisplayName = "createObsolete"
              Parameters = [ parameter ]
              ResultType = TypeRef.Named(removedTypeId, [])
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = inputId; Kind = SemanticNodeKind.ParameterReference parameter.Id }
                      { Id = constructId
                        Kind = SemanticNodeKind.ProductConstruct(removedTypeId, [],
                            [ { TargetFieldId = fieldId; ValueNodeId = inputId } ]) } ]
                  ResultNodeId = constructId } }
        let retainedFn: SemanticFunction =
            { Exported = true; Id = retainedFunctionId
              TypeParameters = []
              DisplayName = "keep"
              Parameters = []
              ResultType = stringType
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = id<GraphNodeTag> "keep-literal"; Kind = SemanticNodeKind.StringLiteral "unchanged" } ]
                  ResultNodeId = id<GraphNodeTag> "keep-literal" } }
        { Snapshot.empty (id<SnapshotTag> "base") with
            Types = [ obsolete; retained ]
            Functions = [ obsoleteFn; retainedFn ] }

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok result -> result
        | Error error -> failwithf "Invalid fixture: %A" error

    [<Fact>]
    let ``function and type deletion compose atomically without altering unrelated identities`` () =
        let initial = fixture ()
        let ws = workspace initial
        let operations =
            [ AgentOperationIntent.DeleteFunction obsoleteFunctionId
              AgentOperationIntent.DeleteType removedTypeId ]
        let proposal = AgentWorkflow.simulate ws operations |> function
            | Ok value -> value
            | Error error -> failwithf "Simulation failed: %A" error
        let review = AgentChangeProposal.review proposal
        let removed = List.exactlyOne review.ResultingChange.Diff.TypesRemoved
        Assert.Equal(removedTypeId, removed.TypeId)
        Assert.Equal<FieldId list>([ fieldId ], removed.RemovedFieldIds)
        Assert.Empty(removed.RemovedVariantIds)
        Assert.Empty(review.ResultingChange.AffectedTypeIds)
        Assert.Empty(review.ResultingChange.FunctionFocus)
        Assert.Equal(obsoleteFunctionId, (List.exactlyOne review.ResultingChange.Diff.FunctionsRemoved).FunctionId)
        Assert.Equal(initial.Id, AgentWorkspace.currentSnapshotId ws)
        let updated = AgentWorkflow.commit ws proposal |> function
            | AgentCommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let snapshot = AgentWorkspace.snapshotForStructuralEditing updated
        Assert.Empty(SnapshotValidator.validate snapshot)
        Assert.Equal<SemanticType list>([ initial.Types[1] ], snapshot.Types)
        Assert.Equal<SemanticFunction list>([ initial.Functions[1] ], snapshot.Functions)
        let json = AgentWorkspace.serializeCurrentSnapshot updated |> function
            | Ok text -> text
            | Error error -> failwithf "Serialization failed: %A" error
        let reopened = SnapshotCodec.deserialize json |> function
            | Ok value -> value
            | Error error -> failwithf "Reopen failed: %A" error
        Assert.Equal<TypeId list>([ retainedTypeId ], reopened.Types |> List.map (fun item -> item.Id))
        Assert.Equal<FunctionId list>([ retainedFunctionId ], reopened.Functions |> List.map (fun item -> item.Id))

    [<Fact>]
    let ``type deletion refuses function and nested type references and field edits in the same transaction`` () =
        let initial = fixture ()
        let ws = workspace initial
        let reject snapshot operations expected =
            match AgentWorkflow.simulate (workspace snapshot) operations with
            | Error(AgentWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal(expected, diagnostic.Code)
            | other -> failwithf "Expected %s, got %A" expected other
        reject initial [ AgentOperationIntent.DeleteType removedTypeId ] "TypeStillReferenced"
        let retained = initial.Types[1]
        let referencing =
            { initial with
                Functions = initial.Functions |> List.filter (fun fn -> fn.Id <> obsoleteFunctionId)
                Types =
                    [ initial.Types[0]
                      { retained with
                          Kind = SemanticTypeKind.Product
                            [ { Id = id<FieldTag> "nested-reference"
                                DisplayName = "nested"
                                Type = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Named(removedTypeId, []) ]) } ] } ] }
        reject referencing [ AgentOperationIntent.DeleteType removedTypeId ] "TypeStillReferenced"
        let unused =
            { initial with Functions = initial.Functions |> List.filter (fun fn -> fn.Id <> obsoleteFunctionId) }
        reject unused
            [ AgentOperationIntent.RemoveProductField fieldId
              AgentOperationIntent.DeleteType removedTypeId ]
            "TypeModifiedInTransaction"
        Assert.Equal(initial.Id, AgentWorkspace.currentSnapshotId ws)

    [<Fact>]
    let ``review lists deleted Union variant and field identities`` () =
        let unionId = id<TypeTag> "unused-union"
        let variantId = id<VariantTag> "unused-variant"
        let unionFieldId = id<FieldTag> "unused-union-field"
        let union: SemanticType =
            { Exported = true; TypeParameters = []; Id = unionId
              DisplayName = "UnusedUnion"
              Kind = SemanticTypeKind.Union
                [ { Id = variantId
                    DisplayName = "Item"
                    Fields = [ { Id = unionFieldId; DisplayName = "value"; Type = stringType } ] } ] }
        let initial = { Snapshot.empty (id<SnapshotTag> "union") with Types = [ union ] }
        let ws = workspace initial
        let proposal = AgentWorkflow.simulate ws [ AgentOperationIntent.DeleteType unionId ] |> function
            | Ok value -> value
            | Error error -> failwithf "Cannot delete unused Union: %A" error
        let removed = AgentChangeProposal.review proposal |> fun review -> List.exactlyOne review.ResultingChange.Diff.TypesRemoved
        Assert.Equal<VariantId list>([ variantId ], removed.RemovedVariantIds)
        Assert.Equal<FieldId list>([ unionFieldId ], removed.RemovedFieldIds)

    [<Fact>]
    let ``JSONL simulates and commits function and type cleanup across reopen`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-delete-type-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let initial = fixture ()
            File.WriteAllText(path, SnapshotCodec.serialize initial |> function
                | Ok text -> text
                | Error error -> failwithf "Serialization failed: %A" error)
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let command =
                $"""{{"id":"cleanup","op":"simulateOperations","operations":[{{"kind":"deleteFunction","functionId":"{StableId.value obsoleteFunctionId}"}},{{"kind":"deleteType","typeId":"{StableId.value removedTypeId}"}}]}}"""
            use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let result = response.RootElement.GetProperty("result")
            Assert.Equal(1, result.GetProperty("review").GetProperty("diff").GetProperty("typesRemoved").GetArrayLength())
            let proposalId = result.GetProperty("proposalId").GetString()
            use commit =
                AuthoringProtocol.executeLine session ($"""{{"id":"commit","op":"commit","proposalId":"{proposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            session.Close()
            use reopened = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Reopen failed: %A" error
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> function
                | Ok value -> value
                | Error error -> failwithf "Stored snapshot invalid: %A" error
            Assert.Equal<TypeId list>([ retainedTypeId ], snapshot.Types |> List.map (fun item -> item.Id))
            Assert.Equal<FunctionId list>([ retainedFunctionId ], snapshot.Functions |> List.map (fun item -> item.Id))
        finally
            if Directory.Exists root then Directory.Delete(root, true)
