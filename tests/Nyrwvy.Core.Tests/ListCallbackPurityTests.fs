namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// List callbacks use the same purity rule as function values and capability implementations
/// (`PureFunctions.isPure`): no required effects, and no capability, resource, task or
/// cancellation behavior, directly or through called functions.
module ListCallbackPurityTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])

    [<Fact>]
    let ``persisted List callbacks reject ambient task and cancellation behavior, also through calls`` () =
        let id name = StableId.createForTesting<FunctionTag> ("function-purity-" + name)
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-purity-" + name)
        let parameter owner index t: SemanticParameter =
            { Id = StableId.createForTesting<ParameterTag> ("parameter-purity-" + owner + "-" + string index); DisplayName = "p" + string index; Type = t }
        let callback name parameterTypes (body: SemanticFunction -> SemanticNode list * GraphNodeId) =
            let shell: SemanticFunction =
                { MustNotWait = false; Exported = true; Id = id name; DisplayName = name
                  TypeParameters = []
                  Parameters = parameterTypes |> List.mapi (fun index t -> parameter name index t)
                  ResultType = boolType; RequiredEffects = []
                  Body = { Nodes = []; ResultNodeId = nodeId (name + "-unused") } }
            let nodes, resultNodeId = body shell
            { shell with Body = { Nodes = nodes; ResultNodeId = resultNodeId } }
        let node name kind: SemanticNode = { Id = nodeId name; Kind = kind }
        let constant name = fun (_: SemanticFunction) -> [ node name (SemanticNodeKind.BoolLiteral true) ], nodeId name
        let cancellation name = fun (_: SemanticFunction) -> [ node name SemanticNodeKind.CancellationRequested ], nodeId name
        let ambient = callback "ambient" [ textType ] (cancellation "ambient-body")
        // Calls `ambient`; nothing in its own body is effectful.
        let indirect =
            callback "indirect" [ textType ] (fun fn ->
                let argument = nodeId "indirect-argument"
                [ node "indirect-argument" (SemanticNodeKind.ParameterReference fn.Parameters.Head.Id)
                  node "indirect-call" (SemanticNodeKind.DirectCall(ambient.Id, [ { TargetParameterId = ambient.Parameters.Head.Id; ValueNodeId = argument } ])) ],
                nodeId "indirect-call")
        let fns =
            [ ambient
              indirect
              callback "pure" [ textType ] (constant "pure-body")
              callback "pureLess" [ textType; textType ] (constant "pureLess-body")
              callback "ambientLess" [ textType; textType ] (cancellation "ambientLess-body")
              callback "pureFolder" [ boolType; textType ] (constant "pureFolder-body")
              callback "ambientFolder" [ boolType; textType ] (cancellation "ambientFolder-body") ]
        let validate kind resultType =
            let source = nodeId "source"
            let seed = nodeId "seed"
            let result = nodeId "result"
            let entry: SemanticFunction =
                { MustNotWait = false; Exported = true; Id = id "entry"; DisplayName = "entry"; Parameters = []; ResultType = resultType; RequiredEffects = []
                  TypeParameters = []
                  Body =
                    { Nodes =
                        [ node "source" (SemanticNodeKind.ListEmpty textType)
                          node "seed" (SemanticNodeKind.BoolLiteral false)
                          node "result" (kind source seed) ]
                      ResultNodeId = result } }
            SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-purity") with Functions = entry :: fns }
            |> List.map (fun issue -> issue.Code)
        let unary make = fun name -> validate (fun source _ -> make (source, id name, [])) (listOf textType)
        let cases: (string * (string -> string list) * string * string list) list =
            [ "ListMap", (fun name -> validate (fun source _ -> SemanticNodeKind.ListMap(source, id name, [])) (listOf boolType)), "pure", [ "ambient"; "indirect" ]
              "ListFilter", unary SemanticNodeKind.ListFilter, "pure", [ "ambient"; "indirect" ]
              "ListAny", (fun name -> validate (fun source _ -> SemanticNodeKind.ListAny(source, id name, [])) boolType), "pure", [ "ambient"; "indirect" ]
              "ListFindIndex", (fun name -> validate (fun source _ -> SemanticNodeKind.ListFindIndex(source, id name, [])) (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))), "pure", [ "ambient"; "indirect" ]
              "ListAll", (fun name -> validate (fun source _ -> SemanticNodeKind.ListAll(source, id name, [])) boolType), "pure", [ "ambient"; "indirect" ]
              "ListSortBy", unary SemanticNodeKind.ListSortBy, "pureLess", [ "ambientLess" ]
              "ListFold", (fun name -> validate (fun source seed -> SemanticNodeKind.ListFold(source, seed, id name, [])) boolType), "pureFolder", [ "ambientFolder" ] ]
        for construct, check, pureName, impureNames in cases do
            Assert.True(List.isEmpty (check pureName), $"{construct} rejected a pure callback: %A{check pureName}")
            for impure in impureNames do
                Assert.True(List.contains "CallbackEffectsNotAllowed" (check impure), $"{construct} accepted {impure}: %A{check impure}")

    [<Fact>]
    let ``authored List callbacks reject ambient cancellation behavior`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-callback-purity-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap
            let slot = ExpressionRecipe.ParameterSlot
            let request name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap).SnapshotId; DisplayName = name
                  TypeParameters = None
                  Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let create name parameters resultType body =
                let proposal = request name parameters resultType body |> session.SimulateCreateFunction |> unwrap
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
                session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let rejects resultType body =
                match session.SimulateCreateFunction(request "rejected" [ "items", listOf textType ] resultType body) with
                | Error error -> sprintf "%A" error
                | Ok _ -> failwith "The function was accepted."
            // A function without declared effects may still check cancellation; it is not pure.
            let ambient = create "ambient" [ "value", textType ] boolType ExpressionRecipe.CancellationRequested
            let ambientLess = create "ambientLess" [ "left", textType; "right", textType ] boolType ExpressionRecipe.CancellationRequested
            let ambientFolder = create "ambientFolder" [ "seen", boolType; "value", textType ] boolType ExpressionRecipe.CancellationRequested
            let code = "PureExpressionCallbackEffectsNotAllowed"
            Assert.Contains(code, rejects (listOf boolType) (ExpressionRecipe.ListMap(slot 0, ambient.Id, [])))
            Assert.Contains("use ListEffectFold instead", rejects (listOf boolType) (ExpressionRecipe.ListMap(slot 0, ambient.Id, [])))
            Assert.Contains(code, rejects (listOf textType) (ExpressionRecipe.ListFilter(slot 0, ambient.Id, [])))
            Assert.Contains(code, rejects boolType (ExpressionRecipe.ListAny(slot 0, ambient.Id, [])))
            Assert.Contains(code, rejects boolType (ExpressionRecipe.ListAll(slot 0, ambient.Id, [])))
            Assert.Contains(code, rejects (listOf textType) (ExpressionRecipe.ListSortBy(slot 0, ambientLess.Id, [])))
            Assert.Contains(code, rejects boolType (ExpressionRecipe.ListFold(slot 0, ExpressionRecipe.BoolLiteral false, ambientFolder.Id, [])))
        finally
            Directory.Delete(root, true)
