open System
open System.IO
open System.Text
open System.Text.Json
open System.Security.Cryptography
open Nyrwvy.Core

let ok = function Ok value -> value | Error error -> failwithf "%A" error
let digest (text: string) = SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString
let repo = fsi.CommandLineArgs[1]
let paths = [
    "examples/verification-planner/planner.snapshot.nyrec"
    "examples/mini-work-planner/planner.snapshot.nyrec"
    "examples/task-cancellation-outcome/fixtures/task-outcomes.nyrec"
    "tests/Nyrwvy.Core.Tests/fixtures/record-format/verification-planner.json"
    "tests/Nyrwvy.Core.Tests/fixtures/record-format/mini-work-planner.json"
    "tests/Nyrwvy.Core.Tests/fixtures/record-format/function-abort-workspace.json"
]
for path in paths do
    use scope = SnapshotComputationScope.Start()
    let text = File.ReadAllText(Path.Combine(repo, path))
    let snapshot = if path.EndsWith ".nyrec" then RecordCodec.deserialize text |> ok else SnapshotCodec.deserialize text |> ok
    let json = SnapshotCodec.serialize snapshot |> ok
    let records = RecordCodec.serialize snapshot |> ok
    let snapshotFingerprint = SnapshotFingerprint.compute snapshot |> ok |> SnapshotFingerprint.value
    let semanticFingerprint = SemanticFingerprint.compute snapshot |> ok |> SemanticFingerprint.value
    printfn "%s" (JsonSerializer.Serialize {| Path = path; JsonSha256 = digest json; RecordSha256 = digest records; SnapshotFingerprint = snapshotFingerprint; SemanticFingerprint = semanticFingerprint |})
