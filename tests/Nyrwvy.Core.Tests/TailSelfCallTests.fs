namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Order 49: a self DirectCall in tail position compiles as a jump back to the function's entry,
/// so a self-recursive loop runs in constant stack. Other calls stay ordinary calls.
module TailSelfCallTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("tail-self-call-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private optionType item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])
    let private listType item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private stepId = id<TypeTag> "step"
    let private evenId = id<VariantTag> "even"
    let private oddId = id<VariantTag> "odd"
    let private doneId = id<VariantTag> "done"
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private slot = ExpressionRecipe.ParameterSlot
    let private int (value: int64) = ExpressionRecipe.Int64Literal value
    let private text = ExpressionRecipe.StringLiteral
    let private add left right = ExpressionRecipe.Int64WrappingAdd(left, right)
    let private minus left right = ExpressionRecipe.Int64WrappingSubtract(left, right)
    let private atMost left right = ExpressionRecipe.Int64LessThanOrEqual(left, right)
    let private self arguments = ExpressionRecipe.SelfCall(arguments |> List.indexed)
    let private bound slotKind = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = slotKind }
    let private value = bound RecipePatternSlot.Value
    let private error = bound RecipePatternSlot.Error
    /// The common loop shape: `n` counts down, `acc` counts the turns.
    let private nAcc = [ "n", intType; "acc", intType ]
    let private turn = [ minus (slot 0) (int 1L); add (slot 1) (int 1L) ]

    type private Fixture =
        { Session: AuthoringSession
          Path: string
          Log: SemanticCapability
          Write: SemanticCapabilityOperation
          Function: string -> SemanticFunction }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-tail-self-call-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let types: SemanticType list =
                [ { Id = stepId; DisplayName = "Step"
                    Kind = SemanticTypeKind.Union
                        [ { Id = evenId; DisplayName = "Even"; Fields = [] }
                          { Id = oddId; DisplayName = "Odd"; Fields = [] }
                          { Id = doneId; DisplayName = "Done"; Fields = [] } ] } ]
            let ws = AgentWorkspace.create { Snapshot.empty (id<SnapshotTag> "initial") with Types = types } |> unwrap "workspace"
            File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot ws |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "session"
            let effectRequest: AgentCreateEffectDeclarationRequest = { ExpectedBaseSnapshotId = current session; DisplayName = "Write" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let logRequest: AgentCreateCapabilityDeclarationRequest =
                { ExpectedBaseSnapshotId = current session
                  DisplayName = "Log"
                  Operations =
                    [ { DisplayName = "write"
                        Parameters = [ { DisplayName = "message"; Type = stringType } ]
                        ResultType = unitType
                        FailureType = None
                        EffectId = effect.Id } ] }
            session.SimulateCreateCapability logRequest |> unwrap "Log" |> publish session
            let log = session.Declarations().Capabilities |> List.exactlyOne
            let write = List.exactlyOne log.Operations
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let createWith effects name parameters result body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = current session
                      DisplayName = name
                      Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
                      ResultType = result
                      RequiredEffects = effects
                      Body = body }
                session.SimulateCreateFunction request |> unwrap name |> publish session
            let create = createWith []

            // Conditional: sumTo(n, acc) = if n <= 0 then acc else sumTo(n - 1, acc + n).
            create "sumTo" nAcc intType
                (ExpressionRecipe.Conditional(atMost (slot 0) (int 0L), slot 1, self [ minus (slot 0) (int 1L); add (slot 1) (slot 0) ]))
            // Bind body: if n <= 0 then acc else (let m = n - 1 in bindLoop(m, acc + 1)).
            create "bindLoop" nAcc intType
                (ExpressionRecipe.Conditional(
                    atMost (slot 0) (int 0L),
                    slot 1,
                    ExpressionRecipe.Bind(minus (slot 0) (int 1L), self [ value; add (slot 1) (int 1L) ])))
            // MatchOption, Some arm: match (if n <= 0 then None else Some(n - 1)) with Some m -> loop(m, acc + 1) | None -> acc.
            create "someLoop" nAcc intType
                (ExpressionRecipe.MatchOption(
                    ExpressionRecipe.Conditional(atMost (slot 0) (int 0L), ExpressionRecipe.OptionNone intType, ExpressionRecipe.OptionSome(minus (slot 0) (int 1L))),
                    self [ value; add (slot 1) (int 1L) ],
                    slot 1))
            // MatchOption, None arm: match (if n <= 0 then Some(acc) else None) with Some v -> v | None -> loop(n - 1, acc + 1).
            create "noneLoop" nAcc intType
                (ExpressionRecipe.MatchOption(
                    ExpressionRecipe.Conditional(atMost (slot 0) (int 0L), ExpressionRecipe.OptionSome(slot 1), ExpressionRecipe.OptionNone intType),
                    value,
                    self turn))
            // MatchResult, Ok arm.
            create "okLoop" nAcc intType
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.Conditional(
                        atMost (slot 0) (int 0L),
                        ExpressionRecipe.ResultErr(intType, slot 1),
                        ExpressionRecipe.ResultOk(intType, minus (slot 0) (int 1L))),
                    self [ value; add (slot 1) (int 1L) ],
                    error))
            // MatchResult, Err arm.
            create "errLoop" nAcc intType
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.Conditional(
                        atMost (slot 0) (int 0L),
                        ExpressionRecipe.ResultOk(intType, slot 1),
                        ExpressionRecipe.ResultErr(intType, minus (slot 0) (int 1L))),
                    value,
                    self [ error; add (slot 1) (int 1L) ]))
            // MatchUnion: Even adds 2, the `otherwise` arm (Odd) adds 1, Done returns.
            let step =
                ExpressionRecipe.Conditional(
                    atMost (slot 0) (int 0L),
                    ExpressionRecipe.UnionConstruct(doneId, []),
                    ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64Equal(ExpressionRecipe.Int64FloorModulo(slot 0, int 2L), int 0L),
                        ExpressionRecipe.UnionConstruct(evenId, []),
                        ExpressionRecipe.UnionConstruct(oddId, [])))
            create "unionLoop" nAcc intType
                (ExpressionRecipe.MatchUnion(
                    step,
                    [ [ evenId ], self [ minus (slot 0) (int 1L); add (slot 1) (int 2L) ]
                      [ doneId ], slot 1
                      [], self turn ]))
            // BoolOr and BoolAnd right operands: boolLoop(n) = n <= 0 || (n > -1 && boolLoop(n - 1)).
            create "boolLoop" [ "n", intType ] boolType
                (ExpressionRecipe.BoolOr(
                    atMost (slot 0) (int 0L),
                    ExpressionRecipe.BoolAnd(ExpressionRecipe.Int64GreaterThan(slot 0, int -1L), self [ minus (slot 0) (int 1L) ])))
            // Parallel assignment: swap(a, b, n) = if n <= 0 then a + "|" + b else swap(b, a, n - 1).
            create "swap" [ "a", stringType; "b", stringType; "n", intType ] stringType
                (ExpressionRecipe.Conditional(
                    atMost (slot 2) (int 0L),
                    ExpressionRecipe.StringConcat(ExpressionRecipe.StringConcat(slot 0, text "|"), slot 1),
                    self [ slot 1; slot 0; minus (slot 2) (int 1L) ]))
            // An abort next to a tail call: if acc > limit then abort "limit" else if n <= 0 then Ok(acc) else loop(n - 1, acc + 1).
            create "abortLoop" (nAcc @ [ "limit", intType ]) (resultType intType stringType)
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64GreaterThan(slot 1, slot 2),
                    ExpressionRecipe.Abort(text "limit"),
                    ExpressionRecipe.Conditional(
                        atMost (slot 0) (int 0L),
                        ExpressionRecipe.ResultOk(stringType, slot 1),
                        self (turn @ [ slot 2 ]))))
            // Non-tail: the call is an operand (n + sum(n - 1)), or a match input.
            create "sumNonTail" [ "n", intType ] intType
                (ExpressionRecipe.Conditional(atMost (slot 0) (int 0L), int 0L, add (slot 0) (self [ minus (slot 0) (int 1L) ])))
            create "countNonTail" [ "n", intType ] (resultType intType stringType)
                (ExpressionRecipe.Conditional(
                    atMost (slot 0) (int 0L),
                    ExpressionRecipe.ResultOk(stringType, int 0L),
                    ExpressionRecipe.MatchResult(
                        self [ minus (slot 0) (int 1L) ],
                        ExpressionRecipe.ResultOk(stringType, add value (int 1L)),
                        ExpressionRecipe.ResultErr(intType, error))))
            // A ListFold folder that loops by calling itself: countDown(acc, item) adds item one turn at a time.
            create "countDown" [ "acc", intType; "item", intType ] intType
                (ExpressionRecipe.Conditional(atMost (slot 1) (int 0L), slot 0, self [ add (slot 0) (int 1L); minus (slot 1) (int 1L) ]))
            create "foldTotal" [ "items", listType intType ] intType
                (ExpressionRecipe.ListFold(slot 0, int 0L, (find "countDown").Id, []))
            // Generic: repeatLength<T>(items: List<T>, n, acc) = if n <= 0 then acc else repeatLength(items, n - 1, acc + length(items)).
            // The self call is at the owner's own type parameter, so it is a jump like any other (review 2026-10-05 E-1).
            let t = TypeRef.TypeParameter(id<TypeParameterTag> "request-T", "T")
            create "repeatLength" [ "items", listType t; "n", intType; "acc", intType ] intType
                (ExpressionRecipe.Conditional(
                    atMost (slot 1) (int 0L),
                    slot 2,
                    self [ slot 0; minus (slot 1) (int 1L); add (slot 2) (ExpressionRecipe.ListLength(slot 0)) ]))
            let repeatLength = find "repeatLength"
            create "repeatIntLength" [ "items", listType intType; "n", intType ] intType
                (ExpressionRecipe.DirectCall(repeatLength.Id, [ repeatLength.Parameters.[0].Id, slot 0; repeatLength.Parameters.[1].Id, slot 1; repeatLength.Parameters.[2].Id, int 0L ]))
            // Effectful: logLoop(log, i, n) = if i >= n then i else (log.write(i); logLoop(log, i + 1, n)).
            createWith [ effect.Id ] "logLoop" [ "log", TypeRef.Capability log.Id; "i", intType; "n", intType ] intType
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64GreaterThanOrEqual(slot 1, slot 2),
                    slot 1,
                    ExpressionRecipe.Bind(
                        ExpressionRecipe.CapabilityInvoke(slot 0, write.Id, [ (List.exactlyOne write.Parameters).Id, ExpressionRecipe.Int64ToString(slot 1) ]),
                        self [ slot 0; add (slot 1) (int 1L); slot 2 ])))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            run { Session = reopened; Path = path; Log = log; Write = write
                  Function = fun name -> reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name) }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private lowered (fixture: Fixture) =
        let snapshot = SnapshotCodec.deserialize (File.ReadAllText fixture.Path) |> unwrap "snapshot"
        TypedSemanticIr.project snapshot |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"

    let private loweredFunction (program: ExecutionProgram) (fn: SemanticFunction) =
        program.Functions |> List.find (fun item -> item.SemanticFunctionId = fn.Id)

    let private tailCalls (fn: ExecutionFunction) =
        fn.Blocks |> List.filter (fun block -> match block.Terminator with ExecutionTerminator.TailCall _ -> true | _ -> false)

    let private selfCallInstructions (fn: ExecutionFunction) =
        fn.Blocks
        |> List.collect (fun block -> block.Instructions)
        |> List.filter (fun instruction ->
            match instruction.Kind with
            | ExecutionInstructionKind.DirectCall(target, _) -> target = fn.SemanticFunctionId
            | _ -> false)

    /// Blocks other than the entry that no terminator targets.
    let private unreachedBlocks (fn: ExecutionFunction) =
        let targeted =
            fn.Blocks
            |> List.collect (fun block ->
                match block.Terminator with
                | ExecutionTerminator.Jump(target, _) -> [ target ]
                | ExecutionTerminator.Branch(_, whenTrue, whenFalse) -> [ whenTrue; whenFalse ]
                | ExecutionTerminator.MatchUnion(_, targets) -> targets |> List.map (fun target -> target.BlockId)
                | ExecutionTerminator.MatchOption(_, someBlock, noneBlock) -> [ someBlock; noneBlock ]
                | ExecutionTerminator.MatchResult(_, okBlock, errBlock) -> [ okBlock; errBlock ]
                | ExecutionTerminator.TailCall _ -> [ fn.EntryBlockId ]
                | ExecutionTerminator.Abort _
                | ExecutionTerminator.Return _ -> [])
            |> Set.ofList
        fn.Blocks |> List.filter (fun block -> block.Id <> fn.EntryBlockId && not (Set.contains block.Id targeted))

    let private tailFunctions =
        [ "sumTo"; "bindLoop"; "someLoop"; "noneLoop"; "okLoop"; "errLoop"; "unionLoop"; "boolLoop"; "swap"; "abortLoop"; "countDown"; "repeatLength"; "logLoop" ]

    [<Fact>]
    let ``self calls in tail position lower to TailCall terminators and other self calls stay calls`` () =
        withFixture (fun fixture ->
            let program = lowered fixture
            for name in tailFunctions do
                let fn = loweredFunction program (fixture.Function name)
                Assert.True(not (List.isEmpty (tailCalls fn)), $"{name} has no TailCall")
                Assert.True(List.isEmpty (selfCallInstructions fn), $"{name} still calls itself")
                // The tail call ends its block, so nothing follows it there.
                for block in tailCalls fn do
                    match block.Terminator with
                    | ExecutionTerminator.TailCall(_, arguments) ->
                        Assert.Equal(fn.Parameters.Length, arguments.Length)
                    | _ -> ()
            // unionLoop has two tail arms (Even and otherwise). A merge that no arm reaches, as in
            // abortLoop where one arm aborts and the other ends in a tail call, is dropped.
            Assert.Equal(2, tailCalls (loweredFunction program (fixture.Function "unionLoop")) |> List.length)
            for fn in program.Functions do
                Assert.Empty(unreachedBlocks fn)
            for name in [ "sumNonTail"; "countNonTail"; "foldTotal"; "repeatIntLength" ] do
                let fn = loweredFunction program (fixture.Function name)
                Assert.Empty(tailCalls fn)
            for name in [ "sumNonTail"; "countNonTail" ] do
                Assert.Single(selfCallInstructions (loweredFunction program (fixture.Function name))) |> ignore
            // The codec writes the new terminator deterministically under schema version 9.
            let first = ExecutionIrSerializer.serialize program
            Assert.Equal(first, ExecutionIrSerializer.serialize program)
            Assert.Contains("\"kind\":\"tailCall\"", first)
            Assert.Contains("\"schemaVersion\":9", first)
            let reordered =
                { program with
                    Functions = program.Functions |> List.rev |> List.map (fun fn -> { fn with Blocks = List.rev fn.Blocks }) }
            Assert.Equal(first, ExecutionIrSerializer.serialize reordered))

    let private run (fixture: Fixture) name arguments =
        (PureRun.run fixture.Session (fixture.Function name).Id arguments None |> unwrap name).Actual

    let private i64 = PureRunValue.Int64

    [<Fact; Trait("Suite", "Extended")>]
    let ``a tail self call runs ten million turns under runPure`` () =
        withFixture (fun fixture ->
            Assert.Equal(i64 50000005000000L, run fixture "sumTo" [ i64 10000000L; i64 0L ]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``a self call in each tail position kind loops`` () =
        withFixture (fun fixture ->
            let turns = 1000000L
            for name in [ "bindLoop"; "someLoop"; "noneLoop"; "okLoop"; "errLoop" ] do
                Assert.Equal(i64 turns, run fixture name [ i64 turns; i64 0L ])
                Assert.Equal(i64 3L, run fixture name [ i64 3L; i64 0L ])
            // Even turns add 2 and odd turns 1.
            Assert.Equal(i64 (turns / 2L * 3L), run fixture "unionLoop" [ i64 turns; i64 0L ])
            Assert.Equal(i64 4L, run fixture "unionLoop" [ i64 3L; i64 0L ])
            Assert.Equal(PureRunValue.Bool true, run fixture "boolLoop" [ i64 turns ])
            // A self call inside a ListFold folder loops in the folder's own function.
            Assert.Equal(i64 3000000L, run fixture "foldTotal" [ PureRunValue.Int64List [ 1000000L; 2000000L ] ])
            // A generic loop keeps compiling: its tail call reuses the generic method's parameter locals.
            Assert.Equal(i64 (turns * 3L), run fixture "repeatIntLength" [ PureRunValue.Int64List [ 1L; 2L; 3L ]; i64 turns ]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``tail call arguments are assigned in parallel`` () =
        withFixture (fun fixture ->
            let swap n = run fixture "swap" [ PureRunValue.String "a"; PureRunValue.String "b"; i64 n ]
            Assert.Equal(PureRunValue.String "a|b", swap 0L)
            Assert.Equal(PureRunValue.String "b|a", swap 1L)
            Assert.Equal(PureRunValue.String "a|b", swap 2L)
            Assert.Equal(PureRunValue.String "b|a", swap 1000001L))

    [<Fact; Trait("Suite", "Extended")>]
    let ``an abort next to a tail call still aborts and the loop still returns`` () =
        withFixture (fun fixture ->
            Assert.Equal(PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 1000000L), run fixture "abortLoop" [ i64 1000000L; i64 0L; i64 2000000L ])
            Assert.Equal(PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String "limit"), run fixture "abortLoop" [ i64 1000000L; i64 0L; i64 500000L ]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``non-tail self calls remain ordinary calls`` () =
        withFixture (fun fixture ->
            Assert.Equal(i64 500500L, run fixture "sumNonTail" [ i64 1000L ])
            Assert.Equal(PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 1000L), run fixture "countNonTail" [ i64 1000L ]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``an effectful tail loop calls its capability once per turn in order`` () =
        withFixture (fun fixture ->
            let turns = 200
            let log =
                PureRunValue.FakeCapability(
                    fixture.Log.Id,
                    [ { OperationId = fixture.Write.Id; Cases = None; Responses = List.replicate turns (PureRunFakeResponse.Return PureRunValue.Unit) } ])
            let result = PureRun.run fixture.Session (fixture.Function "logLoop").Id [ log; i64 0L; i64 (int64 turns) ] None |> unwrap "logLoop"
            Assert.Equal(i64 (int64 turns), result.Actual)
            let messages = result.Calls |> List.map (fun call -> snd (List.exactlyOne call.Arguments))
            Assert.Equal<PureRunValue list>([ for index in 0 .. turns - 1 -> PureRunValue.String(string index) ], messages))
