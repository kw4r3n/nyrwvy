#!/usr/bin/env python3
"""Build temporary profiling copies; production sources are never edited."""
import argparse
import pathlib
import shutil
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
PROJECT = pathlib.Path("tools/Nyrwvy.AuthoringSession/Nyrwvy.AuthoringSession.fsproj")

PROBE = """namespace Nyrwvy.Core
open System
open System.Collections.Generic
open System.Diagnostics
module AllocationProbe =
    let private counters = Dictionary<string, SnapshotComputationMeasurement>()
    let reset () = counters.Clear()
    let results () = counters.Values |> Seq.sortBy (fun m -> m.Name) |> List.ofSeq
    type Phase(initial: string) =
        let mutable name = initial
        let mutable start = Stopwatch.GetTimestamp()
        let mutable allocated = GC.GetAllocatedBytesForCurrentThread()
        let record () =
            let ticks = Stopwatch.GetTimestamp() - start
            let bytes = GC.GetAllocatedBytesForCurrentThread() - allocated
            let old = match counters.TryGetValue name with
                      | true, m -> m
                      | _ -> { Name=name; Computations=0; CacheHits=0; ElapsedTicks=0L; AllocatedBytes=0L }
            counters[name] <- { old with Computations=old.Computations+1; ElapsedTicks=old.ElapsedTicks+ticks; AllocatedBytes=old.AllocatedBytes+bytes }
        member _.Switch(next: string) =
            record ()
            name <- next
            start <- Stopwatch.GetTimestamp()
            allocated <- GC.GetAllocatedBytesForCurrentThread()
        interface IDisposable with
            member _.Dispose() = record ()
"""


def replace_once(text, before, after):
    if text.count(before) != 1:
        raise ValueError(f"Expected one instrumentation anchor: {before[:100]!r}")
    return text.replace(before, after, 1)


def instrument(mirror, deep=False):
    sem = mirror / "src/Nyrwvy.Semantics"
    (sem / "AllocationProbe.fs").write_text(PROBE)
    project = sem / "Nyrwvy.Semantics.fsproj"
    project.write_text(replace_once(project.read_text(), '    <Compile Include="PureFunctions.fs" />',
                                   '    <Compile Include="AllocationProbe.fs" />\n    <Compile Include="PureFunctions.fs" />'))
    changes = {
        sem / "RecordCodec.fs": [
            ('        let records = List<Record>()\n', '        use profile = new AllocationProbe.Phase("record.header")\n        let records = List<Record>()\n'),
            ('            // Rows: tokenize, index and check the block grammar in one pass.', '            profile.Switch("record.rows")\n            // Rows: tokenize, index and check the block grammar in one pass.'),
            ('            // Structural checks before any model is built:', '            profile.Switch("record.references")\n            // Structural checks before any model is built:'),
            ('            // Materialize: the writer\'s JSON, checking key presence', '            profile.Switch("record.materialize")\n            // Materialize: the writer\'s JSON, checking key presence'),
            ('            match SnapshotCodec.deserializeUtf8 dependency buffer.WrittenMemory with', '            profile.Switch("record.json-reader-inclusive")\n            match SnapshotCodec.deserializeUtf8 dependency buffer.WrittenMemory with'),
            ('    let ofJson (json: string) : string =\n', '    let ofJson (json: string) : string =\n        use profile = new AllocationProbe.Phase("record.encode")\n'),
        ],
        sem / "SnapshotCodec.fs": [
            ('        ensureObject context element\n', '        use profile = new AllocationProbe.Phase("json.ensure-properties")\n        ensureObject context element\n'),
            ('        use stream = new MemoryStream()\n', '        use profile = new AllocationProbe.Phase("json.write")\n        use stream = new MemoryStream()\n'),
            ('                use document = parse ()\n', '                use profile = new AllocationProbe.Phase("json.parse")\n                use document = parse ()\n                profile.Switch("json.model")\n'),
            ('                    match SnapshotValidator.validate resolved with\n', '                    profile.Switch("json.validate-inclusive")\n                    match SnapshotValidator.validate resolved with\n'),
        ],
        sem / "Validation.fs": [
            ('    let private validateUncached (snapshot: Snapshot): ValidationIssue list =\n', '    let private validateUncached (snapshot: Snapshot): ValidationIssue list =\n        use profile = new AllocationProbe.Phase("validation.setup")\n'),
            ('        let fieldEntries: (TypeId * VariantId option * SemanticField) list =', '        profile.Switch("validation.setup-collect")\n        let fieldEntries: (TypeId * VariantId option * SemanticField) list ='),
            ('        duplicateBy snapshot.Types "DuplicateTypeId"', '        profile.Switch("validation.setup-duplicates")\n        duplicateBy snapshot.Types "DuplicateTypeId"'),
            ('        let typeIds = snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList', '        profile.Switch("validation.setup-indexes")\n        let typeIds = snapshot.Types |> List.map (fun item -> item.Id) |> Set.ofList'),
            ('        let reaches =\n', '        profile.Switch("validation.types")\n        let reaches =\n'),
            ('        for functionEntity in snapshot.Functions do\n            let label =', '        profile.Switch("validation.contracts")\n        for functionEntity in snapshot.Functions do\n            let label ='),
            ('        let createCompletionResolver = SemanticFacts.createNodeCompletionResolverFactory snapshot', '        profile.Switch("validation.functions")\n        let createCompletionResolver = SemanticFacts.createNodeCompletionResolverFactory snapshot'),
            ('        for functionEntity in snapshot.Functions do\n            let allNodes =', '        for functionEntity in snapshot.Functions do\n            use functionProfile = new AllocationProbe.Phase("validation.fn-setup")\n            let allNodes ='),
            ('            for node in allNodes do\n', '            functionProfile.Switch("validation.fn-resolve-inputs")\n            for node in allNodes do\n'),
            ('            let requireNodeType code message expectedType nodeId =', '            functionProfile.Switch("validation.fn-rules")\n            let requireNodeType code message expectedType nodeId ='),
            ('            match GraphOrder.tryAbortOrderConflict functionsById functionEntity with', '            functionProfile.Switch("validation.fn-order")\n            match GraphOrder.tryAbortOrderConflict functionsById functionEntity with'),
            ('            if hasDependencyCycle nodesById then', '            functionProfile.Switch("validation.fn-cycle")\n            if hasDependencyCycle nodesById then'),
            ('        for obligation in snapshot.Obligations do\n', '        profile.Switch("validation.obligations")\n        for obligation in snapshot.Obligations do\n'),
            ('            GraphOrder.newEffectOrderConflicts baseline proposed\n', '            SnapshotComputationScope.Measure("validation.proposal-order", fun () -> GraphOrder.newEffectOrderConflicts baseline proposed)\n'),
        ],
        sem / "SemanticFacts.fs": [
            ('        : GraphNodeId -> GraphCompletion option =\n', '        : GraphNodeId -> GraphCompletion option =\n        use resolverProfile = new AllocationProbe.Phase("validation.resolver-indexes")\n'),
            ('        let readsOf (node: SemanticNode) =\n', '        resolverProfile.Switch("validation.resolver-infer")\n        let readsOf (node: SemanticNode) =\n'),
        ],
        mirror / "tools/Nyrwvy.AuthoringSession/Session.fs": [
            ('    let checkPersistedSource () =\n', '    let checkPersistedSource () =\n        use profile = new AllocationProbe.Phase("persistence.source-check")\n'),
            ('            let bytes = UTF8Encoding(false).GetBytes content\n', '            use profile = new AllocationProbe.Phase("persistence.utf8")\n            let bytes = UTF8Encoding(false).GetBytes content\n            profile.Switch("persistence.write-flush")\n'),
        ],
    }
    for file, replacements in changes.items():
        if file.name == "SemanticFacts.fs" and not deep:
            continue
        text = file.read_text()
        for before, after in replacements:
            if not deep and any(name in after for name in (
                'Phase("json.ensure-properties")', 'Switch("validation.setup-collect")',
                'Switch("validation.setup-duplicates")', 'Switch("validation.setup-indexes")',
            )):
                continue
            text = replace_once(text, before, after)
        file.write_text(text)


