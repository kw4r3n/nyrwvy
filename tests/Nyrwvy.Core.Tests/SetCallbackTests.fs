namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// SetFold, SetFilter and SetMap (post-M2 order 68): the List and Map callback operations over a
/// Set<T>, visiting the items in insertion order. SetMap's result item type must be equatable.
module SetCallbackTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private setOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
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
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-set-callbacks-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)

    /// The callbacks: a length folder, a length-bounded folder with context, a predicate, a mapper
    /// to Int64 and one to Float64 (not equatable).
    type private Callbacks =
        { AddLength: SemanticFunction
          CountLongerThan: SemanticFunction
          IsShort: SemanticFunction
          Length: SemanticFunction
          LengthAsFloat: SemanticFunction }

    let private callbacks session =
        let length recipe = ExpressionRecipe.StringLength recipe
        { AddLength = create session "addLength" [ "total", intType; "item", textType ] intType (ExpressionRecipe.Int64WrappingAdd(slot 0, length (slot 1)))
          CountLongerThan =
            create session "countLongerThan" [ "limit", intType; "count", intType; "item", textType ] intType
                (ExpressionRecipe.Conditional(ExpressionRecipe.Int64LessThan(slot 0, length (slot 2)), ExpressionRecipe.Int64WrappingAdd(slot 1, ExpressionRecipe.Int64Literal 1L), slot 1))
          IsShort = create session "isShort" [ "item", textType ] boolType (ExpressionRecipe.Int64LessThan(length (slot 0), ExpressionRecipe.Int64Literal 3L))
          Length = create session "length" [ "item", textType ] intType (length (slot 0))
          LengthAsFloat = create session "lengthAsFloat" [ "item", textType ] floatType (ExpressionRecipe.Int64ToFloat64(length (slot 0))) }

    let private program session =
        let fns = callbacks session
        let totalLength = create session "totalLength" [ "items", setOf textType ] intType (ExpressionRecipe.SetFold(slot 0, ExpressionRecipe.Int64Literal 0L, fns.AddLength.Id, []))
        let limit = (fns.CountLongerThan.Parameters |> List.head).Id
        let longerThan =
            create session "longerThan" [ "items", setOf textType; "limit", intType ] intType
                (ExpressionRecipe.SetFold(slot 0, ExpressionRecipe.Int64Literal 0L, fns.CountLongerThan.Id, [ limit, slot 1 ]))
        let shortOnes = create session "shortOnes" [ "items", setOf textType ] (setOf textType) (ExpressionRecipe.SetFilter(slot 0, fns.IsShort.Id, []))
        let lengths = create session "lengths" [ "items", setOf textType ] (setOf intType) (ExpressionRecipe.SetMap(slot 0, fns.Length.Id, []))
        fns, totalLength, longerThan, shortOnes, lengths

    [<Fact>]
    let ``Set callbacks check their operands and callbacks and persist through every layer`` () =
        withSession (fun session path ->
            let fns, _, _, _, lengths = program session
            // The source must be a Set.
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "items", listOf textType ] intType (ExpressionRecipe.SetFold(slot 0, ExpressionRecipe.Int64Literal 0L, fns.AddLength.Id, [])))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "items", listOf textType ] (listOf textType) (ExpressionRecipe.SetFilter(slot 0, fns.IsShort.Id, [])))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "items", textType ] (setOf intType) (ExpressionRecipe.SetMap(slot 0, fns.Length.Id, [])))
            // Callback shapes: arity, parameter type, Bool result for the filter, the seed type for the fold.
            Assert.Contains("PureExpressionCallbackArityMismatch", rejects session [ "items", setOf textType ] (setOf intType) (ExpressionRecipe.SetMap(slot 0, fns.AddLength.Id, [])))
            Assert.Contains("PureExpressionCallbackArityMismatch", rejects session [ "items", setOf textType ] intType (ExpressionRecipe.SetFold(slot 0, ExpressionRecipe.Int64Literal 0L, fns.Length.Id, [])))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", rejects session [ "items", setOf intType ] (setOf intType) (ExpressionRecipe.SetFilter(slot 0, fns.IsShort.Id, [])))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", rejects session [ "items", setOf textType ] textType (ExpressionRecipe.SetFold(slot 0, ExpressionRecipe.StringLiteral "", fns.AddLength.Id, [])))
            Assert.Contains("PureExpressionCallbackResultTypeMismatch", rejects session [ "items", setOf textType ] (setOf textType) (ExpressionRecipe.SetFilter(slot 0, fns.Length.Id, [])))
            Assert.Contains("PureExpressionCallbackNotFound", rejects session [ "items", setOf textType ] (setOf textType) (ExpressionRecipe.SetFilter(slot 0, StableId.createForTesting<FunctionTag> "function-missing", [])))
            // The mapped item type must be equatable, as every Set item type is.
            Assert.Contains("SetItemTypeNotEquatable", rejects session [ "items", setOf textType ] (setOf floatType) (ExpressionRecipe.SetMap(slot 0, fns.LengthAsFloat.Id, [])))
            // The results are typed: Set<T> for the filter, Set<U> for the map, the seed type for the fold.
            rejects session [ "items", setOf textType ] (listOf textType) (ExpressionRecipe.SetFilter(slot 0, fns.IsShort.Id, [])) |> ignore
            rejects session [ "items", setOf textType ] (setOf textType) (ExpressionRecipe.SetMap(slot 0, fns.Length.Id, [])) |> ignore
            rejects session [ "items", setOf textType ] textType (ExpressionRecipe.SetFold(slot 0, ExpressionRecipe.Int64Literal 0L, fns.AddLength.Id, [])) |> ignore
            session.Close()

            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap
            for kind in [ "\"setFold\""; "\"setFilter\""; "\"setMap\""; "\"schemaVersion\":20" ] do
                Assert.Contains(kind, json)
            let nyason = NyasonCodec.serialize snapshot |> unwrap
            for tag in [ "set-fold"; "set-filter"; "set-map"; "nayamnyam.snapshot.v16" ] do
                Assert.Contains(tag, nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap)
            let typed = TypedSemanticIr.project restored |> unwrap
            let typedJson = TypedSemanticIrSerializer.serialize typed
            let execution = ExecutionIr.lower typed |> unwrap
            let executionJson = ExecutionIrSerializer.serialize execution
            for kind in [ "\"setFold\""; "\"setFilter\""; "\"setMap\"" ] do
                Assert.Contains(kind, typedJson)
                Assert.Contains(kind, executionJson)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            for call in [ ".Fold("; ".Filter("; ".Map(" ] do
                Assert.Contains(call, source)
            match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":20", "\"schemaVersion\":19")) with
            | Ok _ -> failwith "A schema 19 snapshot with Set callbacks decoded."
            | Error error -> Assert.Contains("SetFold, SetFilter and SetMap", sprintf "%A" error)

            // A persisted graph reports its own diagnostics.
            let rewrite (target: SemanticFunction) rewriteKind =
                { snapshot with
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            if fn.Id <> target.Id then fn
                            else { fn with Body = { fn.Body with Nodes = fn.Body.Nodes |> List.map (fun node -> { node with Kind = rewriteKind node.Kind }) } }) }
            let codes tampered = SnapshotValidator.validate tampered |> List.map (fun issue -> issue.Code)
            let toFloat kind =
                match kind with
                | SemanticNodeKind.SetMap(source, _, context) -> SemanticNodeKind.SetMap(source, fns.LengthAsFloat.Id, context)
                | other -> other
            Assert.Contains("SetItemTypeNotEquatable", codes (rewrite lengths toFloat))
            let parameterId = (lengths.Parameters |> List.head).Id
            let notASet kind =
                match kind with
                | SemanticNodeKind.ParameterReference reference when reference = parameterId -> SemanticNodeKind.Int64Literal 1L
                | other -> other
            Assert.Contains("HigherOrderSourceTypeMismatch", codes (rewrite lengths notASet))
            let totalLength = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = "totalLength")
            let totalParameterId = (totalLength.Parameters |> List.head).Id
            let foldOverInt kind =
                match kind with
                | SemanticNodeKind.ParameterReference reference when reference = totalParameterId -> SemanticNodeKind.Int64Literal 1L
                | other -> other
            Assert.Contains("SetInputTypeMismatch", codes (rewrite totalLength foldOverInt)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Set callbacks visit the items in insertion order under runPure`` () =
        withSession (fun session path ->
            let _, totalLength, longerThan, shortOnes, lengths = program session
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let texts items = PureRunValue.SetValue(textType, items |> List.map PureRunValue.String)
            let ints items = PureRunValue.SetValue(intType, items |> List.map PureRunValue.Int64)
            Assert.Equal(PureRunValue.Int64 0L, run totalLength [ texts [] ])
            Assert.Equal(PureRunValue.Int64 6L, run totalLength [ texts [ "a"; "bb"; "ccc" ] ])
            // Context binds the limit once; the folder sees the items in insertion order.
            Assert.Equal(PureRunValue.Int64 2L, run longerThan [ texts [ "a"; "bbbb"; "cc"; "ddd" ]; PureRunValue.Int64 2L ])
            Assert.Equal(PureRunValue.Int64 0L, run longerThan [ texts [ "a"; "bbbb" ]; PureRunValue.Int64 9L ])
            // The kept items stay in their order; nothing kept gives the empty set.
            Assert.Equal(texts [ "cc"; "a" ], run shortOnes [ texts [ "cc"; "bbbb"; "a"; "ddd" ] ])
            Assert.Equal(texts [], run shortOnes [ texts [ "bbbb"; "ddd" ] ])
            Assert.Equal(texts [], run shortOnes [ texts [] ])
            // Mapped results are inserted in order, once each, so the result can be smaller.
            Assert.Equal(ints [ 3L; 1L; 2L ], run lengths [ texts [ "ccc"; "a"; "bb"; "dd"; "eee" ] ])
            Assert.Equal(ints [], run lengths [ texts [] ]))
