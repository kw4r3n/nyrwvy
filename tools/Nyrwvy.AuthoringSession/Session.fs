namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Security.Cryptography
open Nyrwvy.Core

[<RequireQualifiedAccess>]
type AuthoringSessionError =
    | SnapshotFileNotFound of string
    | SnapshotFileAlreadyExists of string
    | LockUnavailable of string
    | IoFailure of string
    | SnapshotCodecFailure of SnapshotCodecError
    | WorkspaceValidationFailure of ValidationIssue list
    | ProposalNotFound of string
    | SessionClosed
    | ExternalModification
    | SessionInvalidated
    | WorkflowSimulationFailure of AgentWorkflowError
    | StructuralSimulationFailure of AgentStructuralWorkflowError
    | FieldTypeMigrationRepairMismatch of TransactionDiagnostic list * missing: FunctionId list * unrelated: FunctionId list
    | TypeDeclarationSimulationFailure of AgentTypeDeclarationWorkflowError
    | EffectDeclarationSimulationFailure of AgentEffectDeclarationWorkflowError
    | CapabilityDeclarationSimulationFailure of AgentCapabilityDeclarationWorkflowError
    | CommitRejected of TransactionDiagnostic list
    | SemanticProjectionFailure of TypedSemanticIrError
    | ExecutionLoweringFailure of ExecutionIrError
    | BackendFailure of CSharpBackendError
    | DependencyFailure of string
    /// `migrate` cannot rewrite the snapshot to the current schema; nothing was written.
    | MigrationRejected of string

type AuthoringSessionStatus =
    { SnapshotId: SnapshotId
      SnapshotFingerprint: SnapshotFingerprint
      SemanticFingerprint: SemanticFingerprint
      /// Proposals simulated in this session and neither committed nor rejected yet.
      PendingProposalCount: int }

/// One pending proposal as the `proposals` listing shows it: enough to tell proposals apart and
/// to decide which to read, commit or reject, without the review itself.
type AuthoringPendingProposalSummary =
    { ProposalId: string
      /// The review kind, as the proposal's `review.kind` names it.
      Kind: string
      BaseSnapshotId: SnapshotId
      /// Whether the proposal was simulated against the current committed snapshot; a stale one is
      /// rejected at commit.
      BaseIsCurrent: bool
      /// One line naming the requested change and its targets by display name.
      Summary: string }

type AuthoringDependencyMember =
    { SnapshotId: string
      Fingerprint: string }

/// A declaration is locally owned or provided by one or several selected pins.
/// Flattened imports report their providing pin, without guessing original authorship.
type AuthoringDeclarationProvenance =
    { Kind: string
      Id: string
      IsLocal: bool
      Sources: AuthoringDependencyMember list }

type AuthoringDeclarations =
    { Functions: SemanticFunction list
      Types: SemanticType list
      Effects: SemanticEffect list
      Capabilities: SemanticCapability list
      Provenance: AuthoringDeclarationProvenance list }

/// One operation of a `simulateOperations` batch as the wire layer reads it. `addUnionVariant`
/// arrives with arm recipes, which the session materializes in each match's scope against the
/// current snapshot before the batch is simulated; every other operation is already an intent.
[<RequireQualifiedAccess>]
type AuthoringOperationRequest =
    | Intent of AgentOperationIntent
    | AddUnionVariant of targetTypeId: TypeId * variant: SemanticVariant * arms: AgentUnionVariantArmRecipe list

/// Read-only execution inputs; candidate projection never publishes into the session.
type internal AuthoringExecutionContext =
    { SnapshotId: SnapshotId
      Fingerprint: string
      Declarations: AuthoringDeclarations
      Project: FunctionId -> Result<ExecutionProgram, AuthoringSessionError>
      /// The session's long-lived runner process for compiled programs.
      Worker: PureRunWorker }

/// One imported declaration an upgrade adds, removes or changes. `Exported` is its visibility in
/// the new version (for a removed declaration, in the replaced one); exported changes are listed
/// first, since only they can affect a dependent, and private ones follow as secondary information.
type AuthoringDependencyChange =
    { Kind: string
      Id: string
      Exported: bool }

/// The reviewed impact of replacing one pinned dependency with a new version.
type AuthoringDependencyUpgradePreview =
    { ReplacedSnapshotId: string
      ReplacedFingerprint: string
      NewSnapshotId: string
      /// Confirms the exact reviewed version when the upgrade is applied.
      NewFingerprint: string
      Added: AuthoringDependencyChange list
      Removed: AuthoringDependencyChange list
      Changed: AuthoringDependencyChange list
      /// Validation issues of the local graph against the upgraded imports; empty when compatible.
      Issues: ValidationIssue list }

/// An explicit replacement of one current dependency member.
type AuthoringDependencyUpgrade =
    { Source: string
      Replaces: string }

/// Arbitrary-cardinality replacement of explicit current members by complete sources.
type AuthoringDependencySelectionUpgrade =
    { Sources: string list
      Replaces: string list }

/// One review of the final combined imports; no intermediate dependency graph is persisted.
type AuthoringDependencyUpgradesPreview =
    { ReviewFingerprint: string
      Upgrades: AuthoringDependencyUpgradePreview list
      Replaced: AuthoringDependencyMember list
      Selected: AuthoringDependencyMember list
      Added: AuthoringDependencyChange list
      Removed: AuthoringDependencyChange list
      Changed: AuthoringDependencyChange list
      Issues: ValidationIssue list }

[<RequireQualifiedAccess>]
type AuthoringDependencyChangeRequest =
    | Upgrades of AuthoringDependencyUpgrade list
    | Selection of AuthoringDependencySelectionUpgrade

type AuthoringDependencyRepairReview =
    { Upgrade: AuthoringDependencyUpgradesPreview
      Repair: AgentDependencyUpgradeReview }

[<RequireQualifiedAccess>]
type AuthoringProposalReview =
    | DependencyUpgrade of AuthoringDependencyRepairReview
    | Operations of AgentChangeReview
    | ReplaceBody of AgentExpressionStructuralChangeReview
    | ReplaceTarget of AgentExpressionStructuralWorkflow.Targeted.Review
    | CreateFunction of AgentExpressionStructuralWorkflow.FunctionCreation.Review
    | ParameterTypeMigration of AgentParameterTypeMigrationReview
    | ResultTypeMigration of AgentResultTypeMigrationReview
    | ProductFieldTypeMigration of AgentProductFieldTypeMigrationReview
    | CapabilityOperationSignatureMigration of AgentCapabilityOperationSignatureMigrationReview
    | CreateType of AgentTypeDeclarationReview
    | CreateTypeGroup of AgentTypeDeclarationGroupReview
    | UnionVariantEvolution of AgentUnionVariantEvolutionReview
    | CreateEffect of AgentEffectDeclarationReview
    | CreateCapability of AgentCapabilityDeclarationReview

type AuthoringProposal =
    { ProposalId: string
      Review: AuthoringProposalReview
      /// Proposed bodies of the functions a migration or union-variant evolution rewrites, in
      /// review order; empty for other reviews. Rendered only by a full review.
      RewrittenFunctions: SemanticFunction list }

/// What a human Change Review page of a pending proposal reads. `ProposedSnapshot` is absent
/// for an operations proposal, whose review already carries its Change Review projection.
type AuthoringPendingProposalView =
    { TransactionId: TransactionId
      Proposal: AuthoringProposal
      BaseSnapshotId: SnapshotId
      BaseIsCommitted: bool
      ProposedSnapshot: Snapshot option
      /// Functions the proposal creates or rewrites, in review order.
      FunctionIds: FunctionId list
      /// Types the proposal creates or rewrites, in review order.
      TypeIds: TypeId list }

type AuthoringCommitReceipt =
    { TransactionId: TransactionId
      PublishedSnapshotId: SnapshotId
      Readiness: TransactionReadiness }

type AuthoringRejectionReceipt =
    { TransactionId: TransactionId
      RetainedSnapshotId: SnapshotId }

[<RequireQualifiedAccess>]
type private PendingProposal =
    | DependencyUpgrade of AgentDependencyUpgradeProposal * AuthoringDependencyChangeRequest * string * AuthoringDeclarationProvenance list
    | Operations of AgentChangeProposal
    | ReplaceBody of AgentExpressionStructuralChangeProposal
    | ReplaceTarget of AgentExpressionStructuralWorkflow.Targeted.Proposal
    | CreateFunction of AgentExpressionStructuralWorkflow.FunctionCreation.Proposal
    | ParameterTypeMigration of AgentParameterTypeMigrationProposal
    | ResultTypeMigration of AgentResultTypeMigrationProposal
    | ProductFieldTypeMigration of AgentProductFieldTypeMigrationProposal
    | CapabilityOperationSignatureMigration of AgentCapabilityOperationSignatureMigrationProposal
    | CreateType of AgentTypeDeclarationProposal
    | CreateTypeGroup of AgentTypeDeclarationGroupProposal
    | UnionVariantEvolution of AgentUnionVariantEvolutionProposal
    | CreateEffect of AgentEffectDeclarationProposal
    | CreateCapability of AgentCapabilityDeclarationProposal

module private PendingProposal =
    let proposedSnapshot (pending: PendingProposal): Snapshot option =
        match pending with
        | PendingProposal.DependencyUpgrade(proposal, _, _, _) -> Some(AgentDependencyUpgradeProposal.proposedSnapshot proposal)
        | PendingProposal.Operations _ -> None
        | PendingProposal.ReplaceBody proposal -> Some(AgentExpressionStructuralChangeProposal.proposedSnapshot proposal)
        | PendingProposal.ReplaceTarget proposal -> Some(AgentExpressionStructuralWorkflow.Targeted.Proposal.proposedSnapshot proposal)
        | PendingProposal.CreateFunction proposal -> Some(AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.proposedSnapshot proposal)
        | PendingProposal.ParameterTypeMigration proposal -> Some(AgentParameterTypeMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.ResultTypeMigration proposal -> Some(AgentResultTypeMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.CapabilityOperationSignatureMigration proposal -> Some(AgentCapabilityOperationSignatureMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.ProductFieldTypeMigration proposal -> Some(AgentProductFieldTypeMigrationProposal.proposedSnapshot proposal)
        | PendingProposal.CreateType proposal -> Some(AgentTypeDeclarationProposal.proposedSnapshot proposal)
        | PendingProposal.CreateTypeGroup proposal -> Some(AgentTypeDeclarationGroupProposal.proposedSnapshot proposal)
        | PendingProposal.UnionVariantEvolution proposal -> Some(AgentUnionVariantEvolutionProposal.proposedSnapshot proposal)
        | PendingProposal.CreateEffect proposal -> Some(AgentEffectDeclarationProposal.proposedSnapshot proposal)
        | PendingProposal.CreateCapability proposal -> Some(AgentCapabilityDeclarationProposal.proposedSnapshot proposal)

module private ProposalReview =
    /// Functions and types a proposal rewrites, from its resulting change as well as its
    /// intent: signature/variant edits can rewrite other callers or match owners.
    let touched (review: AuthoringProposalReview): FunctionId list * TypeId list =
        match review with
        | AuthoringProposalReview.DependencyUpgrade item -> item.Repair.ChangedFunctions |> List.map (fun f -> f.Id), item.Repair.ChangedTypes |> List.map (fun t -> t.Id)
        | AuthoringProposalReview.Operations item ->
            item.ResultingChange.AffectedFunctionIds @ (item.ResultingChange.Diff.FunctionsRemoved |> List.map (fun removed -> removed.FunctionId)),
            item.ResultingChange.AffectedTypeIds @ (item.ResultingChange.Diff.TypesRemoved |> List.map (fun removed -> removed.TypeId))
        | AuthoringProposalReview.ReplaceBody item -> [ item.RequestedChange.TargetFunctionId ], []
        | AuthoringProposalReview.ReplaceTarget item -> [ item.RequestedChange.TargetFunctionId ], []
        | AuthoringProposalReview.ParameterTypeMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), []
        | AuthoringProposalReview.ResultTypeMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), []
        | AuthoringProposalReview.CapabilityOperationSignatureMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), []
        | AuthoringProposalReview.ProductFieldTypeMigration item ->
            item.ResultingChange.BodyRewrites |> List.map (fun rewrite -> rewrite.OwnerFunctionId), [ item.ResultingChange.FieldChange.TypeId ]
        | AuthoringProposalReview.UnionVariantEvolution item ->
            item.RepairedFunctionIds, [ item.UpdatedType.Id ]
        | AuthoringProposalReview.CreateFunction _
        | AuthoringProposalReview.CreateType _
        | AuthoringProposalReview.CreateTypeGroup _
        | AuthoringProposalReview.CreateEffect _
        | AuthoringProposalReview.CreateCapability _ -> [], []

