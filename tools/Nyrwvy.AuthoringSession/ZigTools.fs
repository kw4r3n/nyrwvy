namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Diagnostics
open System.IO

/// The Zig tools under `tools/zig/<tool>` (docs/decisions/zig-tools.md), shared by runPure's Zig
/// route and the session's snapshot read path: one build cache and one per-process table. Failures
/// are a code and a plain-text message; each caller maps them to its own diagnostics.
module internal ZigTools =
    /// Runs a process and returns its exit code with bounded output; timeouts and I/O failures are
    /// errors. `phase` names the timed step in the timeout diagnostic.
    let launch (executable: string) (phase: string) (workingDirectory: string) (args: string list) (timeoutMs: int) =
        try
            let info = ProcessStartInfo(executable)
            info.WorkingDirectory <- workingDirectory
            info.RedirectStandardInput <- true
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            // The tools and runners write UTF-8; without this, Windows decodes with the console code page.
            info.StandardOutputEncoding <- Text.UTF8Encoding false
            info.StandardErrorEncoding <- Text.UTF8Encoding false
            for arg in args do info.ArgumentList.Add(arg)
            use child = Process.Start(info)
            if isNull child then Error("RunStartupFailure", $"Could not start {executable}.") else
                child.StandardInput.Close()
                // One character beyond the 1 MiB limit, so the check below sees the overflow.
                let stdout = BoundedOutput.read child.StandardOutput 1048577
                let stderr = BoundedOutput.read child.StandardError 1048577
                if not (child.WaitForExit(timeoutMs)) then
                    try child.Kill(true)
                    with
                    | :? InvalidOperationException
                    | :? System.ComponentModel.Win32Exception -> ()
                    if child.WaitForExit(5000) then
                        Error("RunTimeout", $"The {phase} exceeded its time limit of {timeoutMs / 1000} s.")
                    else
                        Error("RunReapFailure", "The timed-out child has not been confirmed terminated.")
                elif not (System.Threading.Tasks.Task.WaitAll([| stdout :> System.Threading.Tasks.Task; stderr :> System.Threading.Tasks.Task |], 5000)) then
                    Error("RunOutputTimeout", "The process exited but its output streams did not close.")
                else
                    let output, errors = stdout.Result, stderr.Result
                    if output.Length > 1048576 || errors.Length > 1048576 then
                        Error("RunOutputTooLarge", "The process produced more than 1 MiB of output.")
                    else Ok(child.ExitCode, output, errors)
        with
        | :? System.ComponentModel.Win32Exception as error -> Error("RunStartupFailure", error.Message)
        | :? IOException as error -> Error("RunIoFailure", error.Message)
        | :? UnauthorizedAccessException as error -> Error("RunIoFailure", error.Message)
        | :? AggregateException as error -> Error("RunIoFailure", error.Message)

    let processFailure (output: string) (errors: string) =
        let details = output + "\n" + errors
        "RunProcessFailure", (if details.Length > 4096 then details.Substring(0, 4096) else details)

    /// The Zig compiler: `NYRWVY_ZIG` when set, otherwise `zig` on the PATH, started from the Nyrwvy
    /// installation (see `directory`).
    let executable () =
        match Environment.GetEnvironmentVariable "NYRWVY_ZIG" with
        | null | "" -> "zig"
        | path -> path

    /// Where `zig` starts: the nearest directory above the session's own binaries that pins tool
    /// versions (`mise.toml`), so a version manager shim resolves the Zig this repository pins even
    /// when the workspace lies elsewhere; else the working directory.
    let directory =
        lazy (
            let rec up (directory: DirectoryInfo) =
                if isNull directory then Environment.CurrentDirectory
                elif File.Exists(Path.Combine(directory.FullName, "mise.toml")) then directory.FullName
                else up directory.Parent
            up (DirectoryInfo AppContext.BaseDirectory))

    /// Built runners and tools keyed by a hash of their source, so repeated runs skip the build.
    /// The hash is predictable, so the cache is per user (`XDG_CACHE_HOME`, else `~/.cache`) and
    /// created owner-only: a shared temporary directory would let another local user plant a runner.
    let cache =
        let root =
            match Environment.GetEnvironmentVariable "XDG_CACHE_HOME" with
            | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache")
            | path -> path
        Path.Combine(root, "nyrwvy", "zig-runners")

    let createPrivateDirectory (path: string) =
        if OperatingSystem.IsWindows() then Directory.CreateDirectory path |> ignore
        else Directory.CreateDirectory(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore

    let private sha256Hex (text: string) = Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes text))

    let private tools = Collections.Generic.Dictionary<string, string>()

    /// Whether the session's paths use the Zig tools (reader, validator, lowering, emitter): only when
    /// `NYRWVY_ZIG_TOOLS=1` or a `NYRWVY_ZIG_*_CHECK` parity mode is set. By default every path is F#,
    /// so that F# and Zig are not mixed at run time (docs/decisions/zig-tools.md).
    let enabled () =
        Environment.GetEnvironmentVariable "NYRWVY_ZIG_TOOLS" = "1"
        || [ "NYRWVY_ZIG_READER_CHECK"; "NYRWVY_ZIG_LOWERING_CHECK"; "NYRWVY_ZIG_EMITTER_CHECK" ]
           |> List.exists (Environment.GetEnvironmentVariable >> String.IsNullOrEmpty >> not)

    // The modules a tool imports (`@import("model_reader")` for `tools/zig/model-reader`), each a
    // directory below the repository with a `main.zig` or, for the viewer page, `asset.zig`.
    let private imports =
        dict [ "host", [ "model_reader", [ "tools"; "zig"; "model-reader" ], "main.zig"
                         "snapshot_reader", [ "tools"; "zig"; "snapshot-reader" ], "main.zig"
                         "zig_emitter", [ "tools"; "zig"; "emitter" ], "main.zig"
                         "csharp_emitter", [ "tools"; "zig"; "csharp-emitter" ], "root.zig"
                         "viewer_asset", [ "src"; "Nyrwvy.Viewer" ], "asset.zig" ] ]

    /// Include embedded resources without hashing the managed helper's build products.
    let sourceText folder =
        let excluded (path: string) =
            let name = Path.GetFileName path
            name = "bin" || name = "obj" || name = ".zig-cache"
        let rec files (directory: string) =
            seq {
                yield! Directory.GetFiles directory |> Seq.filter (excluded >> not)
                for child in Directory.GetDirectories directory do
                    if not (excluded child) then yield! files child
            }
        files folder
        |> Seq.toArray
        |> Array.sortWith (fun x y -> String.CompareOrdinal(x, y))
        |> Array.map (fun file ->
            let contents =
                if Path.GetExtension file = ".bin" then
                    "sha256:" + Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes file))
                else File.ReadAllText file
            Path.GetRelativePath(folder, file) + "\u0000" + contents)
        |> String.concat "\u0000"

    /// A Zig tool (`tools/zig/<name>` beside mise.toml), built once per process into the cache,
    /// keyed by a hash of every file in its directory and in the directories it imports.
    let tool (name: string) =
        lock tools (fun () ->
            match tools.TryGetValue name with
            | true, path -> Ok path
            | _ ->
                try
                    let below parts = Path.Combine(Array.ofList (directory.Value :: parts))
                    let sources = below [ "tools"; "zig"; name ]
                    let imported = match imports.TryGetValue name with | true, modules -> modules | _ -> []
                    // Record the checkout at compile time, not when the cached executable is launched.
                    let build =
                        if name <> "host" then ""
                        else
                            match launch "git" "build identity" directory.Value [ "rev-parse"; "--verify"; "HEAD" ] 2000 with
                            | Ok (0, output, _) when Text.RegularExpressions.Regex.IsMatch(output.Trim(), "^[0-9a-f]{40}$") -> "nyrwvy-zig-host+" + output.Trim()
                            | _ -> "nyrwvy-zig-host"
                    let text =
                        sources :: (imported |> List.map (fun (_, parts, _) -> below parts))
                        |> List.map sourceText
                        |> String.concat "\u0000\u0000"
                    // `-O` is a per-module option: it applies to the module defined after it.
                    let roots =
                        if imported.IsEmpty then [ Path.Combine(sources, "main.zig"); "-O"; "ReleaseSafe" ]
                        else
                            [ yield! [ "-O"; "ReleaseSafe" ]
                              for moduleName, _, _ in imported do yield! [ "--dep"; moduleName ]
                              if name = "host" then yield! [ "--dep"; "build_info" ]
                              yield "-Mroot=" + Path.Combine(sources, "main.zig")
                              for moduleName, parts, root in imported do yield! [ "-O"; "ReleaseSafe"; $"-M{moduleName}=" + Path.Combine(below parts, root) ] ]
                    // The build options are part of the key, so a build made with other options is not reused.
                    let options = String.Join(" ", roots |> List.map (fun arg -> if arg.EndsWith ".zig" then Path.GetFileName(Path.GetDirectoryName arg) else arg))
                    let target = Path.Combine(cache, name + "-" + (sha256Hex (text + "\u0000" + options + "\u0000" + build)).Substring(0, 32))
                    let built = Path.Combine(target, name)
                    let result =
                        if File.Exists built then Ok()
                        else
                            createPrivateDirectory cache
                            createPrivateDirectory target
                            let buildModule = Path.Combine(target, "build_info.zig")
                            if name = "host" then
                                let partialModule = buildModule + "." + Guid.NewGuid().ToString("N")
                                File.WriteAllText(partialModule, $"pub const value = \"{build}\";\n")
                                File.Move(partialModule, buildModule, true)
                            // Built under a fresh name and moved, so another process never runs a partial file.
                            let partial = built + "." + Guid.NewGuid().ToString("N")
                            launch (executable ()) $"Zig {name} build" directory.Value
                                (("build-exe" :: roots) @ (if name = "host" then [ "-Mbuild_info=" + buildModule ] else []) @ [ "--cache-dir"; Path.Combine(cache, "cache"); "-femit-bin=" + partial ]) 300000
                            |> Result.bind (fun (code, output, errors) ->
                                if code = 0 && File.Exists partial then
                                    File.Move(partial, built, true)
                                    Ok()
                                else Error(processFailure output errors))
                    result |> Result.map (fun () -> tools[name] <- built; built)
                with
                | :? IOException as error -> Error("RunIoFailure", error.Message)
                | :? UnauthorizedAccessException as error -> Error("RunIoFailure", error.Message))
