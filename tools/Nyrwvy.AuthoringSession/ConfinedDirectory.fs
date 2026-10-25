namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

/// File writes confined below a trusted directory by directory handles (Linux `openat` and its
/// relatives) rather than by paths. Each component is opened relative to the handle of its parent
/// without following links, and the file is created and renamed relative to the handle of its own
/// directory, so swapping a directory for a link after a check cannot carry the write elsewhere: no
/// path below the trusted directory is ever resolved twice.
module internal ConfinedDirectory =
    [<DllImport("libc", SetLastError = true)>]
    extern int openat(int dirfd, string path, int flags, int mode)

    [<DllImport("libc", SetLastError = true)>]
    extern int mkdirat(int dirfd, string path, int mode)

    [<DllImport("libc", SetLastError = true)>]
    extern int renameat(int olddirfd, string oldpath, int newdirfd, string newpath)

    [<DllImport("libc", SetLastError = true)>]
    extern int unlinkat(int dirfd, string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int close(int fd)

    // Linux open(2) flags. O_DIRECTORY and O_NOFOLLOW differ between x86-64 and the generic ABI of arm64.
    let private oWrOnly, oCreat, oExcl, oCloexec = 0x1, 0x40, 0x80, 0x80000
    let private flags =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.X64 -> Some(0x10000, 0x20000)
        | Architecture.Arm64 -> Some(0x4000, 0x8000)
        | _ -> None
    let private eNoEnt, eExist, eNotDir, eLoop = 2, 17, 20, 40

    /// Whether this platform writes through directory handles. Elsewhere the caller keeps its own checks.
    /// `close(-1)` only fails with EBADF; it probes that `libc` resolves (it may not on musl), so a missing
    /// library falls back to the path-based write instead of throwing from the first write.
    let supported =
        OperatingSystem.IsLinux()
        && Option.isSome flags
        && (try
                close -1 |> ignore
                true
            with
            | :? DllNotFoundException
            | :? EntryPointNotFoundException -> false)

    let private oDirectory, oNoFollow = flags |> Option.defaultValue (0, 0)

    /// An open directory handle. Disposing it closes the descriptor.
    type Handle internal (fd: int) =
        member internal _.Fd = fd
        interface IDisposable with
            member _.Dispose() = close fd |> ignore

    let private failure (operation: string) (name: string) =
        let errno = Marshal.GetLastPInvokeError()
        if errno = eLoop || errno = eNotDir then
            Error $"output must not pass through a symbolic link or reparse point; {name} is one or is not a directory."
        else
            Error $"I/O error: {operation} {name} failed: {Marshal.GetPInvokeErrorMessage errno}"

    let private openDirectory (parent: int) (name: string) (follow: bool) =
        openat(parent, name, oDirectory ||| oCloexec ||| (if follow then 0 else oNoFollow), 0)

    /// Opens `trusted` (following links: it is the anchor the caller chose) and then each of `components`
    /// below it, creating missing directories, never following a link below the anchor.
    let openBelow (trusted: string) (components: string list) : Result<Handle, string> =
        let anchor = openDirectory -100 (* AT_FDCWD *) trusted true
        if anchor < 0 then failure "open" trusted
        else
            let rec descend (current: int) (remaining: string list) =
                match remaining with
                | [] -> Ok(new Handle(current))
                | name :: rest ->
                    let mutable next = openDirectory current name false
                    if next < 0 && Marshal.GetLastPInvokeError() = eNoEnt then
                        if mkdirat(current, name, 0o777) = 0 || Marshal.GetLastPInvokeError() = eExist then
                            next <- openDirectory current name false
                    if next < 0 then
                        let result = failure "open" name
                        close current |> ignore
                        result
                    else
                        close current |> ignore
                        descend next rest
            descend anchor components

    /// Writes `content` as `name` in `directory`: to a new temporary file there first, then renamed over
    /// `name`. Both steps resolve against the same handle, and a link at `name` is replaced, not followed.
    let writeFile (directory: Handle) (name: string) (content: byte[]) : Result<unit, string> =
        // Not derived from `name`, so a target name near the 255-byte limit still fits.
        let temporary = $".nyrwvy-{Guid.NewGuid():N}.tmp"
        let fd = openat(directory.Fd, temporary, oWrOnly ||| oCreat ||| oExcl ||| oNoFollow ||| oCloexec, 0o666)
        if fd < 0 then failure "create" temporary
        else
            // The temporary file is removed on every path that does not rename it, exceptions included.
            let mutable renamed = false
            try
                let written =
                    try
                        use stream = new FileStream(new SafeFileHandle(nativeint fd, true), FileAccess.Write)
                        stream.Write(content, 0, content.Length)
                        stream.Flush true
                        Ok()
                    with
                    | :? IOException as error -> Error $"I/O error: {error.Message}"
                    | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"
                let result =
                    written |> Result.bind (fun () -> if renameat(directory.Fd, temporary, directory.Fd, name) = 0 then Ok() else failure "rename" name)
                renamed <- Result.isOk result
                result
            finally
                if not renamed then unlinkat(directory.Fd, temporary, 0) |> ignore