def mirror_sources(destination):
    for subtree in ("src", "tools/Nyrwvy.AuthoringSession"):
        for source in (ROOT / subtree).rglob("*"):
            relative = source.relative_to(ROOT)
            if not source.is_file() or "bin" in relative.parts:
                continue
            if "obj" in relative.parts and not (source.name == "project.assets.json" or ".nuget.g." in source.name):
                continue
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
    for name in ("Directory.Build.props", "global.json"):
        shutil.copy2(ROOT / name, destination / name)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--instrument", action="store_true")
    parser.add_argument("--deep", action="store_true", help="Also split resolver setup and JSON property checks")
    parser.add_argument("--no-measure", action="store_true", help="Disable scope counters for a timing control")
    parser.add_argument("--sizes", default="100,1000,3000")
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if args.deep and not args.instrument:
        parser.error("--deep requires --instrument")
    if args.samples < 1 or any(int(n) < 1 for n in args.sizes.split(",")):
        parser.error("sizes and samples must be positive")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="nyrec-profile-") as temporary:
        scratch = pathlib.Path(temporary)
        source = ROOT
        if args.instrument:
            source = scratch / "source"
            source.mkdir()
            mirror_sources(source)
            instrument(source, args.deep)
        binary = scratch / "bin"
        subprocess.run(["dotnet", "build", str(source / PROJECT), "--no-restore", "-c", "Release", "-o", str(binary), "-m:1", "-v:q"], check=True)
        references = ["Nyrwvy.Semantics", "Nyrwvy.Execution", "Nyrwvy.Backend.CSharp", "Nyrwvy.Viewer", "Nyrwvy.AuthoringSession"]
        command = ["dotnet", "fsi", *[f"--reference:{binary / (name + '.dll')}" for name in references],
                   str(pathlib.Path(__file__).with_name("profile.fsx")), str(ROOT), str(args.output.resolve()), args.sizes, str(args.samples), str(not args.no_measure).lower(), str(args.instrument).lower()]
        subprocess.run(command, check=True)


if __name__ == "__main__":
    main()
