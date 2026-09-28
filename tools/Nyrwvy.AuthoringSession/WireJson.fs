namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.Text.Json

module internal WireJson =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj = items |> List.toArray |> box

    /// A response must be able to render any graph the snapshot codec can persist. Per nested
    /// scope a response uses at most 4/3 of the snapshot's JSON levels (Conditional: 4 against 3;
    /// Bind: 5 against 4; MatchUnion: 6 against 5), plus a small envelope, so twice the snapshot
    /// codec's depth covers it with margin. This is a response-encoding allowance, not a change
    /// to Core recipe bounds.
    let responseMaxJsonDepth = 2 * Nyrwvy.Core.SnapshotCodec.maxJsonDepth
    let private responseOptions = JsonSerializerOptions(MaxDepth = responseMaxJsonDepth)
    let serialize (value: obj): string = JsonSerializer.Serialize(value, responseOptions)
