namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Expression recipe kinds are listed by hand in the ID-form wire reader (`WireRecipe`), the
/// display-name wire reader (`WireSymbolic`), the reference-free kinds both share
/// (`WireRecipeCommon`) and the agent-facing reference
/// (`tools/nyrwvy-agent/recipe-reference.mjs`). The readers are probed by behaviour: a kind is
/// supported when the reader does not reject it as an unknown kind. The reference signatures of
/// fixed-signature kinds are generated from `OperationSignatures` and checked here as well.
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
              literals "tools/Nyrwvy.AuthoringSession/WireRecipeCommon.fs"
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
        let declarations: AuthoringDeclarations = { Functions = []; Types = []; Effects = []; Capabilities = []; Provenance = [] }
        accepts
            (fun kind -> $"Unsupported symbolic expression recipe kind '{kind}'.")
            (WireSymbolic.recipeForNewFunction declarations "Probe" [])

    let private describe (label: string) (kinds: Set<string>) =
        if Set.isEmpty kinds then None else Some(label + ": " + String.Join(", ", kinds))

    [<Fact>]
    let ``ID form, name form and agent reference list the same recipe kinds`` () =
        let documented = documentedKinds ()
        let candidates = candidateKinds documented
        let retired = OperationSignatures.retired.Keys |> Set.ofSeq
        Assert.False(idForm "noSuchRecipeKind", "The ID-form probe must reject unknown kinds.")
        Assert.False(nameForm "noSuchRecipeKind", "The name-form probe must reject unknown kinds.")
        let idKinds = candidates |> Set.filter idForm
        let recipeCaseKinds = FSharpType.GetUnionCases(typeof<ExpressionRecipe>, true) |> Seq.map (fun case -> camelCase case.Name) |> Set.ofSeq
        let nameKinds = candidates |> Set.filter nameForm
        let problems =
            [ describe "ID form only (add to WireSymbolic or idOnlyKinds)" (idKinds - nameKinds - idOnlyKinds)
              describe "Name form only (add to WireRecipe)" (nameKinds - idKinds)
              describe "idOnlyKinds entries that the name form now accepts" (Set.intersect idOnlyKinds nameKinds)
              describe "idOnlyKinds entries that the ID form no longer accepts" (idOnlyKinds - idKinds)
              describe "Name form but undocumented in recipe-reference.mjs" (nameKinds - documented - retired)
              describe "Retired kinds still documented in recipe-reference.mjs" (Set.intersect retired documented)
              describe "Retired kinds the readers no longer accept (drop them from OperationSignatures.retired)" (retired - Set.intersect idKinds nameKinds)
              describe "Documented in recipe-reference.mjs but rejected by the name form" (documented - nameKinds)
              describe "ExpressionRecipe cases whose kind the ID form rejects" (recipeCaseKinds - idKinds) ]
            |> List.choose id
        Assert.True(List.isEmpty problems, String.Join(Environment.NewLine, problems))

    /// The fixed-signature operations' reference signatures are generated from `OperationSignatures`.
    [<Fact>]
    let ``recipe-signatures.generated.mjs is current with the operation signature table`` () =
        let current = read "tools/nyrwvy-agent/recipe-signatures.generated.mjs"
        Assert.True(
            (current = OperationSignatures.referenceModule ()),
            "tools/nyrwvy-agent/recipe-signatures.generated.mjs is stale. Regenerate it with "
            + "`dotnet build src/Nyrwvy.Semantics/Nyrwvy.Semantics.fsproj -c Release && dotnet fsi scripts/generate-recipe-signatures.fsx`."
        )

    /// The Zig host's copy of the table is generated by the same script.
    [<Fact>]
    let ``recipe_signatures.txt is current with the operation signature table`` () =
        let current = read "tools/zig/host/recipe_signatures.txt"
        Assert.True(
            (current = OperationSignatures.zigTable ()),
            "tools/zig/host/recipe_signatures.txt is stale. Regenerate it with "
            + "`dotnet build src/Nyrwvy.Semantics/Nyrwvy.Semantics.fsproj -c Release && dotnet fsi scripts/generate-recipe-signatures.fsx`."
        )

    /// Each table entry names an `ExpressionRecipe` case whose operands are exactly the entry's
    /// operands, in case field order and under the entry's name-form wire fields, so the generated
    /// reference signatures describe what the reader accepts and inference checks.
    [<Fact>]
    let ``every operation signature matches its recipe case and wire fields`` () =
        let declarations: AuthoringDeclarations = { Functions = []; Types = []; Effects = []; Capabilities = []; Provenance = [] }
        let check (entry: OperationSignatures.Signature) =
            let fields =
                entry.Operands
                |> List.mapi (fun index operand -> $$""","{{operand.Field}}":{"kind":"int64Literal","value":{{index}}}""")
                |> String.concat ""
            use document = JsonDocument.Parse($$"""{"kind":"{{entry.Kind}}"{{fields}}}""")
            try
                let recipe = WireSymbolic.recipeForNewFunction declarations "Probe" [] document.RootElement
                let case, values = FSharpValue.GetUnionFields(recipe, typeof<ExpressionRecipe>, true)
                let expected = entry.Operands |> List.mapi (fun index _ -> box (ExpressionRecipe.Int64Literal(int64 index)))
                if camelCase case.Name <> entry.Kind then Some $"{entry.Kind}: the name form builds {case.Name}"
                elif List.ofArray values <> expected then Some $"{entry.Kind}: the operands are not the case fields in order"
                else None
            with error ->
                Some $"{entry.Kind}: {error.Message}"
        let duplicates =
            OperationSignatures.table
            |> List.countBy (fun entry -> entry.Kind)
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (kind, _) -> $"{kind}: listed more than once")
        let problems = duplicates @ (OperationSignatures.table |> List.choose check)
        Assert.True(List.isEmpty problems, String.Join(Environment.NewLine, problems))

    /// `ExpressionRecipe` cases that build a node of a differently named `SemanticNodeKind` case.
    /// `Parameter` and the ID-only `ParameterSlot` both become `ParameterReference`, and the ID-only
    /// `SelfCall` persists as `DirectCall` to the function being authored.
    let private recipeOnlyCases = Map.ofList [ "Parameter", "ParameterReference"; "ParameterSlot", "ParameterReference"; "SelfCall", "DirectCall" ]

    /// `ExpressionRecipe` cases that build no node of their own: `OriginalTarget` reuses the old target node.
    let private nodelessCases = Set.ofList [ "OriginalTarget" ]

    /// The readers are probed with kind names drawn from the recipe cases, so a recipe case no
    /// reader accepts, or a node kind with no recipe case, is only caught by comparing the unions.
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
