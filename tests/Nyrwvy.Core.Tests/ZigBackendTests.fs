namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// The Zig backend runs through runPure with `"backend": "zig"` and agrees with the C# backend:
/// Int64, Bool and Unit, direct calls and tail self calls; Strings, Option, Result, Products and
/// Unions. Needs `zig` (mise.toml).
module ZigBackendTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e
    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private boolType = """{"kind":"primitive","primitive":"bool"}"""
    let private stringType = """{"kind":"primitive","primitive":"string"}"""
    let private int64 (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private p name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private binary kind = $"""{{"kind":"{kind}","left":{p "a"},"right":{p "b"}}}"""

    /// The Zig emitter embeds the case-mapping runs as a file; they must stay what .NET computes, the
    /// mapping the C# backend applies (regenerate the file from `ZigRuntime.caseMapping` on a change).
    [<Fact>]
    let ``The Zig emitter's case mapping matches the .NET mapping`` () =
        let rec root (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName else root directory.Parent
        let file = Path.Combine(root (DirectoryInfo AppContext.BaseDirectory), "tools", "zig", "emitter", "case_mapping.zig")
        Assert.True((ZigRuntime.caseMapping.Value = File.ReadAllText file), "tools/zig/emitter/case_mapping.zig is stale.")
        let host = Path.Combine(root (DirectoryInfo AppContext.BaseDirectory), "tools", "zig", "host", "case_runs.zig")
        Assert.True((ZigRuntime.caseRunTables.Value = File.ReadAllText host), "tools/zig/host/case_runs.zig is stale.")
