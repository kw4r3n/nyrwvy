namespace NyamNyam.AgentEval

open System
open System.IO

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
