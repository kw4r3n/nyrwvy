namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `ListMaxBy(list, lessThan, context): Option<T>` (post-M2 order 69): the counterpart of ListMinBy
/// under the same less-than callback; the first of equal largest items wins, as Python max.
module ListMaxByTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private maxBy source (callback: SemanticFunction) = ExpressionRecipe.ListMaxBy(source, callback.Id, [])
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
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
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-max-by-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)

    type private Callbacks =
        { TextLess: SemanticFunction
          ShorterText: SemanticFunction
          IntLess: SemanticFunction
          AlwaysTrue: SemanticFunction
          AlwaysFalse: SemanticFunction }

    let private callbacks session =
        let pair t = [ "left", t; "right", t ]
        { TextLess = create session "textLess" (pair textType) boolType (ExpressionRecipe.StringLessThan(slot 0, slot 1))
          ShorterText =
            create session "shorterText" (pair textType) boolType
                (ExpressionRecipe.Int64LessThan(ExpressionRecipe.StringLength(slot 0), ExpressionRecipe.StringLength(slot 1)))
          IntLess = create session "intLess" (pair intType) boolType (ExpressionRecipe.Int64LessThan(slot 0, slot 1))
          AlwaysTrue = create session "alwaysTrue" (pair intType) boolType (ExpressionRecipe.BoolLiteral true)
          AlwaysFalse = create session "alwaysFalse" (pair intType) boolType (ExpressionRecipe.BoolLiteral false) }

    [<Fact>]
    let ``ListMaxBy checks its operands and callbacks and persists through every layer`` () =
        withSession (fun session path ->
            let fns = callbacks session
            let maxText = create session "maxText" [ "items", listOf textType ] (optionOf textType) (maxBy (slot 0) fns.TextLess)
            // The result is Option<T>; the source must be a List whose items both callback parameters accept.
            rejects session [ "items", listOf textType ] textType (maxBy (slot 0) fns.TextLess) |> ignore
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "items", textType ] (optionOf textType) (maxBy (slot 0) fns.TextLess))
            Assert.Contains("PureExpressionCallbackInputTypeMismatch", rejects session [ "items", listOf intType ] (optionOf intType) (maxBy (slot 0) fns.TextLess))
            let single = create session "single" [ "value", textType ] boolType (ExpressionRecipe.StringIsEmpty(slot 0))
            let lengthDiff =
                create session "lengthDiff" [ "left", textType; "right", textType ] intType
                    (ExpressionRecipe.Int64WrappingSubtract(ExpressionRecipe.StringLength(slot 0), ExpressionRecipe.StringLength(slot 1)))
            Assert.Contains("PureExpressionCallbackArityMismatch", rejects session [ "items", listOf textType ] (optionOf textType) (maxBy (slot 0) single))
            Assert.Contains("PureExpressionCallbackResultTypeMismatch", rejects session [ "items", listOf textType ] (optionOf textType) (maxBy (slot 0) lengthDiff))
            Assert.Contains("PureExpressionCallbackNotFound", rejects session [ "items", listOf textType ] (optionOf textType) (ExpressionRecipe.ListMaxBy(slot 0, StableId.createForTesting<FunctionTag> "function-missing", [])))
            session.Close()

            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap
            Assert.Contains("\"kind\":\"listMaxBy\"", json)
            Assert.Contains("\"schemaVersion\":20", json)
            let nyason = NyasonCodec.serialize snapshot |> unwrap
            Assert.Contains("list-max-by", nyason)
            Assert.Contains("nayamnyam.snapshot.v16", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"listMaxBy\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains("\"kind\":\"listMaxBy\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            Assert.Contains(".MaxBy(", source)
            match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":20", "\"schemaVersion\":19")) with
            | Ok _ -> failwith "A schema 19 snapshot with ListMaxBy decoded."
            | Error error -> Assert.Contains("ListMaxBy", sprintf "%A" error)

            // A persisted graph reports a callback of the wrong shape and a source that is not a List.
            let rewrite rewriteKind =
                { snapshot with
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            if fn.Id <> maxText.Id then fn
                            else { fn with Body = { fn.Body with Nodes = fn.Body.Nodes |> List.map (fun node -> { node with Kind = rewriteKind node.Kind }) } }) }
            let codes tampered = SnapshotValidator.validate tampered |> List.map (fun issue -> issue.Code)
            let singleCallback kind =
                match kind with
                | SemanticNodeKind.ListMaxBy(source, _, context) -> SemanticNodeKind.ListMaxBy(source, single.Id, context)
                | other -> other
            Assert.Contains("CallbackArityMismatch", codes (rewrite singleCallback))
            let parameterId = (maxText.Parameters |> List.head).Id
            let notAList kind =
                match kind with
                | SemanticNodeKind.ParameterReference reference when reference = parameterId -> SemanticNodeKind.Int64Literal 1L
                | other -> other
            Assert.Contains("HigherOrderSourceTypeMismatch", codes (rewrite notAList)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``ListMaxBy keeps the first largest item and runs through runPure`` () =
        withSession (fun session path ->
            let fns = callbacks session
            let maxText = create session "maxText" [ "items", listOf textType ] (optionOf textType) (maxBy (slot 0) fns.TextLess)
            let longest = create session "longest" [ "items", listOf textType ] (optionOf textType) (maxBy (slot 0) fns.ShorterText)
            let largest = create session "largest" [ "items", listOf intType ] (optionOf intType) (maxBy (slot 0) fns.IntLess)
            let maxAlwaysTrue = create session "maxAlwaysTrue" [ "items", listOf intType ] (optionOf intType) (maxBy (slot 0) fns.AlwaysTrue)
            let maxAlwaysFalse = create session "maxAlwaysFalse" [ "items", listOf intType ] (optionOf intType) (maxBy (slot 0) fns.AlwaysFalse)
            // Context binds the target; the item farthest from it wins, the first on a tie.
            let distance a b = ExpressionRecipe.Int64WrappingAbs(ExpressionRecipe.Int64WrappingSubtract(a, b))
            let closer =
                create session "closer" [ "target", intType; "left", intType; "right", intType ] boolType
                    (ExpressionRecipe.Int64LessThan(distance (slot 1) (slot 0), distance (slot 2) (slot 0)))
            let target = (closer.Parameters |> List.head).Id
            let farthest =
                create session "farthest" [ "items", listOf intType; "target", intType ] (optionOf intType)
                    (ExpressionRecipe.ListMaxBy(slot 0, closer.Id, [ target, slot 1 ]))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let texts items = PureRunValue.StringList items
            let ints items = PureRunValue.Int64List items
            Assert.Equal(PureRunValue.StringOption None, run maxText [ texts [] ])
            Assert.Equal(PureRunValue.StringOption(Some "only"), run maxText [ texts [ "only" ] ])
            Assert.Equal(PureRunValue.StringOption(Some "pear"), run maxText [ texts [ "fig"; "pear"; "apple"; "pear" ] ])
            // Ties: the first of the equally long items wins, as Python max(items, key=len).
            Assert.Equal(PureRunValue.StringOption(Some "dddd"), run longest [ texts [ "ccc"; "bb"; "dddd"; "aa"; "eeee" ] ])
            Assert.Equal(PureRunValue.Int64Option(Some 9L), run largest [ ints [ 1L; 5L; -2L; 9L; 0L; 9L ] ])
            Assert.Equal(PureRunValue.Int64Option None, run largest [ ints [] ])
            // Any callback gives a deterministic result: always true keeps replacing, so the last
            // item wins; always false never replaces, so the first item wins.
            Assert.Equal(PureRunValue.Int64Option(Some 5L), run maxAlwaysTrue [ ints [ 1L; 2L; 3L; 4L; 5L ] ])
            Assert.Equal(PureRunValue.Int64Option(Some 3L), run maxAlwaysFalse [ ints [ 3L; 1L; 2L ] ])
            Assert.Equal(PureRunValue.Int64Option(Some 1L), run farthest [ ints [ 1L; 8L; 12L; 15L ]; PureRunValue.Int64 10L ])
            Assert.Equal(PureRunValue.Int64Option(Some 1L), run farthest [ ints [ 1L; 19L; 8L ]; PureRunValue.Int64 10L ]))
