namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Nyrwvy.Core
open Xunit

/// The Zig snapshot reader (tools/zig/snapshot-reader) turns record text into the same JSON as
/// `RecordCodec.toJsonDeferred` (with its deferred placement error), or fails at the same line and
/// column with the same message.
module ZigSnapshotReaderTests =
    let private root () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName else find directory.Parent
        find (DirectoryInfo AppContext.BaseDirectory)

    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    [<Fact>]
    let ``The Zig snapshot reader accepts the record schema versions the F# reader accepts`` () =
        let source = File.ReadAllText(Path.Combine(root (), "tools", "zig", "snapshot-reader", "main.zig"))
        let constant name = Int32.Parse(Regex.Match(source, $@"const {name} = (\d+);").Groups[1].Value)
        Assert.Equal(SnapshotSchema.firstRecord.Json, constant "schema_first_record")
        Assert.Equal(SnapshotSchema.current.Json, constant "schema_current")

    /// Deterministic damage to a record text: lines dropped, repeated, swapped, cut short, blanked or
    /// given a wrong character.
    let private mutations (seed: int) (count: int) (text: string) =
        let random = Random seed
        let lines = text.Split '\n' |> Array.take (text.Split('\n').Length - 1)
        let marks = [| " "; "="; "\""; "["; "]"; "0"; "f"; "-"; ":"; "x"; "\\"; "("; ")"; ","; "é"; "\t"; "/" |]
        [ for _ in 1 .. count ->
            let edited = ResizeArray lines
            let k = random.Next edited.Count
            match random.Next 7 with
            | 0 -> edited.RemoveAt k
            | 1 -> edited.Insert(k, edited[k])
            | 2 when k + 1 < edited.Count ->
                let line = edited[k]
                edited[k] <- edited[k + 1]
                edited[k + 1] <- line
            | 3 when edited[k].Length > 1 -> edited[k] <- edited[k].Substring(0, random.Next(1, edited[k].Length))
            | 4 -> edited.Insert(k, "")
            | _ when edited[k].Length > 0 ->
                let at = random.Next edited[k].Length
                edited[k] <- edited[k].Substring(0, at) + marks[random.Next marks.Length] + edited[k].Substring(at + 1)
            | _ -> edited.RemoveAt k
            String.Join("\n", edited) + "\n" ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig snapshot reader matches the F# record reader`` () =
        let repository =
            Directory.GetFiles(Path.Combine(root (), "examples"), "*.nyrec", SearchOption.AllDirectories)
            |> Array.map File.ReadAllText |> List.ofArray
        let fixtures =
            Directory.GetFiles(Path.Combine(root (), "tests", "Nyrwvy.Core.Tests", "fixtures", "record-format"), "*.json")
            |> Array.map (File.ReadAllText >> SnapshotCodec.deserialize >> unwrap >> RecordCodec.serialize >> unwrap) |> List.ofArray
        let allKinds = RecordCodecAllKindsTests.allKindSnapshots () |> List.map (RecordCodec.serialize >> unwrap)
        let mutated =
            (repository @ fixtures @ List.truncate 12 allKinds)
            |> List.mapi (fun index text -> mutations index 40 text)
            |> List.concat
        // Paths random damage rarely reaches: string spelling, the writer's depth limit, the header's
        // dependency marker and schema range.
        let replaceFirst (pattern: string) (replacement: string) (text: string) = Regex(pattern).Replace(text, replacement, 1)
        let header (extra: string) (text: string) = text.Insert(text.IndexOf '\n', extra)
        let deep = String.replicate 501 "list(" + "int64" + String.replicate 501 ")"
        let targeted =
            [ for text in repository @ fixtures do
                  yield replaceFirst "name=\"" "name=\"\\u0041" text
                  yield replaceFirst "name=\"" "name=\"\\/" text
                  yield replaceFirst "name=\"" "name=\"<" text
                  yield replaceFirst @"=(int64|string|bool)\b" ("=" + deep) text
                  yield replaceFirst @"=(int64|string|bool)\b" "=list(int64,)" text
                  yield header " dependency=\"local\" digest=00ff" text
                  yield header " dependency=\"local\" digest=0X" text
                  yield header " dependency=local digest=00" text
                  yield header " dependency=\"local\"" text
                  yield header " parent=snapshot:x" text
                  yield replaceFirst @"schema=\d+" "schema=20" text
                  yield replaceFirst @"schema=\d+" "schema=024" text ]
        let texts = repository @ fixtures @ allKinds @ mutated @ targeted @ [ ""; "\n"; "\uFEFFsnapshot x\n"; "snapshot x\r\n"; "snapshot x" ]
        // Ill-formed UTF-8, which the F# reader sees as U+FFFD per maximal subpart: stray and truncated
        // sequences, overlong forms, surrogates and code points above U+10FFFF, in a name and anywhere.
        let invalid =
            let random = Random 7
            let sequences = [| [| 0x80uy |]; [| 0xFFuy |]; [| 0xC3uy |]; [| 0xC0uy; 0xAFuy |]; [| 0xE2uy; 0x82uy |]; [| 0xEDuy; 0xA0uy; 0x80uy |]
                               [| 0xF0uy; 0x9Fuy; 0x98uy |]; [| 0xF4uy; 0x90uy; 0x80uy; 0x80uy |]; [| 0xE0uy; 0x80uy; 0x41uy |]; [| 0xC3uy; 0xA9uy; 0xBFuy |] |]
            [ for text in repository @ fixtures do
                  let bytes = Text.Encoding.UTF8.GetBytes text
                  let name = text.IndexOf("name=\"") + 6
                  for sequence in sequences do
                      for at in [ name; random.Next bytes.Length ] do
                          yield Array.concat [ bytes[.. at - 1]; sequence; bytes[at ..] ] ]
        // Resolved locals: each original split into a dependency and a local that names its subjects
        // (the types, effects and capabilities; or every function but the last), with damage.
        let dependencyId = StableId.createForTesting<SnapshotTag> "zig-reader-dependency"
        let asDependency (snapshot: Snapshot) = { snapshot with Id = dependencyId; ParentId = None; Obligations = [] }
        let resolved =
            [ for snapshot in (repository @ fixtures |> List.map (RecordCodec.deserialize >> unwrap)) @ RecordCodecAllKindsTests.allKindSnapshots () do
                  yield asDependency { snapshot with Functions = [] }, { snapshot with Types = []; Effects = []; Capabilities = [] }
                  match List.rev snapshot.Functions with
                  | last :: rest when not rest.IsEmpty ->
                      yield asDependency { snapshot with Functions = List.rev rest }, { snapshot with Types = []; Effects = []; Capabilities = []; Functions = [ last ]; Obligations = [] }
                  | _ -> () ]
            |> List.choose (fun (dependency, local) -> RecordCodec.serializeResolved dependency local |> Result.toOption |> Option.map (fun text -> dependency, text))
            // Only locals that name a dependency subject: without the dependency they do not decode.
            |> List.filter (snd >> RecordCodec.toJsonDeferred >> Result.isError)
        let reader = ZigTools.tool "snapshot-reader" |> unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-reader-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let mutable accepted, deferred, rejected = 0, 0, 0
        // One process per dependency: `--dependency` names its subjects for every pair.
        let utf8 (text: string) = Text.Encoding.UTF8.GetBytes text
        let run (dependency: Snapshot option) (inputs: byte array list) =
            let texts = inputs |> List.map Text.Encoding.UTF8.GetString
            let info = ProcessStartInfo(reader, RedirectStandardOutput = true, StandardOutputEncoding = Text.Encoding.UTF8)
            dependency |> Option.iter (fun d ->
                File.WriteAllText(Path.Combine(dir, "subjects"), String.Join("\n", RecordCodec.dependencySubjects d) + "\n")
                info.ArgumentList.Add "--dependency"
                info.ArgumentList.Add(Path.Combine(dir, "subjects")))
            inputs |> List.iteri (fun index input ->
                File.WriteAllBytes(Path.Combine(dir, $"{index}.nyrec"), input)
                info.ArgumentList.Add(Path.Combine(dir, $"{index}.nyrec"))
                info.ArgumentList.Add(Path.Combine(dir, $"{index}.json")))
            use child = Process.Start info
            let lines = child.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            child.WaitForExit()
            Assert.Equal(0, child.ExitCode)
            Assert.Equal(texts.Length, lines.Length)
            texts |> List.iteri (fun index text ->
                match (match dependency with Some d -> RecordCodec.toJsonDeferredResolved d text | None -> RecordCodec.toJsonDeferred text) with
                | Ok(json, later) ->
                    match later with
                    | None ->
                        accepted <- accepted + 1
                        Assert.True("ok" = lines[index], $"case {index}: F# accepts, Zig says {lines[index]}\n{text}")
                    | Some error ->
                        deferred <- deferred + 1
                        Assert.Equal($"deferred {error.Line}:{error.Column}: {error.Message}", lines[index])
                    Assert.True((json = File.ReadAllText(Path.Combine(dir, $"{index}.json"))), $"case {index}: the JSON differs")
                | Error { Model = Some(SnapshotCodecError.MaximumDepthExceeded depth) } ->
                    rejected <- rejected + 1
                    Assert.Equal($"error MaximumDepthExceeded: {depth}", lines[index])
                | Error error ->
                    rejected <- rejected + 1
                    Assert.Equal($"error {error.Line}:{error.Column}: {error.Message}", lines[index]))
        try
            run None (List.map utf8 texts @ invalid)
            // Every outcome occurs, so no part of the comparison is vacuous.
            Assert.True(accepted > 100 && deferred > 0 && rejected > 300, $"accepted {accepted}, deferred {deferred}, rejected {rejected}")
            accepted <- 0
            rejected <- 0
            resolved |> List.iteri (fun index (dependency, text) -> run (Some dependency) (List.map utf8 (text :: mutations index 40 text)))
            Assert.True(resolved.Length > 40 && accepted > resolved.Length && rejected > resolved.Length, $"{resolved.Length} resolved: accepted {accepted}, rejected {rejected}")
            // Without its dependency each is rejected, on both sides.
            run None (resolved |> List.map (snd >> utf8))
        finally Directory.Delete(dir, true)
