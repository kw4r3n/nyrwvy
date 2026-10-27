namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// StringLength and StringSlice: code-point length and clamped code-point slicing.
module StringSliceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-slice-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private caseTypeId = id<TypeTag> "case"
    let private textFieldId = id<FieldTag> "text"
    let private startFieldId = id<FieldTag> "start"
    let private endFieldId = id<FieldTag> "end"
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private caseType: SemanticType =
        { Exported = true; TypeParameters = []; Id = caseTypeId
          DisplayName = "SliceCase"
          Kind =
            SemanticTypeKind.Product
                [ { Id = textFieldId; DisplayName = "text"; Type = stringType }
                  { Id = startFieldId; DisplayName = "start"; Type = intType }
                  { Id = endFieldId; DisplayName = "end"; Type = intType } ] }

    [<Fact>]
    let ``length and slice reject mistyped operands in recipes and persisted graphs`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "measure"
              Parameters = parameters |> List.mapi (fun index parameterType -> { DisplayName = $"input{index}"; Type = parameterType })
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped string operation was accepted: %A" other
        let slot = ExpressionRecipe.ParameterSlot
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ intType ] intType (ExpressionRecipe.StringLength(slot 0))))
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ stringType; stringType ] stringType (ExpressionRecipe.StringSlice(slot 0, slot 1, ExpressionRecipe.Int64Literal 0L))))
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request [ stringType ] stringType (ExpressionRecipe.StringSlice(slot 0, ExpressionRecipe.Int64Literal 0L, slot 0))))
        // The length is Int64, not String.
        Assert.NotEqual<string>("", rejects (request [ stringType ] stringType (ExpressionRecipe.StringLength(slot 0))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
                (request [ stringType; intType ] stringType (ExpressionRecipe.StringSlice(slot 0, slot 1, slot 1))) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        let intParameterId = committed.Functions.Head.Parameters[1].Id
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference parameterId when parameterId = intParameterId ->
                                        { node with Kind = SemanticNodeKind.StringLiteral "1" }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored length and slice persist lower and run on code points`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-slice-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let initial = { Snapshot.empty (id<SnapshotTag> "session") with Types = [ caseType ] }
            File.WriteAllText(path, SnapshotCodec.serialize initial |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let create line =
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let field name = sprintf """{"kind":"fieldAccess","source":{"kind":"parameter","name":"case"},"type":"SliceCase","field":"%s"}""" name
            create (sprintf """{"id":"measure","op":"simulateCreateFunctionByName","exported":true,"displayName":"measure","parameters":[{"displayName":"text","type":%s}],"resultType":%s,"requiredEffects":[],"body":{"kind":"stringLength","value":{"kind":"parameter","name":"text"}}}""" (primitive "string") (primitive "int64"))
            create (sprintf """{"id":"cut","op":"simulateCreateFunctionByName","exported":true,"displayName":"cut","parameters":[{"displayName":"case","type":{"kind":"named","name":"SliceCase"}}],"resultType":%s,"requiredEffects":[],"body":{"kind":"stringSlice","value":%s,"start":%s,"end":%s}}""" (primitive "string") (field "text") (field "start") (field "end"))
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let create' name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = parameters |> List.map (fun parameterType -> { DisplayName = "items"; Type = parameterType })
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            let listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
            create' "measureAll" [ listOf stringType ] (listOf intType) (ExpressionRecipe.ListMap(ExpressionRecipe.ParameterSlot 0, functionId "measure", []))
            create' "cutAll" [ listOf (TypeRef.Named(caseTypeId, [])) ] (listOf stringType) (ExpressionRecipe.ListMap(ExpressionRecipe.ParameterSlot 0, functionId "cut", []))
            session.Close()
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "reopen"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let persisted = SnapshotCodec.serialize snapshot |> unwrap "reserialize"
            let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains("\"stringSlice\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"stringLength\"", ExecutionIrSerializer.serialize execution)
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen session"
            let functionId name = (reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let texts = [ ""; "hello"; "a😀b"; "日本語"; "é"; "😀😀" ]
            let measured = PureRun.run reopened (functionId "measureAll") [ PureRunValue.StringList texts ] None |> unwrap "measure"
            Assert.Equal(PureRunValue.Int64List [ 0L; 5L; 3L; 3L; 2L; 2L ], measured.Actual)
            let cases =
                [ ("hello", 1L, 4L), "ell"
                  ("hello", -5L, 2L), "he"
                  ("hello", 3L, 100L), "lo"
                  ("hello", 4L, 2L), ""
                  ("hello", 5L, 5L), ""
                  ("hello", 9L, 12L), ""
                  ("", 0L, 1L), ""
                  ("a😀b", 1L, 2L), "😀"
                  ("a😀b", 0L, 2L), "a😀"
                  ("a😀b", 2L, 3L), "b"
                  ("😀😀", 1L, 2L), "😀"
                  ("日本語", 1L, 3L), "本語"
                  ("abc", Int64.MinValue, Int64.MaxValue), "abc" ]
            let caseValue (text, start, stop) =
                PureRunValue.Product(
                    caseTypeId, [],
                    [ textFieldId, PureRunValue.String text
                      startFieldId, PureRunValue.Int64 start
                      endFieldId, PureRunValue.Int64 stop ])
            let cut =
                PureRun.run reopened (functionId "cutAll")
                    [ PureRunValue.ListValue(TypeRef.Named(caseTypeId, []), cases |> List.map (fst >> caseValue)) ]
                    (Some(PureRunValue.StringList(List.map snd cases))) |> unwrap "cut"
            Assert.True((cut.Matches = Some true), sprintf "%A" cut.Mismatch)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
