namespace NyamNyam.Core

open System
open System.Text

[<RequireQualifiedAccess>]
type ViewerSourceEncoding =
    | Json
    | Nyason

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
    | JsonDecodeFailed of SnapshotCodecError
    | NyasonDecodeFailed of NyasonCodecError
    | FocusQueryFailed of SemanticQueryError
    | ChangeReviewProjectionFailed of ChangeReviewQueryError

[<RequireQualifiedAccess>]
module ViewerArtifact =
    let private utf8Hex (value: string) =
        Encoding.UTF8.GetBytes(value: string)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private focusFileName functionId =
        "nyamnyam-focus-function-"
        + utf8Hex (StableId.value functionId)
        + ".html"

    let private reviewFileName transactionId =
        "nyamnyam-change-review-transaction-"
        + utf8Hex (StableId.value transactionId)
        + ".html"

    let ofFocus (focus: FunctionFocusView): ViewerArtifact =
        let model = ViewerProjection.focus focus
        { Kind = ViewerArtifactKind.FunctionFocus
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = focusFileName focus.Function.Id
          Content = ViewerHtml.renderFocusDocument model }

    let ofChangeReview (review: ChangeReviewView): ViewerArtifact =
        let model = ViewerProjection.changeReview review
        { Kind = ViewerArtifactKind.ChangeReview
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = reviewFileName review.TransactionId
          Content = ViewerHtml.renderChangeReviewDocument model }

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
        | ViewerSourceEncoding.Nyason ->
            NyasonCodec.deserialize source
            |> Result.mapError ViewerExportError.NyasonDecodeFailed

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

    let changeReview
        (simulation: TransactionSimulation)
        : Result<ViewerArtifact, ViewerExportError> =
        ChangeReviewQuery.project simulation
        |> Result.mapError ViewerExportError.ChangeReviewProjectionFailed
        |> Result.map ViewerArtifact.ofChangeReview
