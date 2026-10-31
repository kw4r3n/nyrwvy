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
                let once zig =
                    let code, errors, replies = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", seed ] zig [ "open"; "--snapshot"; path ] requests
                    Assert.True((code = 0), errors)
                    Assert.Equal(seedWarning, errors)
                    Assert.Equal(requests.Length, replies.Length)
                    Assert.Equal<byte>(saved, File.ReadAllBytes path)
                    for suffix in [ ".dependency.json"; ".dependency.json.previous"; ".backends.json"; ".previous" ] do
                        Assert.False(File.Exists(path + suffix), suffix)
                    replies
                let expected, actual = once false, once true
                for request, (left, right) in List.zip requests (List.zip expected actual) do
                    Assert.True((comparable left = comparable right), $"{request}\nF#: {left}\nZig: {right}")
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
                Assert.False(createReplyMatches invalid expected.Head unsupported)
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
