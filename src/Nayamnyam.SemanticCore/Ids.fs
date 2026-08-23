namespace Nayamnyam.SemanticCore

open System

[<RequireQualifiedAccess>]
type StableIdError =
    | BlankValue

[<Struct>]
type StableId<'Tag> = private StableIdValue of string

[<RequireQualifiedAccess>]
module StableId =
    let create<'Tag> (value: string) : Result<StableId<'Tag>, StableIdError> =
        if String.IsNullOrWhiteSpace value then
            Error StableIdError.BlankValue
        else
            Ok(StableIdValue value)

    let value (StableIdValue value) = value

type FunctionIdTag = interface end
type SnapshotIdTag = interface end
type TransactionIdTag = interface end
type ParameterIdTag = interface end
type CallIdTag = interface end
type ValueIdTag = interface end
type ObligationIdTag = interface end

type FunctionId = StableId<FunctionIdTag>
type SnapshotId = StableId<SnapshotIdTag>
type TransactionId = StableId<TransactionIdTag>
type ParameterId = StableId<ParameterIdTag>
type CallId = StableId<CallIdTag>
type ValueId = StableId<ValueIdTag>
type ObligationId = StableId<ObligationIdTag>
