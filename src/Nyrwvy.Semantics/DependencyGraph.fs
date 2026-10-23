namespace Nyrwvy.Core

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type DependencyCombinationError =
    | NoDependencies
    /// The same identity carries different content in two dependencies.
    | ConflictingDeclaration of kind: string * id: string
    | FingerprintFailure of SnapshotCodecError

/// Combines complete dependency snapshots into the one imported graph a workspace resolves against.
///
/// Import policy (Q-002): imported declarations keep their typed identities. A declaration reached
/// through more than one dependency, such as a shared transitive dependency, is kept once only when
/// its content is identical. Any other collision is rejected and never merged. Collisions with local
/// declarations are rejected by snapshot validation of the resolved graph.
[<RequireQualifiedAccess>]
module DependencyGraph =
    let private merge kind (identity: 'T -> string) (groups: 'T list list) =
        groups
        |> List.concat
        |> List.fold (fun state item ->
            state
            |> Result.bind (fun (seen: Map<string, 'T>, ordered: 'T list) ->
                let key = identity item
                match Map.tryFind key seen with
                // Structural equality on purpose: parameter and type-parameter order is meaningful, so
                // the same identity with reordered parameters is a conflict. Members keyed by ID are
                // compared in ID order (`combine` canonicalizes them first).
                | Some existing when existing = item -> Ok(seen, ordered)
                | Some _ -> Error(DependencyCombinationError.ConflictingDeclaration(kind, key))
                | None -> Ok(Map.add key item seen, item :: ordered))) (Ok(Map.empty, []))
        |> Result.map (snd >> List.rev)

    /// A deterministic identity for a combination: the same dependency contents, in any order,
    /// always yield the same combined snapshot identity.
    let private combinedId (dependencies: Snapshot list) =
        dependencies
        |> List.map SnapshotFingerprint.compute
        |> List.fold (fun state item ->
            match state, item with
            | Ok collected, Ok fingerprint -> Ok(SnapshotFingerprint.value fingerprint :: collected)
            | Error error, _ -> Error error
            | _, Error error -> Error(DependencyCombinationError.FingerprintFailure error)) (Ok [])
        |> Result.map (fun fingerprints ->
            let digest = fingerprints |> List.sort |> String.concat "\n" |> Encoding.UTF8.GetBytes |> SHA256.HashData
            match StableId.tryCreate<SnapshotTag> ("dependency-set-" + Guid(digest.AsSpan(0, 16)).ToString("D")) with
            | Ok id -> id
            | Error error -> invalidOp $"A derived snapshot identity is always valid: {error}.")

    let combine (dependencies: Snapshot list) : Result<Snapshot, DependencyCombinationError> =
        match dependencies with
        | [] -> Error DependencyCombinationError.NoDependencies
        | [ single ] -> Ok single
        | _ ->
            let canonical = dependencies |> List.map Snapshot.withCanonicalMemberOrder
            let field kind identity select = merge kind identity (canonical |> List.map select)
            match
                field "type" (fun (item: SemanticType) -> StableId.value item.Id) (fun snapshot -> snapshot.Types),
                field "effect" (fun (item: SemanticEffect) -> StableId.value item.Id) (fun snapshot -> snapshot.Effects),
                field "capability" (fun (item: SemanticCapability) -> StableId.value item.Id) (fun snapshot -> snapshot.Capabilities),
                field "function" (fun (item: SemanticFunction) -> StableId.value item.Id) (fun snapshot -> snapshot.Functions),
                field "obligation" (fun (item: MissingArgumentObligation) -> StableId.value item.Id) (fun snapshot -> snapshot.Obligations)
            with
            | Ok types, Ok effects, Ok capabilities, Ok functions, Ok obligations ->
                combinedId dependencies
                |> Result.map (fun id ->
                    { Id = id
                      ParentId = None
                      Types = types
                      Effects = effects
                      Capabilities = capabilities
                      Functions = functions
                      Obligations = obligations })
            | Error error, _, _, _, _
            | _, Error error, _, _, _
            | _, _, Error error, _, _
            | _, _, _, Error error, _
            | _, _, _, _, Error error -> Error error
