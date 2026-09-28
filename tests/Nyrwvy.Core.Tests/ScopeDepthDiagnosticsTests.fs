namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Scope diagnostics name the referenced slot and parameter by display name and list the visible
/// lexical frames innermost first, with the depth where a missing slot is actually bound.
module ScopeDepthDiagnosticsTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("scope-diag-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private optionType item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private pattern depth slot = ExpressionRecipe.PatternReference { ScopeDepth = depth; Slot = slot }

    let private actionTypeId = id<TypeTag> "action"
    let private stopVariantId = id<VariantTag> "stop"
    let private goVariantId = id<VariantTag> "go"
    let private memberFieldId = id<FieldTag> "member"
    let private targetId = id<FunctionTag> "target"
    let private otherId = id<FunctionTag> "other"
    let private resultNodeId = id<GraphNodeTag> "result"
    let private actionParameterId = id<ParameterTag> "action"
    let private maybeParameterId = id<ParameterTag> "maybe"
    let private foreignParameterId = id<ParameterTag> "foreign"

    let private snapshot =
        let action: SemanticType =
            { Id = actionTypeId
              DisplayName = "Action"
              Kind =
                SemanticTypeKind.Union
                    [ { Id = stopVariantId
                        DisplayName = "Stop"
                        Fields = [ { Id = memberFieldId; DisplayName = "member"; Type = stringType } ] }
                      { Id = goVariantId; DisplayName = "Go"; Fields = [] } ] }
        let literalBody name: ValueGraph =
            let nodeId = id<GraphNodeTag> name
            { Nodes = [ ({ Id = nodeId; Kind = SemanticNodeKind.StringLiteral "" }: SemanticNode) ]
              ResultNodeId = nodeId }
        let target: SemanticFunction =
            { Id = targetId
              DisplayName = "target"
              Parameters =
                [ { Id = actionParameterId; DisplayName = "action"; Type = TypeRef.Named actionTypeId }
                  { Id = maybeParameterId; DisplayName = "maybe"; Type = optionType int64Type } ]
              ResultType = stringType
              RequiredEffects = []
              Body = { literalBody "unused" with Nodes = [ { Id = resultNodeId; Kind = SemanticNodeKind.StringLiteral "" } ]; ResultNodeId = resultNodeId } }
        let other: SemanticFunction =
            { Id = otherId
              DisplayName = "other"
              Parameters = [ { Id = foreignParameterId; DisplayName = "foreign"; Type = stringType } ]
              ResultType = stringType
              RequiredEffects = []
              Body = literalBody "other-result" }
        let value: Snapshot =
            { Snapshot.empty (id<SnapshotTag> "base") with
                Types = [ action ]
                Functions = [ target; other ] }
        Assert.Empty(SnapshotValidator.validate value)
        value

    let private rejection replacement =
        let workspace = AgentWorkspace.create snapshot |> function Ok value -> value | Error issues -> failwithf "%A" issues
        let request: AgentReplaceFunctionBodyWithExpressionRecipe =
            { TargetFunctionId = targetId
              ExpectedBaseSnapshotId = snapshot.Id
              ExpectedCurrentResultNodeId = resultNodeId
              Replacement = replacement }
        match AgentExpressionStructuralWorkflow.simulate workspace request with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic
        | other -> failwithf "Expected one rejection, got %A" other

    /// The probe-3 shape: `Stop.member` read from a bindingless None arm inside the `Stop` arm.
    let private stopArmWith noneBody =
        ExpressionRecipe.MatchUnion(
            ExpressionRecipe.Parameter actionParameterId,
            [ stopVariantId,
              ExpressionRecipe.MatchOption(ExpressionRecipe.Parameter maybeParameterId, ExpressionRecipe.StringLiteral "some", noneBody)
              goVariantId, ExpressionRecipe.StringLiteral "go" ]
        )

    [<Fact>]
    let ``a field read from a bindingless None arm names the field and every visible frame`` () =
        let diagnostic = rejection (stopArmWith (pattern 0 (RecipePatternSlot.Field memberFieldId)))
        Assert.Equal("PureExpressionPatternSlotNotFound", diagnostic.Code)
        Assert.Equal(
            $"Pattern-reference slot field 'member' ({StableId.value memberFieldId}) of variant 'Stop' of 'Action' does not exist in scope depth 0. "
            + "The slot is found at depth 1. Visible frames, innermost first: "
            + "depth 0 = None arm of MatchOption (no bindings); depth 1 = arm 'Stop' of MatchUnion on 'Action' (binds 'member').",
            diagnostic.Message
        )
        Assert.DoesNotContain("StableId", diagnostic.Message)

    [<Fact>]
    let ``depth 1 from the None arm is accepted`` () =
        let workspace = AgentWorkspace.create snapshot |> function Ok value -> value | Error issues -> failwithf "%A" issues
        let request: AgentReplaceFunctionBodyWithExpressionRecipe =
            { TargetFunctionId = targetId
              ExpectedBaseSnapshotId = snapshot.Id
              ExpectedCurrentResultNodeId = resultNodeId
              Replacement = stopArmWith (pattern 1 (RecipePatternSlot.Field memberFieldId)) }
        match AgentExpressionStructuralWorkflow.simulate workspace request with
        | Ok _ -> ()
        | Error error -> failwithf "Depth 1 was rejected: %A" error

    [<Fact>]
    let ``a Bind frame is described and a slot bound nowhere gets no found-at hint`` () =
        let diagnostic =
            rejection (ExpressionRecipe.Bind(ExpressionRecipe.StringLiteral "x", pattern 0 RecipePatternSlot.Error))
        Assert.Equal("PureExpressionPatternSlotNotFound", diagnostic.Code)
        Assert.Equal(
            "Pattern-reference slot Error does not exist in scope depth 0. Visible frames, innermost first: depth 0 = Bind body (binds Value).",
            diagnostic.Message
        )

    [<Fact>]
    let ``a depth beyond the visible frames lists them with the found-at hint`` () =
        let diagnostic =
            ExpressionRecipe.Bind(
                ExpressionRecipe.StringLiteral "x",
                ExpressionRecipe.MatchOption(ExpressionRecipe.Parameter maybeParameterId, pattern 3 RecipePatternSlot.Value, ExpressionRecipe.StringLiteral "none")
            )
            |> rejection
        Assert.Equal("PureExpressionPatternScopeNotFound", diagnostic.Code)
        Assert.Equal(
            "Pattern-reference scope depth 3 for slot Value is not visible here. The slot is found at depths 0, 1 (the innermost is usually meant). "
            + "Visible frames, innermost first: depth 0 = Some arm of MatchOption (binds Value); depth 1 = Bind body (binds Value).",
            diagnostic.Message
        )
        let outside = rejection (pattern 0 RecipePatternSlot.Value)
        Assert.Equal("PureExpressionPatternScopeNotFound", outside.Code)
        Assert.EndsWith("No frames are visible here; only a Bind body or a match arm opens one.", outside.Message)

    [<Fact>]
    let ``a parameter of another function is named with its owner and the target's parameters`` () =
        let diagnostic = rejection (ExpressionRecipe.Parameter foreignParameterId)
        Assert.Equal("PureExpressionParameterOutOfScope", diagnostic.Code)
        Assert.Equal(
            $"Parameter 'foreign' ({StableId.value foreignParameterId}) of function 'other' ({StableId.value otherId}) is not owned by "
            + $"target function 'target' ({StableId.value targetId}). Parameters of target function 'target' ({StableId.value targetId}): "
            + $"0 = 'action' ({StableId.value actionParameterId}), 1 = 'maybe' ({StableId.value maybeParameterId}).",
            diagnostic.Message
        )

    [<Fact>]
    let ``a parameter slot out of range lists the requested parameters by name`` () =
        let workspace = AgentWorkspace.create snapshot |> function Ok value -> value | Error issues -> failwithf "%A" issues
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "fresh"
              Parameters = [ { DisplayName = "text"; Type = stringType } ]
              ResultType = stringType
              RequiredEffects = []
              Body = ExpressionRecipe.ParameterSlot 2 }
        match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace request with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("PureExpressionParameterSlotOutOfRange", diagnostic.Code)
            Assert.Equal(
                "Parameter slot 2 is outside the requested parameter list. Parameters of the function being created: 0 = 'text'.",
                diagnostic.Message
            )
        | other -> failwithf "Expected one rejection, got %A" other

    [<Fact>]
    let ``name-resolved recipes get the same frame description`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-scope-diag-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> function Ok json -> json | Error error -> failwithf "%A" error)
            use session = AuthoringSession.openExisting path |> function Ok value -> value | Error error -> failwithf "%A" error
            let request depth =
                let reference =
                    sprintf """{"kind":"patternReference","scopeDepth":%d,"slot":"field","type":"Action","variant":"Stop","field":"member"}""" depth
                sprintf
                    """{"id":"create","op":"simulateCreateFunctionByName","displayName":"describe","parameters":[{"displayName":"action","type":{"kind":"named","name":"Action"}},{"displayName":"maybe","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}}],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"kind":"matchUnion","type":"Action","input":{"kind":"parameter","name":"action"},"arms":[{"variant":"Stop","body":{"kind":"matchOption","input":{"kind":"parameter","name":"maybe"},"someBody":{"kind":"stringLiteral","value":"some"},"noneBody":%s}},{"variant":"Go","body":{"kind":"stringLiteral","value":"go"}}]}}"""
                    reference
            use rejected = AuthoringProtocol.executeLine session (request 0) |> JsonDocument.Parse
            let text = rejected.RootElement.ToString()
            Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean(), text)
            let error = rejected.RootElement.GetProperty("error").GetProperty("details").GetProperty("diagnostics").[0]
            Assert.Equal("PureExpressionPatternSlotNotFound", error.GetProperty("code").GetString())
            let text = error.GetProperty("message").GetString()
            Assert.Contains("field 'member' (", text)
            Assert.Contains("of variant 'Stop' of 'Action' does not exist in scope depth 0. The slot is found at depth 1.", text)
            Assert.Contains("depth 0 = None arm of MatchOption (no bindings); depth 1 = arm 'Stop' of MatchUnion on 'Action' (binds 'member')", text)
            use accepted = AuthoringProtocol.executeLine session (request 1) |> JsonDocument.Parse
            Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean(), accepted.RootElement.ToString())
        finally
            Directory.Delete(root, true)
