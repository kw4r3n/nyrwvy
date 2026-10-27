namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `StringLastIndexOfBefore(value, substring, end): Option<Int64>` (post-M2 order 71): Python
/// `str.rfind(sub, 0, end)`, the end bound StringLastIndexOf lacks, clamped like a slice bound.
module StringLastIndexOfBeforeTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private before value substring stop = ExpressionRecipe.StringLastIndexOfBefore(value, substring, stop)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
          TypeParameters = None
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = []; Body = body }
    let private create (session: AuthoringSession) name parameters resultType body =
        let proposal = request session name parameters resultType body |> session.SimulateCreateFunction |> unwrap
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
        find session name
    let private rejects (session: AuthoringSession) parameters resultType body =
        match session.SimulateCreateFunction(request session "rejected" parameters resultType body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwith "The function was accepted."
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-last-index-of-before-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private parameters = [ "text", stringType; "sub", stringType; "end", intType ]

    [<Fact>]
    let ``StringLastIndexOfBefore checks its operands and persists through every layer`` () =
        withSession (fun session path ->
            let rfind = create session "rfind" parameters (optionOf intType) (before (slot 0) (slot 1) (slot 2))
            let mismatch = "PureExpressionOperandTypeMismatch"
            Assert.Contains(mismatch, rejects session [ "text", intType; "sub", stringType; "end", intType ] (optionOf intType) (before (slot 0) (slot 1) (slot 2)))
            Assert.Contains(mismatch, rejects session [ "text", stringType; "sub", intType; "end", intType ] (optionOf intType) (before (slot 0) (slot 1) (slot 2)))
            Assert.Contains(mismatch, rejects session [ "text", stringType; "sub", stringType; "end", stringType ] (optionOf intType) (before (slot 0) (slot 1) (slot 2)))
            // The result is Option<Int64>, not Int64.
            rejects session parameters intType (before (slot 0) (slot 1) (slot 2)) |> ignore
            // The wire form names the operands value, substring and end, as stringIndexOf does with from.
            let wire =
                """{"id":"wire","op":"simulateCreateFunctionByName","exported":true,"displayName":"rfindWire","parameters":[{"displayName":"text","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]},"requiredEffects":[],"body":{"kind":"stringLastIndexOfBefore","value":{"kind":"parameter","name":"text"},"substring":{"kind":"stringLiteral","value":"a"},"end":{"kind":"int64Literal","value":"2"}}}"""
            use proposed = AuthoringProtocol.executeLine session wire |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            session.Close()

            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serializeCanonical snapshot |> unwrap
            Assert.Contains("\"kind\":\"stringLastIndexOfBefore\"", json)
            Assert.Contains("\"schemaVersion\":20", json)
            let restored = SnapshotCodec.deserialize json |> unwrap
            Assert.Equal(json, SnapshotCodec.serializeCanonical restored |> unwrap)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"stringLastIndexOfBefore\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains("\"kind\":\"stringLastIndexOfBefore\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            Assert.Contains("NyText.LastIndexOfBefore(", source)
            match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":20", "\"schemaVersion\":19")) with
            | Ok _ -> failwith "A schema 19 snapshot with StringLastIndexOfBefore decoded."
            | Error error -> Assert.Contains("StringLastIndexOfBefore", sprintf "%A" error)

            // The viewer names the end bound as a slice end; a persisted graph reports a mistyped operand.
            let focus = SemanticQuery.focusFunction snapshot rfind.Id |> unwrap |> ViewerProjection.focus
            let node = focus.Graph.Nodes |> List.find (fun item -> item.Id = rfind.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.Operation "StringLastIndexOfBefore", node.Role)
            Assert.Equal<ValueDependencyRole list>(
                [ ValueDependencyRole.Source; ValueDependencyRole.Substring; ValueDependencyRole.SliceEnd ],
                node.ValueDependencies |> List.map (fun item -> item.Role))
            let endParameterId = (rfind.Parameters |> List.last).Id
            let tampered =
                { snapshot with
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            if fn.Id <> rfind.Id then fn
                            else
                                { fn with
                                    Body =
                                        { fn.Body with
                                            Nodes =
                                                fn.Body.Nodes
                                                |> List.map (fun node ->
                                                    match node.Kind with
                                                    | SemanticNodeKind.ParameterReference reference when reference = endParameterId -> { node with Kind = SemanticNodeKind.StringLiteral "2" }
                                                    | _ -> node) } }) }
            Assert.Contains("StringIntrinsicTypeMismatch", SnapshotValidator.validate tampered |> List.map (fun issue -> issue.Code)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``StringLastIndexOfBefore is Python rfind with an end bound under runPure`` () =
        withSession (fun session path ->
            let rfind = create session "rfind" parameters (optionOf intType) (before (slot 0) (slot 1) (slot 2))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (text: string) (sub: string) (stop: int64) =
                (PureRun.run reopened rfind.Id [ PureRunValue.String text; PureRunValue.String sub; PureRunValue.Int64 stop ] None |> unwrap).Actual
            // Python parity: text.rfind(sub, 0, end) for 0 <= end <= len(text), -1 as None.
            let cases =
                [ "abcabc", "bc", 6L, Some 4L
                  "abcabc", "bc", 5L, Some 1L
                  "abcabc", "bc", 3L, Some 1L
                  "abcabc", "bc", 2L, None
                  "abcabc", "abc", 3L, Some 0L
                  "abcabc", "abcabc", 6L, Some 0L
                  "abcabc", "abcabc", 5L, None
                  "aaaa", "aa", 4L, Some 2L
                  "aaaa", "aa", 3L, Some 1L
                  "aaaa", "aa", 1L, None
                  "abc", "d", 3L, None
                  "", "a", 0L, None
                  // An empty substring is found at the clamped end, as rfind("", 0, end) gives end.
                  "abc", "", 2L, Some 2L
                  "abc", "", 0L, Some 0L
                  "", "", 0L, Some 0L
                  // end clamps to [0, length] like a slice bound: beyond the end searches the whole text, negative is 0.
                  "abcabc", "bc", 99L, Some 4L
                  "abc", "", 99L, Some 3L
                  "abcabc", "bc", -1L, None
                  "abc", "", -5L, Some 0L
                  // Positions and the bound count code points: the emoji is one code point.
                  "x\U0001F600y\U0001F600", "\U0001F600", 4L, Some 3L
                  "x\U0001F600y\U0001F600", "\U0001F600", 3L, Some 1L
                  "x\U0001F600y\U0001F600", "y", 3L, Some 2L
                  "x\U0001F600y\U0001F600", "y", 2L, None ]
            for text, sub, stop, expected in cases do
                Assert.Equal(PureRunValue.Int64Option expected, run text sub stop)
            // Scanning backwards through every occurrence: each match's position becomes the next end.
            let text = "a-b-c-d"
            let positions =
                List.unfold
                    (fun stop ->
                        match run text "-" stop with
                        | PureRunValue.Int64Option(Some position) -> Some(position, position)
                        | _ -> None)
                    (int64 text.Length)
            Assert.Equal<int64 list>([ 5L; 3L; 1L ], positions))
