namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure hands arguments and fake scripts to the child as input, so repeated calls of one entry
/// build the same program and reuse the session's compiled assembly.
module PureRunBuildCacheTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private list item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private option item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private createType (session: AuthoringSession) name kind =
        let request: AgentCreateTypeDeclarationRequest = { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = current session; DisplayName = name; Kind = kind }
        session.SimulateCreateType request |> unwrap ("create " + name) |> publish session
        session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)

    let private field name itemType: AgentTypeFieldIntent = { DisplayName = name; Type = itemType }

    let private createFunction (session: AuthoringSession) name parameters resultType effects body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = current session
              DisplayName = name
              Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
              ResultType = resultType
              RequiredEffects = effects
              Body = body }
        session.SimulateCreateFunction request |> unwrap ("create " + name) |> publish session
        session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)

    let private fields (itemType: SemanticType) =
        match itemType.Kind with
        | SemanticTypeKind.Product fields -> fields
        | _ -> failwith "Expected a Product."

    let private variants (itemType: SemanticType) =
        match itemType.Kind with
        | SemanticTypeKind.Union variants -> variants
        | _ -> failwith "Expected a Union."

    /// A Product value with fields named by display name, in canonical order as results report them.
    let private product (itemType: SemanticType) (values: (string * PureRunValue) list) =
        let declared = fields itemType
        PureRunValue.Product(itemType.Id, [],
            values |> List.map (fun (name, value) -> (declared |> List.find (fun item -> item.DisplayName = name)).Id, value)
            |> List.sortBy (fst >> StableId.value))

    let private union (itemType: SemanticType) variantName (values: (string * PureRunValue) list) =
        let variant = variants itemType |> List.find (fun item -> item.DisplayName = variantName)
        PureRunValue.Union(itemType.Id, [], variant.Id,
            values |> List.map (fun (name, value) -> (variant.Fields |> List.find (fun item -> item.DisplayName = name)).Id, value)
            |> List.sortBy (fst >> StableId.value))

    let private lookup () =
        match GeneratedBuild.lastLookup.Value with
        | Some lookup -> lookup
        | None -> failwith "No build was looked up."

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-build-cache-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap "session"
            run session
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``repeated runs of one entry reuse the build and round-trip varied arguments`` () =
        withSession (fun session ->
            let pair = createType session "CachePair" (AgentTypeDeclarationKindIntent.Product [ field "text" stringType; field "count" intType ])
            let shape =
                createType session "CacheShape"
                    (AgentTypeDeclarationKindIntent.Union
                        [ { DisplayName = "Circle"; Fields = [ field "radius" (TypeRef.Primitive PrimitiveType.Float64) ] }
                          { DisplayName = "Empty"; Fields = [] } ])
            let all =
                createType session "CacheAll"
                    (AgentTypeDeclarationKindIntent.Product
                        [ field "s" stringType
                          field "n" intType
                          field "b" (TypeRef.Primitive PrimitiveType.Bool)
                          field "f" (TypeRef.Primitive PrimitiveType.Float64)
                          field "w" (TypeRef.Primitive PrimitiveType.Bits32)
                          field "u" (TypeRef.Primitive PrimitiveType.Unit)
                          field "pairs" (list (TypeRef.Named(pair.Id, [])))
                          field "maybe" (option intType)
                          field "outcome" (TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ stringType; intType ]))
                          field "texts" (list stringType)
                          field "shape" (TypeRef.Named(shape.Id, []))
                          field "shapes" (list (option (TypeRef.Named(shape.Id, [])))) ])
            let identity = createFunction session "cacheIdentity" [ "all", TypeRef.Named(all.Id, []) ] (TypeRef.Named(all.Id, [])) [] (ExpressionRecipe.ParameterSlot 0)
            let other = createFunction session "cacheOther" [ "all", TypeRef.Named(all.Id, []) ] (TypeRef.Named(all.Id, [])) [] (ExpressionRecipe.ParameterSlot 0)
            let float bits = PureRunValue.Float64(Float64Bits.create bits)
            let pairValue text count = product pair [ "text", PureRunValue.String text; "count", PureRunValue.Int64 count ]
            let circle bits = union shape "Circle" [ "radius", float bits ]
            let value s n b f w pairs maybe outcome texts shapeValue shapes =
                product all
                    [ "s", PureRunValue.String s; "n", PureRunValue.Int64 n; "b", PureRunValue.Bool b; "f", float f
                      "w", PureRunValue.Bits32 w; "u", PureRunValue.Unit
                      "pairs", PureRunValue.ListValue(TypeRef.Named(pair.Id, []), pairs)
                      "maybe", PureRunValue.Int64Option maybe; "outcome", outcome; "texts", PureRunValue.StringList texts
                      "shape", shapeValue
                      "shapes", PureRunValue.ListValue(option (TypeRef.Named(shape.Id, [])), shapes |> List.map (fun item -> PureRunValue.OptionValue(TypeRef.Named(shape.Id, []), item))) ]
            let values =
                [ value "" 0L false 0UL 0u [] None (PureRunValue.ResultOk(PureRunScalarKind.Int64, PureRunScalar.String "")) [] (union shape "Empty" []) []
                  value "a b,c\n\"k" Int64.MinValue true 0x8000000000000000UL UInt32.MaxValue
                      [ pairValue "x y" 1L; pairValue "" -1L ] (Some Int64.MaxValue)
                      (PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.Int64 -7L)) [ "雪"; "😀 s"; ""; "k,1" ]
                      (circle 0x7ff8000000000001UL) [ Some(circle 0x3ff0000000000000UL); None; Some(union shape "Empty" []) ]
                  value "😀" Int64.MaxValue false 0x7ff0000000000000UL 1u [ pairValue "p" 0L ] (Some 0L)
                      (PureRunValue.ResultOk(PureRunScalarKind.Int64, PureRunScalar.String "ok")) [ "u" ] (circle 0xfff0000000000000UL) [ None ] ]
            let runs =
                values |> List.map (fun argument ->
                    let result = PureRun.run session identity.Id [ argument ] (Some argument) |> unwrap "identity"
                    Assert.Equal(argument, result.Actual)
                    Assert.Equal(Some true, result.Matches)
                    lookup ())
            let key = fst runs.Head
            // Every later call builds the same source and reuses the assembly.
            for later in runs.Tail do Assert.Equal((key, true), later)
            // Another entry is another program.
            PureRun.run session other.Id [ values.Head ] None |> unwrap "other" |> ignore
            Assert.NotEqual<string>(key, fst (lookup ()))
            // A snapshot change that alters the entry's code builds again.
            let bump = createFunction session "cacheBump" [ "n", intType ] intType [] (ExpressionRecipe.Int64WrappingAdd(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 1L))
            Assert.Equal(PureRunValue.Int64 6L, (PureRun.run session bump.Id [ PureRunValue.Int64 5L ] None |> unwrap "bump").Actual)
            let before = fst (lookup ())
            Assert.Equal(PureRunValue.Int64 8L, (PureRun.run session bump.Id [ PureRunValue.Int64 7L ] None |> unwrap "bump").Actual)
            Assert.Equal((before, true), lookup ())
            session.SimulateReplaceBody
                { TargetFunctionId = bump.Id
                  ExpectedBaseSnapshotId = current session
                  ExpectedCurrentResultNodeId = bump.Body.ResultNodeId
                  Replacement = ExpressionRecipe.Int64WrappingAdd(ExpressionRecipe.Parameter bump.Parameters.Head.Id, ExpressionRecipe.Int64Literal 2L) }
            |> unwrap "replace" |> publish session
            Assert.Equal(PureRunValue.Int64 7L, (PureRun.run session bump.Id [ PureRunValue.Int64 5L ] None |> unwrap "bumped").Actual)
            Assert.Equal((fst (lookup ()), false), lookup ())
            Assert.NotEqual<string>(before, fst (lookup ())))

    [<Fact; Trait("Suite", "Extended")>]
    let ``fake scripts and keyed cases are input, so different scripts share one build`` () =
        withSession (fun session ->
            let pair = createType session "KeyPair" (AgentTypeDeclarationKindIntent.Product [ field "text" stringType; field "count" intType ])
            let effectRequest: AgentCreateEffectDeclarationRequest = { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "Store" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let capabilityRequest: AgentCreateCapabilityDeclarationRequest =
                { Exported = true; ExpectedBaseSnapshotId = current session
                  DisplayName = "KeyStore"
                  Operations =
                    [ { DisplayName = "get"
                        Parameters = [ { DisplayName = "key"; Type = stringType }; { DisplayName = "pair"; Type = TypeRef.Named(pair.Id, []) } ]
                        ResultType = stringType
                        FailureType = None
                        EffectId = effect.Id } ] }
            session.SimulateCreateCapability capabilityRequest |> unwrap "capability" |> publish session
            let store = session.Declarations().Capabilities |> List.exactlyOne
            let get = store.Operations |> List.exactlyOne
            let parameterId name = (get.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
            let ask =
                createFunction session "ask" [ "key", stringType; "pair", TypeRef.Named(pair.Id, []); "store", TypeRef.Capability store.Id ] stringType [ effect.Id ]
                    (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 2, get.Id,
                        [ parameterId "key", ExpressionRecipe.ParameterSlot 0; parameterId "pair", ExpressionRecipe.ParameterSlot 1 ]))
            let pairValue text count = product pair [ "text", PureRunValue.String text; "count", PureRunValue.Int64 count ]
            let arguments key pairArgument = [ parameterId "key", PureRunValue.String key; parameterId "pair", pairArgument ] |> List.sortBy (fst >> StableId.value)
            let case key pairArgument answer: PureRunFakeCase =
                { Arguments = arguments key pairArgument; Responses = [ PureRunFakeResponse.Return(PureRunValue.String answer) ] }
            let keyed =
                PureRunValue.FakeCapability(store.Id,
                    [ { OperationId = get.Id; Responses = []
                        Cases = Some [ case "a" (pairValue "x" 1L) "A"; case "a" (pairValue "x" 2L) "B"; case "b😀" (pairValue "" -5L) "C" ] } ])
            let run key pairArgument binding = PureRun.run session ask.Id [ PureRunValue.String key; pairArgument; binding ] None
            let answer key pairArgument = (run key pairArgument keyed |> unwrap key).Actual
            Assert.Equal(PureRunValue.String "A", answer "a" (pairValue "x" 1L))
            let build = fst (lookup ())
            Assert.Equal(PureRunValue.String "B", answer "a" (pairValue "x" 2L))
            Assert.Equal((build, true), lookup ())
            Assert.Equal(PureRunValue.String "C", answer "b😀" (pairValue "" -5L))
            for key, pairArgument in [ "a", pairValue "x" 3L; "a", pairValue "y" 1L; "b", pairValue "" -5L; "a very long key that exceeds every case key", pairValue "x" 1L ] do
                match run key pairArgument keyed with
                | Error(PureRunError.RuntimeFailure failure) ->
                    Assert.Equal("FakeArgumentsUnmatchedException", failure.ExceptionType)
                    Assert.Equal<PureRunCall list>([ { ParameterId = ask.Parameters[2].Id; OperationId = get.Id; Arguments = arguments key pairArgument } ], failure.Calls)
                | other -> failwithf "An unmatched key was answered: %A" other
            // An ordered script with a throw, and another with a return, still reuse the build.
            let ordered responses = PureRunValue.FakeCapability(store.Id, [ { OperationId = get.Id; Responses = responses; Cases = None } ])
            match run "a" (pairValue "x" 1L) (ordered [ PureRunFakeResponse.Throw "down, 2 of 3 \"nodes\" 😀" ]) with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("FakeCapabilityException", failure.ExceptionType)
                Assert.Equal("down, 2 of 3 \"nodes\" 😀", failure.Message)
                Assert.Equal<PureRunFrame list>([ PureRunFrame.CapabilityOperation get.Id; PureRunFrame.Function ask.Id ], failure.Frames)
            | other -> failwithf "The throwing fake was not reported: %A" other
            Assert.Equal((build, true), lookup ())
            Assert.Equal(PureRunValue.String "ordered", (run "z" (pairValue "q" 9L) (ordered [ PureRunFakeResponse.Return(PureRunValue.String "ordered") ]) |> unwrap "ordered").Actual)
            Assert.Equal((build, true), lookup ())
            match run "z" (pairValue "q" 9L) (ordered []) with
            | Error(PureRunError.RuntimeFailure failure) -> Assert.True(failure.BindingExhausted)
            | other -> failwithf "The exhausted fake was not reported: %A" other
            Assert.Equal((build, true), lookup ()))
