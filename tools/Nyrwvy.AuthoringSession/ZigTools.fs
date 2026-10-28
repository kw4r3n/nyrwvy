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
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            // The tools and runners write UTF-8; without this, Windows decodes with the console code page.
            info.StandardOutputEncoding <- Text.UTF8Encoding false
            info.StandardErrorEncoding <- Text.UTF8Encoding false
            for arg in args do info.ArgumentList.Add(arg)
            use child = Process.Start(info)
            if isNull child then Error("RunStartupFailure", $"Could not start {executable}.") else
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

    // The tools a tool imports as modules (`@import("model_reader")` for `tools/zig/model-reader`).
    let private imports = dict [ "host", [ "model-reader"; "snapshot-reader" ] ]

    /// A Zig tool (`tools/zig/<name>` beside mise.toml), built once per process into the cache,
    /// keyed by a hash of every file in its directory and in the directories it imports.
    let tool (name: string) =
        lock tools (fun () ->
            match tools.TryGetValue name with
            | true, path -> Ok path
            | _ ->
                try
                    let source tool = Path.Combine(directory.Value, "tools", "zig", tool)
                    let sources = source name
                    let imported = match imports.TryGetValue name with | true, tools -> tools | _ -> []
                    let text =
                        name :: imported
                        |> List.map (fun tool ->
                            Directory.GetFiles(source tool)
                            |> Array.sortWith (fun x y -> String.CompareOrdinal(x, y))
                            |> Array.map (fun file -> Path.GetFileName file + "\u0000" + File.ReadAllText file)
                            |> String.concat "\u0000")
                        |> String.concat "\u0000\u0000"
                    let moduleName (tool: string) = tool.Replace('-', '_')
                    // `-O` is a per-module option: it applies to the module defined after it.
                    let roots =
                        if imported.IsEmpty then [ Path.Combine(sources, "main.zig"); "-O"; "ReleaseSafe" ]
                        else
                            [ yield! [ "-O"; "ReleaseSafe" ]
                              for tool in imported do yield! [ "--dep"; moduleName tool ]
                              yield "-Mroot=" + Path.Combine(sources, "main.zig")
                              for tool in imported do yield! [ "-O"; "ReleaseSafe"; $"-M{moduleName tool}=" + Path.Combine(source tool, "main.zig") ] ]
                    // The build options are part of the key, so a build made with other options is not reused.
                    let options = String.Join(" ", roots |> List.map (fun arg -> if arg.Contains "main.zig" then Path.GetFileName(Path.GetDirectoryName arg) else arg))
                    let target = Path.Combine(cache, name + "-" + (sha256Hex (text + "\u0000" + options)).Substring(0, 32))
                    let built = Path.Combine(target, name)
                    let result =
                        if File.Exists built then Ok()
                        else
                            createPrivateDirectory cache
                            createPrivateDirectory target
                            // Built under a fresh name and moved, so another process never runs a partial file.
                            let partial = built + "." + Guid.NewGuid().ToString("N")
                            launch (executable ()) $"Zig {name} build" directory.Value
                                (("build-exe" :: roots) @ [ "--cache-dir"; Path.Combine(cache, "cache"); "-femit-bin=" + partial ]) 300000
                            |> Result.bind (fun (code, output, errors) ->
                                if code = 0 && File.Exists partial then
                                    File.Move(partial, built, true)
                                    Ok()
                                else Error(processFailure output errors))
                    result |> Result.map (fun () -> tools[name] <- built; built)
                with
                | :? IOException as error -> Error("RunIoFailure", error.Message)
                | :? UnauthorizedAccessException as error -> Error("RunIoFailure", error.Message))
