namespace Nyrwvy.Core

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading

/// Inclusive costs: nested measurements overlap and must not be summed.
type SnapshotComputationMeasurement =
    { Name: string
      Computations: int
      CacheHits: int
      ElapsedTicks: int64
      AllocatedBytes: int64 }

type private SnapshotComputationContext =
    { Tables: Dictionary<obj, obj>
      Measurements: Dictionary<string, SnapshotComputationMeasurement>
      Reuse: bool
      Measure: bool
      mutable Active: bool }

module private SnapshotComputationContext =
    let current = AsyncLocal<SnapshotComputationContext option>()

    let record context name computations hits ticks bytes =
        let previous =
            match context.Measurements.TryGetValue name with
            | true, item -> item
            | _ -> { Name = name; Computations = 0; CacheHits = 0; ElapsedTicks = 0L; AllocatedBytes = 0L }
        context.Measurements[name] <-
            { previous with
                Computations = previous.Computations + computations
                CacheHits = previous.CacheHits + hits
                ElapsedTicks = previous.ElapsedTicks + ticks
                AllocatedBytes = previous.AllocatedBytes + bytes }

    let measure context name action =
        if not context.Measure then action ()
        else
            let start = Stopwatch.GetTimestamp()
            let allocated = GC.GetAllocatedBytesForCurrentThread()
            try action ()
            finally
                record context name 1 0 (Stopwatch.GetTimestamp() - start)
                    (GC.GetAllocatedBytesForCurrentThread() - allocated)

/// Short-lived synchronous request/pass scope for immutable derived snapshot facts.
/// Reference identity, never SnapshotId or structural equality, keys the caches.
/// Nested scopes share the request; disposal clears all retained snapshot values.
/// Without a scope the original uncached correctness paths are used.
[<Sealed>]
type SnapshotComputationScope private (context: SnapshotComputationContext, ownsContext: bool) =
    let mutable disposed = false

    member _.Measurements =
        lock context (fun () -> context.Measurements.Values |> Seq.sortBy (fun item -> item.Name) |> List.ofSeq)

    /// Disable reuse only for comparison/profiling. Measurement is opt-in so
    /// ordinary requests do not pay stopwatch/allocation-counter overhead.
    static member Start(?reuse: bool, ?measure: bool) =
        match SnapshotComputationContext.current.Value with
        | Some context when context.Active -> new SnapshotComputationScope(context, false)
        | _ ->
            let context =
                { Tables = Dictionary<obj, obj>()
                  Measurements = Dictionary<string, SnapshotComputationMeasurement>()
                  Reuse = defaultArg reuse true
                  Measure = defaultArg measure false
                  Active = true }
            SnapshotComputationContext.current.Value <- Some context
            new SnapshotComputationScope(context, true)

    /// Measure an operation without caching it (in particular, filesystem checks
    /// and writes always execute, even inside a reuse scope).
    static member Measure(name, action) =
        match SnapshotComputationContext.current.Value with
        | Some context ->
            lock context (fun () ->
                if context.Active then SnapshotComputationContext.measure context name action
                else action ())
        | None -> action ()

    /// `compute` runs under the context lock. The lock is reentrant, so nested memoized
    /// computations on the same thread are fine, but the scope assumes no concurrent callers
    /// of one context: a computation that waited on other threads using the same (flowed)
    /// context would deadlock. Switch to per-key lazies computed outside the lock before
    /// fanning snapshot computations out across threads.
    ///
    /// With `retain`, a result is also kept for as long as the snapshot object itself is alive,
    /// across scopes and requests, and a later scope reports it as a cache hit. Only for a fact that
    /// is small and depends on the snapshot alone, such as its validation issues or a fingerprint:
    /// the committed snapshot stays the same object from one request to the next, so without it
    /// every simulation re-validated and re-fingerprinted the base. Only a scope with reuse reads
    /// and fills the retained results; a scope without reuse and a caller outside any scope compute.
    static member internal Memoize(name, compute: Snapshot -> 'Result, ?retain: bool): Snapshot -> 'Result =
        let slot = obj() // One private typed table per computation, per scope.
        let retained = if defaultArg retain false then Some(System.Runtime.CompilerServices.ConditionalWeakTable<Snapshot, System.Runtime.CompilerServices.StrongBox<'Result>>()) else None
        let scoped snapshot =
            match SnapshotComputationContext.current.Value with
            | None -> compute snapshot
            | Some context ->
                lock context (fun () ->
                    if not context.Active then compute snapshot
                    elif not context.Reuse then SnapshotComputationContext.measure context name (fun () -> compute snapshot)
                    else
                        let table =
                            match context.Tables.TryGetValue slot with
                            | true, table -> unbox<Dictionary<Snapshot, 'Result>> table
                            | _ ->
                                let table = Dictionary<Snapshot, 'Result>(HashIdentity.Reference)
                                context.Tables[slot] <- table
                                table
                        match table.TryGetValue snapshot with
                        | true, result ->
                            if context.Measure then SnapshotComputationContext.record context name 0 1 0L 0L
                            result
                        | _ ->
                            let result = SnapshotComputationContext.measure context name (fun () -> compute snapshot)
                            table[snapshot] <- result
                            result)
        match retained with
        | None -> scoped
        | Some retained ->
            fun snapshot ->
                let reuse =
                    match SnapshotComputationContext.current.Value with
                    | Some context when context.Active -> context.Reuse
                    | _ -> false
                if not reuse then scoped snapshot
                else
                    match retained.TryGetValue snapshot with
                    | true, result ->
                        match SnapshotComputationContext.current.Value with
                        | Some context when context.Active && context.Measure ->
                            lock context (fun () -> SnapshotComputationContext.record context name 0 1 0L 0L)
                        | _ -> ()
                        result.Value
                    | _ ->
                        let result = scoped snapshot
                        retained.AddOrUpdate(snapshot, System.Runtime.CompilerServices.StrongBox result)
                        result

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                if ownsContext then
                    lock context (fun () ->
                        context.Active <- false
                        context.Tables.Clear())
                    SnapshotComputationContext.current.Value <- None
