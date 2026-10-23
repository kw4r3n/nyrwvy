namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Int64ToString and StringParseInt64: canonical decimal conversion between Int64 and String.
module Int64TextTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("int64-text-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionInt = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ intType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``conversions reject mistyped operands in recipes and persisted graphs`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let request parameterType resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "convert"
              Parameters = [ { DisplayName = "input"; Type = parameterType } ]
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let rejects request =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) -> diagnostic.Code
            | other -> failwithf "Mistyped conversion was accepted: %A" other
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request stringType stringType (ExpressionRecipe.Int64ToString(ExpressionRecipe.ParameterSlot 0))))
        Assert.Equal("PureExpressionOperandTypeMismatch",
            rejects (request intType optionInt (ExpressionRecipe.StringParseInt64(ExpressionRecipe.ParameterSlot 0))))
        // The parse result is Option<Int64>, not Int64.
        Assert.NotEqual<string>("", rejects (request stringType intType (ExpressionRecipe.StringParseInt64(ExpressionRecipe.ParameterSlot 0))))
        let proposal =
            AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws
                (request intType stringType (ExpressionRecipe.Int64ToString(ExpressionRecipe.ParameterSlot 0))) |> unwrap "simulate"
        let committed =
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> AgentWorkspace.snapshotForStructuralEditing next
            | other -> failwithf "Commit failed: %A" other
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference _ -> { node with Kind = SemanticNodeKind.StringLiteral "1" }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "Int64IntrinsicTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``JSONL-authored Int64 text conversions persist lower and run strictly`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-int64-text-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let create line =
                use proposed = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
                let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                use commit = AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId) |> JsonDocument.Parse
                Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let optionOfInt = sprintf """{"kind":"builtinGeneric","generic":"option","arguments":[%s]}""" (primitive "int64")
            create (sprintf """{"id":"render","op":"simulateCreateFunctionByName","exported":true,"displayName":"render","parameters":[{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[],"body":{"kind":"int64ToString","value":{"kind":"parameter","name":"n"}}}""" (primitive "int64") (primitive "string"))
            create (sprintf """{"id":"parse","op":"simulateCreateFunctionByName","exported":true,"displayName":"parse","parameters":[{"displayName":"text","type":%s}],"resultType":%s,"requiredEffects":[],"body":{"kind":"stringParseInt64","value":{"kind":"parameter","name":"text"}}}""" (primitive "string") optionOfInt)
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let listRecipe name source = ExpressionRecipe.ListMap(source, functionId name, [])
            let createMap name parameterType resultType mapper =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = [ { DisplayName = "items"; Type = parameterType } ]
                      ResultType = resultType
                      RequiredEffects = []
                      Body = listRecipe mapper (ExpressionRecipe.ParameterSlot 0) }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            let listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
            createMap "renderAll" (listOf intType) (listOf stringType) "render"
            createMap "parseAll" (listOf stringType) (listOf optionInt) "parse"
            session.Close()
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap "reopen"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let persisted = SnapshotCodec.serialize snapshot |> unwrap "reserialize"
            let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap "typed"
            let execution = ExecutionIr.lower typed |> unwrap "lower"
            Assert.Contains("\"stringParseInt64\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"int64ToString\"", ExecutionIrSerializer.serialize execution)
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen session"
            let functionId name = (reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let numbers = [ 0L; -1L; 42L; Int64.MaxValue; Int64.MinValue ]
            let rendered = PureRun.run reopened (functionId "renderAll") [ PureRunValue.Int64List numbers ] None |> unwrap "render"
            Assert.Equal(PureRunValue.StringList [ "0"; "-1"; "42"; "9223372036854775807"; "-9223372036854775808" ], rendered.Actual)
            let cases =
                [ "0", Some 0L; "-0", Some 0L; "007", Some 7L; "9223372036854775807", Some Int64.MaxValue
                  "-9223372036854775808", Some Int64.MinValue; "9223372036854775808", None; "-9223372036854775809", None
                  "", None; "-", None; "+1", None; " 1", None; "1 ", None; "1_000", None; "0x10", None; "1e3", None
                  "١", None; "１", None; "--1", None ]
            let parsed =
                PureRun.run reopened (functionId "parseAll") [ PureRunValue.StringList(List.map fst cases) ]
                    (Some(PureRunValue.ListValue(optionInt, cases |> List.map (snd >> PureRunValue.Int64Option)))) |> unwrap "parse"
            Assert.True((parsed.Matches = Some true), sprintf "%A" parsed.Mismatch)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
