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
    let serialize (value: obj): string = JsonSerializer.Serialize(value)