/// One-line summaries of pending proposals for the `proposals` listing. Names come from the
/// snapshot the proposal was simulated against where the review carries them, otherwise from the
/// current snapshot with the exact ID as the fallback.
module private ProposalSummary =
    let private quote (name: string) = $"'{name}'"

    let private named (items: (StableId<'Kind> * string) list) (id: StableId<'Kind>) =
        items |> List.tryFind (fun (candidate, _) -> candidate = id) |> Option.map (snd >> quote) |> Option.defaultValue (StableId.value id)

    let describe (declarations: AuthoringDeclarations) (review: AuthoringProposalReview) =
        let functions = declarations.Functions |> List.map (fun item -> item.Id, item.DisplayName)
        let types = declarations.Types |> List.map (fun item -> item.Id, item.DisplayName)
        let effects = declarations.Effects |> List.map (fun item -> item.Id, item.DisplayName)
        let capabilities = declarations.Capabilities |> List.map (fun item -> item.Id, item.DisplayName)
        let operations = declarations.Capabilities |> List.collect (fun item -> item.Operations |> List.map (fun op -> op.Id, op.DisplayName))
        let capabilityParameters =
            declarations.Capabilities |> List.collect (fun item -> item.Operations |> List.collect (fun op -> op.Parameters |> List.map (fun p -> p.Id, p.DisplayName)))
        let parameters = declarations.Functions |> List.collect (fun item -> item.Parameters |> List.map (fun p -> p.Id, p.DisplayName))
        let variants =
            declarations.Types |> List.collect (fun item ->
                match item.Kind with
                | SemanticTypeKind.Union variants -> variants |> List.map (fun v -> v.Id, v.DisplayName)
                | SemanticTypeKind.Product _ -> [])
        let fields =
            declarations.Types |> List.collect (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun f -> f.Id, f.DisplayName)
                | SemanticTypeKind.Union variants -> variants |> List.collect (fun v -> v.Fields |> List.map (fun f -> f.Id, f.DisplayName)))
        let fn = named functions
        let ty = named types
        let rename target =
            match target with
            | AgentRenameTarget.Type id -> "renameType " + ty id
            | AgentRenameTarget.Function id -> "renameFunction " + fn id
            | AgentRenameTarget.Field id -> "renameField " + named fields id
            | AgentRenameTarget.Variant id -> "renameVariant " + named variants id
            | AgentRenameTarget.Parameter id -> "renameParameter " + named parameters id
            | AgentRenameTarget.Effect id -> "renameEffect " + named effects id
            | AgentRenameTarget.Capability id -> "renameCapability " + named capabilities id
            | AgentRenameTarget.CapabilityOperation id -> "renameCapabilityOperation " + named operations id
            | AgentRenameTarget.CapabilityParameter id -> "renameCapabilityParameter " + named capabilityParameters id
        let operation intent =
            match intent with
            | AgentOperationIntent.RenameDisplayName(target, newName) -> $"{rename target} to {quote newName}"
            | AgentOperationIntent.SetExported(target, exported) ->
                let subject =
                    match target with
                    | AgentVisibilityTarget.Type id -> ty id
                    | AgentVisibilityTarget.Function id -> fn id
                    | AgentVisibilityTarget.Effect id -> named effects id
                    | AgentVisibilityTarget.Capability id -> named capabilities id
                let visibility = if exported then "exported" else "private"
                $"setExported {subject} to {visibility}"
            | AgentOperationIntent.AddFunctionParameter item -> $"addFunctionParameter {quote item.NewParameterDisplayName} to {fn item.TargetFunctionId}"
            | AgentOperationIntent.AddProductField item -> $"addProductField {quote item.NewFieldDisplayName} to {ty item.TargetTypeId}"
            | AgentOperationIntent.RemoveProductField id -> "removeProductField " + named fields id
            | AgentOperationIntent.DeleteFunction id -> "deleteFunction " + fn id
            | AgentOperationIntent.DeleteType id -> "deleteType " + ty id
            | AgentOperationIntent.DeleteEffect id -> "deleteEffect " + named effects id
            | AgentOperationIntent.DeleteCapability id -> "deleteCapability " + named capabilities id
            | AgentOperationIntent.DeleteCapabilityOperation id -> "deleteCapabilityOperation " + named operations id
            | AgentOperationIntent.AddCapabilityOperation item -> $"addCapabilityOperation {quote item.Operation.DisplayName} to {named capabilities item.TargetCapabilityId}"
            | AgentOperationIntent.ResolveMissingArgument item -> $"resolveMissingArgument {StableId.value item.TargetObligationId}"
            | AgentOperationIntent.AddFunctionRequiredEffect item -> $"addRequiredEffect {named effects item.EffectId} on {fn item.TargetFunctionId}"
            | AgentOperationIntent.RemoveFunctionRequiredEffect item -> $"removeRequiredEffect {named effects item.EffectId} from {fn item.TargetFunctionId}"
            | AgentOperationIntent.RemoveFunctionParameter id -> "removeFunctionParameter " + named parameters id
            | AgentOperationIntent.RemoveUnionVariant id -> "removeUnionVariant " + named variants id
            | AgentOperationIntent.AddUnionVariant item -> $"addUnionVariant {quote item.Variant.DisplayName} to {ty item.TargetTypeId}"
        match review with
        | AuthoringProposalReview.DependencyUpgrade item ->
            $"dependencyUpgrade: {item.Upgrade.Selected.Length} selected, {item.Repair.ChangedFunctions.Length} functions and {item.Repair.ChangedTypes.Length} types repaired"
        | AuthoringProposalReview.Operations item -> item.RequestedOperations |> List.map operation |> String.concat "; "
        | AuthoringProposalReview.ReplaceBody item -> $"replaceBody of {quote item.ProposedFocus.Function.DisplayName}"
        | AuthoringProposalReview.ReplaceTarget item ->
            $"replaceTarget {StableId.value item.RequestedChange.TargetNodeId} in {quote item.ProposedFocus.Function.DisplayName}"
        | AuthoringProposalReview.CreateFunction item -> $"createFunction {quote item.CreatedFunction.DisplayName}"
        | AuthoringProposalReview.ParameterTypeMigration item ->
            $"parameterTypeMigration of {fn item.ResultingChange.ParameterChange.FunctionId} parameter {named parameters item.ResultingChange.ParameterChange.ParameterId}"
        | AuthoringProposalReview.ResultTypeMigration item -> $"resultTypeMigration of {fn item.ResultingChange.ResultChange.FunctionId}"
        | AuthoringProposalReview.ProductFieldTypeMigration item ->
            $"productFieldTypeMigration of {ty item.ResultingChange.FieldChange.TypeId} field {named fields item.ResultingChange.FieldChange.FieldId}"
        | AuthoringProposalReview.CapabilityOperationSignatureMigration item ->
            $"capabilityOperationSignatureMigration of {named capabilities item.ResultingChange.CapabilityId} operation {quote item.ResultingChange.NewOperation.DisplayName}"
        | AuthoringProposalReview.CreateType item -> $"createType {quote item.CreatedType.DisplayName}"
        | AuthoringProposalReview.CreateTypeGroup item ->
            "createTypes " + (item.CreatedTypes |> List.map (fun t -> quote t.DisplayName) |> String.concat ", ")
        | AuthoringProposalReview.UnionVariantEvolution item ->
            $"addUnionVariantLike {quote item.AddedVariant.DisplayName} on {quote item.UpdatedType.DisplayName} ({item.RepairedMatchCount} matches repaired)"
        | AuthoringProposalReview.CreateEffect item -> $"createEffect {quote item.CreatedEffect.DisplayName}"
        | AuthoringProposalReview.CreateCapability item -> $"createCapability {quote item.CreatedCapability.DisplayName}"

    let kind (review: AuthoringProposalReview) =
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

/// Test seam for storage failures that cannot be produced reliably through the file system.
module internal SessionStorageFaults =
    /// When it returns true for a full path, the next replacement of that path fails.
    let mutable failReplacement: string -> bool = fun _ -> false

