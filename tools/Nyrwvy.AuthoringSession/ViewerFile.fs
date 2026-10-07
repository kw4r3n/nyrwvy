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
        | None -> Ok(directory, Path.Combine(directory, defaultDirectoryName, artifact.SuggestedFileName))
        | Some path when String.IsNullOrWhiteSpace path -> Error "output must not be blank."
        | Some path when not (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ->
            Error "output must name an .html file."
        | Some path ->
            let resolved = Path.GetFullPath(Path.Combine(directory, path))
            if isInside directory resolved then Ok(directory, resolved)
            else Error $"output must stay inside the snapshot's directory {directory}; {path} resolves to {resolved}."

    /// The first existing component below `directory` on the way to `path` (the file included) that is
    /// a symbolic link or reparse point. The confinement above compares strings, so a link would carry
    /// the write outside the directory while the string stays inside it.
    let private linkedComponent (directory: string) (path: string) =
        Path.GetRelativePath(directory, path).Split(
            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.scan (fun (current: string) (part: string) -> Path.Combine(current, part)) directory
        |> Array.tail
        |> Array.tryFind (fun (item: string) -> not (isNull (FileInfo(item).LinkTarget)))

    let private write (snapshotPath: string) (requested: string option) (artifact: ViewerArtifact) =
        outputPath snapshotPath requested artifact
        |> Result.bind (fun (directory, path) ->
            match linkedComponent directory path with
            | Some link -> Error $"output must not pass through a symbolic link or reparse point; {link} is one."
            | None when ConfinedDirectory.supported ->
                let components = Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
                let parents, name = List.take (components.Length - 1) components, List.last components
                ConfinedDirectory.openBelow directory parents
                |> Result.bind (fun handle ->
                    use handle = handle
                    ConfinedDirectory.writeFile handle name (UTF8Encoding(false).GetBytes artifact.Content))
                |> Result.map (fun () -> path, artifact)
            | None ->
                // Without directory handles a parent swapped for a link between the check above and
                // this write is not caught.
                try
                    let parent = Path.GetDirectoryName path
                    Directory.CreateDirectory parent |> ignore
                    // Written beside the target and renamed over it: a rename replaces a link created at the
                    // final name after the check above instead of writing through it.
                    let temporary = Path.Combine(parent, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")
                    try
                        File.WriteAllText(temporary, artifact.Content, UTF8Encoding(false))
                        File.Move(temporary, path, true)
                    finally
                        if File.Exists temporary then File.Delete temporary
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
        | AuthoringProposalReview.DependencyUpgrade _ -> "dependencyUpgrade"
        | AuthoringProposalReview.Operations _ -> "operations"
        | AuthoringProposalReview.ReplaceBody _ -> "replaceBody"
        | AuthoringProposalReview.ReplaceTarget _ -> "replaceTarget"
        | AuthoringProposalReview.CreateFunction _ -> "createFunction"
        | AuthoringProposalReview.ParameterTypeMigration _ -> "parameterTypeMigration"
        | AuthoringProposalReview.ResultTypeMigration _ -> "resultTypeMigration"
        | AuthoringProposalReview.CapabilityOperationSignatureMigration _ -> "capabilityOperationSignatureMigration"
        | AuthoringProposalReview.ProductFieldTypeMigration _ -> "productFieldTypeMigration"
        | AuthoringProposalReview.CreateType _ -> "createType"
        | AuthoringProposalReview.CreateTypeGroup _ -> "createTypes"
        | AuthoringProposalReview.FieldlessUnionVariantEvolution _ -> "fieldlessUnionVariantEvolution"
        | AuthoringProposalReview.CreateEffect _ -> "createEffect"
        | AuthoringProposalReview.CreateCapability _ -> "createCapability"

    let private readiness (review: AuthoringProposalReview) =
        match review with
        | AuthoringProposalReview.DependencyUpgrade item -> item.Repair.Readiness
        | AuthoringProposalReview.Operations item -> item.Readiness
        | AuthoringProposalReview.ReplaceBody item -> item.Readiness
        | AuthoringProposalReview.ReplaceTarget item -> item.Readiness
        | AuthoringProposalReview.CreateFunction item -> item.Readiness
        | AuthoringProposalReview.ParameterTypeMigration item -> item.Readiness
        | AuthoringProposalReview.ResultTypeMigration item -> item.Readiness
        | AuthoringProposalReview.CapabilityOperationSignatureMigration item -> item.Readiness
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

    /// One review artifact contains the structural proposal and the computed behavioral evidence.
    let writeVerificationReview snapshotPath requested (view: AuthoringPendingProposalView) committedFocus (evidence: ChangeVerificationEvidence) evidenceJson =
        let artifact =
            match view.ProposedSnapshot with
            | Some proposed -> proposalReview view proposed committedFocus
            | None -> Error "Candidate verification is unsupported for this proposal."
        artifact |> Result.bind (fun artifact ->
            let evidenceSection = "<section><h2>Change verification</h2><p>Explicit cases only. Tested agreement is not proof. This saved report is historical evidence for the fingerprints below; rerun verifyChange after editing.</p><pre>" + System.Net.WebUtility.HtmlEncode(evidenceJson) + "</pre></section>"
            let counts = evidence.Observations |> List.countBy (fun item -> item.Status) |> Map.ofList
            let count status = Map.tryFind status counts |> Option.defaultValue 0
            let counterexamples, unknown = count "counterexample", count "unknown"
            let summary =
                if counterexamples > 0 then "counterexample"
                elif unknown > 0 then "unknown"
                else "tested agreement / explicit intended results; no proof"
            let agreement, intended = count "tested-agreement", count "intended-change-tested"
            let entries = evidence.Observations |> List.map (fun item -> item.Case.FunctionId) |> Set.ofList
            let affected = evidence.AffectedFunctionIds |> Set.ofList
            let explicitAffected = Set.intersect entries affected |> Set.count
            let coverage = $"Impact: {Set.count affected} affected function(s). Explicit case entry points: {Set.count entries}; {explicitAffected} affected function(s) directly targeted, {Set.count affected - explicitAffected} without a direct case. This counts requested entry points, not dynamic coverage or proof that callers were exercised."
            let banner = $"<p class=\"status\">Behavioral verification: {summary}</p><p>{counterexamples} counterexample(s), {unknown} unknown, {agreement} tested agreement, {intended} intended result(s). Explicit cases only; this historical report does not gate commit.</p><p>" + coverage + "</p>"
            let content =
                artifact.Content
                    .Replace("<p class=\"status\">Readiness:", "<p class=\"status\">Structural readiness:")
                    .Replace("</header>", banner + "</header>")
                    .Replace("</body>", evidenceSection + "</body>")
            write snapshotPath requested { artifact with Content = content })
