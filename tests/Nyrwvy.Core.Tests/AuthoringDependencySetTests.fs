namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Workspaces that import several dependencies under the import collision policy (Q-002).
[<Collection("Session storage faults")>]
module AuthoringDependencySetTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("dependency-set-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private moneyId = id<TypeTag> "money"
    let private amountId = id<FieldTag> "amount"

    let private money name: SemanticType =
        { Exported = true; TypeParameters = []; Id = moneyId; DisplayName = name; Kind = SemanticTypeKind.Product [ { Id = amountId; DisplayName = "amount"; Type = intType } ] }

    /// A function `name : Money -> Money` that returns its input.
    let private identity name: SemanticFunction =
        let parameter = id<ParameterTag> (name + "-input")
        let node = id<GraphNodeTag> (name + "-node")
        { MustNotWait = false; Exported = true; Id = id<FunctionTag> name
          TypeParameters = []
          DisplayName = name
          Parameters = [ { Id = parameter; DisplayName = "input"; Type = TypeRef.Named(moneyId, []) } ]
          ResultType = TypeRef.Named(moneyId, [])
          RequiredEffects = []
          Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference parameter } ]; ResultNodeId = node } }

    let private library name (types: SemanticType list) functions =
        { Snapshot.empty (id<SnapshotTag> name) with Types = types; Functions = functions }

    /// A and B both contain the identical Money type, as if both depended on one shared library.
    let private libraryA = library "a" [ money "Money" ] [ identity "keep" ]
    let private libraryB = library "b" [ money "Money" ] [ identity "pass" ]
    /// C reuses Money's identity for a differently named declaration.
    let private libraryC = library "c" [ money "Cash" ] [ identity "hold" ]

    let private withLibraries run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-dependency-set-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let write name snapshot =
                let path = Path.Combine(root, name + ".json")
                File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap "serialize")
                path
            run root (write "a" libraryA) (write "b" libraryB) (write "c" libraryC)
        finally Directory.Delete(root, true)

    [<Fact>]
    let ``identical shared declarations combine once and other collisions are rejected`` () =
        let combined = DependencyGraph.combine [ libraryA; libraryB ] |> unwrap "combine"
        Assert.Equal<TypeId list>([ moneyId ], combined.Types |> List.map (fun item -> item.Id))
        Assert.Equal<string list>([ "keep"; "pass" ], combined.Functions |> List.map (fun fn -> fn.DisplayName))
        Assert.Equal(combined.Id, (DependencyGraph.combine [ libraryB; libraryA ] |> unwrap "reordered").Id)
        Assert.Equal(libraryA, DependencyGraph.combine [ libraryA ] |> unwrap "single")
        match DependencyGraph.combine [ libraryA; libraryC ] with
        | Error(DependencyCombinationError.ConflictingDeclaration(kind, collided)) ->
            Assert.Equal("type", kind)
            Assert.Equal(StableId.value moneyId, collided)
        | other -> failwithf "Colliding identity was merged: %A" other
        Assert.Equal(Error DependencyCombinationError.NoDependencies, DependencyGraph.combine [])

    [<Fact>]
    let ``the same declaration with reordered Product fields merges, reordered parameters conflict`` () =
        let currencyId = id<FieldTag> "currency"
        let withFields fields: SemanticType =
            { Exported = true; TypeParameters = []; Id = moneyId; DisplayName = "Money"; Kind = SemanticTypeKind.Product fields }
        let amount: SemanticField = { Id = amountId; DisplayName = "amount"; Type = intType }
        let currency: SemanticField = { Id = currencyId; DisplayName = "currency"; Type = TypeRef.Primitive PrimitiveType.String }
        // Fields are keyed by ID and persisted in ID order, so their in-memory order is not content.
        let first = library "ordered" [ withFields [ amount; currency ] ] []
        let second = library "reordered" [ withFields [ currency; amount ] ] []
        Assert.True(DependencyGraph.combine [ first; second ] |> Result.isOk)
        // Parameter order is meaningful, so only identical declarations merge.
        let pair: SemanticFunction =
            let left: SemanticParameter = { Id = id<ParameterTag> "pair-left"; DisplayName = "left"; Type = intType }
            let right: SemanticParameter = { Id = id<ParameterTag> "pair-right"; DisplayName = "right"; Type = intType }
            let node = id<GraphNodeTag> "pair-node"
            { MustNotWait = false; Exported = true; Id = id<FunctionTag> "pair"; TypeParameters = []; DisplayName = "pair"
              Parameters = [ left; right ]; ResultType = intType; RequiredEffects = []
              Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference left.Id } ]; ResultNodeId = node } }
        let swapped = { pair with Parameters = List.rev pair.Parameters }
        match DependencyGraph.combine [ library "pair" [] [ pair ]; library "swapped" [] [ swapped ] ] with
        | Error(DependencyCombinationError.ConflictingDeclaration(kind, collided)) ->
            Assert.Equal("function", kind)
            Assert.Equal(StableId.value pair.Id, collided)
        | other -> failwithf "Reordered parameters were merged: %A" other

    let private writeVersion root name snapshot =
        let path = Path.Combine(root, name + ".json")
        File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap name)
        path

    let private changedMoney =
        { money "Money" with Kind = SemanticTypeKind.Product [ { Id = amountId; DisplayName = "amount"; Type = TypeRef.Primitive PrimitiveType.String } ] }

    /// A workspace created without dependencies, holding one local type.
    let private standalone root =
        let note: SemanticType =
            { Exported = true; TypeParameters = []; Id = id<TypeTag> "note"; DisplayName = "Note"
              Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "note-text"; DisplayName = "text"; Type = TypeRef.Primitive PrimitiveType.String } ] }
        writeVersion root "standalone" (library "standalone" [ note ] [])
