// dotnet fsi resource-parity.fsx BASELINE_BACKEND.dll BASELINE_SESSION.dll CURRENT_BACKEND.dll [CURRENT_SESSION.dll]
open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Security.Cryptography
open System.Text
let args=fsi.CommandLineArgs |> Array.skip 1
if args.Length<3 || args.Length>4 then failwith "Expected baseline backend/session and current backend DLL paths, optionally current session."
let flags=BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static
let load path =
    let path=Path.GetFullPath path
    let context=new AssemblyLoadContext(Guid.NewGuid().ToString(),true)
    context.add_Resolving(fun context name ->
        let candidate=Path.Combine(Path.GetDirectoryName path,name.Name+".dll")
        if File.Exists candidate then context.LoadFromAssemblyPath candidate else null)
    context,context.LoadFromAssemblyPath path
let originalContext,original=load args[0]
let currentContext,current=load args[2]
let runtime=Path.Combine(__SOURCE_DIRECTORY__,"runtime")
let sha (text:string)=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()
let assertText name expected actual =
    if expected<>actual then failwithf "%s differs: original %s, current %s" name (sha expected) (sha actual)
    printfn "%s %s" name (sha actual)
let lines (assembly:Assembly) name =
    let typ=assembly.GetType("Nyrwvy.Core.CSharpBackendRuntime",true)
    let value=typ.GetProperty(name,flags).GetValue(null) :?> System.Collections.IEnumerable
    value |> Seq.cast<string> |> String.concat "\n"
for name,file in ["listRuntime","List";"bytesRuntime","Bytes";"oneShotRuntime","OneShot";"taskRuntime","Task";"concurrencyRuntime","Concurrency"] do
    let expected=lines original name
    let resourceExpected=if file="Task" then expected.Substring((lines original "oneShotRuntime").Length+1) else expected
    assertText (file+" resource") resourceExpected (File.ReadAllText(Path.Combine(runtime,file+".cs")))
    assertText (file+" F# after extraction") expected (lines current name)
let core (assembly:Assembly) =
    let backend=assembly.GetType("Nyrwvy.Core.CSharpBackend",true)
    let writerType=backend.GetNestedType("SourceWriter",BindingFlags.Public ||| BindingFlags.NonPublic)
    let writer=Activator.CreateInstance(writerType,true)
    backend.GetMethod("emitRuntime",flags).Invoke(null,[|writer|]) |> ignore
    writer.ToString()
let originalCore=core original
let suffix=lines original "listRuntime"+"\n\n"+lines original "bytesRuntime"+"\n\n"
if not(originalCore.EndsWith suffix) then failwith "Original Core no longer ends with List and Bytes runtime blocks."
assertText "Core resource" (originalCore.Substring(0,originalCore.Length-suffix.Length)) (File.ReadAllText(Path.Combine(runtime,"Core.cs")))
assertText "Core F# after extraction" originalCore (core current)
let worker (assembly:Assembly) = assembly.GetType("Nyrwvy.Tools.AuthoringSession.PureRunWorkerProgram",true).GetProperty("source",flags).GetValue(null) :?> string
let originalSessionContext,originalSession=load args[1]
let originalWorker=worker originalSession
assertText "Worker resource" originalWorker (File.ReadAllText(Path.Combine(runtime,"Worker.cs")))
if args.Length=4 then
    let context,session=load args[3]
    assertText "Worker F# after extraction" originalWorker (worker session)
    context.Unload()
originalContext.Unload()
currentContext.Unload()
originalSessionContext.Unload()
