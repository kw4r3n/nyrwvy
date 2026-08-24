namespace Nayamnyam.Core

open System

[<RequireQualifiedAccess>]
type StableIdError =
    | BlankValue
    | ContainsLineBreak

[<Struct; StructuralEquality; StructuralComparison>]
type StableId<'Kind> = private StableId of string

type TypeTag = class end
type FieldTag = class end
type VariantTag = class end
type FunctionTag = class end
type ParameterTag = class end
type PatternBindingTag = class end
type GraphNodeTag = class end
type ObligationTag = class end
type SnapshotTag = class end
type TransactionTag = class end

type TypeId = StableId<TypeTag>
type FieldId = StableId<FieldTag>
type VariantId = StableId<VariantTag>
type FunctionId = StableId<FunctionTag>
type ParameterId = StableId<ParameterTag>
type PatternBindingId = StableId<PatternBindingTag>
type GraphNodeId = StableId<GraphNodeTag>
type ObligationId = StableId<ObligationTag>
type SnapshotId = StableId<SnapshotTag>
type TransactionId = StableId<TransactionTag>

[<RequireQualifiedAccess>]
module StableId =
    let tryCreate<'Kind> (value: string): Result<StableId<'Kind>, StableIdError> =
        if String.IsNullOrWhiteSpace value then
            Error StableIdError.BlankValue
        elif value.Contains('\r') || value.Contains('\n') then
            Error StableIdError.ContainsLineBreak
        else
            Ok(StableId value)

    let createForTesting<'Kind> (value: string): StableId<'Kind> =
        match tryCreate<'Kind> value with
        | Ok id -> id
        | Error error -> invalidArg (nameof value) $"Invalid fixture stable ID: {error}."

    let value (StableId value) = value