module internal SessionStorage =
    let fullPath (path: string): string = Path.GetFullPath(path)
    let lockPath (path: string): string = path + ".lock"

    let acquireLock (path: string): Result<FileStream, AuthoringSessionError> =
        try
            Ok(new FileStream(lockPath path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        with
        | :? IOException -> Error(AuthoringSessionError.LockUnavailable path)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    /// Releases the lock and removes its file, so a clean close or a failed open leaves nothing
    /// behind. The file is unlinked before the handle closes where the OS allows it (POSIX), so a
    /// concurrent opener either fails against the still-held lock or creates a fresh file; where it
    /// does not (Windows), the delete is retried after the close and fails harmlessly if another
    /// session has already taken the lock. Session commits verify the persisted source digest, so
    /// the remaining POSIX window (an opener between its open and lock calls) cannot corrupt a snapshot.
    let releaseLock (path: string) (lockHandle: FileStream) =
        let tryDelete () =
            try File.Delete(lockPath path); true
            with
            | :? IOException
            | :? UnauthorizedAccessException -> false
        let deleted = tryDelete ()
        lockHandle.Dispose()
        if not deleted then tryDelete () |> ignore

    let readText (path: string): Result<string, AuthoringSessionError> =
        try Ok(File.ReadAllText(path, Encoding.UTF8))
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    /// The persisted form follows the file extension (docs/decisions/snapshot-record-format.md):
    /// `.nyrec` is the record form, every other path the strict JSON codec.
    let isRecordPath (path: string) = Path.GetExtension path = ".nyrec"

    /// The writer's JSON in the form `path` persists.
    let encode (path: string) (json: string) = if isRecordPath path then RecordCodec.ofJson json else json

    // A model-level failure keeps the JSON reader's structured error, as a `.json` source reports it.
    let private recordFailure (error: RecordCodecError) =
        match error.Model with
        | Some model -> AuthoringSessionError.SnapshotCodecFailure model
        | None -> AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidShape $"line {error.Line}, column {error.Column}: {error.Message}")

    /// Decodes `source` in the record form or strict JSON, resolved against `dependency` when given.
    let decodeWith (record: bool) (dependency: Snapshot option) (source: string): Result<Snapshot, AuthoringSessionError> =
        match record, dependency with
        | true, None -> RecordCodec.deserialize source |> Result.mapError recordFailure
        | true, Some imported -> RecordCodec.deserializeResolved imported source |> Result.mapError recordFailure
        | false, None -> SnapshotCodec.deserialize source |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
        | false, Some imported -> SnapshotCodec.deserializeResolved imported source |> Result.mapError AuthoringSessionError.SnapshotCodecFailure

    /// Decodes `source` in the form `path` persists.
    let decodeAs (path: string) = decodeWith (isRecordPath path)

    /// The schema version the document at `path` declares; the record form only decodes the current one.
    let declaredVersion (path: string) (source: string): Result<SnapshotSchemaVersion, AuthoringSessionError> =
        if isRecordPath path then Ok SnapshotSchema.current
        else SnapshotCodec.declaredVersion source |> Result.mapError AuthoringSessionError.SnapshotCodecFailure

    // SnapshotCodec establishes validity; pinning additionally requires completeness.
    let ensureCompleteDependency (imported: Snapshot): Result<Snapshot, AuthoringSessionError> =
        if imported.Obligations |> List.exists (fun obligation -> obligation.Status = ObligationStatus.Open) then
            Error(AuthoringSessionError.DependencyFailure "The selected dependency has open obligations; complete it before pinning.")
        else Ok imported

    let decodeCompleteDependency (record: bool) (source: string): Result<Snapshot, AuthoringSessionError> =
        decodeWith record None source |> Result.bind ensureCompleteDependency

    /// A resolved local snapshot cannot be decoded without its exact dependency sidecar.
    /// Invalid JSON still goes to SnapshotCodec, which owns syntax diagnostics.
    let requireSidecarForResolvedSource (path: string) (source: string) =
        let bound =
            if isRecordPath path then
                // The record header is the first line; `dependency=` marks a resolved snapshot.
                let header = match source.IndexOf '\n' with -1 -> source | at -> source.Substring(0, at)
                header.Contains " dependency="
            else
                try
                    use document = JsonDocument.Parse source
                    document.RootElement.ValueKind = JsonValueKind.Object
                    && fst (document.RootElement.TryGetProperty "dependency")
                with :? JsonException -> false
        if bound then
            Error(AuthoringSessionError.DependencyFailure
                $"The dependency sidecar '{path}.dependency.json' is missing. Restore the exact sidecar that belongs to this snapshot; pinning a new version cannot recover a lost selection.")
        else Ok()

    let readCompleteDependency path =
        readText path |> Result.bind (fun source ->
            requireSidecarForResolvedSource path source |> Result.bind (fun () -> decodeCompleteDependency (isRecordPath path) source))

    let private writeTemporary (path: string) (content: string): Result<string, AuthoringSessionError> =
        let directory = Path.GetDirectoryName(path)
        let tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp")
        try
            // Flushed to disk before the rename so a crash cannot leave the final name on empty content.
            use stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write)
            let bytes = UTF8Encoding(false).GetBytes content
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true
            Ok tempPath
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

    // Also used for a manifest whose associated snapshot could not be created.
    // Cleanup must not mask the original failure or prevent lock disposal.
    let cleanupTemporary (tempPath: string) =
        try if File.Exists tempPath then File.Delete tempPath
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    let private createAtomicallyCore (path: string) (content: string): Result<unit, AuthoringSessionError> =
        match writeTemporary path content with
        | Error error -> Error error
        | Ok tempPath ->
            try
                File.Move(tempPath, path, false)
                Ok()
            with
            | :? IOException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)
            | :? UnauthorizedAccessException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)

    let private replaceAtomicallyCore (path: string) (content: string): Result<unit, AuthoringSessionError> =
        if SessionStorageFaults.failReplacement path then Error(AuthoringSessionError.IoFailure $"Injected replacement failure for '{path}'.") else
        match writeTemporary path content with
        | Error error -> Error error
        | Ok tempPath ->
            try
                File.Move(tempPath, path, true)
                Ok()
            with
            | :? IOException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)
            | :? UnauthorizedAccessException as error ->
                cleanupTemporary tempPath
                Error(AuthoringSessionError.IoFailure error.Message)

    let createAtomically path content =
        SnapshotComputationScope.Measure("persistence.create", fun () -> createAtomicallyCore path content)

    let replaceAtomically path content =
        SnapshotComputationScope.Measure("persistence.replace", fun () -> replaceAtomicallyCore path content)

    /// One shared persistence boundary for startup upgrades and reviewed repairs.
    /// The previous sidecar is kept as `<snapshot>.dependency.json.previous` while the pair is replaced,
    /// so a crash between the two renames leaves the only copy of the previous selection recoverable
    /// (`open` names it); it is removed once the pair is consistent again.
    let replaceDependencyPair path previousSource previousManifest nextSource nextManifest =
        let sidecar = path + ".dependency.json"
        let previous = sidecar + ".previous"
        match readText path, readText sidecar with
        | Ok source, Ok manifest when source = previousSource && manifest = previousManifest ->
            replaceAtomically previous previousManifest
            |> Result.bind (fun () ->
                let mutable inconsistent = false
                let result =
                    replaceAtomically sidecar nextManifest
                    |> Result.bind (fun () ->
                        match replaceAtomically path nextSource with
                        | Ok() -> Ok()
                        | Error error ->
                            match replaceAtomically sidecar previousManifest with
                            | Ok() -> Error error
                            | Error rollback ->
                                inconsistent <- true
                                let text = function AuthoringSessionError.IoFailure message -> message | other -> $"%A{other}"
                                Error(AuthoringSessionError.IoFailure $"{text error} Restoring the previous dependency selection also failed: {text rollback} The previous sidecar is kept at '{previous}'."))
                if not inconsistent then cleanupTemporary previous
                result)
        | Error error, _ | _, Error error -> Error error
        | _ -> Error AuthoringSessionError.ExternalModification

    let ensureParentDirectory (path: string): Result<unit, AuthoringSessionError> =
        try
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            Ok()
        with
        | :? IOException as error -> Error(AuthoringSessionError.IoFailure error.Message)
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.IoFailure error.Message)

/// One imported declaration that an upgrade adds, removes or changes.
type internal PinnedDependencySelection =
    { Imported: Snapshot
      Manifest: string
      SourcePath: string
      Fingerprint: string
      IsEmbedded: bool
      /// The individually pinned dependencies of a combined selection; empty for one dependency,
      /// whose `Imported` graph is then the dependency itself.
      Members: PinnedDependencySelection list }

