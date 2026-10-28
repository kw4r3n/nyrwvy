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

/// The viewer data: the JSON a session host embeds in the viewer page (`ViewerHtml.page`) for one
/// view, a direct encoding of the viewer model. Types, declarations, readiness and the semantic diff
/// use their wire notation; IDs are raw stable ID values and union cases their fixed names.
[<RequireQualifiedAccess>]
module ViewerData =
    open Nyrwvy.Tools.AuthoringSession
    open Nyrwvy.Tools.AuthoringSession.WireJson
    open Nyrwvy.Tools.AuthoringSession.WireEncodeCommon

    let private id (value: StableId<'Kind>) = box (StableId.value value)
    let private ids values = values |> List.map id |> arrayValue
    let private optional value encode =
        match value with
        | Some item -> encode item
        | None -> null
    let private caseName<'T> (value: 'T) = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value, typeof<'T>) |> fst).Name

    let private typeParameters (values: (TypeParameterId * string) list) =
        values |> List.map (fun (typeParameterId, name) -> objectValue [ "typeParameterId", id typeParameterId; "displayName", box name ]) |> arrayValue

    let private parameters (values: SemanticParameter list) =
        values |> List.map (fun item -> objectValue [ "id", id item.Id; "displayName", box item.DisplayName; "type", typeRef item.Type ]) |> arrayValue

    let private role (value: ViewerNodeRole) =
        match value with
        | ViewerNodeRole.StringLiteral text -> objectValue [ "kind", box "stringLiteral"; "value", runString text ]
        | ViewerNodeRole.BoolLiteral flag -> objectValue [ "kind", box "boolLiteral"; "value", box flag ]
        | ViewerNodeRole.Int64Literal number -> objectValue [ "kind", box "int64Literal"; "value", box (number.ToString(Globalization.CultureInfo.InvariantCulture)) ]
        | ViewerNodeRole.Float64Literal bits -> objectValue [ "kind", box "float64Literal"; "bits", box (Float64Bits.toHex bits) ]
        | ViewerNodeRole.TaskWait mode ->
            objectValue [ "kind", box "taskWait"; "mode", box (match mode with TaskWaitMode.Any -> "any" | TaskWaitMode.All -> "all") ]
        | ViewerNodeRole.CapabilityConstruct(capabilityId, implementations) ->
            objectValue
                [ "kind", box "capabilityConstruct"
                  "capabilityId", id capabilityId
                  "implementations",
                    implementations |> List.map (fun (operationId, functionId) -> objectValue [ "operationId", id operationId; "functionId", id functionId ]) |> arrayValue ]
        | ViewerNodeRole.Operation kind -> objectValue [ "kind", box "operation"; "operation", box kind ]

    let private targetId (target: ViewerTarget) =
        match target with
        | ViewerTarget.Node value -> id value
        | ViewerTarget.Function value -> id value
        | ViewerTarget.Parameter value -> id value
        | ViewerTarget.PatternBinding value -> id value
        | ViewerTarget.CapabilityOperation value -> id value
        | ViewerTarget.Type value -> id value
        | ViewerTarget.Field value -> id value
        | ViewerTarget.Variant value -> id value

    let rec private graph (value: ViewerGraph): obj =
        let node (item: ViewerNode) =
            objectValue
                [ "id", id item.Id
                  "type", optional item.Type typeRef
                  "role", role item.Role
                  // A DependsOn relation repeats a value dependency, so only the other relations are written.
                  "relations",
                    item.Relations
                    |> List.filter (fun relation -> relation.Kind <> ViewerRelationKind.DependsOn)
                    |> List.map (fun relation -> objectValue [ "kind", box (caseName relation.Kind); "id", targetId relation.Target ])
                    |> arrayValue
                  "valueDependencies",
                    item.ValueDependencies |> List.map (fun dependency -> objectValue (dependencyRole dependency.Role @ [ "nodeId", id dependency.NodeId ])) |> arrayValue
                  "branches",
                    item.Branches
                    |> List.map (fun branch ->
                        objectValue
                            ([ "role", box (caseName branch.Role) ]
                             @ (match branch.Role with
                                | ViewerBranchRole.UnionVariant variantIds -> [ "variantIds", ids variantIds ]
                                | _ -> [])
                             @ [ "bindings",
                                   branch.Bindings
                                   |> List.map (fun binding ->
                                       objectValue
                                           [ "id", id binding.Id
                                             "displayName", optional binding.DisplayName box
                                             "type", typeRef binding.Type
                                             "sourceFieldId", optional binding.SourceFieldId id ])
                                   |> arrayValue
                                 "graph", graph branch.Graph ]))
                    |> arrayValue ]
        objectValue [ "resultNodeId", id value.ResultNodeId; "nodes", value.Nodes |> List.map node |> arrayValue ]

    let private effect (value: SemanticEffect) =
        objectValue [ "id", id value.Id; "displayName", box value.DisplayName; "exported", box value.Exported ]

    let private focus (value: ViewerFocusModel): obj =
        let signature = value.Function
        objectValue
            [ "snapshotId", id value.SnapshotId
              "function",
                objectValue
                    [ "id", id signature.FunctionId
                      "displayName", box signature.DisplayName
                      "exported", box signature.Exported
                      "typeParameters", typeParameters signature.TypeParameters
                      "parameters", parameters signature.Parameters
                      "resultType", typeRef signature.ResultType
                      "requiredEffects", ids signature.RequiredEffects ]
              "mustNotWait", box value.MustNotWait
              "waitPath", optional value.WaitPath box
              "bodyEffectRequirements", bodyEffectRequirements value.BodyEffectRequirements
              "aborts", aborts value.Aborts
              "callers", value.Callers |> List.map callSite |> arrayValue
              "callees", value.Callees |> List.map callSite |> arrayValue
              "relatedFunctionSignatures",
                value.RelatedFunctionSignatures
                |> List.map (fun item ->
                    objectValue
                        [ "id", id item.FunctionId
                          "displayName", box item.DisplayName
                          "typeParameters", typeParameters item.TypeParameters
                          "parameters", parameters item.Parameters
                          "resultType", typeRef item.ResultType
                          "requiredEffects", ids item.RequiredEffects ])
                |> arrayValue
              "openObligations",
                value.OpenObligations
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", id item.ObligationId
                          "callNodeId", id item.CallNodeId
                          "parameterId", id item.ParameterId
                          "originTransactionId", id item.OriginTransactionId ])
                |> arrayValue
              "referencedTypes", value.ReferencedTypes |> List.map semanticType |> arrayValue
              "referencedCapabilities", value.ReferencedCapabilities |> List.map capabilityEntity |> arrayValue
              "referencedEffects", value.ReferencedEffects |> List.map effect |> arrayValue
              "missingFunctionIds", ids value.MissingFunctionIds
              "missingTypeIds", ids value.MissingTypeIds
              "missingCapabilityIds", ids value.MissingCapabilityIds
              "missingEffectIds", ids value.MissingEffectIds
              "graph", graph value.Graph ]

    let ofFocus (value: ViewerFocusModel): string =
        serialize (objectValue [ "view", box "functionFocus"; "focus", focus value ])

    let ofChangeReview baseIsCommitted (value: ViewerChangeReviewModel): string =
        let readiness =
            match value.Readiness with
            | ChangeReviewReadiness.Complete -> objectValue [ "kind", box "complete" ]
            | ChangeReviewReadiness.Incomplete obligationIds -> objectValue [ "kind", box "incomplete"; "obligationIds", ids obligationIds ]
            | ChangeReviewReadiness.Invalid issues ->
                objectValue
                    [ "kind", box "invalid"
                      "issues", issues |> List.map (fun issue -> objectValue [ "code", box issue.Code; "message", box issue.Message ]) |> arrayValue ]
        serialize (
            objectValue
                [ "view", box "changeReview"
                  "transactionId", id value.TransactionId
                  "baseSnapshotId", id value.BaseSnapshotId
                  "proposedSnapshotId", id value.ProposedSnapshotId
                  "baseIsCommitted", box baseIsCommitted
                  "readiness", readiness
                  "diff", WireEncode.semanticDiff value.Diff
                  "affectedFunctionIds", ids value.AffectedFunctionIds
                  "affectedTypeIds", ids value.AffectedTypeIds
                  "functionFocus", value.FunctionFocus |> List.map focus |> arrayValue ])

    /// `verification`, a verifyChange report's evidence, is shown beside the review when present.
    let ofProposalReview (verification: obj option) (value: ViewerProposalReviewModel): string =
        use engineReview = Text.Json.JsonDocument.Parse(value.EngineReview, Text.Json.JsonDocumentOptions(MaxDepth = responseMaxJsonDepth))
        serialize (
            objectValue
                ([ "view", box "proposalReview"
                   "transactionId", id value.TransactionId
                   "proposalKind", box value.ProposalKind
                   "baseSnapshotId", id value.BaseSnapshotId
                   "proposedSnapshotId", id value.ProposedSnapshotId
                   "baseIsCommitted", box value.BaseIsCommitted
                   "readiness", readiness value.Readiness
                   "changedFunctions",
                     value.ChangedFunctions
                     |> List.map (fun item -> objectValue [ "committed", optional item.CommittedVersion focus; "proposed", focus item.ProposedVersion ])
                     |> arrayValue
                   "declaredTypes", value.DeclaredTypes |> List.map semanticType |> arrayValue
                   "declaredCapabilities", value.DeclaredCapabilities |> List.map capabilityEntity |> arrayValue
                   "declaredEffects", value.DeclaredEffects |> List.map effect |> arrayValue
                   "engineReview", box engineReview.RootElement ]
                 @ (match verification with
                    | Some evidence -> [ "verification", evidence ]
                    | None -> [])))

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
          Content = ViewerHtml.page (ViewerData.ofFocus model) }

    let ofChangeReviewWithBaseState baseIsCommitted (review: ChangeReviewView): ViewerArtifact =
        let model = ViewerProjection.changeReview review
        { Kind = ViewerArtifactKind.ChangeReview
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = reviewFileName review.TransactionId
          Content = ViewerHtml.page (ViewerData.ofChangeReview baseIsCommitted model) }

    let ofChangeReview review = ofChangeReviewWithBaseState true review

    /// A pending structural proposal's Change Review; named by transaction ID like `ofChangeReview`.
    let ofProposalReviewWith verification (model: ViewerProposalReviewModel): ViewerArtifact =
        { Kind = ViewerArtifactKind.ChangeReview
          MediaType = "text/html; charset=utf-8"
          SuggestedFileName = reviewFileName model.TransactionId
          Content = ViewerHtml.page (ViewerData.ofProposalReview verification model) }

    let ofProposalReview model = ofProposalReviewWith None model

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
