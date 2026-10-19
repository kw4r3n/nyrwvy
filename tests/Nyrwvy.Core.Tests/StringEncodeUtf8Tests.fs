namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringEncodeUtf8 (post-M2 order 67): the UTF-8 bytes of a String held one per code point
/// (U+0000..U+00FF), as Python text.encode("utf-8"); an unpaired surrogate becomes U+FFFD, the 3
/// bytes StringUtf8ByteCount counts for it. Never fails.
module StringEncodeUtf8Tests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-encode-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value

    /// Each input with Python's text.encode("utf-8") written one code point per byte; the unpaired
    /// surrogates, where Python raises, encode as U+FFFD (EF BF BD).
    let private cases =
        [ "", ""
          "abc", "abc"
          "été", "Ã©tÃ©"
          "日本", "æ\u0097¥æ\u009C¬"
          "\U0001F600", "ð\u009F\u0098\u0080"
          "\u0080", "Â\u0080"
          "ÿ", "Ã¿"
          "€", "â\u0082¬"
          "￿", "ï¿¿"
          "\u0000", "\u0000"
          "a\u0000b", "a\u0000b"
          // A BOM is encoded like any other code point, as with Python's "utf-8" (not "utf-8-sig").
          "﻿x", "ï»¿x"
          "\U00010000", "ð\u0090\u0080\u0080"
          "\U0010FFFF", "ô\u008F¿¿"
          // Unpaired surrogates: U+FFFD each, 3 bytes, as StringUtf8ByteCount counts them.
          "\uD800", "ï¿½"
          "a\uDC00b", "aï¿½b"
          "\uD83D😀", "ï¿½ð\u009F\u0098\u0080" ]

    [<Fact>]
    let ``StringEncodeUtf8 type checks, persists, and lowers through every layer`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              DisplayName = "encode"
              Parameters = [ { DisplayName = "text"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped encode was accepted: %A" other
        let body = ExpressionRecipe.StringEncodeUtf8(ExpressionRecipe.ParameterSlot 0)
        Assert.Equal("PureExpressionOperandTypeMismatch", rejects (request intType stringType body))
        Assert.NotEqual<string>("", rejects (request stringType intType body))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws (request stringType stringType body) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        Assert.Empty(SnapshotValidator.validate committed)
        Assert.Equal<Set<SnapshotSchemaFeature>>(Set.singleton SnapshotSchemaFeature.StringEncodeUtf8, SnapshotSchema.usedFeatures committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"stringEncodeUtf8\"", json)
        Assert.Contains("\"schemaVersion\":19", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let nyason = NyasonCodec.serialize decoded |> unwrap "nyason"
        Assert.Contains("string-encode-utf8", nyason)
        Assert.Contains("nayamnyam.snapshot.v15", nyason)
        let restored = NyasonCodec.deserialize nyason |> unwrap "nyason decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"stringEncodeUtf8\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"stringEncodeUtf8\"", ExecutionIrSerializer.serialize execution)
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        Assert.Contains("NyText.EncodeUtf8(", artifact.Source)
        match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":19", "\"schemaVersion\":18")) with
        | Ok _ -> failwith "A schema 18 snapshot with StringEncodeUtf8 decoded."
        | Error error -> Assert.Contains("StringEncodeUtf8", sprintf "%A" error)
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

    [<Fact; Trait("Suite", "Extended")>]
    let ``StringEncodeUtf8 matches Python's encoding, inverts StringDecodeUtf8Lossy and has the counted length`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-encode-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let define name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      DisplayName = name
                      Parameters = parameters
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap name
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap ("commit " + name) |> ignore
                session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let textParameter: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent = { DisplayName = "text"; Type = stringType }
            let slot = ExpressionRecipe.ParameterSlot 0
            let encode = define "encode" [ textParameter ] stringType (ExpressionRecipe.StringEncodeUtf8 slot)
            // decode(encode(text)) and the byte count of text, for the same inputs.
            let roundTrip = define "roundTrip" [ textParameter ] stringType (ExpressionRecipe.StringDecodeUtf8Lossy(ExpressionRecipe.StringEncodeUtf8 slot))
            let encodedLength = define "encodedLength" [ textParameter ] intType (ExpressionRecipe.StringLength(ExpressionRecipe.StringEncodeUtf8 slot))
            let byteCount = define "byteCount" [ textParameter ] intType (ExpressionRecipe.StringUtf8ByteCount slot)
            let table name (fn: SemanticFunction) resultType =
                define name [] (listOf resultType)
                    (ExpressionRecipe.ListConstruct(resultType, cases |> List.map (fun (input, _) -> ExpressionRecipe.DirectCall(fn.Id, [ fn.Parameters.Head.Id, text input ]))))
            let run (fn: SemanticFunction) expected =
                let result = PureRun.run session fn.Id [] (Some expected) |> unwrap fn.DisplayName
                Assert.True((result.Matches = Some true), sprintf "%s: %A" fn.DisplayName result.Actual)
            run (table "encoded" encode stringType) (PureRunValue.StringList(cases |> List.map snd))
            // Well-formed text comes back unchanged; an unpaired surrogate comes back as U+FFFD.
            let replaced (input: string) =
                let builder = Text.StringBuilder()
                let mutable index = 0
                while index < input.Length do
                    if Char.IsHighSurrogate input.[index] && index + 1 < input.Length && Char.IsLowSurrogate input.[index + 1] then
                        builder.Append(input, index, 2) |> ignore
                        index <- index + 2
                    else
                        builder.Append(if Char.IsSurrogate input.[index] then '�' else input.[index]) |> ignore
                        index <- index + 1
                builder.ToString()
            run (table "roundTripped" roundTrip stringType) (PureRunValue.StringList(cases |> List.map (fst >> replaced)))
            let lengths = cases |> List.map (fun (_, bytes) -> int64 bytes.Length)
            run (table "encodedLengths" encodedLength intType) (PureRunValue.Int64List lengths)
            run (table "byteCounts" byteCount intType) (PureRunValue.Int64List lengths)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
