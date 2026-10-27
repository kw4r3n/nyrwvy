namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ActionableRunDiagnosticsTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("input-diagnostic-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private exprId, literalId, pairId = id<TypeTag> "expr", id<VariantTag> "literal", id<VariantTag> "pair"
    let private amountId, leftId, rightId = id<FieldTag> "amount", id<FieldTag> "left", id<FieldTag> "right"
    let private exprType = TypeRef.Named(exprId, [])
    let private expr =
        { Exported = true; TypeParameters = []; Id = exprId; DisplayName = "Expr"
          Kind = SemanticTypeKind.Union
              [ { Id = literalId; DisplayName = "Literal"; Fields = [ { Id = amountId; DisplayName = "amount"; Type = intType } ] }
                { Id = pairId; DisplayName = "Pair"; Fields = [ { Id = leftId; DisplayName = "left"; Type = exprType }; { Id = rightId; DisplayName = "right"; Type = exprType } ] } ] }
    let private unwrap = function Ok v -> v | Error e -> failwithf "%A" e
    let private withSession test =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-run-diagnostic-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let path = Path.Combine(root, "graph.json")
            let parameter, node = id<ParameterTag> "input", id<GraphNodeTag> "input"
            let fn = { MustNotWait = false; Exported = true; Id = id<FunctionTag> "identity"; DisplayName = "identity"; Parameters = [ { Id = parameter; DisplayName = "input"; Type = exprType } ]; ResultType = exprType; RequiredEffects = []
                       TypeParameters = []
                       Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference parameter } ]; ResultNodeId = node } }
            File.WriteAllText(path, SnapshotCodec.serialize { Snapshot.empty (id<SnapshotTag> "base") with Types = [ expr ]; Functions = [ fn ] } |> unwrap)
            // A snapshot written directly declares its backend as `create` would.
            File.WriteAllText(path + ".backends.json", """{"targets": ["csharp"]}""")
            use session = AuthoringSession.openExisting path |> unwrap
            test session fn path
        finally Directory.Delete(root, true)
    let private literal value = PureRunValue.Union(exprId, [], literalId, [ amountId, value ])

    [<Fact>]
    let ``recursive input error names exact nested field and expected actual types`` () =
        withSession (fun session fn path ->
            let before = File.ReadAllBytes path
            let value = PureRunValue.Union(exprId, [], pairId, [ rightId, literal(PureRunValue.String "3"); leftId, literal(PureRunValue.Int64 2L) ])
            let issue = PureRun.inputIssue (session.Declarations()) fn.Id [ value ] None |> Option.get
            Assert.Equal<string list>([ "arguments"; "input"; "Expr"; "Pair"; "right"; "Expr"; "Literal"; "amount" ], issue.Path)
            Assert.Equal(Some intType, issue.ExpectedType)
            Assert.Equal(Some(TypeRef.Primitive PrimitiveType.String), issue.ActualType)
            match PureRun.run session fn.Id [ value ] None with
            | Error(PureRunError.Unsupported(code, message)) -> Assert.Equal("RunUnionShapeMismatch", code); Assert.Contains("right.Expr.Literal.amount", message)
            | other -> failwithf "%A" other
            Assert.Equal<byte>(before, File.ReadAllBytes path))

    [<Fact>]
    let ``missing duplicate and foreign fields have precise diagnostic paths`` () =
        withSession (fun session fn _ ->
            let missing = PureRun.inputIssue (session.Declarations()) fn.Id [ PureRunValue.Union(exprId, [], literalId, []) ] None |> Option.get
            Assert.Equal("amount", List.last missing.Path)
            Assert.True(missing.ActualType.IsNone)
            let duplicate = PureRun.inputIssue (session.Declarations()) fn.Id [ PureRunValue.Union(exprId, [], literalId, [ amountId, PureRunValue.Int64 1L; amountId, PureRunValue.Int64 2L ]) ] None |> Option.get
            Assert.Contains("more than once", duplicate.Reason)
            let foreign = PureRun.inputIssue (session.Declarations()) fn.Id [ PureRunValue.Union(exprId, [], literalId, [ leftId, PureRunValue.Int64 1L ]) ] None |> Option.get
            Assert.Contains(StableId.value leftId, List.last foreign.Path)
            Assert.True(foreign.ExpectedType.IsNone))

    [<Fact>]
    let ``expectation defect and typed scalar payload defect have distinct repair diagnostics`` () =
        withSession (fun session fn _ ->
            let issue = PureRun.inputIssue (session.Declarations()) fn.Id [ literal(PureRunValue.Int64 1L) ] (Some(literal(PureRunValue.Bool true))) |> Option.get
            Assert.Equal("expected", issue.Path.Head)
            let declarations = session.Declarations()
            let listFn = { fn with Parameters = [ { fn.Parameters.Head with Type = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ]) } ] }
            let names = { declarations with Functions = [ listFn ] }
            Assert.True((PureRun.inputIssue names fn.Id [ PureRunValue.ListValue(intType, [ PureRunValue.Int64 1L ]) ] None).IsNone)
            let issue = PureRun.inputIssue names fn.Id [ PureRunValue.ListValue(intType, [ PureRunValue.Bool true ]) ] None |> Option.get
            Assert.Equal(Some intType, issue.ExpectedType)
            Assert.Equal(Some(TypeRef.Primitive PrimitiveType.Bool), issue.ActualType)
            Assert.Equal<string list>([ "arguments"; "input"; "items[0]" ], issue.Path))

    [<Fact>]
    let ``generic and task entry refusal never reports an input defect`` () =
        withSession (fun session _ path ->
            let publish p = session.Commit((unwrap p).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let genericType = TypeRef.TypeParameter(id<TypeParameterTag> "T")
            for name, parameterType, resultType, body, code in
                [ "generic", genericType, genericType, ExpressionRecipe.ParameterSlot 0, "RunGenericEntryUnsupported"
                  "task", TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ intType ]), intType, ExpressionRecipe.Int64Literal 0L, "RunTaskTypeUnsupported" ] do
                session.SimulateCreateFunction
                    { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = name
                      TypeParameters = None
                      Parameters = [ { DisplayName = "x"; Type = parameterType } ]; ResultType = resultType
                      RequiredEffects = []; Body = body } |> publish
                let before = File.ReadAllBytes path
                use response = JsonDocument.Parse(AuthoringProtocol.executeLine session ($"""{{"id":"test","op":"runPureByName","function":"{name}","arguments":[{{"kind":"int64","value":"1"}}]}}"""))
                let error = response.RootElement.GetProperty("error")
                Assert.Equal(code, error.GetProperty("code").GetString())
                Assert.False(error.TryGetProperty("details") |> fst)
                Assert.DoesNotContain("At arguments", error.GetProperty("message").GetString())
                Assert.Equal<byte>(before, File.ReadAllBytes path))

    [<Fact>]
    let ``diagnostic path follows runner error category across multiple invalid arguments`` () =
        withSession (fun session _ _ ->
            let publish p = session.Commit((unwrap p).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.SimulateCreateType
                { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Money"
                  Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = "amount"; Type = intType } ] } |> publish
            session.SimulateCreateFunction
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "mixed"
                  TypeParameters = None
                  Parameters = [ { DisplayName = "xs"; Type = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ]) }
                                 { DisplayName = "p"; Type = TypeRef.Named((session.Declarations().Types |> List.find (fun t -> t.DisplayName = "Money")).Id, []) } ]
                  ResultType = intType; RequiredEffects = []; Body = ExpressionRecipe.Int64Literal 0L } |> publish
            use response = JsonDocument.Parse(AuthoringProtocol.executeLine session """{"id":"mixed","op":"runPureByName","function":"mixed","arguments":[{"kind":"list","itemType":{"kind":"primitive","primitive":"int64"},"items":[{"kind":"int64","value":"1"}]},{"kind":"product","type":"Money","fields":[]}]}""")
            let error = response.RootElement.GetProperty("error")
            Assert.Equal("RunProductShapeMismatch", error.GetProperty("code").GetString())
            let path = error.GetProperty("details").GetProperty("path")
            Assert.Equal("p", path[1].GetString())
            Assert.Contains("p.Money.amount", error.GetProperty("message").GetString())
            let money = (session.Declarations().Types |> List.find (fun t -> t.DisplayName = "Money")).Id
            let optionType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Named(money, []) ])
            session.SimulateCreateFunction
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "nestedMixed"
                  TypeParameters = None
                  Parameters = [ { DisplayName = "xs"; Type = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ optionType ]) } ]
                  ResultType = intType; RequiredEffects = []; Body = ExpressionRecipe.Int64Literal 0L } |> publish
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "nestedMixed")
            let items =
                [ PureRunValue.OptionValue(TypeRef.Named(money, []), Some(PureRunValue.Bool true))
                  PureRunValue.OptionValue(TypeRef.Named(money, []), Some(PureRunValue.Product(money, [], []))) ]
            let values = [ PureRunValue.ListValue(optionType, items) ]
            match PureRun.run session fn.Id values None with
            | Error(PureRunError.Unsupported(code, message) as error) ->
                Assert.Equal("RunProductShapeMismatch", code)
                Assert.Contains("items[1].some.Money.amount", message)
                let issue = PureRun.inputIssueForError (session.Declarations()) fn.Id values None error |> Option.get
                Assert.Equal<string list>([ "arguments"; "xs"; "items[1]"; "some"; "Money"; "amount" ], issue.Path)
            | other -> failwithf "%A" other)

    [<Fact>]
    let ``effect-order diagnostics name both operations and the Bind repair, at proposal time and in full validation`` () =
        withSession (fun session _ path ->
            let publish p = session.Commit((unwrap p).ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Io" } |> publish
            let effect = session.Declarations().Effects.Head.Id
            session.SimulateCreateCapability { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "Values"
                                               Operations = [ { MayWait = false; DisplayName = "read"; Parameters = []; ResultType = intType; FailureType = None; EffectId = effect } ] } |> publish
            let cap = session.Declarations().Capabilities.Head
            let invoke = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, cap.Operations.Head.Id, [])
            let creation: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = session.SnapshotId
                  TypeParameters = None
                  DisplayName = "unordered"
                  Parameters = [ { DisplayName = "values"; Type = TypeRef.Capability cap.Id } ]
                  ResultType = intType
                  RequiredEffects = [ effect ]
                  Body = ExpressionRecipe.Int64WrappingAdd(invoke, invoke) }
            // Proposal time applies the lowering rule and names both operations.
            let refused = session.SimulateCreateFunction creation
            Assert.True(Result.isError refused)
            let refusal = sprintf "%A" refused
            Assert.Contains("AmbiguousEffectOrder", refusal)
            Assert.Contains("CapabilityInvoke Values.read", refusal)
            Assert.Contains("Sequence them with Bind", refusal)
            // A snapshot holding that pair can no longer be written or opened: full validation applies
            // the same rule and names both operations (docs/decisions/proposal-time-effect-ordering.md).
            session.SimulateCreateFunction { creation with Body = ExpressionRecipe.Bind(invoke, ExpressionRecipe.Bind(invoke, ExpressionRecipe.Int64Literal 0L)) } |> publish
            let ordered = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "unordered")
            let historical = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let invokes = ValueGraph.collectNodes ordered.Body |> List.filter (fun n -> match n.Kind with SemanticNodeKind.CapabilityInvoke _ -> true | _ -> false)
            let receiver = ordered.Body.Nodes |> List.find (fun n -> match n.Kind with SemanticNodeKind.ParameterReference _ -> true | _ -> false)
            let addId = StableId.createForTesting<GraphNodeTag> "legacy-unordered-add"
            let firstId, secondId = invokes[0].Id, invokes[1].Id
            let legacyBody: ValueGraph =
                { Nodes =
                    [ receiver
                      { invokes[0] with Kind = SemanticNodeKind.CapabilityInvoke(receiver.Id, cap.Operations.Head.Id, []) }
                      { invokes[1] with Kind = SemanticNodeKind.CapabilityInvoke(receiver.Id, cap.Operations.Head.Id, []) }
                      { Id = addId; Kind = SemanticNodeKind.Int64WrappingAdd(firstId, secondId) } ]
                  ResultNodeId = addId }
            let legacy = { historical with Functions = historical.Functions |> List.map (fun f -> if f.Id = ordered.Id then { f with Body = legacyBody } else f) }
            let issue = Assert.Single(SnapshotValidator.validate legacy)
            Assert.Equal("AmbiguousEffectOrder", issue.Code)
            Assert.Contains("CapabilityInvoke Values.read", issue.Message)
            Assert.Contains("Sequence them with Bind", issue.Message)
            match SnapshotCodec.serialize legacy with
            | Error(SnapshotCodecError.InvalidSnapshot issues) -> Assert.Contains(issues, fun issue -> issue.Code = "AmbiguousEffectOrder")
            | other -> failwithf "The writer accepted an unordered pair: %A" other)

    [<Fact>]
    let ``status reports the build and misshaped run values explain the expected form`` () =
        withSession (fun session fn _ ->
            use status = AuthoringProtocol.executeLine session """{"id":"status","op":"status"}""" |> JsonDocument.Parse
            Assert.False(String.IsNullOrWhiteSpace(status.RootElement.GetProperty("result").GetProperty("build").GetString()))
            let message argument =
                use reply =
                    AuthoringProtocol.executeLine session ($"""{{"id":"run","op":"runPure","functionId":"{StableId.value fn.Id}","arguments":[{argument}]}}""")
                    |> JsonDocument.Parse
                Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
                reply.RootElement.GetProperty("error").GetProperty("message").GetString()
            Assert.Contains("positional", message """{"parameter":"input","value":{"kind":"int64","value":1}}""")
            Assert.Contains("isOk", message """{"kind":"result","case":"err","value":{"kind":"string","value":"e"}}"""))
