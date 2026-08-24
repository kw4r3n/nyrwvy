namespace Nayamnyam.Core

open System

[<Struct>]
type ValidationIssue =
    { Code: string
      Message: string }

[<RequireQualifiedAccess>]
module SnapshotValidator =
    let validate (snapshot: Snapshot): ValidationIssue list =
        let issues = ResizeArray<ValidationIssue>()
        let add code message = issues.Add({ Code = code; Message = message })

        if snapshot.ParentId = Some snapshot.Id then
            add "SnapshotParentSelfReference" "A snapshot cannot name itself as its parent."

        snapshot.Types
        |> Seq.groupBy (fun item -> item.Id)
        |> Seq.filter (fun (_, items) -> Seq.length items > 1)
        |> Seq.iter (fun (id, _) ->
            add "DuplicateTypeId" $"Type ID '{StableId.value id}' occurs more than once.")

        snapshot.Functions
        |> Seq.groupBy (fun item -> item.Id)
        |> Seq.filter (fun (_, items) -> Seq.length items > 1)
        |> Seq.iter (fun (id, _) ->
            add "DuplicateFunctionId" $"Function ID '{StableId.value id}' occurs more than once.")

        snapshot.Types
        |> Seq.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> Seq.iter (fun item ->
            add "BlankTypeDisplayName" $"Type '{StableId.value item.Id}' has a blank display name.")

        snapshot.Functions
        |> Seq.filter (fun item -> String.IsNullOrWhiteSpace item.DisplayName)
        |> Seq.iter (fun item ->
            add "BlankFunctionDisplayName" $"Function '{StableId.value item.Id}' has a blank display name.")

        issues
        |> Seq.sortBy (fun issue -> issue.Code, issue.Message)
        |> List.ofSeq