module internal PinnedDependency =
    /// Retains `imported` in the embedded manifest form, written by `serialize`: the ordinary
    /// smallest-version form, or the current explicit form for the migration command.
    let embedWith (serialize: Snapshot -> Result<string, SnapshotCodecError>) sourcePath imported =
        match serialize imported, SnapshotFingerprint.compute imported with
        | Ok source, Ok fingerprint ->
            let fingerprint = SnapshotFingerprint.value fingerprint
            // The embedded form follows the dependency file's extension: `snapshotRecords` for `.nyrec`.
            let manifest =
                if SessionStorage.isRecordPath sourcePath then
                    JsonSerializer.Serialize(
                        {| path = sourcePath
                           snapshotId = StableId.value imported.Id
                           fingerprint = fingerprint
                           snapshotRecords = RecordCodec.ofJson source |})
                else
                    JsonSerializer.Serialize(
                        {| path = sourcePath
                           snapshotId = StableId.value imported.Id
                           fingerprint = fingerprint
                           snapshotJson = source |})
            Ok { Imported = imported; Manifest = manifest; SourcePath = sourcePath
                 Fingerprint = fingerprint; IsEmbedded = true; Members = [] }
        | Error error, _
        | _, Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)

    let embed sourcePath imported = embedWith SnapshotCodec.serialize sourcePath imported

    let private describeMember selected =
        {| path = selected.SourcePath
           snapshotId = StableId.value selected.Imported.Id
           fingerprint = selected.Fingerprint
           storage = if selected.IsEmbedded then "embedded" else "legacySource" |}

    let describe selected =
        // Discovery must not repeat the potentially large stored snapshot.
        match selected.Members with
        | [] -> JsonSerializer.Serialize(describeMember selected)
        | members -> JsonSerializer.Serialize({| dependencies = members |> List.map describeMember |})

    let private combinationFailure (members: PinnedDependencySelection list) = function
        | DependencyCombinationError.NoDependencies -> AuthoringSessionError.DependencyFailure "A dependency set needs at least one dependency."
        | DependencyCombinationError.ConflictingDeclaration(kind, id) ->
            let providers =
                members |> List.choose (fun memberSelection ->
                    let name =
                        match kind with
                        | "type" -> memberSelection.Imported.Types |> List.tryFind (fun item -> StableId.value item.Id = id) |> Option.map (fun item -> item.DisplayName)
                        | "effect" -> memberSelection.Imported.Effects |> List.tryFind (fun item -> StableId.value item.Id = id) |> Option.map (fun item -> item.DisplayName)
                        | "capability" -> memberSelection.Imported.Capabilities |> List.tryFind (fun item -> StableId.value item.Id = id) |> Option.map (fun item -> item.DisplayName)
                        | "function" -> memberSelection.Imported.Functions |> List.tryFind (fun item -> StableId.value item.Id = id) |> Option.map (fun item -> item.DisplayName)
                        | _ -> None
                    name |> Option.map (fun displayName -> $"'{displayName}' from providing pin '{StableId.value memberSelection.Imported.Id}' (fingerprint {memberSelection.Fingerprint})"))
            let context = if providers.IsEmpty then "" else " Providing declarations: " + String.concat "; " providers + "."
            AuthoringSessionError.DependencyFailure ($"Dependencies declare the {kind} '{id}' with different content; colliding imported identities are never merged." + context)
        | DependencyCombinationError.FingerprintFailure error -> AuthoringSessionError.SnapshotCodecFailure error

    /// Combines individually pinned dependencies under the import collision policy.
    let combine (members: PinnedDependencySelection list) =
        match members with
        | [ single ] -> Ok single
        | _ ->
            DependencyGraph.combine (members |> List.map (fun item -> item.Imported))
            |> Result.mapError (combinationFailure members)
            |> Result.bind (fun combined ->
                match SnapshotFingerprint.compute combined with
                | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
                | Ok fingerprint ->
                    let manifest =
                        members
                        |> List.map (fun item -> JsonSerializer.Deserialize<JsonElement>(item.Manifest))
                        |> fun items -> JsonSerializer.Serialize({| dependencies = items |})
                    Ok { Imported = combined; Manifest = manifest; SourcePath = ""
                         Fingerprint = SnapshotFingerprint.value fingerprint; IsEmbedded = true; Members = members })

    let private loadMember (manifest: string) (root: JsonElement) sourceOverride: Result<PinnedDependencySelection, AuthoringSessionError> =
        let fields = root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toList
        let embeddedKey = fields |> List.tryFind (fun name -> name = "snapshotJson" || name = "snapshotRecords")
        let embedded = fields = [ "fingerprint"; "path"; "snapshotId"; Option.defaultValue "" embeddedKey ]
        if not embedded && fields <> [ "fingerprint"; "path"; "snapshotId" ] then
            Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest.")
        else
            let sourcePath = root.GetProperty("path").GetString()
            let fingerprint = root.GetProperty("fingerprint").GetString()
            let snapshotId = root.GetProperty("snapshotId").GetString()
            if String.IsNullOrWhiteSpace sourcePath || String.IsNullOrWhiteSpace fingerprint
               || String.IsNullOrWhiteSpace snapshotId then
                Error(AuthoringSessionError.DependencyFailure "Dependency manifest fields must be non-blank strings.")
            else
                let selectedPath = sourceOverride |> Option.map SessionStorage.fullPath |> Option.defaultValue sourcePath
                let imported =
                    match sourceOverride with
                    | None when embedded ->
                        let source = root.GetProperty(embeddedKey.Value).GetString()
                        if String.IsNullOrWhiteSpace source then
                            Error(AuthoringSessionError.DependencyFailure "The embedded dependency is missing.")
                        else
                            // The key marks the embedded form; `path` is provenance only.
                            SessionStorage.decodeCompleteDependency (embeddedKey.Value = "snapshotRecords") source
                    | _ when not (Path.IsPathFullyQualified selectedPath) || not (File.Exists selectedPath) ->
                        Error(AuthoringSessionError.DependencyFailure "The selected dependency is unavailable. Use pin with an exact copy of the original version to recover a legacy selection.")
                    | _ -> SessionStorage.readCompleteDependency selectedPath
                imported |> Result.bind (fun imported ->
                    match SnapshotFingerprint.compute imported with
                    | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
                    | Ok actual when SnapshotFingerprint.value actual = fingerprint
                                     && StableId.value imported.Id = snapshotId ->
                        Ok { Imported = imported; Manifest = manifest; SourcePath = selectedPath
                             Fingerprint = fingerprint; IsEmbedded = embedded; Members = [] }
                    | Ok _ -> Error(AuthoringSessionError.DependencyFailure "The selected dependency version changed. Pin only accepts the exact previously selected content, not an upgrade."))

    let loadFrom (manifestPath: string) sourceOverride: Result<PinnedDependencySelection option, AuthoringSessionError> =
        // Read directly: File.Exists treats permission failures as absence.
        try
            let manifest = File.ReadAllText(manifestPath, Encoding.UTF8)
            use document = JsonDocument.Parse(manifest)
            let root = document.RootElement
            match root.TryGetProperty("dependencies") with
            | true, members when root.EnumerateObject() |> Seq.length = 1 ->
                // A dependency set stores every member's exact content; there is no legacy form.
                if Option.isSome sourceOverride then
                    Error(AuthoringSessionError.DependencyFailure "Pin with a source applies only to a single legacy dependency.")
                elif members.ValueKind <> JsonValueKind.Array || members.GetArrayLength() < 2 then
                    Error(AuthoringSessionError.DependencyFailure "A dependency set lists at least two dependencies.")
                else
                    members.EnumerateArray()
                    |> Seq.toList
                    |> List.fold (fun state item ->
                        state |> Result.bind (fun loaded ->
                            if item.ValueKind <> JsonValueKind.Object || not (fst (item.TryGetProperty "snapshotJson") || fst (item.TryGetProperty "snapshotRecords")) then
                                Error(AuthoringSessionError.DependencyFailure "Every member of a dependency set must embed its content.")
                            else loadMember (item.GetRawText()) item None |> Result.map (fun selected -> loaded @ [ selected ]))) (Ok [])
                    |> Result.bind combine
                    |> Result.map (fun combined -> Some { combined with Manifest = manifest })
            | _ -> loadMember manifest root sourceOverride |> Result.map Some
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Ok None
        | :? JsonException
        | :? InvalidOperationException
        | :? KeyNotFoundException
        | :? ArgumentException
        | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency manifest or source path.")
        | :? UnauthorizedAccessException as error -> Error(AuthoringSessionError.DependencyFailure error.Message)
        | :? IOException as error -> Error(AuthoringSessionError.DependencyFailure error.Message)

    let load path sourceOverride = loadFrom (path + ".dependency.json") sourceOverride

    let persistPin path source selected =
        embed selected.SourcePath selected.Imported
        |> Result.bind (fun pinned ->
            // Only the manifest is replaced. The graph and its digest marker are
            // unchanged; a failed replacement leaves the legacy selection intact.
            match SessionStorage.readText path, SessionStorage.readText (path + ".dependency.json") with
            | Ok current, Ok manifest when current = source && manifest = selected.Manifest ->
                SessionStorage.replaceAtomically (path + ".dependency.json") pinned.Manifest
                |> Result.map (fun () -> pinned)
            | Error error, _
            | _, Error error -> Error error
            | _ -> Error AuthoringSessionError.ExternalModification)

    /// Pins one source for the workspace at `localPath`. A source that has its own pinned
    /// dependencies is flattened into its complete graph; its sidecar already retains exact,
    /// flattened content, so no further recursion is needed.
    let pinSource (localPath: string) (sourcePath: string) =
        try
            let sourcePath = SessionStorage.fullPath sourcePath
            if sourcePath = localPath || not (File.Exists sourcePath) then
                Error(AuthoringSessionError.DependencyFailure "The selected dependency is unavailable.")
            elif File.Exists(sourcePath + ".dependency.json") then
                load sourcePath None
                |> Result.bind (function
                    | None -> Error(AuthoringSessionError.DependencyFailure "The dependency's own dependency selection is unavailable.")
                    | Some nested ->
                        SessionStorage.readText sourcePath
                        |> Result.bind (SessionStorage.decodeAs sourcePath (Some nested.Imported))
                        |> Result.map (SnapshotCodec.resolveDependency nested.Imported)
                        |> Result.bind SessionStorage.ensureCompleteDependency)
                |> Result.bind (embed sourcePath)
            else
                SessionStorage.readCompleteDependency sourcePath
                |> Result.bind (embed sourcePath)
        with
        | :? ArgumentException
        | :? NotSupportedException -> Error(AuthoringSessionError.DependencyFailure "Invalid dependency path.")

    type UpgradePlan =
        { Selection: PinnedDependencySelection
          Local: Snapshot
          Preview: AuthoringDependencyUpgradePreview }

    /// The declarations an upgrade adds, removes or changes, exported ones first in each list: a
    /// private declaration cannot be referred to by a dependent, so its change is secondary
    /// information. A visibility change alone counts as a change.
    let private declarationChanges (before: Snapshot) (after: Snapshot) =
        let entries (snapshot: Snapshot) =
            (snapshot.Types |> List.map (fun item -> ("type", StableId.value item.Id), (box item, item.Exported)))
            @ (snapshot.Effects |> List.map (fun item -> ("effect", StableId.value item.Id), (box item, item.Exported)))
            @ (snapshot.Capabilities |> List.map (fun item -> ("capability", StableId.value item.Id), (box item, item.Exported)))
            @ (snapshot.Functions |> List.map (fun item -> ("function", StableId.value item.Id), (box item, item.Exported)))
            |> Map.ofList
        let oldEntries, newEntries = entries before, entries after
        let change ((kind, id), (_, exported)) = { Kind = kind; Id = id; Exported = exported }
        let exportedFirst (items: AuthoringDependencyChange list) = items |> List.sortBy (fun item -> not item.Exported)
        let added = newEntries |> Map.toList |> List.filter (fun (key, _) -> not (Map.containsKey key oldEntries)) |> List.map change |> exportedFirst
        let removed = oldEntries |> Map.toList |> List.filter (fun (key, _) -> not (Map.containsKey key newEntries)) |> List.map change |> exportedFirst
        let changed =
            newEntries |> Map.toList
            |> List.filter (fun (key, (item, _)) -> Map.tryFind key oldEntries |> Option.exists (fun (old, _) -> old <> item))
            |> List.map change
            |> exportedFirst
        added, removed, changed

    /// The validation issues of a local graph against a selection, plus one
    /// `ImportedDeclarationNotExported` issue per private imported declaration the local
    /// declarations refer to: an existing reference to a declaration the new version no longer
    /// exports makes the upgrade incompatible.
    let private compatibilityIssues (selection: PinnedDependencySelection) (local: Snapshot) =
        let resolved = SnapshotCodec.resolveDependency selection.Imported local
        SnapshotValidator.validate resolved
        @ (DeclarationVisibility.hiddenReferences selection.Imported resolved
           |> List.map (fun hidden ->
               { Code = DeclarationVisibility.notExportedCode
                 Message = DeclarationVisibility.describe [ hidden ] }))

    /// Plans replacing one pinned dependency (the only one, or the member whose SnapshotId is
    /// `replaces`) with the exact content of `sourcePath`, and validates the local graph against it.
    let planUpgrade (localPath: string) (localSource: string) (current: PinnedDependencySelection) (sourcePath: string) (replaces: string option) =
        let members = match current.Members with [] -> [ current ] | members -> members
        let target =
            match replaces, members with
            | None, [ single ] -> Ok single
            | None, _ -> Error(AuthoringSessionError.DependencyFailure "Name the dependency to replace by its SnapshotId when the workspace has several.")
            | Some snapshotId, _ ->
                members |> List.tryFind (fun item -> StableId.value item.Imported.Id = snapshotId)
                |> Option.map Ok
                |> Option.defaultValue (Error(AuthoringSessionError.DependencyFailure $"No selected dependency has SnapshotId '{snapshotId}'."))
        target
        |> Result.bind (fun target ->
            pinSource localPath sourcePath
            |> Result.bind (fun replacement ->
                if replacement.Fingerprint = target.Fingerprint then
                    Error(AuthoringSessionError.DependencyFailure "The source is the version already selected; use pin to retain or relocate it.")
                else
                    let upgraded = members |> List.map (fun item -> if item = target then replacement else item)
                    combine upgraded
                    |> Result.bind (fun selection ->
                        SessionStorage.decodeAs localPath (Some current.Imported) localSource
                        |> Result.map (fun local ->
                            let added, removed, changed = declarationChanges target.Imported replacement.Imported
                            { Selection = selection
                              Local = local
                              Preview =
                                { ReplacedSnapshotId = StableId.value target.Imported.Id
                                  ReplacedFingerprint = target.Fingerprint
                                  NewSnapshotId = StableId.value replacement.Imported.Id
                                  NewFingerprint = replacement.Fingerprint
                                  Added = added
                                  Removed = removed
                                  Changed = changed
                                  Issues = compatibilityIssues selection local } }))))

    type UpgradesPlan =
        { Selection: PinnedDependencySelection
          Local: Snapshot
          Preview: AuthoringDependencyUpgradesPreview }

    let planUpgrades (localPath: string) (localSource: string) (current: PinnedDependencySelection) (upgrades: AuthoringDependencyUpgrade list) =
        let members = match current.Members with [] -> [ current ] | members -> members
        let failure message = Error(AuthoringSessionError.DependencyFailure message)
        if List.isEmpty upgrades then failure "An upgrade batch needs at least one replacement."
        elif upgrades |> List.exists (fun item -> String.IsNullOrWhiteSpace item.Source || String.IsNullOrWhiteSpace item.Replaces) then
            failure "Every replacement needs a non-blank source and the current SnapshotId to replace."
        elif (upgrades |> List.map (fun item -> item.Replaces) |> List.distinct).Length <> upgrades.Length then
            failure "Each current dependency may be replaced only once."
        else
            upgrades
            |> List.fold (fun state request ->
                state |> Result.bind (fun replacements ->
                    match members |> List.tryFind (fun item -> StableId.value item.Imported.Id = request.Replaces) with
                    | None -> failure $"No selected dependency has SnapshotId '{request.Replaces}'."
                    | Some target ->
                        pinSource localPath request.Source
                        |> Result.bind (fun replacement ->
                            if replacement.Fingerprint = target.Fingerprint then
                                failure "The source is the version already selected; use pin to retain or relocate it."
                            else Ok(replacements @ [ target, replacement ])))) (Ok [])
            |> Result.bind (fun replacements ->
                let upgraded = members |> List.map (fun item ->
                    replacements |> List.tryFind (fun (target, _) -> target = item)
                    |> Option.map snd |> Option.defaultValue item)
                if (upgraded |> List.map (fun item -> item.Imported.Id) |> List.distinct).Length <> upgraded.Length then
                    failure "The final selection must have distinct dependency SnapshotIds."
                else
                    combine upgraded
                    |> Result.bind (fun selection ->
                        SessionStorage.decodeAs localPath (Some current.Imported) localSource
                        |> Result.map (fun local ->
                            let issues = compatibilityIssues selection local
                            let previews = replacements |> List.map (fun (target, replacement) ->
                                let added, removed, changed = declarationChanges target.Imported replacement.Imported
                                { ReplacedSnapshotId = StableId.value target.Imported.Id
                                  ReplacedFingerprint = target.Fingerprint
                                  NewSnapshotId = StableId.value replacement.Imported.Id
                                  NewFingerprint = replacement.Fingerprint
                                  Added = added; Removed = removed; Changed = changed; Issues = issues })
                            let added, removed, changed = declarationChanges current.Imported selection.Imported
                            // Bind the review to exact local bytes, the complete current manifest,
                            // workspace location and each target's exact selected content/source.
                            let review = JsonSerializer.Serialize(
                                {| version = 1; path = localPath; localSource = localSource; manifest = current.Manifest
                                   upgrades = replacements |> List.sortBy (fun (target, _) -> StableId.value target.Imported.Id)
                                       |> List.map (fun (target, replacement) ->
                                           {| replaces = StableId.value target.Imported.Id; manifest = replacement.Manifest |}) |})
                            { Selection = selection; Local = local
                              Preview = { ReviewFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes review)).ToLowerInvariant()
                                          Upgrades = previews
                                          Replaced = replacements |> List.map (fun (target, _) -> { SnapshotId = StableId.value target.Imported.Id; Fingerprint = target.Fingerprint })
                                          Selected = replacements |> List.map (fun (_, replacement) -> { SnapshotId = StableId.value replacement.Imported.Id; Fingerprint = replacement.Fingerprint })
                                          Added = added; Removed = removed; Changed = changed; Issues = issues } })))

    /// N-to-M selection changes validate only final imports, without inventing an old/new
    /// one-to-one mapping when consolidating or splitting libraries.
    let planSelectionUpgrade (localPath: string) (localSource: string) (current: PinnedDependencySelection) (request: AuthoringDependencySelectionUpgrade) =
        let members = match current.Members with [] -> [ current ] | members -> members
        let failure message = Error(AuthoringSessionError.DependencyFailure message)
        if List.isEmpty request.Sources || List.isEmpty request.Replaces then failure "A selection upgrade needs at least one source and one current SnapshotId to replace."
        elif request.Sources @ request.Replaces |> List.exists String.IsNullOrWhiteSpace then failure "Selection upgrade sources and targets must be non-blank."
        elif (List.distinct request.Replaces).Length <> request.Replaces.Length then failure "Each current dependency may be replaced only once."
        elif request.Replaces |> List.exists (fun target -> members |> List.exists (fun memberItem -> StableId.value memberItem.Imported.Id = target) |> not) then
            failure "Every replacement target must be a currently selected dependency SnapshotId."
        else
            request.Sources
            |> List.fold (fun state source -> state |> Result.bind (fun selected -> pinSource localPath source |> Result.map (fun item -> selected @ [ item ]))) (Ok [])
            |> Result.bind (fun selected ->
                let selected = selected |> List.sortBy (fun item -> item.SourcePath)
                let retained = members |> List.filter (fun item -> not (List.contains (StableId.value item.Imported.Id) request.Replaces))
                let upgraded = retained @ selected
                let identities items = items |> List.map (fun item -> StableId.value item.Imported.Id, item.Fingerprint) |> List.sort
                if (upgraded |> List.map (fun item -> item.SourcePath) |> List.distinct).Length <> upgraded.Length
                   || (upgraded |> List.map (fun item -> item.Imported.Id) |> List.distinct).Length <> upgraded.Length then
                    failure "Each final dependency source and SnapshotId must be selected only once."
                elif identities upgraded = identities members then failure "The final selection is already pinned; use pin to retain or relocate it."
                else
                    combine upgraded
                    |> Result.bind (fun selection ->
                        SessionStorage.decodeAs localPath (Some current.Imported) localSource
                        |> Result.map (fun local ->
                            let issues = compatibilityIssues selection local
                            let added, removed, changed = declarationChanges current.Imported selection.Imported
                            let replaced = members |> List.filter (fun item -> List.contains (StableId.value item.Imported.Id) request.Replaces)
                            let describe item = { SnapshotId = StableId.value item.Imported.Id; Fingerprint = item.Fingerprint }
                            let review = JsonSerializer.Serialize(
                                {| version = 2; path = localPath; localSource = localSource; manifest = current.Manifest
                                   replaces = request.Replaces |> List.sort
                                   selected = selected |> List.sortBy (fun item -> item.SourcePath) |> List.map (fun item -> item.Manifest) |})
                            { Selection = selection; Local = local
                              Preview = { ReviewFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes review)).ToLowerInvariant()
                                          Upgrades = []; Replaced = List.map describe replaced; Selected = List.map describe selected
                                          Added = added; Removed = removed; Changed = changed; Issues = issues } })))

    /// The declarations a dependent may see: its own, and the exported ones of its dependency.
    /// A dependency's private declarations stay in the resolved graph (an exported function may
    /// call one) but leave name resolution, `declarations` and new references.
    let private visible (selection: PinnedDependencySelection option) (declarations: AuthoringDeclarations) =
        match selection with
        | None -> declarations
        | Some current ->
            let imported = current.Imported
            let hiddenFunctions = imported.Functions |> List.filter (fun item -> not item.Exported) |> List.map (fun item -> item.Id) |> Set.ofList
            let hiddenTypes = imported.Types |> List.filter (fun item -> not item.Exported) |> List.map (fun item -> item.Id) |> Set.ofList
            let hiddenEffects = imported.Effects |> List.filter (fun item -> not item.Exported) |> List.map (fun item -> item.Id) |> Set.ofList
            let hiddenCapabilities = imported.Capabilities |> List.filter (fun item -> not item.Exported) |> List.map (fun item -> item.Id) |> Set.ofList
            { declarations with
                Functions = declarations.Functions |> List.filter (fun item -> not (Set.contains item.Id hiddenFunctions))
                Types = declarations.Types |> List.filter (fun item -> not (Set.contains item.Id hiddenTypes))
                Effects = declarations.Effects |> List.filter (fun item -> not (Set.contains item.Id hiddenEffects))
                Capabilities = declarations.Capabilities |> List.filter (fun item -> not (Set.contains item.Id hiddenCapabilities)) }

    let withProvenance selection (declarations: AuthoringDeclarations) =
        let declarations = visible selection declarations
        let entries (functions: SemanticFunction list) (types: SemanticType list) (effects: SemanticEffect list) (capabilities: SemanticCapability list) =
            (functions |> List.map (fun f -> "function", StableId.value f.Id))
            @ (types |> List.map (fun t -> "type", StableId.value t.Id))
            @ (effects |> List.map (fun e -> "effect", StableId.value e.Id))
            @ (capabilities |> List.map (fun c -> "capability", StableId.value c.Id))
        let members = selection |> Option.map (fun current -> match current.Members with [] -> [ current ] | members -> members) |> Option.defaultValue []
        let sources =
            members |> List.map (fun memberItem ->
                let imported = memberItem.Imported
                Set.ofList (entries imported.Functions imported.Types imported.Effects imported.Capabilities),
                { SnapshotId = StableId.value imported.Id; Fingerprint = memberItem.Fingerprint })
        let provenance =
            entries declarations.Functions declarations.Types declarations.Effects declarations.Capabilities
            |> List.map (fun (kind, id) ->
                let providing = sources |> List.choose (fun (ids, source) -> if Set.contains (kind, id) ids then Some source else None)
                { Kind = kind; Id = id; IsLocal = List.isEmpty providing; Sources = providing })
        { declarations with Provenance = provenance }

    let planChange path source current = function
        | AuthoringDependencyChangeRequest.Upgrades upgrades -> planUpgrades path source current upgrades
        | AuthoringDependencyChangeRequest.Selection request -> planSelectionUpgrade path source current request

