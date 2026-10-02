namespace Nyrwvy.AgentEval

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
module ArtifactPermissions =
    let private directoryMode =
        UnixFileMode.UserRead
        ||| UnixFileMode.UserWrite
        ||| UnixFileMode.UserExecute

    let private fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    let private isUnix = not (OperatingSystem.IsWindows())

    let ensureDirectory (path: string) =
        Directory.CreateDirectory(path) |> ignore

        if isUnix then
            File.SetUnixFileMode(path, directoryMode)

    let ensureFile (path: string) =
        if isUnix then
            File.SetUnixFileMode(path, fileMode)

    let restrictTree (root: string) =
        if isUnix then
            for path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories) do
                if (File.GetAttributes(path) &&& FileAttributes.Directory) = FileAttributes.Directory then
                    File.SetUnixFileMode(path, directoryMode)
                else
                    File.SetUnixFileMode(path, fileMode)

            File.SetUnixFileMode(root, directoryMode)

module internal ArtifactJson =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj =
        items |> List.toArray |> box

    let optionalString (value: string option): obj =
        match value with
        | Some item -> box item
        | None -> null

    let serialize (value: obj): string =
        JsonSerializer.Serialize(value)

    let utf8ByteCount (value: string): int64 =
        UTF8Encoding(false).GetByteCount(value) |> int64

[<RequireQualifiedAccess>]
module internal ArtifactFiles =
    let private utf8 = UTF8Encoding(false)

    let writeText path mode (value: string) =
        let bytes = utf8.GetBytes(value)

        use stream =
            new FileStream(
                path,
                mode,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough
            )

        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)
        ArtifactPermissions.ensureFile path

    let writeAtomically path (value: string) =
        let temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N")

        try
            writeText temporaryPath FileMode.CreateNew value
            File.Move(temporaryPath, path, true)
            ArtifactPermissions.ensureFile path
        finally
            if File.Exists temporaryPath then
                File.Delete temporaryPath

[<RequireQualifiedAccess>]
module internal DotnetProcess =
    type ProcessResult =
        { ExitCode: int
          TimedOut: bool
          StandardOutput: string
          StandardError: string }

    let private timeoutMilliseconds = 120000

    let run (workingDirectory: string) (arguments: string list): Result<ProcessResult, string> =
        try
            let startInfo = ProcessStartInfo("dotnet")
            startInfo.WorkingDirectory <- workingDirectory
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
            startInfo.Environment["DOTNET_NOLOGO"] <- "1"

            for argument in arguments do
                startInfo.ArgumentList.Add(argument)

            match Process.Start(startInfo) with
            | null -> Error "Failed to start dotnet process."
            | childProcess ->
                use childProcess = childProcess
                let standardOutputTask = childProcess.StandardOutput.ReadToEndAsync()
                let standardErrorTask = childProcess.StandardError.ReadToEndAsync()
                let completed = childProcess.WaitForExit(timeoutMilliseconds)
                let timedOut = not completed

                if timedOut then
                    try
                        childProcess.Kill(true)
                    with
                    | _ -> ()

                    childProcess.WaitForExit()

                Ok
                    { ExitCode = childProcess.ExitCode
                      TimedOut = timedOut
                      StandardOutput = standardOutputTask.GetAwaiter().GetResult()
                      StandardError = standardErrorTask.GetAwaiter().GetResult() }
        with
        | _ -> Error "Verification process could not be started or completed."
