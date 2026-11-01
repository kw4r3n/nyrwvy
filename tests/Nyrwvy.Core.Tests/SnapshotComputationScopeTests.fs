namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Threading.Tasks
open Nyrwvy.Core
open Xunit

module SnapshotComputationScopeTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private metric name (scope: SnapshotComputationScope) = scope.Measurements |> List.find (fun item -> item.Name = name)

    [<Fact>]
    let ``scoped validation caches valid and invalid results but never conflates equal snapshot IDs`` () =
        let snapshot = (TestGraph.create()).Snapshot
        let invalid = { snapshot with Functions = snapshot.Functions |> List.map (fun fn -> { fn with DisplayName = "" }) }
        let expected = SnapshotValidator.validate invalid
        use scope = SnapshotComputationScope.Start(measure = true)
        for _ in 1 .. 2 do
            Assert.Empty(SnapshotValidator.validate snapshot)
            Assert.Equal<ValidationIssue list>(expected, SnapshotValidator.validate invalid)
        Assert.NotEmpty(expected)
        Assert.Equal(2, (metric "validation" scope).Computations)
        Assert.Equal(2, (metric "validation" scope).CacheHits)
        match SnapshotCodec.serialize invalid with
        | Error(SnapshotCodecError.InvalidSnapshot issues) -> Assert.Equal<ValidationIssue list>(expected, issues)
        | other -> failwithf "Invalid snapshot reused a valid result: %A" other

    [<Fact>]
    let ``serialization and fingerprints retain exact contents under scoped reuse`` () =
        let snapshot = (TestGraph.create()).Snapshot
        let renamed = { snapshot with Functions = snapshot.Functions |> List.map (fun fn -> { fn with DisplayName = fn.DisplayName + "_renamed" }) }
        let originalJson, renamedJson = SnapshotCodec.serialize snapshot |> unwrap, SnapshotCodec.serialize renamed |> unwrap
        use scope = SnapshotComputationScope.Start(measure = true)
        for _ in 1 .. 2 do
            Assert.Equal(originalJson, SnapshotCodec.serialize snapshot |> unwrap)
            Assert.Equal(renamedJson, SnapshotCodec.serialize renamed |> unwrap)
            Assert.NotEqual<SnapshotFingerprint>(SnapshotFingerprint.compute snapshot |> unwrap, SnapshotFingerprint.compute renamed |> unwrap)
            Assert.Equal(SemanticFingerprint.compute snapshot |> unwrap, SemanticFingerprint.compute renamed |> unwrap)
        Assert.Equal(2, (metric "snapshotFingerprint" scope).Computations)
        Assert.Equal(2, (metric "snapshotFingerprint" scope).CacheHits)
        Assert.Equal(2, (metric "semanticFingerprint" scope).Computations)

    [<Fact>]
    let ``nested scopes share facts and exception disposal cannot leak into the next request`` () =
        let snapshot = (TestGraph.create()).Snapshot
        let run () =
            use outer = SnapshotComputationScope.Start(measure = true)
            SnapshotValidator.validate snapshot |> ignore
            do
                use nested = SnapshotComputationScope.Start()
                SnapshotValidator.validate snapshot |> ignore
            SnapshotValidator.validate snapshot |> ignore
            Assert.Equal(1, (metric "validation" outer).Computations)
            Assert.Equal(2, (metric "validation" outer).CacheHits)
            failwith "request failed"
        Assert.Throws<Exception>(Action(run)) |> ignore
        use next = SnapshotComputationScope.Start(measure = true)
        // Validation issues are retained with the snapshot object itself, so the next request finds
        // them without the failed request's scope; an equal but distinct object is computed afresh.
        SnapshotValidator.validate snapshot |> ignore
        Assert.Equal(0, (metric "validation" next).Computations)
        Assert.Equal(1, (metric "validation" next).CacheHits)
        SnapshotValidator.validate { snapshot with Functions = snapshot.Functions } |> ignore
        Assert.Equal(1, (metric "validation" next).Computations)

    [<Fact>]
    let ``retained facts outlive the scope and a no-reuse scope bypasses them`` () =
        let snapshot = (TestGraph.create()).Snapshot
        let expected, semantic =
            use first = SnapshotComputationScope.Start(measure = true)
            let fingerprint = SnapshotFingerprint.compute snapshot |> unwrap
            let semantic = SemanticFingerprint.compute snapshot |> unwrap
            Assert.Equal(1, (metric "snapshotFingerprint" first).Computations)
            Assert.Equal(1, (metric "semanticFingerprint" first).Computations)
            fingerprint, semantic
        do
            use again = SnapshotComputationScope.Start(measure = true)
            Assert.Equal(expected, SnapshotFingerprint.compute snapshot |> unwrap)
            Assert.Equal(semantic, SemanticFingerprint.compute snapshot |> unwrap)
            Assert.Equal(1, (metric "snapshotFingerprint" again).CacheHits)
            Assert.Equal(0, (metric "snapshotFingerprint" again).Computations)
            Assert.Equal(1, (metric "semanticFingerprint" again).CacheHits)
            // Serialization is not retained, and the fingerprint hits did not need it.
            Assert.False(again.Measurements |> List.exists (fun item -> item.Name = "serialization"))
        use oracle = SnapshotComputationScope.Start(reuse = false, measure = true)
        Assert.Equal(expected, SnapshotFingerprint.compute snapshot |> unwrap)
        Assert.Equal(1, (metric "snapshotFingerprint" oracle).Computations)
        Assert.Equal(0, (metric "snapshotFingerprint" oracle).CacheHits)

    [<Fact>]
    let ``concurrent readers sharing one request keep typed caches safe`` () =
        let snapshot = (TestGraph.create()).Snapshot
        use scope = SnapshotComputationScope.Start(measure = true)
        let tasks = Array.init 16 (fun _ -> Task.Run<ValidationIssue list>(fun () -> SnapshotValidator.validate snapshot))
        let results = Task.WhenAll(tasks).GetAwaiter().GetResult()
        for result in results do Assert.Empty(result)
        Assert.Equal(1, (metric "validation" scope).Computations)
        Assert.Equal(15, (metric "validation" scope).CacheHits)