type AuthoringSession internal (path: string, initialWorkspace: AgentWorkspace, initialPersistedSource: string, lockHandle: FileStream, selection: PinnedDependencySelection option) =
    let mutable selection = selection
    let mutable dependency = selection |> Option.map (fun item -> item.Imported)
    let mutable dependencyManifest = selection |> Option.map (fun item -> item.Manifest)
    let proposals = Dictionary<string, TransactionId * PendingProposal>(StringComparer.Ordinal)
    // The registered review and the snapshot it was simulated against, for review pages.
    let registered = Dictionary<string, AuthoringProposal * SnapshotId>(StringComparer.Ordinal)
    let mutable workspace = initialWorkspace
    let mutable persistedSource = initialPersistedSource
    let mutable invalidated = false
    let mutable closed = false
    // Runs compiled runPure programs; it starts with the first run and dies with the session.
    let worker = new PureRunWorker()
    // Entry projections by workspace object; an entry's program dies with the workspace state it was projected from.
    let entryProjections = System.Runtime.CompilerServices.ConditionalWeakTable<AgentWorkspace, Dictionary<FunctionId, Result<ExecutionProgram, AuthoringSessionError>>>()

    let ensureUsable () =
        if closed then Error AuthoringSessionError.SessionClosed
        elif invalidated then Error AuthoringSessionError.SessionInvalidated
        else Ok()

    let mutable importedFunctionIds =
        dependency |> Option.map (fun snapshot -> snapshot.Functions |> List.map (fun item -> item.Id) |> Set.ofList)
        |> Option.defaultValue Set.empty
    let mutable importedTypeIds =
        dependency |> Option.map (fun snapshot -> snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList)
        |> Option.defaultValue Set.empty

    /// Registers a proposal simulated against `imported` (the current dependency, or the new
    /// selection of a dependency upgrade). Edits of imported declarations are rejected as
    /// `ImportedDeclarationModified`; a local reference to a private imported declaration is
    /// rejected as `ImportedDeclarationNotExported`, so a dependent uses only what its
    /// dependency exports.
    let registerAgainst (imported: Snapshot option) (proposedFunctions: SemanticFunction list) transactionId pending review =
        let functions, types = ProposalReview.touched review
        // An operations proposal keeps its candidate out of the review pages, but the check reads it.
        let candidate =
            match pending with
            | PendingProposal.Operations proposal -> Some(AgentChangeProposal.proposedSnapshot proposal)
            | other -> PendingProposal.proposedSnapshot other
        let hiddenReferences =
            match imported, candidate with
            | Some imported, Some candidate -> DeclarationVisibility.hiddenReferences imported candidate
            | _ -> []
        let changesImportedDeclaration =
            match review, imported with
            | AuthoringProposalReview.Operations item, Some imported ->
                (item.ResultingChange.Diff.EffectsRemoved |> List.exists (fun removed ->
                    imported.Effects |> List.exists (fun effect -> effect.Id = removed.EffectId)))
                || (item.ResultingChange.Diff.VisibilityChanges |> List.exists (fun change ->
                    (change.Kind = "effect" && imported.Effects |> List.exists (fun effect -> StableId.value effect.Id = change.Id))
                    || (change.Kind = "capability" && imported.Capabilities |> List.exists (fun capability -> StableId.value capability.Id = change.Id))))
                || (item.ResultingChange.Diff.CapabilitiesRemoved |> List.exists (fun removed ->
                    imported.Capabilities |> List.exists (fun capability -> capability.Id = removed.CapabilityId)))
                || (item.ResultingChange.Diff.CapabilityOperationsRemoved |> List.exists (fun removed ->
                    imported.Capabilities |> List.exists (fun capability -> capability.Id = removed.OwnerCapabilityId)))
                || (item.ResultingChange.Diff.CapabilityOperationsAdded |> List.exists (fun added ->
                    imported.Capabilities |> List.exists (fun capability -> capability.Id = added.OwnerCapabilityId)))
                // Renamed type members and parameters are covered by the touched types and functions.
                || (item.ResultingChange.Diff.Renames |> List.exists (function
                    | SymbolRename.EffectRenamed(effectId, _, _) -> imported.Effects |> List.exists (fun effect -> effect.Id = effectId)
                    | SymbolRename.CapabilityRenamed(capabilityId, _, _)
                    | SymbolRename.CapabilityOperationRenamed(capabilityId, _, _, _)
                    | SymbolRename.CapabilityParameterRenamed(capabilityId, _, _, _, _) ->
                        imported.Capabilities |> List.exists (fun capability -> capability.Id = capabilityId)
                    | _ -> false))
            | AuthoringProposalReview.CapabilityOperationSignatureMigration item, Some imported ->
                imported.Capabilities |> List.exists (fun c -> c.Id = item.ResultingChange.CapabilityId)
            | _ -> false
        if functions |> List.exists (fun id -> Set.contains id importedFunctionIds)
           || types |> List.exists (fun id -> Set.contains id importedTypeIds)
           || changesImportedDeclaration then
            Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot
                [ { Code = "ImportedDeclarationModified"; Message = "Dependency declarations are read-only; no proposal was registered." } ]))
        elif not (List.isEmpty hiddenReferences) then
            Error(AuthoringSessionError.SnapshotCodecFailure(SnapshotCodecError.InvalidSnapshot
                [ { Code = DeclarationVisibility.notExportedCode
                    Message = DeclarationVisibility.describe hiddenReferences + " No proposal was registered." } ]))
        else
            let proposalId = StableId.value transactionId
            proposals.Add(proposalId, (transactionId, pending))
            let rewritten =
                functions |> List.distinct |> List.choose (fun id -> proposedFunctions |> List.tryFind (fun item -> item.Id = id))
            let proposal = { ProposalId = proposalId; Review = review; RewrittenFunctions = rewritten }
            registered.Add(proposalId, (proposal, AgentWorkspace.currentSnapshotId workspace))
            Ok proposal

    let registerWith proposedFunctions transactionId pending review = registerAgainst dependency proposedFunctions transactionId pending review

    let register transactionId pending review = registerWith [] transactionId pending review

    let dependencyStillPinned () =
        match selection with
        | Some selected when not selected.IsEmbedded ->
            if not (File.Exists selected.SourcePath) then Ok false else
            match SessionStorage.readCompleteDependency selected.SourcePath with
            // A read failure is transient: only content that was read and differs is a change.
            | Error(AuthoringSessionError.IoFailure _ as error) -> Error error
            | Error _ -> Ok false
            | Ok current ->
                match SnapshotFingerprint.compute current with
                | Ok actual -> Ok(SnapshotFingerprint.value actual = selected.Fingerprint && current.Id = selected.Imported.Id)
                | _ -> Ok false
        // Embedded content was validated on open; the exact manifest is checked
        // again at publication. The original source is provenance, not authority.
        | _ -> Ok true

    let checkPersistedSource () =
        let sidecar = path + ".dependency.json"
        let selectionUnchanged =
            match dependencyManifest with
            | None -> Ok true
            | Some _ when not (File.Exists sidecar) -> Ok false
            | Some expected -> SessionStorage.readText sidecar |> Result.map (fun actual -> actual = expected)
        match selectionUnchanged |> Result.bind (fun unchanged -> if unchanged then dependencyStillPinned () else Ok false) with
        | Error error -> Error error
        | Ok false ->
            invalidated <- true
            Error(AuthoringSessionError.DependencyFailure "The dependency selection changed outside this session.")
        | Ok true when not (File.Exists path) ->
            invalidated <- true
            Error AuthoringSessionError.ExternalModification
        | Ok true ->
            match SessionStorage.readText path with
            | Error error -> Error error
            | Ok currentSource when currentSource <> persistedSource ->
                invalidated <- true
                Error AuthoringSessionError.ExternalModification
            | Ok _ -> Ok()

    let persistCandidate candidate =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            let serialized =
                match dependency with
                | None -> AgentWorkspace.serializeCurrentSnapshot candidate
                | Some imported -> AgentWorkspace.serializeWithDependency imported candidate
            match serialized |> Result.map (SessionStorage.encode path) with
            | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
            | Ok candidateSource ->
                match checkPersistedSource () with
                | Error error -> Error error
                | Ok() ->
                    match SessionStorage.replaceAtomically path candidateSource with
                    | Error error -> Error error
                    | Ok() ->
                        workspace <- candidate
                        persistedSource <- candidateSource
                        Ok()

    let commitCandidate proposalId transactionId publishedSnapshotId readiness candidate =
        match persistCandidate candidate with
        | Error error -> Error error
        | Ok() ->
            proposals.Remove proposalId |> ignore
            registered.Remove proposalId |> ignore
            Ok { TransactionId = transactionId; PublishedSnapshotId = publishedSnapshotId; Readiness = readiness }

    member _.Path = path
    /// Whether `runPure`/`runPureByName` may compile and run host C# sources (`hostSources` and
    /// `hostCapability` bindings). Off by default; the session process enables it only for the
    /// `--allow-host-sources` startup flag, never per request, because host code runs with the
    /// user's permissions.
    member val HostSourcesAllowed = false with get, set
    member _.DependencySelection = selection |> Option.map PinnedDependency.describe

    /// Reviews replacing a pinned dependency with a new version without changing anything.
    member _.PreviewDependencyUpgrade(sourcePath: string, replaces: string option): Result<AuthoringDependencyUpgradePreview, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())
        |> Result.bind (fun () ->
            match selection with
            | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
            | Some current ->
                PinnedDependency.planUpgrade path persistedSource current sourcePath replaces
                |> Result.map (fun plan -> plan.Preview))

    member _.PreviewDependencyUpgrades(upgrades: AuthoringDependencyUpgrade list): Result<AuthoringDependencyUpgradesPreview, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())
        |> Result.bind (fun () ->
            match selection with
            | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
            | Some current -> PinnedDependency.planUpgrades path persistedSource current upgrades |> Result.map (fun plan -> plan.Preview))

    member _.PreviewDependencySelectionUpgrade(request: AuthoringDependencySelectionUpgrade): Result<AuthoringDependencyUpgradesPreview, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())
        |> Result.bind (fun () ->
            match selection with
            | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
            | Some current -> PinnedDependency.planSelectionUpgrade path persistedSource current request |> Result.map (fun plan -> plan.Preview))

    member _.DependencyUpgradeDeclarations(request: AuthoringDependencyChangeRequest): Result<AuthoringDeclarations, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())
        |> Result.bind (fun () ->
            match selection with
            | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
            | Some current ->
                PinnedDependency.planChange path persistedSource current request
                |> Result.map (fun plan ->
                    let combined = SnapshotCodec.resolveDependency plan.Selection.Imported plan.Local
                    { Functions = combined.Functions; Types = combined.Types; Capabilities = combined.Capabilities; Effects = combined.Effects; Provenance = [] }
                    |> PinnedDependency.withProvenance (Some plan.Selection)))

    member _.SimulateDependencyUpgrade(request: AuthoringDependencyChangeRequest, repairs: AgentDependencyLocalRepairs, expectedSnapshotId: SnapshotId): Result<AuthoringProposal, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())
        |> Result.bind (fun () ->
            if expectedSnapshotId <> AgentWorkspace.currentSnapshotId workspace then
                Error(AuthoringSessionError.DependencyFailure "The dependency repair targets a different base snapshot.")
            else
                match selection with
                | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade.")
                | Some current ->
                    PinnedDependency.planChange path persistedSource current request
                    |> Result.bind (fun plan ->
                        AgentDependencyUpgradeWorkflow.simulate workspace current.Imported plan.Selection.Imported repairs
                        |> Result.mapError AuthoringSessionError.StructuralSimulationFailure
                        |> Result.bind (fun proposal ->
                            let review = AgentDependencyUpgradeProposal.review proposal
                            let proposed = AgentDependencyUpgradeProposal.proposedSnapshot proposal
                            let provenance = PinnedDependency.withProvenance (Some plan.Selection) { Functions = proposed.Functions; Types = proposed.Types; Effects = proposed.Effects; Capabilities = proposed.Capabilities; Provenance = [] }
                            registerAgainst (Some plan.Selection.Imported) review.ChangedFunctions review.Receipt.TransactionId
                                (PendingProposal.DependencyUpgrade(proposal, request, plan.Preview.ReviewFingerprint, provenance.Provenance))
                                (AuthoringProposalReview.DependencyUpgrade { Upgrade = plan.Preview; Repair = review }))))

    /// Read-only execution must not run a stale in-memory graph after the selected
    /// snapshot or its pinned dependency was changed outside the session.
    member _.VerifyPersistedSource(): Result<unit, AuthoringSessionError> =
        ensureUsable () |> Result.bind (fun () -> checkPersistedSource ())

    /// The current snapshot's id, without computing the fingerprints that `Status` reports.
    member _.SnapshotId: SnapshotId = AgentWorkspace.currentSnapshotId workspace

    member _.Status(): Result<AuthoringSessionStatus, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match AgentWorkspace.currentSnapshotFingerprint workspace with
        | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
        | Ok snapshotFingerprint ->
            match AgentWorkspace.currentSemanticFingerprint workspace with
            | Error error -> Error(AuthoringSessionError.SnapshotCodecFailure error)
            | Ok semanticFingerprint ->
                Ok
                    { SnapshotId = AgentWorkspace.currentSnapshotId workspace
                      SnapshotFingerprint = snapshotFingerprint
                      SemanticFingerprint = semanticFingerprint
                      PendingProposalCount = proposals.Count }

    member _.Declarations(): AuthoringDeclarations =
        { Functions = AgentWorkspace.listFunctions workspace
          Types = AgentWorkspace.listTypes workspace
          Effects = AgentWorkspace.listEffects workspace
          Capabilities = AgentWorkspace.listCapabilities workspace
          Provenance = [] } |> PinnedDependency.withProvenance selection

    member _.FocusFunction(functionId) = AgentWorkspace.focusFunction workspace functionId
    member _.GetOpenObligations(functionId) = AgentWorkspace.getOpenObligations workspace functionId

    /// Every site of the current snapshot that refers to a declaration or one of its members
    /// (the `references` operation), so the sites can be repaired before a deletion or removal.
    member _.References(subject: ReferenceSubject) = AgentWorkspace.references workspace subject

    /// The pending proposals of this session (the `proposals` operation), in proposal-ID order.
    member this.PendingProposals(): AuthoringPendingProposalSummary list =
        let declarations = this.Declarations()
        let current = AgentWorkspace.currentSnapshotId workspace
        registered
        |> Seq.map (fun entry ->
            let proposal, baseSnapshotId = entry.Value
            { ProposalId = proposal.ProposalId
              Kind = ProposalSummary.kind proposal.Review
              BaseSnapshotId = baseSnapshotId
              BaseIsCurrent = (baseSnapshotId = current)
              Summary = ProposalSummary.describe declarations proposal.Review })
        |> Seq.sortBy (fun item -> item.ProposalId)
        |> Seq.toList

    /// `expectedBaseSnapshotId` defaults to the current snapshot; a different one is rejected as stale.
    member _.SimulateOperations(operations: AgentOperationIntent list, ?expectedBaseSnapshotId: SnapshotId): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            let expectedBase = defaultArg expectedBaseSnapshotId (AgentWorkspace.currentSnapshotId workspace)
            match AgentWorkflow.simulateFromBase workspace expectedBase operations with
            | Error error -> Error(AuthoringSessionError.WorkflowSimulationFailure error)
            | Ok proposal ->
                register (AgentChangeProposal.transactionId proposal) (PendingProposal.Operations proposal) (AuthoringProposalReview.Operations(AgentChangeProposal.review proposal))

    /// `SimulateOperations` for a batch the wire layer read: `addUnionVariant` arm recipes are
    /// materialized in their matches' scopes against the current snapshot first, and a recipe that
    /// does not bind or type-check rejects the whole batch as a simulation rejection.
    member this.SimulateOperationRequests(requests: AuthoringOperationRequest list, ?expectedBaseSnapshotId: SnapshotId): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            let rec prepare acc remaining =
                match remaining with
                | [] -> Ok(List.rev acc)
                | AuthoringOperationRequest.Intent intent :: rest -> prepare (intent :: acc) rest
                | AuthoringOperationRequest.AddUnionVariant(targetTypeId, variant, arms) :: rest ->
                    match AgentUnionVariantArmPlans.prepare workspace targetTypeId variant arms with
                    | Error diagnostic -> Error(AuthoringSessionError.WorkflowSimulationFailure(AgentWorkflowError.SimulationRejected [ diagnostic ]))
                    | Ok intent -> prepare (AgentOperationIntent.AddUnionVariant intent :: acc) rest
            match prepare [] requests with
            | Error error -> Error error
            | Ok operations -> this.SimulateOperations(operations, ?expectedBaseSnapshotId = expectedBaseSnapshotId)

    member _.SimulateReplaceBody(request: AgentReplaceFunctionBodyWithExpressionRecipe): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralChangeProposal.transactionId proposal) (PendingProposal.ReplaceBody proposal) (AuthoringProposalReview.ReplaceBody(AgentExpressionStructuralChangeProposal.review proposal))

    member _.SimulateReplaceTarget(request: AgentExpressionStructuralWorkflow.Targeted.Request): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.Targeted.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralWorkflow.Targeted.Proposal.transactionId proposal) (PendingProposal.ReplaceTarget proposal) (AuthoringProposalReview.ReplaceTarget(AgentExpressionStructuralWorkflow.Targeted.Proposal.review proposal))

    member _.SimulateCreateFunction(request: AgentExpressionStructuralWorkflow.FunctionCreation.Request): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                register (AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.transactionId proposal) (PendingProposal.CreateFunction proposal) (AuthoringProposalReview.CreateFunction(AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review proposal))

    member _.SimulateParameterTypeMigration(request: AgentParameterTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentParameterTypeMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentParameterTypeMigrationProposal.proposedFunctions proposal) (AgentParameterTypeMigrationProposal.transactionId proposal) (PendingProposal.ParameterTypeMigration proposal) (AuthoringProposalReview.ParameterTypeMigration(AgentParameterTypeMigrationProposal.review proposal))

    member _.SimulateResultTypeMigration(request: AgentResultTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentResultTypeMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentResultTypeMigrationProposal.proposedFunctions proposal) (AgentResultTypeMigrationProposal.transactionId proposal) (PendingProposal.ResultTypeMigration proposal) (AuthoringProposalReview.ResultTypeMigration(AgentResultTypeMigrationProposal.review proposal))

    member _.SimulateProductFieldTypeMigration(request: AgentProductFieldTypeMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentProductFieldTypeMigrationWorkflow.simulate workspace request with
            | Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                when diagnostics |> List.exists (fun d -> d.Code = "FieldTypeMigrationRewriteMismatch") ->
                let owner = AgentWorkspace.listTypes workspace |> List.find (fun t ->
                    match t.Kind with
                    | SemanticTypeKind.Product fields -> fields |> List.exists (fun f -> f.Id = request.FieldId)
                    | _ -> false)
                let expected = AgentProductFieldTypeMigrationWorkflow.affectedFunctions (AgentWorkspace.listFunctions workspace) owner.Id request.FieldId |> List.map (fun fn -> fn.Id) |> Set.ofList
                let requested = request.FunctionRewrites |> List.map (fun r -> r.FunctionId) |> Set.ofList
                Error(AuthoringSessionError.FieldTypeMigrationRepairMismatch(
                    diagnostics, Set.difference expected requested |> Set.toList, Set.difference requested expected |> Set.toList))
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentProductFieldTypeMigrationProposal.proposedFunctions proposal) (AgentProductFieldTypeMigrationProposal.transactionId proposal) (PendingProposal.ProductFieldTypeMigration proposal) (AuthoringProposalReview.ProductFieldTypeMigration(AgentProductFieldTypeMigrationProposal.review proposal))

    member _.SimulateCapabilityOperationSignatureMigration(request: AgentCapabilityOperationSignatureMigrationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentCapabilityOperationSignatureMigrationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith (AgentCapabilityOperationSignatureMigrationProposal.proposedFunctions proposal) (AgentCapabilityOperationSignatureMigrationProposal.transactionId proposal) (PendingProposal.CapabilityOperationSignatureMigration proposal) (AuthoringProposalReview.CapabilityOperationSignatureMigration(AgentCapabilityOperationSignatureMigrationProposal.review proposal))

    member _.SimulateCreateType(request: AgentCreateTypeDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentTypeDeclarationProposal.transactionId proposal) (PendingProposal.CreateType proposal) (AuthoringProposalReview.CreateType(AgentTypeDeclarationProposal.review proposal))

    member _.SimulateCreateTypeGroup(request: AgentCreateTypeDeclarationGroupRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentTypeDeclarationWorkflow.simulateGroup workspace request with
            | Error error -> Error(AuthoringSessionError.TypeDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentTypeDeclarationGroupProposal.transactionId proposal) (PendingProposal.CreateTypeGroup proposal) (AuthoringProposalReview.CreateTypeGroup(AgentTypeDeclarationGroupProposal.review proposal))

    member _.SimulateAddUnionVariantLike(request: AgentAddUnionVariantLikeRequest, additionalFields: AgentTypeFieldIntent list): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentUnionVariantEvolutionWorkflow.simulateWithFields workspace request additionalFields with
            | Error error -> Error(AuthoringSessionError.StructuralSimulationFailure error)
            | Ok proposal ->
                registerWith
                    (AgentUnionVariantEvolutionProposal.proposedFunctions proposal)
                    (AgentUnionVariantEvolutionProposal.transactionId proposal)
                    (PendingProposal.UnionVariantEvolution proposal)
                    (AuthoringProposalReview.UnionVariantEvolution(
                        AgentUnionVariantEvolutionProposal.review proposal
                    ))

    member _.SimulateCreateEffect(request: AgentCreateEffectDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentEffectDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.EffectDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentEffectDeclarationProposal.transactionId proposal) (PendingProposal.CreateEffect proposal) (AuthoringProposalReview.CreateEffect(AgentEffectDeclarationProposal.review proposal))

    member _.SimulateCreateCapability(request: AgentCreateCapabilityDeclarationRequest): Result<AuthoringProposal, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match AgentCapabilityDeclarationWorkflow.simulate workspace request with
            | Error error -> Error(AuthoringSessionError.CapabilityDeclarationSimulationFailure error)
            | Ok proposal ->
                register (AgentCapabilityDeclarationProposal.transactionId proposal) (PendingProposal.CreateCapability proposal) (AuthoringProposalReview.CreateCapability(AgentCapabilityDeclarationProposal.review proposal))

    member _.Commit(proposalId: string, policy: CommitPolicy): Result<AuthoringCommitReceipt, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId with
            | false, _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)
            | true, (_, PendingProposal.DependencyUpgrade(proposal, request, reviewed, _)) ->
                match selection with
                | None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency selection.")
                | Some current ->
                    checkPersistedSource ()
                    |> Result.bind (fun () -> PinnedDependency.planChange path persistedSource current request)
                    |> Result.bind (fun plan ->
                        if plan.Preview.ReviewFingerprint <> reviewed then
                            Error(AuthoringSessionError.DependencyFailure "The workspace, pins, targets or selected source changed after dependency repair review. Simulate again.")
                        else
                            AgentDependencyUpgradeWorkflow.commitWithPolicy workspace proposal policy
                            |> Result.mapError AuthoringSessionError.CommitRejected
                            |> Result.bind (fun candidate ->
                                AgentWorkspace.serializeWithDependency plan.Selection.Imported candidate
                                |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
                                |> Result.map (SessionStorage.encode path)
                                |> Result.bind (fun source ->
                                    checkPersistedSource () |> Result.bind (fun () ->
                                        SessionStorage.replaceDependencyPair path persistedSource current.Manifest source plan.Selection.Manifest)
                                    |> Result.map (fun () ->
                                        workspace <- candidate
                                        persistedSource <- source
                                        selection <- Some plan.Selection
                                        dependency <- Some plan.Selection.Imported
                                        dependencyManifest <- Some plan.Selection.Manifest
                                        importedFunctionIds <- plan.Selection.Imported.Functions |> List.map (fun f -> f.Id) |> Set.ofList
                                        importedTypeIds <- plan.Selection.Imported.Types |> List.map (fun t -> t.Id) |> Set.ofList
                                        proposals.Remove proposalId |> ignore
                                        registered.Remove proposalId |> ignore
                                        let review = AgentDependencyUpgradeProposal.review proposal
                                        { TransactionId = review.Receipt.TransactionId; PublishedSnapshotId = review.Receipt.ProposedSnapshotId; Readiness = review.Readiness }))))
            | true, (_, PendingProposal.Operations proposal) ->
                match AgentWorkflow.commitWithPolicy workspace proposal policy with
                | AgentCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ReplaceBody proposal) ->
                match AgentExpressionStructuralWorkflow.commitWithPolicy workspace proposal policy with
                | AgentStructuralCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentStructuralCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ReplaceTarget proposal) ->
                match AgentExpressionStructuralWorkflow.Targeted.commitWithPolicy workspace proposal policy with
                | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentExpressionStructuralWorkflow.Targeted.CommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateFunction proposal) ->
                match AgentExpressionStructuralWorkflow.FunctionCreation.commitWithPolicy workspace proposal policy with
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ParameterTypeMigration proposal) ->
                match AgentParameterTypeMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentParameterTypeMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentParameterTypeMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ResultTypeMigration proposal) ->
                match AgentResultTypeMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentResultTypeMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentResultTypeMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.ProductFieldTypeMigration proposal) ->
                match AgentProductFieldTypeMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentProductFieldTypeMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentProductFieldTypeMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CapabilityOperationSignatureMigration proposal) ->
                match AgentCapabilityOperationSignatureMigrationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentCapabilityOperationSignatureMigrationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentCapabilityOperationSignatureMigrationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateType proposal) ->
                match AgentTypeDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentTypeDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentTypeDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateTypeGroup proposal) ->
                match AgentTypeDeclarationWorkflow.commitGroupWithPolicy workspace proposal policy with
                | AgentTypeDeclarationGroupCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentTypeDeclarationGroupCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.UnionVariantEvolution proposal) ->
                match AgentUnionVariantEvolutionWorkflow.commitWithPolicy workspace proposal policy with
                | AgentUnionVariantEvolutionCommitResult.Rejected diagnostics ->
                    Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentUnionVariantEvolutionCommitResult.Committed(candidate, publication) ->
                    commitCandidate
                        proposalId
                        publication.TransactionId
                        publication.PublishedSnapshotId
                        publication.Readiness
                        candidate
            | true, (_, PendingProposal.CreateEffect proposal) ->
                match AgentEffectDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentEffectDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentEffectDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate
            | true, (_, PendingProposal.CreateCapability proposal) ->
                match AgentCapabilityDeclarationWorkflow.commitWithPolicy workspace proposal policy with
                | AgentCapabilityDeclarationCommitResult.Rejected diagnostics -> Error(AuthoringSessionError.CommitRejected diagnostics)
                | AgentCapabilityDeclarationCommitResult.Committed(candidate, publication) ->
                    commitCandidate proposalId publication.TransactionId publication.PublishedSnapshotId publication.Readiness candidate

    /// Read-only: the review of a pending proposal and the snapshots a human review page renders.
    member _.PendingProposalView(proposalId: string): Result<AuthoringPendingProposalView, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId, registered.TryGetValue proposalId with
            | (true, (transactionId, pending)), (true, (proposal, baseSnapshotId)) ->
                let touched, touchedTypes = ProposalReview.touched proposal.Review
                let signatureOwners =
                    match proposal.Review with
                    | AuthoringProposalReview.CreateFunction item -> [ item.CreatedFunction.Id ]
                    | AuthoringProposalReview.ParameterTypeMigration item -> [ item.ResultingChange.ParameterChange.FunctionId ]
                    | AuthoringProposalReview.ResultTypeMigration item -> [ item.ResultingChange.ResultChange.FunctionId ]
                    | _ -> []
                Ok { TransactionId = transactionId
                     Proposal = proposal
                     BaseSnapshotId = baseSnapshotId
                     BaseIsCommitted = (AgentWorkspace.currentSnapshotId workspace = baseSnapshotId)
                     ProposedSnapshot = PendingProposal.proposedSnapshot pending
                     FunctionIds = signatureOwners @ touched |> List.distinct
                     TypeIds =
                        match proposal.Review with
                        | AuthoringProposalReview.CreateType item -> [ item.CreatedType.Id ]
                        | AuthoringProposalReview.CreateTypeGroup item -> item.CreatedTypes |> List.map (fun typeEntity -> typeEntity.Id)
                        | _ -> touchedTypes
                        |> List.distinct }
            | _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)

    member _.Reject(proposalId: string): Result<AuthoringRejectionReceipt, AuthoringSessionError> =
        match ensureUsable () with
        | Error error -> Error error
        | Ok() ->
            match proposals.TryGetValue proposalId with
            | false, _ -> Error(AuthoringSessionError.ProposalNotFound proposalId)
            | true, (transactionId, _) ->
                // Rejection only forgets a handle; all workflows retain the current
                // workspace and return the same identity-only receipt.
                proposals.Remove proposalId |> ignore
                registered.Remove proposalId |> ignore
                Ok { TransactionId = transactionId; RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }

    member _.ProjectExecution(): Result<ExecutionProgram, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        match AgentExecution.projectTypedSemanticIr workspace with
        | Error error -> Error(AuthoringSessionError.SemanticProjectionFailure error)
        | Ok typed -> ExecutionIr.lower typed |> Result.mapError AuthoringSessionError.ExecutionLoweringFailure

    /// The execution program of one entry over the committed snapshot. It is a pure function of the
    /// workspace state and the entry, so it is kept with the workspace object, which every commit
    /// replaces: repeated runs of one entry (every runPure case of an authoring step) select,
    /// validate and lower it once.
    member internal _.ProjectEntryExecution(entry): Result<ExecutionProgram, AuthoringSessionError> =
        let projections = entryProjections.GetValue(workspace, fun _ -> Dictionary<FunctionId, Result<ExecutionProgram, AuthoringSessionError>>())
        lock projections (fun () ->
            match projections.TryGetValue entry with
            | true, projected -> projected
            | _ ->
                use _scope = SnapshotComputationScope.Start()
                let projected =
                    AgentExecution.projectEntryTypedSemanticIr workspace entry
                    |> Result.mapError AuthoringSessionError.SemanticProjectionFailure
                    |> Result.bind (fun typed -> ExecutionIr.lower typed |> Result.mapError AuthoringSessionError.ExecutionLoweringFailure)
                projections[entry] <- projected
                projected)

    member internal this.ExecutionContext(proposalId: string option): Result<AuthoringExecutionContext, AuthoringSessionError> =
        let context snapshot =
            SnapshotFingerprint.compute snapshot
            |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
            |> Result.map (fun fingerprint ->
                { SnapshotId = snapshot.Id
                  Fingerprint = SnapshotFingerprint.value fingerprint
                  Declarations =
                    let declarations = PinnedDependency.withProvenance selection { Functions = snapshot.Functions; Types = snapshot.Types; Effects = snapshot.Effects; Capabilities = snapshot.Capabilities; Provenance = [] }
                    match proposalId with
                    | Some id ->
                        match proposals.TryGetValue id with
                        | true, (_, PendingProposal.DependencyUpgrade(_, _, _, provenance)) -> { declarations with Provenance = provenance }
                        | _ -> declarations
                    | None -> declarations
                  Project = fun entry ->
                    use _scope = SnapshotComputationScope.Start()
                    ExecutionCallGraph.forSnapshotEntry snapshot entry
                    |> TypedSemanticIr.project
                    |> Result.mapError AuthoringSessionError.SemanticProjectionFailure
                    |> Result.bind (fun typed -> ExecutionIr.lower typed |> Result.mapError AuthoringSessionError.ExecutionLoweringFailure)
                  Worker = worker })
        this.VerifyPersistedSource()
        |> Result.bind (fun () ->
            match proposalId with
            | None ->
                this.Status()
                |> Result.map (fun status ->
                    { SnapshotId = status.SnapshotId
                      Fingerprint = SnapshotFingerprint.value status.SnapshotFingerprint
                      Declarations = this.Declarations()
                      Project = this.ProjectEntryExecution
                      Worker = worker })
            | Some id ->
                this.PendingProposalView id
                |> Result.bind (fun view ->
                    if not view.BaseIsCommitted then Error AuthoringSessionError.ExternalModification
                    else match view.ProposedSnapshot with
                         | Some snapshot -> context snapshot
                         | None -> Error(AuthoringSessionError.DependencyFailure "Candidate execution is unsupported for operations proposals.")))

    member this.EmitCSharp(profile: CSharpBackendProfile): Result<CSharpArtifact, AuthoringSessionError> =
        this.ProjectExecution()
        |> Result.bind (fun execution -> CSharpBackend.emit execution profile |> Result.mapError AuthoringSessionError.BackendFailure)

    /// The session's runner worker, for tests of its lifetime.
    member internal _.PureRunWorker = worker

    member _.Close() =
        if not closed then
            closed <- true
            worker.Dispose()
            SessionStorage.releaseLock path lockHandle

    interface IDisposable with
        member this.Dispose() = this.Close()

