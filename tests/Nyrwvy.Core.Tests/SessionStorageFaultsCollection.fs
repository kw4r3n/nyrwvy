namespace Nyrwvy.Core.Tests

open Xunit

/// `SessionStorageFaults.failReplacement` is process-wide, and each test that sets it resets it when it
/// finishes, so two such tests running at once can clear each other's fault. Every test class that sets
/// it joins this collection, which runs alone.
[<CollectionDefinition("Session storage faults", DisableParallelization = true)>]
type SessionStorageFaultsCollection() = class end
