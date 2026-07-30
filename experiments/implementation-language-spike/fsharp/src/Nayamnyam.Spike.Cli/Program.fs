namespace Nayamnyam.Spike.Cli

open System
open System.IO
open Nayamnyam.Spike

module Program =
    let private usage () =
        eprintfn "Usage: Nayamnyam.Spike.Cli <canonical|summary> <snapshot-path>"
        2

    [<EntryPoint>]
    let main arguments =
        if arguments.Length <> 2 then
            usage ()
        else
            let snapshot =
                File.ReadAllText arguments.[1]
                |> CanonicalJson.readSnapshot

            if
                String.Equals(
                    arguments.[0],
                    "canonical",
                    StringComparison.Ordinal
                )
            then
                Console.Out.Write(CanonicalJson.writeSnapshot snapshot)
                0
            elif
                String.Equals(
                    arguments.[0],
                    "summary",
                    StringComparison.Ordinal
                )
            then
                Console.Out.Write(CanonicalJson.writeSummary snapshot)
                0
            else
                usage ()
