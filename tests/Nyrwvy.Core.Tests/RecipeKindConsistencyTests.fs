namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Expression recipe kinds are listed three times by hand: the ID-form wire reader (`WireRecipe`),
/// the display-name wire reader (`WireSymbolic`) and the agent-facing reference
/// (`tools/nyrwvy-agent/recipe-reference.mjs`). The readers are probed by behaviour: a kind is
/// supported when the reader does not reject it as an unknown kind.
module RecipeKindConsistencyTests =
    /// Kinds that exist only in the ID form. The name form reaches the same `ExpressionRecipe`
    /// cases by resolution: a `parameter` of a function being created becomes `ParameterSlot`, and
    /// a `directCall` naming the function being created or replaced becomes `SelfCall`.
    let private idOnlyKinds = set [ "parameterSlot"; "selfCall" ]

    let private repoRoot () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "Could not locate repository root."
            elif File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName
            else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory))

    let private read relativePath = File.ReadAllText(Path.Combine(repoRoot (), relativePath))

    let private camelCase (name: string) = string (Char.ToLowerInvariant name.[0]) + name.Substring 1

    /// The `categories` table of recipe-reference.mjs, which `recipeKinds` flattens.
    let private documentedKinds () =
        let source = read "tools/nyrwvy-agent/recipe-reference.mjs"
        let block = Regex.Match(source, @"const categories = \{(.*?)\n\}", RegexOptions.Singleline)
        Assert.True(block.Success, "recipe-reference.mjs no longer declares `const categories = { ... }`.")
        Regex.Matches(block.Groups.[1].Value, "\"([A-Za-z0-9]+)\"") |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq

    /// Every camelCase string literal in the readers plus every `ExpressionRecipe` case name, so a
    /// kind added to any one source is probed against all of them.
    let private candidateKinds documented =
        let literals path =
            Regex.Matches(read path, "\"([a-z][A-Za-z0-9]*)\"") |> Seq.map (fun m -> m.Groups.[1].Value)
        let cases =
            FSharpType.GetUnionCases(typeof<ExpressionRecipe>, true) |> Seq.map (fun case -> camelCase case.Name)
        Seq.concat
            [ literals "tools/Nyrwvy.AuthoringSession/WireRecipe.fs"
              literals "tools/Nyrwvy.AuthoringSession/WireSymbolic.fs"
              cases
              Set.toSeq documented ]
        |> Set.ofSeq

    let private accepts (unknownKindMessage: string -> string) (parse: JsonElement -> ExpressionRecipe) (kind: string) =
        use document = JsonDocument.Parse($"{{\"kind\":\"{kind}\"}}")
        try
            parse document.RootElement |> ignore
            true
        with
        | WireInput.ProtocolInputException message when message = unknownKindMessage kind -> false
        | _ -> true

    let private idForm = accepts (fun kind -> $"Unsupported expression recipe kind '{kind}'.") WireRecipe.recipe

    let private nameForm =
        let declarations: AuthoringDeclarations = { Functions = []; Types = []; Effects = []; Capabilities = [] }
        accepts
            (fun kind -> $"Unsupported symbolic expression recipe kind '{kind}'.")
            (WireSymbolic.recipeForNewFunction declarations "Probe" [])

    let private describe (label: string) (kinds: Set<string>) =
        if Set.isEmpty kinds then None else Some(label + ": " + String.Join(", ", kinds))

    [<Fact>]
    let ``ID form, name form and agent reference list the same recipe kinds`` () =
        let documented = documentedKinds ()
        let candidates = candidateKinds documented
        Assert.False(idForm "noSuchRecipeKind", "The ID-form probe must reject unknown kinds.")
        Assert.False(nameForm "noSuchRecipeKind", "The name-form probe must reject unknown kinds.")
        let idKinds = candidates |> Set.filter idForm
        let nameKinds = candidates |> Set.filter nameForm
        let problems =
            [ describe "ID form only (add to WireSymbolic or idOnlyKinds)" (idKinds - nameKinds - idOnlyKinds)
              describe "Name form only (add to WireRecipe)" (nameKinds - idKinds)
              describe "idOnlyKinds entries that the name form now accepts" (Set.intersect idOnlyKinds nameKinds)
              describe "idOnlyKinds entries that the ID form no longer accepts" (idOnlyKinds - idKinds)
              describe "Name form but undocumented in recipe-reference.mjs" (nameKinds - documented)
              describe "Documented in recipe-reference.mjs but rejected by the name form" (documented - nameKinds) ]
            |> List.choose id
        Assert.True(List.isEmpty problems, String.Join(Environment.NewLine, problems))
