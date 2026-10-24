namespace Nyrwvy.Core

open System
open System.Text

[<RequireQualifiedAccess>]
type ViewerSourceEncoding =
    | Json
    /// The record form (`.nyrec`, docs/decisions/snapshot-record-format.md).
    | Record

[<RequireQualifiedAccess>]
type ViewerArtifactKind =
    | FunctionFocus
    | ChangeReview

type ViewerArtifact =
    { Kind: ViewerArtifactKind
      MediaType: string
      SuggestedFileName: string
      Content: string }

[<RequireQualifiedAccess>]
type ViewerExportError =
    | InvalidFunctionId of StableIdError
    | UnknownFunctionName of displayName: string
    | AmbiguousFunctionName of displayName: string * candidates: FunctionId list
    | JsonDecodeFailed of SnapshotCodecError
    | RecordDecodeFailed of RecordCodecError
    | FocusQueryFailed of SemanticQueryError
    | ChangeReviewProjectionFailed of ChangeReviewQueryError

[<RequireQualifiedAccess>]
module ViewerArtifact =
    let private utf8Hex (value: string) =
        Encoding.UTF8.GetBytes(value: string)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private focusFileName functionId =
        "nyrwvy-focus-function-"
        + utf8Hex (StableId.value functionId)
        + ".html"

    let private reviewFileName transactionId =
        "nyrwvy-change-review-transaction-"
        + utf8Hex (StableId.value transactionId)
        + ".html"

    let ofFocus (focus: FunctionFocusView): ViewerArtifact =
        let model = ViewerProjection.focus focus
        { Kind = ViewerArtifactKind.FunctionFocus
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = focusFileName focus.Function.Id
          Content = ViewerHtml.renderFocusDocument model }

    let ofChangeReviewWithBaseState baseIsCommitted (review: ChangeReviewView): ViewerArtifact =
        let model = ViewerProjection.changeReview review
        { Kind = ViewerArtifactKind.ChangeReview
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = reviewFileName review.TransactionId
          Content = ViewerHtml.renderChangeReviewDocumentWithBaseState baseIsCommitted model }

    let ofChangeReview review = ofChangeReviewWithBaseState true review

    /// A pending structural proposal's Change Review; named by transaction ID like `ofChangeReview`.
    let ofProposalReview (model: ViewerProposalReviewModel): ViewerArtifact =
        { Kind = ViewerArtifactKind.ChangeReview
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = reviewFileName model.TransactionId
          Content = ViewerHtml.renderProposalReviewDocument model }

[<RequireQualifiedAccess>]
module ViewerExport =
    let private decodeSnapshot
        (encoding: ViewerSourceEncoding)
        (source: string)
        : Result<Snapshot, ViewerExportError> =
        match encoding with
        | ViewerSourceEncoding.Json ->
            SnapshotCodec.deserialize source
            |> Result.mapError ViewerExportError.JsonDecodeFailed
        | ViewerSourceEncoding.Record ->
            RecordCodec.deserialize source
            |> Result.mapError ViewerExportError.RecordDecodeFailed

    let focusFromSnapshot
        (snapshot: Snapshot)
        (functionId: FunctionId)
        : Result<ViewerArtifact, ViewerExportError> =
        SemanticQuery.focusFunction snapshot functionId
        |> Result.mapError ViewerExportError.FocusQueryFailed
        |> Result.map ViewerArtifact.ofFocus

    let focusFromSource
        (encoding: ViewerSourceEncoding)
        (source: string)
        (functionIdText: string)
        : Result<ViewerArtifact, ViewerExportError> =
        match StableId.tryCreate<FunctionTag> functionIdText with
        | Error error -> Error(ViewerExportError.InvalidFunctionId error)
        | Ok functionId ->
            decodeSnapshot encoding source
            |> Result.bind (fun snapshot -> focusFromSnapshot snapshot functionId)

    /// Resolves a function by exact display name. Names carry no identity, so an
    /// unknown or ambiguous name fails rather than picking one; the artifact itself
    /// stays stable-ID named.
    let resolveFunctionName
        (snapshot: Snapshot)
        (displayName: string)
        : Result<FunctionId, ViewerExportError> =
        match snapshot.Functions |> List.filter (fun functionEntity -> functionEntity.DisplayName = displayName) with
        | [ functionEntity ] -> Ok functionEntity.Id
        | [] -> Error(ViewerExportError.UnknownFunctionName displayName)
        | matches ->
            Error(
                ViewerExportError.AmbiguousFunctionName(
                    displayName,
                    matches |> List.map (fun functionEntity -> functionEntity.Id) |> List.sortBy StableId.value
                )
            )

    let focusFromSourceByName
        (encoding: ViewerSourceEncoding)
        (source: string)
        (displayName: string)
        : Result<ViewerArtifact, ViewerExportError> =
        decodeSnapshot encoding source
        |> Result.bind (fun snapshot ->
            resolveFunctionName snapshot displayName
            |> Result.bind (focusFromSnapshot snapshot))

    let changeReview
        (simulation: TransactionSimulation)
        : Result<ViewerArtifact, ViewerExportError> =
        ChangeReviewQuery.project simulation
        |> Result.mapError ViewerExportError.ChangeReviewProjectionFailed
        |> Result.map ViewerArtifact.ofChangeReview
