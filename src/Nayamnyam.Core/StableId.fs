namespace Nayamnyam.Core

open System

[<Struct; StructuralEquality; StructuralComparison>]
type StableId<'Kind> = private StableId of string

type TypeTag = class end
type FunctionTag = class end
type SnapshotTag = class end

type TypeId = StableId<TypeTag>
type FunctionId = StableId<FunctionTag>
type SnapshotId = StableId<SnapshotTag>

[<RequireQualifiedAccess>]
module StableId =
    let createForTesting<'Kind> (value: string): StableId<'Kind> =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Fixture stable ID must not be blank."

        if value.Contains('\r') || value.Contains('\n') then
            invalidArg (nameof value) "Fixture stable ID must be one logical line."

        StableId value

    let value (StableId value) = value
