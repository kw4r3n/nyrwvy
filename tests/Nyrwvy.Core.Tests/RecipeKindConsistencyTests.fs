namespace Nyrwvy.Core.Tests

open System
open System.IO
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

/// The files scripts/generate-recipe-signatures.fsx writes from `OperationSignatures` must match
/// the table, and every recipe case must still build a node kind.
module RecipeKindConsistencyTests =
    let private repoRoot () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "Could not locate repository root."
            elif File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName
            else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory))

    let private read relativePath = File.ReadAllText(Path.Combine(repoRoot (), relativePath))

    let private regenerate =
        "Regenerate it with `dotnet build src/Nyrwvy.Semantics/Nyrwvy.Semantics.fsproj -c Release && dotnet fsi scripts/generate-recipe-signatures.fsx`."

    [<Fact>]
    let ``recipe-signatures.generated.mjs is current with the operation signature table`` () =
        let path = "tools/nyrwvy-agent/recipe-signatures.generated.mjs"
        Assert.True((read path = OperationSignatures.referenceModule ()), $"{path} is stale. {regenerate}")

    [<Fact>]
    let ``recipe_signatures.txt is current with the operation signature table`` () =
        let path = "tools/zig/host/recipe_signatures.txt"
        Assert.True((read path = OperationSignatures.zigTable ()), $"{path} is stale. {regenerate}")

    let private describe (label: string) (kinds: Set<string>) =
        if Set.isEmpty kinds then None else Some(label + ": " + String.Join(", ", kinds))

    /// `ExpressionRecipe` cases that build a node of a differently named `SemanticNodeKind` case.
    /// `Parameter` and the ID-only `ParameterSlot` both become `ParameterReference`, and the ID-only
    /// `SelfCall` persists as `DirectCall` to the function being authored.
    let private recipeOnlyCases = Map.ofList [ "Parameter", "ParameterReference"; "ParameterSlot", "ParameterReference"; "SelfCall", "DirectCall" ]

    /// `ExpressionRecipe` cases that build no node of their own: `OriginalTarget` reuses the old target node.
    let private nodelessCases = Set.ofList [ "OriginalTarget" ]

    [<Fact>]
    let ``every ExpressionRecipe case maps to a SemanticNodeKind case and back`` () =
        let names unionType = FSharpType.GetUnionCases(unionType, true) |> Seq.map (fun case -> case.Name) |> Set.ofSeq
        let recipes = names typeof<ExpressionRecipe>
        let nodes = names typeof<SemanticNodeKind>
        let mapped = recipeOnlyCases |> Map.toSeq |> Seq.map snd |> Set.ofSeq
        let problems =
            [ describe "ExpressionRecipe cases with no SemanticNodeKind case (add the node kind or list the rename)" (recipes - nodes - (recipeOnlyCases.Keys |> Set.ofSeq) - nodelessCases)
              describe "SemanticNodeKind cases no recipe can build (add a recipe case)" (nodes - recipes - mapped)
              describe "Listed renames whose recipe case is gone or now matches a node kind" (recipeOnlyCases.Keys |> Set.ofSeq |> Set.filter (fun name -> not (recipes.Contains name) || nodes.Contains name))
              describe "Listed rename targets that are not SemanticNodeKind cases" (mapped - nodes) ]
            |> List.choose id
        Assert.True(List.isEmpty problems, String.Join(Environment.NewLine, problems))
