namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
module internal CSharpBackendRuntime =
    let private read name =
        use stream = typeof<CSharpBackendProfile>.Assembly.GetManifestResourceStream("Nyrwvy.CSharp.Runtime." + name + ".cs")
        use reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8)
        reader.ReadToEnd().Split('\n') |> Array.toList

    let coreRuntime = read "Core" |> List.rev |> List.tail |> List.rev
    let listRuntime = read "List"
    let bytesRuntime = read "Bytes"
    let oneShotRuntime = read "OneShot"
    let taskRuntime = oneShotRuntime @ read "Task"
    let concurrencyRuntime = read "Concurrency"
