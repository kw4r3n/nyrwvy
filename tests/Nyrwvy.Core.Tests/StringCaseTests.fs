namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringToLower and StringUtf8ByteCount: culture-invariant simple lowercase per code point and
/// the UTF-8 byte length, both pure and never failing.
module StringCaseTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-case-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    [<Fact>]
    let ``StringToLower, StringToUpper and StringUtf8ByteCount type check, persist, and lower through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "case"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string builtin was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringToLower(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] intType (ExpressionRecipe.StringUtf8ByteCount(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringToUpper(slot 0))))
        // Result types: String and Int64, not the other way round.
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringToLower(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringUtf8ByteCount(slot 0))))
        let cases =
            [ "stringToLower", "NyText.ToLower(", stringType, ExpressionRecipe.StringToLower(slot 0)
              "stringToUpper", "NyText.ToUpper(", stringType, ExpressionRecipe.StringToUpper(slot 0)
              "stringUtf8ByteCount", "NyText.Utf8ByteCount(", intType, ExpressionRecipe.StringUtf8ByteCount(slot 0) ]
        for kind, emitted, resultType, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request [ stringType ] resultType body)
                |> unwrap ("simulate " + kind)
            let committed =
                match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
                | other -> failwithf "Commit failed: %A" other
            Assert.Empty(SnapshotValidator.validate committed)
            let json = SnapshotCodec.serialize committed |> unwrap "serialize"
            Assert.Contains($"\"{kind}\"", json)
            let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
            let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
            let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            Assert.Contains($"\"{kind}\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains($"\"{kind}\"", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            Assert.Contains(emitted, artifact.Source)
            // A persisted node over a non-String operand is a validation error.
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