[<RequireQualifiedAccess>]
module AuthoringSession =
    let private workspaceFromSource (path: string) (dependency: Snapshot option) (source: string): Result<AgentWorkspace, AuthoringSessionError> =
        match SessionStorage.decodeAs path dependency source with
        | Error error -> Error error
        | Ok snapshot ->
            (match dependency with
             | None -> AgentWorkspace.create snapshot
             | Some imported -> AgentWorkspace.createResolved imported snapshot)
            |> Result.mapError AuthoringSessionError.WorkspaceValidationFailure

    let private openUsing (path: string) pin sourceOverride: Result<AuthoringSession, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        if not (File.Exists path) then Error(AuthoringSessionError.SnapshotFileNotFound path)
        else
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                match SessionStorage.readText path with
                | Error error ->
                    SessionStorage.releaseLock path lockHandle
                    Error error
                | Ok source ->
                    let loaded = PinnedDependency.load path sourceOverride
                    match loaded |> Result.bind (fun selected ->
                        (match selected with
                         | None -> SessionStorage.requireSidecarForResolvedSource path source
                         | Some _ -> Ok())
                        |> Result.bind (fun () -> workspaceFromSource path (selected |> Option.map (fun item -> item.Imported)) source)
                        |> Result.bind (fun workspace ->
                            // Verify the local digest and complete combined graph
                            // before changing even storage-only dependency metadata.
                            let prepared =
                                match pin, selected with
                                | false, _ -> Ok selected
                                | true, None -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency selection to pin.")
                                | true, Some selected when selected.IsEmbedded && Option.isNone sourceOverride -> Ok(Some selected)
                                | true, Some selected -> PinnedDependency.persistPin path source selected |> Result.map Some
                            prepared |> Result.map (fun selected -> selected, workspace))) with
                    | Error error ->
                        SessionStorage.releaseLock path lockHandle
                        // An interrupted dependency replacement leaves the previous sidecar beside the pair;
                        // when that one matches the snapshot it is named, never restored silently.
                        let previous = path + ".dependency.json.previous"
                        let previousMatches =
                            not pin && File.Exists previous
                            && (PinnedDependency.loadFrom previous None
                                |> Result.bind (fun selected -> workspaceFromSource path (selected |> Option.map (fun item -> item.Imported)) source)
                                |> Result.isOk)
                        if previousMatches then
                            Error(AuthoringSessionError.DependencyFailure
                                $"The dependency sidecar '{path}.dependency.json' does not match the snapshot, but '{previous}' does: a dependency replacement was interrupted. Restore it over the sidecar to recover the previous selection.")
                        else Error error
                    | Ok(selected, workspace) ->
                        Ok(new AuthoringSession(path, workspace, source, lockHandle, selected))

    let openExisting path = openUsing path false None

    /// Apply a reviewed final selection under the existing workspace lock. Storage faults
    /// restore the previous manifest; crash atomicity across the two files is unchanged.
    let private upgradeUsing (path: string) (plan: string -> string -> PinnedDependencySelection -> Result<PinnedDependencySelection * Snapshot * ValidationIssue list, AuthoringSessionError>) =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        if not (File.Exists path) then Error(AuthoringSessionError.SnapshotFileNotFound path)
        else
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                let result =
                    SessionStorage.readText path
                    |> Result.bind (fun source ->
                        PinnedDependency.load path None
                        |> Result.bind (function
                            | None ->
                                SessionStorage.requireSidecarForResolvedSource path source
                                |> Result.bind (fun () -> Error(AuthoringSessionError.DependencyFailure "This workspace has no dependency to upgrade."))
                            | Some current ->
                                workspaceFromSource path (Some current.Imported) source
                                |> Result.bind (fun _ -> plan path source current)
                                |> Result.bind (fun (selection, local, issues) ->
                                    if not (List.isEmpty issues) then Error(AuthoringSessionError.WorkspaceValidationFailure issues)
                                    else
                                        SnapshotCodec.serializeResolved selection.Imported local
                                        |> Result.mapError AuthoringSessionError.SnapshotCodecFailure
                                        |> Result.map (SessionStorage.encode path)
                                        |> Result.bind (fun upgradedSource ->
                                            workspaceFromSource path (Some selection.Imported) upgradedSource
                                            |> Result.bind (fun workspace ->
                                                SessionStorage.replaceDependencyPair path source current.Manifest upgradedSource selection.Manifest
                                                |> Result.map (fun () -> upgradedSource, workspace, selection))))))
                match result with
                | Error error ->
                    SessionStorage.releaseLock path lockHandle
                    Error error
                | Ok(source, workspace, selected) -> Ok(new AuthoringSession(path, workspace, source, lockHandle, Some selected))

    /// Original single-member API remains compatible with its fingerprint review.
    let upgradeDependency path sourcePath replaces expectedFingerprint =
        upgradeUsing path (fun path source current ->
            PinnedDependency.planUpgrade path source current sourcePath replaces
            |> Result.bind (fun plan ->
                if plan.Preview.NewFingerprint <> expectedFingerprint then
                    Error(AuthoringSessionError.DependencyFailure "The source differs from the reviewed upgrade version.")
                else Ok(plan.Selection, plan.Local, plan.Preview.Issues)))

    /// Replace all named members together after recomputing the context-bound review.
    let upgradeDependencies path upgrades reviewFingerprint =
        upgradeUsing path (fun path source current ->
            PinnedDependency.planUpgrades path source current upgrades
            |> Result.bind (fun plan ->
                if plan.Preview.ReviewFingerprint <> reviewFingerprint then
                    Error(AuthoringSessionError.DependencyFailure "The workspace, current pins, targets or new selected content differ from the reviewed batch. Preview the dependency upgrades again.")
                else Ok(plan.Selection, plan.Local, plan.Preview.Issues)))

    let upgradeDependencySelection path request reviewFingerprint =
        upgradeUsing path (fun path source current ->
            PinnedDependency.planSelectionUpgrade path source current request
            |> Result.bind (fun plan ->
                if plan.Preview.ReviewFingerprint <> reviewFingerprint then
                    Error(AuthoringSessionError.DependencyFailure "The workspace, current pins, targets or new selected content differ from the reviewed selection. Preview the dependency upgrades again.")
                else Ok(plan.Selection, plan.Local, plan.Preview.Issues)))

    /// Storage-only recovery: retain exactly the selected ID/fingerprint/digest.
    /// Supplying another semantic version never authorizes an upgrade.
    let pinExistingDependency path sourceOverride = openUsing path true sourceOverride

    /// Creates a workspace whose imported graph combines the exact content of each dependency.
    let createNewWithDependencies (path: string) (dependencyPaths: string list): Result<AuthoringSession, AuthoringSessionError> =
        use _scope = SnapshotComputationScope.Start()
        let path = SessionStorage.fullPath path
        match SessionStorage.ensureParentDirectory path with
        | Error error -> Error error
        | Ok() when File.Exists path || File.Exists(path + ".dependency.json") -> Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
        | Ok() ->
            match SessionStorage.acquireLock path with
            | Error error -> Error error
            | Ok lockHandle ->
                if File.Exists path || File.Exists(path + ".dependency.json") then
                    SessionStorage.releaseLock path lockHandle
                    Error(AuthoringSessionError.SnapshotFileAlreadyExists path)
                else
                    let pinOne = PinnedDependency.pinSource path
                    let selection =
                        match dependencyPaths with
                        | [] -> Ok None
                        | paths ->
                            paths
                            |> List.fold (fun state sourcePath ->
                                state |> Result.bind (fun pinned -> pinOne sourcePath |> Result.map (fun item -> pinned @ [ item ]))) (Ok [])
                            |> Result.bind (fun pinned ->
                                if (pinned |> List.map (fun item -> item.SourcePath) |> List.distinct).Length <> pinned.Length then
                                    Error(AuthoringSessionError.DependencyFailure "Each dependency may be selected only once.")
                                else PinnedDependency.combine pinned)
                            |> Result.map Some
                    match selection with
                    | Error error ->
                        SessionStorage.releaseLock path lockHandle
                        Error error
                    | Ok selected ->
                        let snapshot = Snapshot.empty (StableIdAllocator.newSnapshotId ())
                        let dependency = selected |> Option.map (fun item -> item.Imported)
                        let workspaceResult =
                            match dependency with
                            | None -> AgentWorkspace.create snapshot
                            | Some imported -> AgentWorkspace.createResolved imported snapshot
                        match workspaceResult with
                        | Error issues ->
                            SessionStorage.releaseLock path lockHandle
                            Error(AuthoringSessionError.WorkspaceValidationFailure issues)
                        | Ok workspace ->
                            let serialized =
                                match dependency with
                                | None -> AgentWorkspace.serializeCurrentSnapshot workspace
                                | Some imported -> AgentWorkspace.serializeWithDependency imported workspace
                            match serialized |> Result.map (SessionStorage.encode path) with
                            | Error error ->
                                SessionStorage.releaseLock path lockHandle
                                Error(AuthoringSessionError.SnapshotCodecFailure error)
                            | Ok source ->
                                let manifestResult =
                                    match selected with
                                    | None -> Ok()
                                    | Some selected -> SessionStorage.createAtomically (path + ".dependency.json") selected.Manifest
                                match manifestResult with
                                | Error error -> SessionStorage.releaseLock path lockHandle; Error error
                                | Ok() ->
                                    match SessionStorage.createAtomically path source with
                                    | Error error ->
                                        if selected.IsSome then SessionStorage.cleanupTemporary (path + ".dependency.json")
                                        SessionStorage.releaseLock path lockHandle
                                        Error error
                                    | Ok() -> Ok(new AuthoringSession(path, workspace, source, lockHandle, selected))

    let createNewWithDependency (path: string) (dependencyPath: string option) =
        createNewWithDependencies path (Option.toList dependencyPath)

    let createNew (path: string): Result<AuthoringSession, AuthoringSessionError> =
        createNewWithDependency path None
