namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core

/// Writes human-facing viewer HTML for the committed state or a pending proposal. The file
/// is a projection only: it is never read back and grants no publication authority.
[<RequireQualifiedAccess>]
module ViewerFile =
    /// Default directory, beside the snapshot, for views written without an explicit output path.
    let defaultDirectoryName = "nyrwvy-views"

    let private isInside (directory: string) (path: string) =
        let relative = Path.GetRelativePath(directory, path)
        not (Path.IsPathRooted relative) && relative <> ".." && not (relative.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal))

    /// A requested output resolves against the snapshot's directory and must stay inside it, so an
    /// auto-approved agent can only write pages next to the graph it was given, never over other files.
    let private outputPath (snapshotPath: string) (requested: string option) (artifact: ViewerArtifact) =
        let directory = Path.GetDirectoryName(Path.GetFullPath snapshotPath)
        match requested with
        | None -> Ok(Path.Combine(directory, defaultDirectoryName, artifact.SuggestedFileName))
        | Some path when String.IsNullOrWhiteSpace path -> Error "output must not be blank."
        | Some path when not (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ->
            Error "output must name an .html file."
        | Some path ->
            let resolved = Path.GetFullPath(Path.Combine(directory, path))
            if isInside directory resolved then Ok resolved
            else Error $"output must stay inside the snapshot's directory {directory}; {path} resolves to {resolved}."

    let private write (snapshotPath: string) (requested: string option) (artifact: ViewerArtifact) =
        outputPath snapshotPath requested artifact
        |> Result.bind (fun path ->
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, artifact.Content, UTF8Encoding(false))
                Ok(path, artifact)
            with
            | :? IOException as error -> Error $"I/O error: {error.Message}"
            | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}")

    let writeFocus
        (snapshotPath: string)
        (requested: string option)
        (focus: FunctionFocusView)
        : Result<string * ViewerArtifact, string> =
        write snapshotPath requested (ViewerArtifact.ofFocus focus)

    let private engineReview (proposal: AuthoringProposal) =
        let options = JsonSerializerOptions(MaxDepth = WireJson.responseMaxJsonDepth, WriteIndented = true)
        JsonSerializer.Serialize(WireEncode.proposal proposal, options)

    let private proposalKind (review: AuthoringProposalReview) =
        match review with
        | AuthoringProposalReview.Operations _ -> "operations"
        | AuthoringProposalReview.ReplaceBody _ -> "replaceBody"
        | AuthoringProposalReview.ReplaceTarget _ -> "replaceTarget"
        | AuthoringProposalReview.CreateFunction _ -> "createFunction"
        | AuthoringProposalReview.ParameterTypeMigration _ -> "parameterTypeMigration"
        | AuthoringProposalReview.ResultTypeMigration _ -> "resultTypeMigration"
        | AuthoringProposalReview.ProductFieldTypeMigration _ -> "productFieldTypeMigration"
        | AuthoringProposalReview.CreateType _ -> "createType"
        | AuthoringProposalReview.CreateTypeGroup _ -> "createTypes"
        | AuthoringProposalReview.FieldlessUnionVariantEvolution _ -> "fieldlessUnionVariantEvolution"
        | AuthoringProposalReview.CreateEffect _ -> "createEffect"
        | AuthoringProposalReview.CreateCapability _ -> "createCapability"

    let private readiness (review: AuthoringProposalReview) =
        match review with
        | AuthoringProposalReview.Operations item -> item.Readiness
        | AuthoringProposalReview.ReplaceBody item -> item.Readiness
        | AuthoringProposalReview.ReplaceTarget item -> item.Readiness
        | AuthoringProposalReview.CreateFunction item -> item.Readiness
        | AuthoringProposalReview.ParameterTypeMigration item -> item.Readiness
        | AuthoringProposalReview.ResultTypeMigration item -> item.Readiness
        | AuthoringProposalReview.ProductFieldTypeMigration item -> item.Readiness
        | AuthoringProposalReview.CreateType item -> item.Readiness
        | AuthoringProposalReview.CreateTypeGroup item -> item.Readiness
        | AuthoringProposalReview.FieldlessUnionVariantEvolution item -> item.Readiness
        | AuthoringProposalReview.CreateEffect item -> item.Readiness
        | AuthoringProposalReview.CreateCapability item -> item.Readiness

    let private proposalReview
        (view: AuthoringPendingProposalView)
        (proposed: Snapshot)
        (committedFocus: FunctionId -> FunctionFocusView option)
        : Result<ViewerArtifact, string> =
        let rec functions acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | functionId :: rest ->
                match SemanticQuery.focusFunction proposed functionId with
                | Error error -> Error $"Proposed focus of function:{StableId.value functionId} failed: %A{error}"
                | Ok after ->
                    let before = if view.BaseIsCommitted then committedFocus functionId else None
                    let item: ViewerProposalFunction =
                        { CommittedVersion = before |> Option.map ViewerProjection.focus
                          ProposedVersion = ViewerProjection.focus after }
                    functions (item :: acc) rest
        functions [] view.FunctionIds
        |> Result.map (fun functions ->
            let review = view.Proposal.Review
            ViewerArtifact.ofProposalReview
                { TransactionId = view.TransactionId
                  ProposalKind = proposalKind review
                  BaseSnapshotId = view.BaseSnapshotId
                  ProposedSnapshotId = proposed.Id
                  BaseIsCommitted = view.BaseIsCommitted
                  Readiness = readiness review
                  ChangedFunctions = functions
                  DeclaredTypes = view.TypeIds |> List.choose (fun id -> proposed.Types |> List.tryFind (fun item -> item.Id = id))
                  DeclaredEffects =
                    match review with
                    | AuthoringProposalReview.CreateEffect item -> [ item.CreatedEffect ]
                    | _ -> []
                  DeclaredCapabilities =
                    match review with
                    | AuthoringProposalReview.CreateCapability item -> [ item.CreatedCapability ]
                    | _ -> []
                  EngineReview = engineReview view.Proposal })

    /// Writes a pending proposal's Change Review. An operations proposal uses the Change Review
    /// projection its review already carries; every other kind shows committed and proposed
    /// focus of the functions it creates or rewrites beside the engine's review.
    let writeProposalReview
        (snapshotPath: string)
        (requested: string option)
        (view: AuthoringPendingProposalView)
        (committedFocus: FunctionId -> FunctionFocusView option)
        : Result<string * ViewerArtifact, string> =
        let artifact =
            match view.Proposal.Review, view.ProposedSnapshot with
            | AuthoringProposalReview.Operations item, _ -> Ok(ViewerArtifact.ofChangeReview item.ResultingChange)
            | _, Some proposed -> proposalReview view proposed committedFocus
            | _, None -> Error "The proposal has no proposed snapshot to render."
        artifact |> Result.bind (write snapshotPath requested)
