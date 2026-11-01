namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringDecodeUtf8Lossy (post-M2 order 33): bytes held one per code point decoded as UTF-8 with one
/// U+FFFD per maximal ill-formed subpart, as Python bytes.decode("utf-8", "replace"). Never fails.
module StringDecodeUtf8Tests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-decode-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    [<Fact>]
    let ``StringDecodeUtf8Lossy type checks, persists, and lowers through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "decode"
              Parameters = [ { DisplayName = "bytes"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped decode was accepted: %A" other
        let body = ExpressionRecipe.StringDecodeUtf8Lossy(ExpressionRecipe.ParameterSlot 0)
        Assert.Equal("PureExpressionOperandTypeMismatch", rejects (request intType stringType body))
        Assert.NotEqual<string>("", rejects (request stringType intType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request stringType stringType body) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringDecodeUtf8Lossy\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyText.DecodeUtf8Lossy(", artifact.Source)
        let parameterId = committed.Functions.Head.Parameters.Head.Id
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId ->
                                        { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
