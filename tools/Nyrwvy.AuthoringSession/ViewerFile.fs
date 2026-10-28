namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Text
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
    /// auto-approved agent can only write files next to the graph it was given; an existing file
    /// with the required extension there is replaced.
    let private outputPath (snapshotPath: string) (requested: string option) (extension: string) (defaultName: string) =
        let directory = Path.GetDirectoryName(Path.GetFullPath snapshotPath)
        match requested with
        | None -> Ok(directory, Path.Combine(directory, defaultDirectoryName, defaultName))
        | Some path when String.IsNullOrWhiteSpace path -> Error "output must not be blank."
        | Some path when not (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ->
            Error $"output must name a {extension} file."
        | Some path ->
            let resolved = Path.GetFullPath(Path.Combine(directory, path))
            let snapshot = Path.GetFullPath snapshotPath
            if not (isInside directory resolved) then
                Error $"output must stay inside the snapshot's directory {directory}; {path} resolves to {resolved}."
            elif List.contains resolved [ snapshot; snapshot + ".lock"; snapshot + ".dependency.json"; snapshot + ".backends.json" ] then
                Error $"output must not replace the snapshot, its lock or its dependency or backends sidecar; {path} resolves to {resolved}."
            else Ok(directory, resolved)

    /// The first existing component below `directory` on the way to `path` (the file included) that is
    /// a symbolic link or reparse point. The confinement above compares strings, so a link would carry
    /// the write outside the directory while the string stays inside it.
    let private linkedComponent (directory: string) (path: string) =
        Path.GetRelativePath(directory, path).Split(
            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.scan (fun (current: string) (part: string) -> Path.Combine(current, part)) directory
        |> Array.tail
        |> Array.tryFind (fun (item: string) -> not (isNull (FileInfo(item).LinkTarget)))

    /// True when `path` does not exist or starts as `content` does up to the end of its `marker`, so an
    /// existing file is replaced only when an earlier write of this tool produced it.
    let private replaceable (path: string) (marker: string) (content: string) =
        if not (File.Exists path) then true
        else
            let index = content.IndexOf(marker, StringComparison.Ordinal)
            let header = Encoding.UTF8.GetBytes(content.Substring(0, max 0 index + marker.Length))
            try
                use stream = File.OpenRead path
                let buffer = Array.zeroCreate<byte> header.Length
                stream.ReadAtLeast(buffer, buffer.Length, false) = buffer.Length && buffer = header
            with
            | :? IOException
            | :? UnauthorizedAccessException -> false

    let private target (snapshotPath: string) (requested: string option) (extension: string) (defaultName: string) (marker: string) (content: string) =
        outputPath snapshotPath requested extension defaultName
        |> Result.bind (fun (directory, path) ->
            match linkedComponent directory path with
            | Some link -> Error $"output must not pass through a symbolic link or reparse point; {link} is one."
            | None when not (replaceable path marker content) ->
                Error $"output must not replace a file this tool did not write; {path} exists without the generated-file marker."
            | None -> Ok(directory, path))

    let private write (snapshotPath: string) (requested: string option) (extension: string) (defaultName: string) (marker: string) (content: string) =
        target snapshotPath requested extension defaultName marker content
        |> Result.bind (fun (directory, path) ->
            if ConfinedDirectory.supported then
                let components = Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
                let parents, name = List.take (components.Length - 1) components, List.last components
                ConfinedDirectory.openBelow directory parents
                |> Result.bind (fun handle ->
                    use handle = handle
                    ConfinedDirectory.writeFile handle name (UTF8Encoding(false).GetBytes content))
                |> Result.map (fun () -> path)
            else
                // Without directory handles a parent swapped for a link between the check above and
                // this write is not caught.
                try
                    let parent = Path.GetDirectoryName path
                    Directory.CreateDirectory parent |> ignore
                    // Written beside the target and renamed over it: a rename replaces a link created at the
                    // final name after the check above instead of writing through it.
                    let temporary = Path.Combine(parent, $".nyrwvy-{Guid.NewGuid():N}.tmp")
                    try
                        File.WriteAllText(temporary, content, UTF8Encoding(false))
                        File.Move(temporary, path, true)
                    finally
                        if File.Exists temporary then File.Delete temporary
                    Ok path
                with
                | :? IOException as error -> Error $"I/O error: {error.Message}"
                | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}")

    let private writeArtifact snapshotPath requested (artifact: ViewerArtifact) =
        write snapshotPath requested ".html" artifact.SuggestedFileName ViewerHtml.generatorMeta artifact.Content
        |> Result.map (fun path -> path, artifact)

    /// Writes emitCSharp source to an explicit .cs path, confined like a view. The emitter's first line
    /// marks generated source.
    let writeCSharp (snapshotPath: string) (requested: string) (source: string) : Result<string, string> =
        write snapshotPath (Some requested) ".cs" "" "// Generated by Nyrwvy." source

    /// Runs writeCSharp's checks without writing, so a request naming two files can refuse both before either is written.
    let checkCSharp (snapshotPath: string) (requested: string) (source: string) : Result<string, string> =
        target snapshotPath (Some requested) ".cs" "" "// Generated by Nyrwvy." source |> Result.map snd

    let writeFocus
        (snapshotPath: string)
        (requested: string option)
        (focus: FunctionFocusView)
        : Result<string * ViewerArtifact, string> =
        writeArtifact snapshotPath requested (ViewerArtifact.ofFocus focus)

    let private engineReview (proposal: AuthoringProposal) =
        // Keep every review detail in the durable page, without recursively printing nested
        // graphs again inside each node's debug string.
        WireJson.serialize (WireEncode.proposalWith "full" WireGraph.functionEntity WireGraph.focus proposal)

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
        | AuthoringProposalReview.UnionVariantEvolution _ -> "unionVariantEvolution"
        | AuthoringProposalReview.CreateEffect _ -> "createEffect"
        | AuthoringProposalReview.CreateCapability _ -> "createCapability"
        | AuthoringProposalReview.Batch _ -> "batch"

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
        | AuthoringProposalReview.UnionVariantEvolution item -> item.Readiness
        | AuthoringProposalReview.CreateEffect item -> item.Readiness
        | AuthoringProposalReview.CreateCapability item -> item.Readiness
        | AuthoringProposalReview.Batch item -> item.Readiness

    let private proposalReview
        verification
        (view: AuthoringPendingProposalView)
        (proposed: Snapshot)
        (committedFocus: FunctionId -> FunctionFocusView option)
        : Result<ViewerArtifact, string> =
        let rec functions acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | functionId :: rest ->
                match SemanticQuery.focusFunction proposed functionId with
                | Error error -> Error $"Proposed focus of function:{StableId.value functionId} failed: {(Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(error, typeof<SemanticQueryError>) |> fst).Name}"
                | Ok after ->
                    let before = if view.BaseIsCommitted then committedFocus functionId else None
                    let item: ViewerProposalFunction =
                        { CommittedVersion = before |> Option.map ViewerProjection.focus
                          ProposedVersion = ViewerProjection.focus after }
                    functions (item :: acc) rest
        functions [] view.FunctionIds
        |> Result.map (fun functions ->
            let review = view.Proposal.Review
            ViewerArtifact.ofProposalReviewWith verification
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
            | AuthoringProposalReview.Operations item, _ -> Ok(ViewerArtifact.ofChangeReviewWithBaseState view.BaseIsCommitted item.ResultingChange)
            | _, Some proposed -> proposalReview None view proposed committedFocus
            | _, None -> Error "The proposal has no proposed snapshot to render."
        artifact |> Result.bind (writeArtifact snapshotPath requested)

    /// One review artifact contains the structural proposal and the computed behavioral evidence
    /// (`evidence`, the verifyChange reply's evidence object).
    let writeVerificationReview snapshotPath requested (view: AuthoringPendingProposalView) committedFocus (evidence: obj) =
        match view.ProposedSnapshot with
        | Some proposed -> proposalReview (Some evidence) view proposed committedFocus
        | None -> Error "Candidate verification is unsupported for this proposal."
        |> Result.bind (writeArtifact snapshotPath requested)
