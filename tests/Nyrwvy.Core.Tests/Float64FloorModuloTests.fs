namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `Float64FloorModulo(left, right): Float64` (post-M2 order 70): Python's float `%`, a nonzero
/// result signed like the divisor as Int64FloorModulo, with IEEE NaN where Python raises.
module Float64FloorModuloTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private slot = ExpressionRecipe.ParameterSlot
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
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-float64-floor-modulo-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private modulo = create

    [<Fact>]
    let ``Float64FloorModulo checks its operands and persists through every layer`` () =
        withSession (fun session path ->
            let remainder = modulo session "remainder" [ "left", floatType; "right", floatType ] floatType (ExpressionRecipe.Float64FloorModulo(slot 0, slot 1))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "left", floatType; "right", intType ] floatType (ExpressionRecipe.Float64FloorModulo(slot 0, slot 1)))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "left", intType; "right", intType ] floatType (ExpressionRecipe.Float64FloorModulo(slot 0, slot 1)))
            rejects session [ "left", floatType; "right", floatType ] intType (ExpressionRecipe.Float64FloorModulo(slot 0, slot 1)) |> ignore
            session.Close()

            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            Assert.Empty(SnapshotValidator.validate snapshot)
            let json = SnapshotCodec.serialize snapshot |> unwrap
            Assert.Contains("\"kind\":\"float64FloorModulo\"", json)
            Assert.Contains("\"schemaVersion\":20", json)
            let nyason = NyasonCodec.serialize snapshot |> unwrap
            Assert.Contains("float64-floor-modulo", nyason)
            Assert.Contains("nayamnyam.snapshot.v16", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(json, SnapshotCodec.serialize restored |> unwrap)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"float64FloorModulo\"", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains("\"kind\":\"float64FloorModulo\"", ExecutionIrSerializer.serialize execution)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            Assert.Contains("NyNumber.FloorModulo(", source)
            match SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":20", "\"schemaVersion\":19")) with
            | Ok _ -> failwith "A schema 19 snapshot with Float64FloorModulo decoded."
            | Error error -> Assert.Contains("Float64FloorModulo", sprintf "%A" error)

            // A persisted graph reports a non-Float64 operand.
            let parameterId = (remainder.Parameters |> List.last).Id
            let tampered =
                { snapshot with
                    Functions =
                        snapshot.Functions
                        |> List.map (fun fn ->
                            if fn.Id <> remainder.Id then fn
                            else
                                { fn with
                                    Body =
                                        { fn.Body with
                                            Nodes =
                                                fn.Body.Nodes
                                                |> List.map (fun node ->
                                                    match node.Kind with
                                                    | SemanticNodeKind.ParameterReference reference when reference = parameterId -> { node with Kind = SemanticNodeKind.Int64Literal 2L }
                                                    | _ -> node) } }) }
            Assert.Contains("Float64OperandTypeMismatch", SnapshotValidator.validate tampered |> List.map (fun issue -> issue.Code)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Float64FloorModulo is Python's float remainder with NaN where Python raises`` () =
        withSession (fun session path ->
            let remainder = modulo session "remainder" [ "left", floatType; "right", floatType ] floatType (ExpressionRecipe.Float64FloorModulo(slot 0, slot 1))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let float (value: float) = PureRunValue.Float64(Float64Bits.ofFloat value)
            let run left right = (PureRun.run reopened remainder.Id [ float left; float right ] None |> unwrap).Actual
            let bits (value: PureRunValue) =
                match value with
                | PureRunValue.Float64 bits -> Float64Bits.value bits
                | other -> failwithf "Expected a Float64, got %A" other
            // Python: 7.5 % 2 == 1.5, -7.5 % 2 == 0.5, 7.5 % -2 == -0.5, -7.5 % -2 == -1.5.
            let cases =
                [ 7.5, 2.0, 1.5
                  -7.5, 2.0, 0.5
                  7.5, -2.0, -0.5
                  -7.5, -2.0, -1.5
                  // fmod is exact, so the result is Python's 0.09999999999999998, not 0.1.
                  0.5, 0.1, 0.09999999999999998
                  10.0, 3.0, 1.0
                  // A finite dividend with an infinite divisor: the dividend, or the infinity when the signs differ.
                  5.0, Double.PositiveInfinity, 5.0
                  -5.0, Double.PositiveInfinity, Double.PositiveInfinity
                  5.0, Double.NegativeInfinity, Double.NegativeInfinity
                  -5.0, Double.NegativeInfinity, -5.0 ]
            for left, right, expected in cases do
                Assert.Equal(float expected, run left right)
            // A zero result takes the divisor's sign, as Python's does.
            Assert.Equal(Float64Bits.value (Float64Bits.ofFloat 0.0), bits (run -6.0 3.0))
            Assert.Equal(Float64Bits.value (Float64Bits.ofFloat -0.0), bits (run 6.0 -3.0))
            Assert.Equal(Float64Bits.value (Float64Bits.ofFloat 0.0), bits (run -0.0 3.0))
            // NaN where Python raises ZeroDivisionError, and for an infinite dividend or a NaN operand.
            for left, right in [ 1.0, 0.0; -1.0, -0.0; 0.0, 0.0; Double.PositiveInfinity, 2.0; Double.NegativeInfinity, Double.PositiveInfinity; Double.NaN, 2.0; 2.0, Double.NaN ] do
                Assert.Equal(Float64Bits.canonicalNaNBits, bits (run left right)))
