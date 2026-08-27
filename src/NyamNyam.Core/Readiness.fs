namespace NyamNyam.Core

[<RequireQualifiedAccess>]
type SemanticReadiness =
    | Complete
    | Incomplete of ObligationId list

[<RequireQualifiedAccess>]
module SnapshotReadiness =
    let evaluate (snapshot: Snapshot): Result<SemanticReadiness, ValidationIssue list> =
        match SnapshotValidator.validate snapshot with
        | issue :: rest -> Error(issue :: rest)
        | [] ->
            let openObligations =
                snapshot.Obligations
                |> List.choose (fun obligation ->
                    match obligation.Status with
                    | ObligationStatus.Open -> Some obligation.Id
                    | ObligationStatus.Resolved _ -> None)
                |> List.sortBy StableId.value

            match openObligations with
            | [] -> Ok SemanticReadiness.Complete
            | ids -> Ok(SemanticReadiness.Incomplete ids)
