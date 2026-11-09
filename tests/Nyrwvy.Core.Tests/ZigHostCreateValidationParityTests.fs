namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Xunit

module ZigHostCreateValidationParityTests =
    open ZigHostParityTests

    [<Fact; Trait("Suite", "Extended")>]
    let ``Create validation rejects waiting mustNotWait functions without publication and preserves allocator consumption`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "empty.json")
            let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> "create-validation-base")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let saved = File.ReadAllBytes path
            let seed = "create-validation"
            let waiting = """{"kind":"bind","value":{"kind":"taskWait","tasks":{"kind":"listEmpty","itemType":{"kind":"builtinGeneric","generic":"task","arguments":[{"kind":"primitive","primitive":"int64"}]}},"mode":"all"},"body":{"kind":"unitLiteral"}}"""
            let request op name body = $"""{{"id":"c","op":"{op}","displayName":"{name}","mustNotWait":true,"parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"requiredEffects":[],"body":{body}}}"""
            let status = """{"id":"status","op":"status"}"""
            let proposals = """{"id":"proposals","op":"proposals"}"""
            let declarations = """{"id":"declarations","op":"declarations"}"""
            let result reply =
                use document = parseDocument reply
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), reply)
                document.RootElement.GetProperty("result").Clone()
            for op in [ "simulateCreateFunction"; "simulateCreateFunctionByName" ] do
                let invalid = request op "review-wait" waiting
                let valid = request op "valid-after-rejection" """{"kind":"unitLiteral"}"""
                let requests = [ invalid; status; proposals; declarations; valid; status; proposals; declarations ]
                let code, errors, actual = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", seed ] [ "open"; "--snapshot"; path ] requests
                Assert.True((code = 0), errors)
                Assert.Equal(seedWarning, errors)
                Assert.Equal(requests.Length, actual.Length)
                Assert.Equal<byte>(saved, File.ReadAllBytes path)
                for suffix in [ ".dependency.json"; ".dependency.json.previous"; ".backends.json"; ".previous" ] do
                    Assert.False(File.Exists(path + suffix), suffix)
                use rejected = parseDocument actual.Head
                let error = rejected.RootElement.GetProperty("error")
                Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal("SimulationRejected", error.GetProperty("code").GetString())
                Assert.Equal("The structural request was rejected.", error.GetProperty("message").GetString())
                let diagnostics = error.GetProperty("details").GetProperty("diagnostics")
                Assert.Equal(1, diagnostics.GetArrayLength())
                Assert.Equal("InvalidProposedSnapshot", diagnostics[0].GetProperty("code").GetString())
                let functionId = StableIdAllocator.seededPayload seed 1L
                let waitNodeId = StableIdAllocator.seededPayload seed 3L
                Assert.Equal($"MustNotWaitFunctionWaits: Function 'review-wait' ({functionId}) must not wait but reaches TaskWait (node '{waitNodeId}').", diagnostics[0].GetProperty("message").GetString())
                let unsupported = """{"id":"c","ok":false,"error":{"code":"UnsupportedOperation","message":"The Zig host does not word proposed-snapshot validation failures yet."}}"""
                Assert.False(createReplyMatches invalid actual.Head unsupported)
                Assert.Equal(0, (result actual[1]).GetProperty("pendingProposalCount").GetInt32())
                Assert.Equal(0, (result actual[2]).GetProperty("count").GetInt32())
                Assert.Equal(0, (result actual[3]).GetProperty("functions").GetArrayLength())
                let receipt = (result actual[4]).GetProperty("review").GetProperty("receipt")
                Assert.Equal(StableIdAllocator.seededPayload seed 11L, receipt.GetProperty("transactionId").GetString())
                Assert.Equal(StableIdAllocator.seededPayload seed 12L, receipt.GetProperty("proposedSnapshotId").GetString())
                Assert.Equal(12L, StableIdAllocator.seededCount seed actual[4])
                Assert.Equal(1, (result actual[5]).GetProperty("pendingProposalCount").GetInt32())
                Assert.Equal(1, (result actual[6]).GetProperty("count").GetInt32())
                Assert.Equal(0, (result actual[7]).GetProperty("functions").GetArrayLength()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig host refuses a simulate once 64 proposals are pending until one is rejected`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "empty.json")
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (StableId.createForTesting<SnapshotTag> "pending-limit-base")) |> unwrap)
            let create index = $"""{{"id":"c{index}","op":"simulateCreateFunctionByName","displayName":"f{index}","parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"requiredEffects":[],"body":{{"kind":"unitLiteral"}}}}"""
            let creates = [ for index in 1..65 -> create index ]
            let proposalId (reply: string) =
                use document = parseDocument reply
                document.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            // Seeded IDs repeat across sessions, so the second session can name the first session's proposal;
            // an unparsable NYRWVY_TEST_PENDING_LIMIT keeps the production cap of 64.
            let session requests = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "pending-limit"; "NYRWVY_TEST_PENDING_LIMIT", "default" ] [ "open"; "--snapshot"; path ] requests
            let code, errors, replies = session creates
            Assert.True((code = 0), errors)
            for reply in List.take 64 replies do Assert.Contains("\"ok\":true", reply)
            use refused = parseDocument replies[64]
            Assert.Equal("SessionError", refused.RootElement.GetProperty("error").GetProperty("code").GetString())
            let rejectFirst = $"""{{"id":"r","op":"reject","proposalId":"{proposalId replies[0]}"}}"""
            let _, _, after = session (List.take 64 creates @ [ rejectFirst; create 65 ])
            Assert.Contains("\"ok\":true", after[64])
            Assert.Contains("\"ok\":true", after[65]))
