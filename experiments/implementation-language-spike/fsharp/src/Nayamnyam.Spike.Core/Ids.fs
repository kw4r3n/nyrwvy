namespace Nayamnyam.Spike

open System

[<Struct; StructuralEquality; StructuralComparison>]
type StableId<'Kind> = private StableId of string

type TypeTag = class end
type FunctionTag = class end
type ParameterTag = class end
type CallTag = class end
type ObligationTag = class end
type SnapshotTag = class end
type TransactionTag = class end

type TypeId = StableId<TypeTag>
type FunctionId = StableId<FunctionTag>
type ParameterId = StableId<ParameterTag>
type CallId = StableId<CallTag>
type ObligationId = StableId<ObligationTag>
type SnapshotId = StableId<SnapshotTag>
type TransactionId = StableId<TransactionTag>

module StableId =
    let private domain<'Kind> () =
        let kind = typeof<'Kind>

        if kind = typeof<TypeTag> then
            "type", 1
        elif kind = typeof<FunctionTag> then
            "function", 1
        elif kind = typeof<ParameterTag> then
            "parameter", 1
        elif kind = typeof<CallTag> then
            "call", 1
        elif kind = typeof<ObligationTag> then
            "obligation", 3
        elif kind = typeof<SnapshotTag> then
            "snapshot", 1
        elif kind = typeof<TransactionTag> then
            "transaction", 1
        else
            invalidOp $"Unsupported stable-ID kind {kind.FullName}."

    let create<'Kind> (value: string): StableId<'Kind> =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Stable ID must not be blank."

        let requiredDomain, requiredTailComponents = domain<'Kind> ()
        let components = value.Split(':')

        let hasInvalidComponent =
            components
            |> Array.exists (fun part -> String.IsNullOrEmpty part)

        let hasLineBreak = value.Contains('\r') || value.Contains('\n')

        if
            components.Length <> requiredTailComponents + 1
            || components.[0] <> requiredDomain
            || hasInvalidComponent
            || hasLineBreak
        then
            invalidArg
                (nameof value)
                $"Stable ID must use the {requiredDomain}: domain with {requiredTailComponents} nonempty atomic tail component(s)."

        StableId value

    let value (StableId value) = value

module TypeId =
    let create value: TypeId = StableId.create<TypeTag> value
    let value (id: TypeId) = StableId.value id

module FunctionId =
    let create value: FunctionId = StableId.create<FunctionTag> value
    let value (id: FunctionId) = StableId.value id

module ParameterId =
    let create value: ParameterId = StableId.create<ParameterTag> value
    let value (id: ParameterId) = StableId.value id

module CallId =
    let create value: CallId = StableId.create<CallTag> value
    let value (id: CallId) = StableId.value id

module ObligationId =
    let create value: ObligationId = StableId.create<ObligationTag> value
    let value (id: ObligationId) = StableId.value id

module SnapshotId =
    let create value: SnapshotId = StableId.create<SnapshotTag> value
    let value (id: SnapshotId) = StableId.value id

module TransactionId =
    let create value: TransactionId = StableId.create<TransactionTag> value
    let value (id: TransactionId) = StableId.value id
