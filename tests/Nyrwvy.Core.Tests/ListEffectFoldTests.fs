namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Effectful iteration: ListEffectFold runs an effectful folder once per item in order, with
/// capabilities passed as context, and stops at the first Err when the folder returns a Result.
module ListEffectFoldTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private stringList = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ])
    let private savedType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ unitType; stringType ])
    let private countType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ intType; stringType ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    type private Fixture =
        { Session: AuthoringSession
          Path: string
          Effect: SemanticEffect
          Store: SemanticCapability
          Save: SemanticCapabilityOperation
          Log: SemanticCapability
          Write: SemanticCapabilityOperation
          Function: string -> SemanticFunction }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private creation (session: AuthoringSession) name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session
          DisplayName = name
          Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
          ResultType = resultType
          RequiredEffects = effects
          Body = body }

    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-effect-fold-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let session = AuthoringSession.createNew path |> unwrap "session"
            let effectRequest: AgentCreateEffectDeclarationRequest = { ExpectedBaseSnapshotId = current session; DisplayName = "Write" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let capability name operation parameter resultType =
                let request: AgentCreateCapabilityDeclarationRequest =
                    { ExpectedBaseSnapshotId = current session
                      DisplayName = name
                      Operations =
                        [ { DisplayName = operation
                            Parameters = [ { DisplayName = parameter; Type = stringType } ]
                            ResultType = resultType
                            FailureType = None
                            EffectId = effect.Id } ] }
                session.SimulateCreateCapability request |> unwrap name |> publish session
                session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = name)
            let store = capability "Store" "save" "key" savedType
            let log = capability "Log" "write" "message" unitType
            let save, write = List.exactlyOne store.Operations, List.exactlyOne log.Operations
            let slot = ExpressionRecipe.ParameterSlot
            let value = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
            let error = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Error }
            let next = ExpressionRecipe.Int64WrappingAdd(slot 1, ExpressionRecipe.Int64Literal 1L)
            // saveOne(store, count, key) -> Result<Int64, String>
            creation session "saveOne" [ "store", TypeRef.Capability store.Id; "count", intType; "key", stringType ] countType [ effect.Id ]
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.CapabilityInvoke(slot 0, save.Id, [ (List.exactlyOne save.Parameters).Id, slot 2 ]),
                    ExpressionRecipe.ResultOk(stringType, next),
                    ExpressionRecipe.ResultErr(intType, error)))
            |> session.SimulateCreateFunction |> unwrap "saveOne" |> publish session
            // logOne(log, count, message) -> Int64
            creation session "logOne" [ "log", TypeRef.Capability log.Id; "count", intType; "message", stringType ] intType [ effect.Id ]
                (ExpressionRecipe.Bind(ExpressionRecipe.CapabilityInvoke(slot 0, write.Id, [ (List.exactlyOne write.Parameters).Id, slot 2 ]), next))
            |> session.SimulateCreateFunction |> unwrap "logOne" |> publish session
            let find name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let saveOne, logOne = find "saveOne", find "logOne"
            let bind (fn: SemanticFunction) name value = [ (fn.Parameters |> List.find (fun item -> item.DisplayName = name)).Id, value ]
            creation session "saveAll" [ "keys", stringList; "store", TypeRef.Capability store.Id ] countType [ effect.Id ]
                (ExpressionRecipe.ListEffectFold(slot 0, ExpressionRecipe.Int64Literal 0L, saveOne.Id, bind saveOne "store" (slot 1)))
            |> session.SimulateCreateFunction |> unwrap "saveAll" |> publish session
            creation session "logAll" [ "messages", stringList; "log", TypeRef.Capability log.Id ] intType [ effect.Id ]
                (ExpressionRecipe.ListEffectFold(slot 0, ExpressionRecipe.Int64Literal 0L, logOne.Id, bind logOne "log" (slot 1)))
            |> session.SimulateCreateFunction |> unwrap "logAll" |> publish session
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            run { Session = reopened; Path = path; Effect = effect; Store = store; Save = save; Log = log; Write = write
                  Function = fun name -> reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name) }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``effect folds need declared effects and a seed or Result result and persist`` () =
        withFixture (fun fixture ->
            let session = fixture.Session
            let saveOne = fixture.Function "saveOne"
            let context = [ (saveOne.Parameters |> List.find (fun item -> item.DisplayName = "store")).Id, ExpressionRecipe.ParameterSlot 1 ]
            let reject resultType effects body =
                match session.SimulateCreateFunction(creation session "bad" [ "keys", stringList; "store", TypeRef.Capability fixture.Store.Id ] resultType effects body) with
                | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) -> diagnostic.Code
                | other -> failwithf "Invalid effect fold accepted: %A" other
            let fold = ExpressionRecipe.ListEffectFold(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 0L, saveOne.Id, context)
            Assert.Equal("ExpressionCallMissingEffectRequirement", reject countType [] fold)
            Assert.Equal("PureExpressionListCallbackInputTypeMismatch",
                reject countType [ fixture.Effect.Id ] (ExpressionRecipe.ListEffectFold(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.StringLiteral "", saveOne.Id, context)))
            // The pure ListFold keeps rejecting effectful folders.
            let logOne = fixture.Function "logOne"
            let pureFold =
                creation session "pureLog" [ "messages", stringList; "log", TypeRef.Capability fixture.Log.Id ] intType [ fixture.Effect.Id ]
                    (ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 0L, logOne.Id,
                        [ (logOne.Parameters |> List.find (fun item -> item.DisplayName = "log")).Id, ExpressionRecipe.ParameterSlot 1 ]))
            match session.SimulateCreateFunction pureFold with
            | Error(AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected(diagnostic :: _))) ->
                Assert.Equal("PureExpressionListCallbackEffectsNotAllowed", diagnostic.Code)
            | other -> failwithf "Effectful folder accepted by ListFold: %A" other
            let json = File.ReadAllText fixture.Path
            Assert.Contains("\"listEffectFold\"", json)
            let snapshot = SnapshotCodec.deserialize json |> unwrap "decode"
            Assert.Empty(SnapshotValidator.validate snapshot)
            let nyason = NyasonCodec.serialize snapshot |> unwrap "nyason"
            Assert.Contains("list-effect-fold", nyason)
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute (NyasonCodec.deserialize nyason |> unwrap "nyason decode"))
            Assert.Equal(countType, (fixture.Function "saveAll").ResultType))

    [<Fact; Trait("Suite", "Extended")>]
    let ``effect folds run each effect once in order and stop at the first Err`` () =
        withFixture (fun fixture ->
            let keys = PureRunValue.StringList [ "a"; "b"; "c"; "d" ]
            let saved = PureRunValue.ResultValue(unitType, stringType, true, PureRunValue.Unit)
            let refused message = PureRunValue.ResultValue(unitType, stringType, false, PureRunValue.String message)
            let store responses =
                PureRunValue.FakeCapability(fixture.Store.Id, [ { OperationId = fixture.Save.Id; Cases = None; Responses = responses |> List.map PureRunFakeResponse.Return } ])
            let keyOf (call: PureRunCall) = snd (List.exactlyOne call.Arguments)
            let stopped = PureRun.run fixture.Session (fixture.Function "saveAll").Id [ keys; store [ saved; saved; refused "full" ] ] None |> unwrap "stopped"
            Assert.Equal(PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String "full"), stopped.Actual)
            Assert.Equal<PureRunValue list>([ PureRunValue.String "a"; PureRunValue.String "b"; PureRunValue.String "c" ], stopped.Calls |> List.map keyOf)
            let complete = PureRun.run fixture.Session (fixture.Function "saveAll").Id [ keys; store [ saved; saved; saved; saved ] ] None |> unwrap "complete"
            Assert.Equal(PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 4L), complete.Actual)
            Assert.Equal(4, complete.Calls.Length)
            let empty = PureRun.run fixture.Session (fixture.Function "saveAll").Id [ PureRunValue.StringList []; store [] ] None |> unwrap "empty"
            Assert.Equal(PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 0L), empty.Actual)
            let log = PureRunValue.FakeCapability(fixture.Log.Id, [ { OperationId = fixture.Write.Id; Cases = None; Responses = List.replicate 3 (PureRunFakeResponse.Return PureRunValue.Unit) } ])
            let logged = PureRun.run fixture.Session (fixture.Function "logAll").Id [ PureRunValue.StringList [ "x"; "y"; "z" ]; log ] (Some(PureRunValue.Int64 3L)) |> unwrap "log"
            Assert.Equal(Some true, logged.Matches)
            Assert.Equal<PureRunValue list>([ PureRunValue.String "x"; PureRunValue.String "y"; PureRunValue.String "z" ], logged.Calls |> List.map keyOf))
