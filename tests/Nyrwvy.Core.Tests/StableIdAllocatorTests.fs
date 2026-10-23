namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
open Xunit

module StableIdAllocatorTests =
    let private assertUuidV7Payload (payload: string) =
        let mutable parsed = Guid.Empty
        if not (Guid.TryParseExact(payload, "D", &parsed)) then
            failwithf "Stable ID payload is not a canonical GUID: %s" payload

        if payload.Length <> 36 || payload[14] <> '7' then
            failwithf "Stable ID payload is not UUIDv7: %s" payload

        let variantNibble = Char.ToLowerInvariant(payload[19])
        if not ("89ab".Contains(variantNibble)) then
            failwithf "Stable ID payload does not use the RFC 9562 variant: %s" payload

    let private assertTypedText prefix payload typedText =
        let expected = prefix + ":" + payload
        if typedText <> expected then
            failwithf "Expected typed stable ID '%s', got '%s'." expected typedText

    [<Fact>]
    let ``production allocator emits UUIDv7 payloads for every stable ID domain`` () =
        let typeId = StableIdAllocator.newTypeId ()
        let fieldId = StableIdAllocator.newFieldId ()
        let variantId = StableIdAllocator.newVariantId ()
        let functionId = StableIdAllocator.newFunctionId ()
        let parameterId = StableIdAllocator.newParameterId ()
        let patternBindingId = StableIdAllocator.newPatternBindingId ()
        let graphNodeId = StableIdAllocator.newGraphNodeId ()
        let effectId = StableIdAllocator.newEffectId ()
        let capabilityId = StableIdAllocator.newCapabilityId ()
        let capabilityOperationId = StableIdAllocator.newCapabilityOperationId ()
        let capabilityParameterId = StableIdAllocator.newCapabilityParameterId ()
        let obligationId = StableIdAllocator.newObligationId ()
        let snapshotId = StableIdAllocator.newSnapshotId ()
        let transactionId = StableIdAllocator.newTransactionId ()

        let cases =
            [ "type", StableId.value typeId, StableIdText.typeId typeId
              "field", StableId.value fieldId, StableIdText.fieldId fieldId
              "variant", StableId.value variantId, StableIdText.variantId variantId
              "function", StableId.value functionId, StableIdText.functionId functionId
              "parameter", StableId.value parameterId, StableIdText.parameterId parameterId
              "pattern-binding", StableId.value patternBindingId, StableIdText.patternBindingId patternBindingId
              "node", StableId.value graphNodeId, StableIdText.graphNodeId graphNodeId
              "effect", StableId.value effectId, StableIdText.effectId effectId
              "capability", StableId.value capabilityId, StableIdText.capabilityId capabilityId
              "capability-operation", StableId.value capabilityOperationId, StableIdText.capabilityOperationId capabilityOperationId
              "capability-parameter", StableId.value capabilityParameterId, StableIdText.capabilityParameterId capabilityParameterId
              "obligation", StableId.value obligationId, StableIdText.obligationId obligationId
              "snapshot", StableId.value snapshotId, StableIdText.snapshotId snapshotId
              "transaction", StableId.value transactionId, StableIdText.transactionId transactionId ]

        for prefix, payload, typedText in cases do
            assertUuidV7Payload payload
            assertTypedText prefix payload typedText

    [<Fact>]
    let ``parallel production allocation does not collide in one semantic domain`` () =
        let payloads =
            [| 1 .. 4096 |]
            |> Array.Parallel.map (fun _ ->
                StableIdAllocator.newGraphNodeId ()
                |> StableId.value)

        let distinct = payloads |> Set.ofArray
        if distinct.Count <> payloads.Length then
            failwithf "Parallel stable-ID allocation produced %d collisions." (payloads.Length - distinct.Count)

    [<Fact>]
    let ``fixture stable IDs remain opaque and compatible with typed text`` () =
        let fixtureId = StableId.createForTesting<FunctionTag> "function-fixture-readable-id"
        if StableId.value fixtureId <> "function-fixture-readable-id" then
            failwith "Testing stable-ID creation unexpectedly rewrote the fixture payload."

        let typed = StableIdText.functionId fixtureId
        if typed <> "function:function-fixture-readable-id" then
            failwithf "Unexpected typed fixture ID: %s" typed

