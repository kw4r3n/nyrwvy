namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// StringReplace (Python str.replace, ordinal in code points, empty pattern is a no-op) and
/// StringTrim, StringTrimStart and StringTrimEnd (Unicode White_Space, as .NET char.IsWhiteSpace).
module StringReplaceTrimTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-replace-trim-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    /// The 25 White_Space code points, all in the BMP.
    let private whiteSpace =
        [ yield! [ 0x09 .. 0x0D ]
          0x20
          0x85
          0xA0
          0x1680
          yield! [ 0x2000 .. 0x200A ]
          0x2028
          0x2029
          0x202F
          0x205F
          0x3000 ]
        |> List.map (char >> string)

    [<Fact>]
    let ``StringReplace and the trims type check, persist, and lower through every layer`` () =
        Assert.Equal(25, whiteSpace.Length)
        Assert.All(whiteSpace, fun space -> Assert.True(Char.IsWhiteSpace(space, 0)))
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "edit"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string edit was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        let mismatch = "PureExpressionOperandTypeMismatch"
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringReplace(slot 0, text "a", text "b"))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringReplace(text "a", slot 0, text "b"))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringReplace(text "a", text "b", slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringTrim(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringTrimStart(slot 0))))
        Assert.Equal(mismatch, rejects (request [ intType ] stringType (ExpressionRecipe.StringTrimEnd(slot 0))))
        // The result is a String, not an Int64.
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringTrim(slot 0))))
        Assert.NotEqual<string>("", rejects (request [ stringType ] intType (ExpressionRecipe.StringReplace(slot 0, text "a", text "b"))))
        let cases =
            [ "stringReplace", "NyText.Replace(", [ stringType; stringType; stringType ], ExpressionRecipe.StringReplace(slot 0, slot 1, slot 2)
              "stringTrim", "NyText.Trim(", [ stringType ], ExpressionRecipe.StringTrim(slot 0)
              "stringTrimStart", "NyText.TrimStart(", [ stringType ], ExpressionRecipe.StringTrimStart(slot 0)
              "stringTrimEnd", "NyText.TrimEnd(", [ stringType ], ExpressionRecipe.StringTrimEnd(slot 0) ]
        for kind, emitted, parameters, body in cases do
            let proposal =
                AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request parameters stringType body)
                |> unwrap ("simulate " + kind)
            let committed =
                match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
                | other -> failwithf "Commit failed: %A" other
            Assert.Empty(SnapshotValidator.validate committed)
            Assert.Equal(SnapshotSchema.minimumVersion SnapshotSchemaFeature.StringReplaceAndTrim, SnapshotSchema.required committed)
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
            let broken =
                { committed with
                    Functions = committed.Functions |> List.map (fun fn ->
                        { fn with
                            Body =
                                { fn.Body with
                                    Nodes = fn.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference _ -> { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
            Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")
