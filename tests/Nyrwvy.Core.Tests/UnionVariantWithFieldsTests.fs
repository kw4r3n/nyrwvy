namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Adding a Union variant with fields "like" an existing variant, with every match arm cloned.
module UnionVariantWithFieldsTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("variant-fields-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private shapeId = id<TypeTag> "shape"
    let private circleId = id<VariantTag> "circle"
    let private emptyId = id<VariantTag> "empty"
    let private radiusId = id<FieldTag> "radius"
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private radius = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Field radiusId }

    let private withFunction displayName parameters resultType body =
        let shape: SemanticType =
            { Id = shapeId; DisplayName = "Shape"
              Kind = SemanticTypeKind.Union
                [ { Id = circleId; DisplayName = "Circle"; Fields = [ { Id = radiusId; DisplayName = "radius"; Type = intType } ] }
                  { Id = emptyId; DisplayName = "Empty"; Fields = [] } ] }
        let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "base") with Types = [ shape ] } |> unwrap "workspace"
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              DisplayName = displayName
              Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> unwrap "create"
        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
        | other -> failwithf "Commit failed: %A" other

    /// `area` squares a Circle's radius and returns 0 for Empty.
    let private fixture () =
        withFunction "area" [ "shape", TypeRef.Named shapeId ] intType (
            ExpressionRecipe.MatchUnion(
                ExpressionRecipe.ParameterSlot 0,
                [ circleId, ExpressionRecipe.Int64WrappingMultiply(radius, radius)
                  emptyId, ExpressionRecipe.Int64Literal 0L ]))

    let private like ws name: AgentAddFieldlessUnionVariantLikeRequest =
        { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
          TargetTypeId = shapeId
          SourceVariantId = circleId
          NewVariantDisplayName = name }

    let private inner: AgentTypeFieldIntent = { DisplayName = "inner"; Type = intType }

    let private arms (snapshot: Snapshot) =
        let area = snapshot.Functions |> List.exactlyOne
        ValueGraph.collectNodes area.Body |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.MatchUnion(_, arms) -> Some arms
            | _ -> None)

    [<Fact>]
    let ``a variant like a source variant copies its fields and clones every match arm`` () =
        let ws = fixture ()
        let proposal = AgentFieldlessUnionVariantEvolutionWorkflow.simulateWithFields ws (like ws "Ring") [ inner ] |> unwrap "simulate"
        let review = AgentFieldlessUnionVariantEvolutionProposal.review proposal
        Assert.Equal(1, review.RepairedMatchCount)
        let ring = review.AddedVariant
        Assert.Equal<string list>([ "radius"; "inner" ], ring.Fields |> List.map (fun field -> field.DisplayName))
        Assert.Equal<TypeRef list>([ intType; intType ], ring.Fields |> List.map (fun field -> field.Type))
        Assert.DoesNotContain(radiusId, ring.Fields |> List.map (fun field -> field.Id))
        let next =
            match AgentFieldlessUnionVariantEvolutionWorkflow.commit ws proposal with
            | AgentFieldlessUnionVariantEvolutionCommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let snapshot = AgentWorkspace.snapshotForStructuralEditing next
        Assert.Empty(SnapshotValidator.validate snapshot)
        let arms = arms snapshot
        let circleArm = arms |> List.find (fun arm -> arm.VariantId = circleId)
        let ringArm = arms |> List.find (fun arm -> arm.VariantId = ring.Id)
        let copiedRadius = ring.Fields.Head.Id
        // Every field is bound: the copied radius, plus the additional field, which is unused so far.
        Assert.Equal<FieldId list>(ring.Fields |> List.map (fun field -> field.Id), ringArm.FieldBindings |> List.map (fun item -> item.SourceFieldId))
        Assert.NotEqual(circleArm.FieldBindings.Head.Binding.Id, ringArm.FieldBindings.Head.Binding.Id)
        // The cloned body refers to the new arm's own binding, not the source arm's.
        let references =
            ValueGraph.collectNodes ringArm.Body |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.PatternReference bindingId -> Some bindingId
                | _ -> None)
        Assert.NotEmpty(references)
        Assert.All(references, fun bindingId -> Assert.Equal(ringArm.FieldBindings.Head.Binding.Id, bindingId))

    [<Fact>]
    let ``match arms inside BoolAnd and BoolOr right operands are repaired and cloned`` () =
        let boolType = TypeRef.Primitive PrimitiveType.Bool
        // `flag && (match shape with Circle -> false || radius = 0 | Empty -> false)`:
        // the match sits in a BoolAnd right graph, and the Circle arm reads its binding in a BoolOr right graph.
        let ws =
            withFunction "flatCircle" [ "shape", TypeRef.Named shapeId; "flag", boolType ] boolType (
                ExpressionRecipe.BoolAnd(
                    ExpressionRecipe.ParameterSlot 1,
                    ExpressionRecipe.MatchUnion(
                        ExpressionRecipe.ParameterSlot 0,
                        [ circleId,
                          ExpressionRecipe.BoolOr(
                              ExpressionRecipe.BoolLiteral false,
                              ExpressionRecipe.Int64Equal(radius, ExpressionRecipe.Int64Literal 0L))
                          emptyId, ExpressionRecipe.BoolLiteral false ])))
        let proposal = AgentFieldlessUnionVariantEvolutionWorkflow.simulateWithFields ws (like ws "Ring") [ inner ] |> unwrap "simulate"
        let review = AgentFieldlessUnionVariantEvolutionProposal.review proposal
        Assert.Equal(1, review.RepairedMatchCount)
        let next =
            match AgentFieldlessUnionVariantEvolutionWorkflow.commit ws proposal with
            | AgentFieldlessUnionVariantEvolutionCommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let snapshot = AgentWorkspace.snapshotForStructuralEditing next
        Assert.Empty(SnapshotValidator.validate snapshot)
        let ringArm = arms snapshot |> List.find (fun arm -> arm.VariantId = review.AddedVariant.Id)
        let cloned =
            ValueGraph.collectNodes ringArm.Body |> List.pick (fun node ->
                match node.Kind with
                | SemanticNodeKind.BoolOr(_, right) -> Some right
                | _ -> None)
        let references =
            ValueGraph.collectNodes cloned |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.PatternReference bindingId -> Some bindingId
                | _ -> None)
        Assert.Equal<PatternBindingId list>([ ringArm.FieldBindings.Head.Binding.Id ], references)

    [<Fact>]
    let ``variant evolution rejects fielded sources through the fieldless entry and invalid names`` () =
        let ws = fixture ()
        let code result =
            match result with
            | Error(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _)) -> diagnostic.Code
            | other -> failwithf "Invalid evolution accepted: %A" other
        Assert.Equal("UnionVariantEvolutionSourceVariantHasFields", code (AgentFieldlessUnionVariantEvolutionWorkflow.simulate ws (like ws "Ring")))
        Assert.Equal("UnionVariantEvolutionFieldDisplayNameInvalid",
            code (AgentFieldlessUnionVariantEvolutionWorkflow.simulateWithFields ws (like ws "Ring") [ { inner with DisplayName = "radius" } ]))
        Assert.Equal("UnionVariantEvolutionDuplicateVariantDisplayName",
            code (AgentFieldlessUnionVariantEvolutionWorkflow.simulateWithFields ws (like ws "Empty") []))

    [<Fact; Trait("Suite", "Extended")>]
    let ``variant with fields is added over the wire and runs through the cloned arm`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-variant-fields-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot (fixture ()) |> unwrap "serialize")
            use session = AuthoringSession.openExisting path |> unwrap "open"
            let line =
                sprintf """{"id":"variant","op":"simulateAddUnionVariantLike","typeId":"%s","sourceVariantId":"%s","displayName":"Ring","additionalFields":[{"displayName":"inner","type":{"kind":"primitive","primitive":"int64"}}]}"""
                    (StableId.value shapeId) (StableId.value circleId)
            use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use committed = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
            Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.ToString())
            let shape = session.Declarations().Types |> List.find (fun item -> item.Id = shapeId)
            let ring =
                match shape.Kind with
                | SemanticTypeKind.Union variants -> variants |> List.find (fun variant -> variant.DisplayName = "Ring")
                | _ -> failwith "Shape must stay a Union."
            let area = (session.Declarations().Functions |> List.exactlyOne).Id
            let fields = ring.Fields |> List.map (fun field -> field.Id, PureRunValue.Int64(if field.DisplayName = "radius" then 7L else 2L))
            let result = PureRun.run session area [ PureRunValue.Union(shapeId, ring.Id, fields) ] (Some(PureRunValue.Int64 49L)) |> unwrap "run"
            Assert.Equal(Some true, result.Matches)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
