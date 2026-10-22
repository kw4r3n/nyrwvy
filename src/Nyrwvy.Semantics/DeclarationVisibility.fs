namespace Nyrwvy.Core

/// One private declaration of a dependency that a local declaration refers to.
type HiddenDependencyReference =
    { /// `type`, `function`, `effect` or `capability`.
      Kind: string
      Id: string
      DisplayName: string
      /// The local declarations that refer to it, in `SemanticQuery.references` order.
      Owners: ReferenceOwner list }

/// Declaration visibility across a dependency boundary. Inside its own snapshot every
/// declaration is usable; a snapshot that depends on it may use only its exported declarations.
/// The resolved graph still holds every dependency declaration (an exported function may call a
/// private one), so hiding is a policy of the authoring surface, not of validation or execution.
/// See docs/decisions/declaration-visibility.md.
[<RequireQualifiedAccess>]
module DeclarationVisibility =
    /// The private declarations of `dependency` that declarations outside it (the local ones of
    /// `resolved`, the graph that combines the dependency with the dependent) refer to, in
    /// signatures, field and operation types, required effects and bodies. Empty when the
    /// dependent uses only exported declarations.
    let hiddenReferences (dependency: Snapshot) (resolved: Snapshot): HiddenDependencyReference list =
        let importedFunctions = dependency.Functions |> List.map (fun item -> item.Id) |> Set.ofList
        let importedTypes = dependency.Types |> List.map (fun item -> item.Id) |> Set.ofList
        let importedCapabilities = dependency.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList
        let isLocal (owner: ReferenceOwner) =
            match owner with
            | ReferenceOwner.Function id -> not (Set.contains id importedFunctions)
            | ReferenceOwner.Type id -> not (Set.contains id importedTypes)
            | ReferenceOwner.Capability id -> not (Set.contains id importedCapabilities)
        let localOwners subject =
            match SemanticQuery.references resolved subject with
            | Ok references -> references |> List.map (fun reference -> reference.Owner) |> List.filter isLocal |> List.distinct
            | Error _ -> []
        let hidden kind id displayName subject =
            match localOwners subject with
            | [] -> None
            | owners -> Some { Kind = kind; Id = id; DisplayName = displayName; Owners = owners }
        [ for item in dependency.Types do
              if not item.Exported then
                  yield! hidden "type" (StableId.value item.Id) item.DisplayName (ReferenceSubject.Type item.Id) |> Option.toList
          for item in dependency.Functions do
              if not item.Exported then
                  yield! hidden "function" (StableId.value item.Id) item.DisplayName (ReferenceSubject.Function item.Id) |> Option.toList
          for item in dependency.Effects do
              if not item.Exported then
                  yield! hidden "effect" (StableId.value item.Id) item.DisplayName (ReferenceSubject.Effect item.Id) |> Option.toList
          for item in dependency.Capabilities do
              if not item.Exported then
                  yield! hidden "capability" (StableId.value item.Id) item.DisplayName (ReferenceSubject.Capability item.Id) |> Option.toList ]

    /// The diagnostic code of a reference from a dependent to a private dependency declaration.
    let notExportedCode = "ImportedDeclarationNotExported"

    /// One sentence naming the hidden declarations a dependent refers to.
    let describe (hidden: HiddenDependencyReference list) =
        let names = hidden |> List.map (fun item -> $"{item.Kind} '{item.DisplayName}' ({item.Id})") |> String.concat ", "
        $"The dependency does not export {names}; a dependent may refer only to exported declarations."
