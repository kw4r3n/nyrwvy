namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Xunit

/// `model-reader --fingerprints` (the Zig session host's status, docs/decisions/zig-session-host.md)
/// against `SnapshotFingerprint` and `SemanticFingerprint`: the same value for every snapshot, so the
/// same minimum-version canonical text (`SnapshotCodec.serializeCanonical`).
module ZigFingerprintTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e
    let private id<'tag> name = StableId.createForTesting<'tag> ("zig-fingerprint-" + name)

    /// Type parameters in the forms the corpus lacks: below schema 21 (an implied list, written
    /// without it and with a name at each occurrence), a declared list in another order (schema
    /// 21), and a generic type (schema 18).
    let private generics () =
        let t, u = id<TypeParameterTag> "t", id<TypeParameterTag> "u"
        let parameter name typeRef: SemanticParameter = { Id = id<ParameterTag> name; DisplayName = name; Type = typeRef }
        let returning (parameterId: ParameterId) : ValueGraph =
            let node = id<GraphNodeTag> ("result-" + StableId.value parameterId)
            { Nodes = [ { Id = node; Kind = SemanticNodeKind.ParameterReference parameterId } ]; ResultNodeId = node }
        let fn name typeParameters (parameters: SemanticParameter list) result : SemanticFunction =
            { MustNotWait = false; Exported = true; Id = id<FunctionTag> name; DisplayName = name
              TypeParameters = typeParameters; Parameters = parameters; ResultType = result
              RequiredEffects = []; Body = returning parameters.Head.Id }
        let list item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
        let x, y = parameter "x" (list (TypeRef.TypeParameter t)), parameter "y" (TypeRef.TypeParameter u)
        let box: SemanticType =
            { Id = id<TypeTag> "box"; DisplayName = "Box"; Exported = true; TypeParameters = [ t, "Item" ]
              Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "item"; DisplayName = "item"; Type = TypeRef.TypeParameter t } ] }
        let empty name = Snapshot.empty (id<SnapshotTag> name)
        [ { empty "implied" with Functions = [ fn "pair" [ t, "T"; u, "U" ] [ x; y ] (list (TypeRef.TypeParameter t)) ] }
          { empty "declared" with Functions = [ fn "pair" [ u, "U"; t, "T" ] [ x; y ] (list (TypeRef.TypeParameter t)) ] }
          { empty "generic-type" with
              Types = [ box ]
              Functions = [ fn "unbox" [ u, "U" ] [ parameter "boxed" (TypeRef.Named(box.Id, [ TypeRef.TypeParameter u ])) ] (TypeRef.Named(box.Id, [ TypeRef.TypeParameter u ])) ] } ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``model-reader --fingerprints gives the F# fingerprints`` () =
        let root = ZigModelReaderSupport.root ()
        let snapshots =
            (Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories)
             |> Array.toList |> List.map (File.ReadAllText >> RecordCodec.deserialize >> unwrap))
            @ RecordCodecAllKindsTests.allKindSnapshots ()
            @ [ MilestoneZeroGraph.create().Snapshot ]
            @ generics ()
        let reader = ZigTools.tool "model-reader" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-fingerprint-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let legacy = snapshots |> List.filter (fun snapshot -> (SnapshotSchema.required snapshot).Json < SnapshotSchema.firstRecord.Json) |> List.length
            Assert.True(legacy > 10 && legacy < snapshots.Length, $"{legacy} of {snapshots.Length} below schema 21")
            for snapshot in snapshots do
                let input, canonical = Path.Combine(dir, "in.json"), Path.Combine(dir, "canonical.json")
                File.WriteAllText(input, SnapshotCodec.serialize snapshot |> unwrap)
                let info = ProcessStartInfo(reader, [ "--fingerprints"; input; canonical; Path.Combine(dir, "normalized.json") ])
                info.RedirectStandardOutput <- true
                use zig = Process.Start info
                let output = zig.StandardOutput.ReadToEnd()
                zig.WaitForExit()
                let expected =
                    $"snapshot {SnapshotFingerprint.compute snapshot |> unwrap |> SnapshotFingerprint.value}\nsemantic {SemanticFingerprint.compute snapshot |> unwrap |> SemanticFingerprint.value}\n"
                if output <> expected then
                    let fsharp = SnapshotCodec.serializeCanonical snapshot |> unwrap
                    let zigText = if File.Exists canonical then File.ReadAllText canonical else ""
                    let at = Seq.zip fsharp zigText |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min fsharp.Length zigText.Length)
                    let around (text: string) = if at < text.Length then text.Substring(max 0 (at - 60), min 120 (text.Length - max 0 (at - 60))) else "<end>"
                    failwithf "%s (schema %d):\n F#:  %s Zig: %s\n canonical texts differ at %d:\n F#:  %s\n Zig: %s"
                        (StableId.value snapshot.Id) (SnapshotSchema.required snapshot).Json expected output at (around fsharp) (around zigText)
        finally Directory.Delete(dir, true)
