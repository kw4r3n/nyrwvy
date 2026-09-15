namespace NyamNyam.Core.Tests

open Microsoft.FSharp.Reflection
open NyamNyam.Core
open Xunit

module AgentTypeDeclarationTests =
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private int64ListType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ int64Type ])

    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-type-declaration-" + name)
    let private functionId name = StableId.createForTesting<FunctionTag> ("function-type-declaration-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-type-declaration-" + name)
    let private typeId name = StableId.createForTesting<TypeTag> ("type-type-declaration-" + name)

    let private field displayName typeRef: AgentTypeFieldIntent =
        { DisplayName = displayName
          Type = typeRef }

    let private variant displayName fields: AgentTypeVariantIntent =
        { DisplayName = displayName
          Fields = fields }

    let private workspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok value -> value
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private simulate ws request =
        match AgentTypeDeclarationWorkflow.simulate ws request with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected type-declaration simulation failure: %A" error

    let private commit ws proposal =
        match AgentTypeDeclarationWorkflow.commit ws proposal with
        | AgentTypeDeclarationCommitResult.Rejected diagnostics ->
            failwithf "Unexpected type-declaration commit failure: %A" diagnostics
        | AgentTypeDeclarationCommitResult.Committed(next, publication) -> next, publication

    let private createFunction ws request =
        let proposal =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Ok value -> value
            | Error error -> failwithf "Unexpected function-creation simulation failure: %A" error
        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics ->
            failwithf "Unexpected function-creation commit failure: %A" diagnostics
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, publication) ->
            next, publication.CreatedFunctionId

    let private untouchedFunction () =
        let resultId = nodeId "untouched-result"
        { Id = functionId "untouched"
          DisplayName = "untouched"
          Parameters = []
          ResultType = unitType
          RequiredEffects = []
          Body =
            { Nodes = [ { Id = resultId; Kind = SemanticNodeKind.UnitLiteral } ]
              ResultNodeId = resultId } }

    [<Fact>]
    let ``product and union declarations allocate identities publish and compose with function authoring`` () =
        let untouched = untouchedFunction ()
        let snapshot =
            { Snapshot.empty (snapshotId "composition") with
                Functions = [ untouched ] }
        let ws0 = workspace snapshot

        let productRequest: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "Record"
              Kind =
                AgentTypeDeclarationKindIntent.Product
                    [ field "name" stringType
                      field "values" int64ListType ] }
        let productProposal = simulate ws0 productRequest
        let productReview = AgentTypeDeclarationProposal.review productProposal
        Assert.Equal(productRequest, productReview.RequestedChange)
        Assert.Equal(TransactionReadiness.SemanticallyReady, productReview.Readiness)
        Assert.Equal(AgentTypeDeclarationProposal.proposedSnapshotId productProposal, productReview.Receipt.ProposedSnapshotId)

        let productType = productReview.CreatedType
        let productFields =
            match productType.Kind with
            | SemanticTypeKind.Product fields -> fields
            | other -> failwithf "Expected product type, got %A" other
        Assert.Equal(2, productFields.Length)
        Assert.NotEqual(productFields[0].Id, productFields[1].Id)
        let ws1, productPublication = commit ws0 productProposal
        Assert.Equal(productType.Id, productPublication.CreatedTypeId)
        match AgentWorkspace.getType ws1 productType.Id with
        | Ok published -> Assert.Equal(productType, published)
        | Error error -> failwithf "Published product was not discoverable: %A" error

        let unionRequest: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws1
              DisplayName = "Choice"
              Kind =
                AgentTypeDeclarationKindIntent.Union
                    [ variant "Number" [ field "value" int64Type ]
                      variant "Empty" [] ] }
        let unionProposal = simulate ws1 unionRequest
        let unionReview = AgentTypeDeclarationProposal.review unionProposal
        let unionType = unionReview.CreatedType
        let variants =
            match unionType.Kind with
            | SemanticTypeKind.Union variants -> variants
            | other -> failwithf "Expected union type, got %A" other
        Assert.Equal(2, variants.Length)
        Assert.NotEqual(variants[0].Id, variants[1].Id)
        let numberVariant = variants |> List.find (fun item -> item.DisplayName = "Number")
        let emptyVariant = variants |> List.find (fun item -> item.DisplayName = "Empty")
        let numberField = List.exactlyOne numberVariant.Fields
        let ws2, unionPublication = commit ws1 unionProposal
        Assert.Equal(unionType.Id, unionPublication.CreatedTypeId)
        match AgentWorkspace.getType ws2 unionType.Id with
        | Ok published -> Assert.Equal(unionType, published)
        | Error error -> failwithf "Published union was not discoverable: %A" error

        match AgentWorkspace.getFunction ws2 untouched.Id with
        | Ok preserved -> Assert.Equal(untouched, preserved)
        | Error error -> failwithf "Unrelated function disappeared: %A" error

        let makeProductRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws2
              DisplayName = "make_record"
              Parameters = []
              ResultType = TypeRef.Named productType.Id
              RequiredEffects = []
              Body =
                ExpressionRecipe.ProductConstruct(
                    productType.Id,
                    [ productFields[0].Id, ExpressionRecipe.StringLiteral "created"
                      productFields[1].Id,
                        ExpressionRecipe.ListConstruct(
                            int64Type,
                            [ ExpressionRecipe.Int64Literal 1L
                              ExpressionRecipe.Int64Literal 2L ]
                        ) ]
                ) }
        let ws3, makeProductId = createFunction ws2 makeProductRequest

        let accessProductRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws3
              DisplayName = "record_name"
              Parameters =
                [ { DisplayName = "record"
                    Type = TypeRef.Named productType.Id } ]
              ResultType = stringType
              RequiredEffects = []
              Body =
                ExpressionRecipe.FieldAccess(
                    ExpressionRecipe.ParameterSlot 0,
                    productFields[0].Id
                ) }
        let ws4, accessProductId = createFunction ws3 accessProductRequest

        let makeUnionRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws4
              DisplayName = "make_number"
              Parameters = []
              ResultType = TypeRef.Named unionType.Id
              RequiredEffects = []
              Body =
                ExpressionRecipe.UnionConstruct(
                    numberVariant.Id,
                    [ numberField.Id, ExpressionRecipe.Int64Literal 7L ]
                ) }
        let ws5, makeUnionId = createFunction ws4 makeUnionRequest

        let matchUnionRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws5
              DisplayName = "choice_value"
              Parameters =
                [ { DisplayName = "choice"
                    Type = TypeRef.Named unionType.Id } ]
              ResultType = int64Type
              RequiredEffects = []
              Body =
                ExpressionRecipe.MatchUnion(
                    ExpressionRecipe.ParameterSlot 0,
                    [ numberVariant.Id,
                        ExpressionRecipe.PatternReference
                            { ScopeDepth = 0
                              Slot = RecipePatternSlot.Field numberField.Id }
                      emptyVariant.Id, ExpressionRecipe.Int64Literal 0L ]
                ) }
        let ws6, matchUnionId = createFunction ws5 matchUnionRequest

        for createdFunctionId in [ makeProductId; accessProductId; makeUnionId; matchUnionId ] do
            match AgentWorkspace.getFunction ws6 createdFunctionId with
            | Ok _ -> ()
            | Error error -> failwithf "Composed function was not published: %A" error

        match AgentWorkspace.getFunction ws6 untouched.Id with
        | Ok preserved -> Assert.Equal(untouched, preserved)
        | Error error -> failwithf "Unrelated semantic state changed: %A" error

    [<Fact>]
    let ``invalid type declaration intent rejects without publication`` () =
        let snapshot = Snapshot.empty (snapshotId "invalid")
        let ws = workspace snapshot
        let unknownType = typeId "unknown"

        let unknownFieldRequest: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "BadProduct"
              Kind =
                AgentTypeDeclarationKindIntent.Product
                    [ field "missing" (TypeRef.Named unknownType) ] }
        match AgentTypeDeclarationWorkflow.simulate ws unknownFieldRequest with
        | Error(AgentTypeDeclarationWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("TypeDeclarationUnknownNamedType", diagnostic.Code)
        | result -> failwithf "Expected unknown field type rejection, got %A" result

        let blankVariantRequest: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "BadUnion"
              Kind = AgentTypeDeclarationKindIntent.Union [ variant " " [] ] }
        match AgentTypeDeclarationWorkflow.simulate ws blankVariantRequest with
        | Error(AgentTypeDeclarationWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("TypeDeclarationVariantDisplayNameBlank", diagnostic.Code)
        | result -> failwithf "Expected blank variant rejection, got %A" result

        Assert.Equal(snapshot.Id, AgentWorkspace.currentSnapshotId ws)
        Assert.Empty(AgentWorkspace.listTypes ws)

    [<Fact>]
    let ``type declaration review binds exact result and stale proposal cannot publish`` () =
        let snapshot = Snapshot.empty (snapshotId "stale")
        let ws = workspace snapshot
        let request name: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = name
              Kind = AgentTypeDeclarationKindIntent.Product [] }

        let first = simulate ws (request "First")
        let second = simulate ws (request "Second")
        let firstReview = AgentTypeDeclarationProposal.review first
        Assert.Equal(firstReview.Receipt.TransactionId, AgentTypeDeclarationProposal.transactionId first)
        Assert.Equal(firstReview.Receipt.ProposedSnapshotId, AgentTypeDeclarationProposal.proposedSnapshotId first)
        Assert.Equal("First", firstReview.CreatedType.DisplayName)

        let advanced, _ = commit ws first
        match AgentTypeDeclarationWorkflow.commit advanced second with
        | AgentTypeDeclarationCommitResult.Rejected [ diagnostic ] ->
            Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
        | result -> failwithf "Expected stale type-declaration rejection, got %A" result

        let names = AgentWorkspace.listTypes advanced |> List.map (fun item -> item.DisplayName)
        Assert.Equal<string list>([ "First" ], names)

    [<Fact>]
    let ``type declaration request exposes intent without production identity authority`` () =
        let requestFields =
            FSharpType.GetRecordFields(typeof<AgentCreateTypeDeclarationRequest>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList
        Assert.Equal<(string * System.Type) list>(
            [ "ExpectedBaseSnapshotId", typeof<SnapshotId>
              "DisplayName", typeof<string>
              "Kind", typeof<AgentTypeDeclarationKindIntent> ],
            requestFields
        )

        let forbidden =
            set
                [ typeof<TypeId>
                  typeof<VariantId>
                  typeof<FieldId>
                  typeof<SemanticType>
                  typeof<SemanticVariant>
                  typeof<SemanticField>
                  typeof<Snapshot> ]
        let intentTypes =
            [ typeof<AgentTypeFieldIntent>
              typeof<AgentTypeVariantIntent> ]
            |> List.collect (fun recordType ->
                FSharpType.GetRecordFields(recordType)
                |> Array.map (fun field -> field.PropertyType)
                |> Array.toList)
        let kindPayloadTypes =
            FSharpType.GetUnionCases(typeof<AgentTypeDeclarationKindIntent>)
            |> Array.collect (fun unionCase -> unionCase.GetFields())
            |> Array.map (fun field -> field.PropertyType)
            |> Array.toList

        Assert.DoesNotContain(intentTypes @ kindPayloadTypes, fun fieldType -> Set.contains fieldType forbidden)
