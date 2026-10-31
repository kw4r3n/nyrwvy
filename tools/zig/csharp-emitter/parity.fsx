open System
open System.IO
open System.Text.Json
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
let sid<'T> s = StableId.createForTesting<'T> s
let union<'T> s : 'T = FSharpValue.MakeUnion(FSharpType.GetUnionCases(typeof<'T>,true)[0],[|box s|],true) :?> 'T
let vid s : ExecutionValueId = union s
let bid s : ExecutionBlockId = union s
let intType = TypeRef.Primitive PrimitiveType.Int64
let stringType = TypeRef.Primitive PrimitiveType.String
let resultType= TypeRef.BuiltinGeneric(BuiltinGenericType.Result,[intType;stringType])
let origin = ExecutionOrigin.SemanticNode(sid<GraphNodeTag> "origin")
let value id t : ExecutionValue = {Id=vid id;Type=t;Origin=origin}
let rec defaultValue (t:Type) : obj =
    if t=typeof<string> then box "id"
    elif t=typeof<bool> then box true
    elif t=typeof<int64> then box Int64.MinValue
    elif t=typeof<uint64> then box 0UL
    elif t=typeof<int> then box 1
    elif t=typeof<TypeRef> then box intType
    elif FSharpType.IsUnion(t,true) then
        let case=FSharpType.GetUnionCases(t,true)[0]
        FSharpValue.MakeUnion(case,case.GetFields() |> Array.map(fun p -> defaultValue p.PropertyType),true)
    elif FSharpType.IsTuple t then FSharpValue.MakeTuple(FSharpType.GetTupleElements t |> Array.map defaultValue,t)
    elif FSharpType.IsRecord(t,true) then FSharpValue.MakeRecord(t,FSharpType.GetRecordFields(t,true) |> Array.map(fun p -> defaultValue p.PropertyType),true)
    else failwithf "No default for %s" t.FullName
let args=fsi.CommandLineArgs |> Array.skip 1
if args.Length=0 then failwith "Pass the standalone csharp-emitter executable, followed by optional snapshot files."
let emitter=Path.GetFullPath args[0]
let scratch=Path.Combine(Path.GetTempPath(),"nyrwvy-csharp-parity-"+Guid.NewGuid().ToString("N"))
Directory.CreateDirectory scratch |> ignore
let symbols (xs:CSharpSymbol<StableId<'Tag>> list) = xs |> List.map(fun x -> {|id=StableId.value x.Id;name=x.Name|})
let expectedSymbols (x:CSharpSymbolManifest) =
    JsonSerializer.Serialize({|namespaceName=x.Namespace;functionContainer=x.FunctionContainer;types=symbols x.Types;fields=symbols x.Fields;variants=symbols x.Variants;functions=symbols x.Functions;parameters=symbols x.Parameters;capabilities=symbols x.Capabilities;capabilityOperations=symbols x.CapabilityOperations;capabilityParameters=symbols x.CapabilityParameters;capabilityAdapters=symbols x.CapabilityAdapters|}).Replace("\"namespaceName\":","\"namespace\":")
let profileJson (p:CSharpBackendProfile) =
    let bindings=p.CapabilityBindings |> List.map(fun x ->
        let kind=match x.Kind with CSharpCapabilityBindingKind.ExternalImplementation -> "externalImplementation" | CSharpCapabilityBindingKind.SystemConsoleWriteLine -> "systemConsoleWriteLine" | _ -> failwith "Native fixtures belong to native-parity.fsx"
        {|operationId=StableId.value x.OperationId;kind=kind|})
    JsonSerializer.Serialize({|capabilityBindings=bindings;concurrentCapabilityIds=p.ConcurrentCapabilityIds |> Set.toList |> List.map StableId.value|})
let mutable count=0
let check label (program:ExecutionProgram) profile =
    let irPath=Path.Combine(scratch,"ir.json")
    let profilePath=Path.Combine(scratch,"profile.json")
    File.WriteAllText(irPath,ExecutionIrSerializer.serialize program)
    File.WriteAllText(profilePath,profileJson profile)
    let start=System.Diagnostics.ProcessStartInfo(emitter)
    start.ArgumentList.Add irPath
    start.ArgumentList.Add profilePath
    start.RedirectStandardOutput<-true
    start.RedirectStandardError<-true
    use child=System.Diagnostics.Process.Start(start)
    let output=child.StandardOutput.ReadToEndAsync()
    let errors=child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    if child.ExitCode<>0 then failwithf "%s: emitter exited %d: %s" label child.ExitCode errors.Result
    use actual=JsonDocument.Parse output.Result
    let root=actual.RootElement
    let equalJson x y = System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(x:string),System.Text.Json.Nodes.JsonNode.Parse(y:string))
    match CSharpBackend.emit program profile with
    | Ok expected ->
        let source=root.GetProperty("source").GetString()
        if source<>expected.Source then
            File.WriteAllText(Path.Combine(scratch,"expected.cs"),expected.Source)
            File.WriteAllText(Path.Combine(scratch,"actual.cs"),source)
            failwithf "%s: source bytes differ; artifacts in %s" label scratch
        if not(equalJson (expectedSymbols expected.Symbols) (root.GetProperty("symbols").GetRawText())) then failwithf "%s: symbol manifest differs" label
    | Error expected ->
        let case, _=FSharpValue.GetUnionFields(expected,typeof<CSharpBackendError>)
        let code,message,details =
            match expected with
            | CSharpBackendError.ConcurrentCapabilityBindingRequired(fn,node,cap,op) ->
                "ConcurrentCapabilityBindingRequired","An operation reachable from a ParallelAll or TaskStart worker requires a capability binding that supports concurrent calls.",JsonSerializer.Serialize({|functionId=StableId.value fn;nodeId=StableId.value node;capabilityId=StableId.value cap;operationId=StableId.value op|})
            | CSharpBackendError.IncompatibleNativeBinding(op,reason) -> "BackendFailure","IncompatibleNativeBinding: "+reason,JsonSerializer.Serialize({|operationId=StableId.value op|})
            | _ -> "BackendFailure",case.Name,"null"
        if root.GetProperty("code").GetString()<>code || root.GetProperty("message").GetString()<>message || not(equalJson details (root.GetProperty("details").GetRawText())) then failwithf "%s: failure differs from %A: %s" label expected output.Result
    count<-count+1
for case in FSharpType.GetUnionCases(typeof<ExecutionInstructionKind>) do
    try
        let raw=FSharpValue.MakeUnion(case,case.GetFields() |> Array.map(fun p -> defaultValue p.PropertyType)) :?> ExecutionInstructionKind
        let kind=match raw with
                 | ExecutionInstructionKind.CapabilityConstruct _ -> ExecutionInstructionKind.CapabilityConstruct(sid "id",[(sid "id",sid "id")])
                 | other -> other
        let n=case.Name
        let inputType = if n="ParallelAll" then TypeRef.BuiltinGeneric(BuiltinGenericType.List,[intType]) elif n="CapabilityInvoke" then TypeRef.Capability(sid "id") elif n="ExtractUnionField" then TypeRef.Named(sid "id",[]) else intType
        let returnType=if n="ParallelAll" then TypeRef.BuiltinGeneric(BuiltinGenericType.Result,[TypeRef.BuiltinGeneric(BuiltinGenericType.List,[intType]);stringType]) elif n="ProductConstruct" || n="UnionConstruct" then TypeRef.Named(sid "id",[]) elif n.StartsWith("Option") then TypeRef.BuiltinGeneric(BuiltinGenericType.Option,[intType]) elif n.StartsWith("Result") || n="UseResource" then resultType else intType
        let instruction:ExecutionInstruction={Result=value "result" returnType;OriginNodeId=sid "origin";Kind=kind}
        let paramCount=if ["ListFold";"ListScan";"ListEffectFold";"SetFold";"ListZipWith";"ListSortBy";"ListMinBy";"ListMaxBy"] |> List.contains n then 2 elif n="MapFold" then 3 else 1
        let callback:ExecutionFunction={SemanticFunctionId=sid "id";TypeParameters=[];Parameters=[for j in 0..paramCount-1 -> {SemanticParameterId=sid ("p"+string j);Value=value ("cb"+string j) intType}];ResultType=intType;RequiredEffects=[];EntryBlockId=bid "cb";Blocks=[{Id=bid "cb";Parameters=[];Instructions=[];Terminator=ExecutionTerminator.Return(vid "cb0")}]}
        let main:ExecutionFunction={SemanticFunctionId=sid "main";TypeParameters=[];Parameters=[];ResultType=returnType;RequiredEffects=[];EntryBlockId=bid "entry";Blocks=[{Id=bid "entry";Parameters=[value "id" inputType];Instructions=[instruction];Terminator=ExecutionTerminator.Return(vid "result")}]}
        let operation:ExecutionCapabilityOperation={Id=sid "id";Parameters=[{Id=sid "cp";Type=intType}];ResultType=TypeRef.Primitive PrimitiveType.Unit;FailureType=None;EffectId=sid "effect"}
        let program:ExecutionProgram={SourceSnapshotId=sid "snapshot";Types=[{Id=sid "id";TypeParameters=[];Kind=ExecutionTypeKind.Product[]}];Effects=[];Capabilities=[{Id=sid "id";Operations=[operation]}];Functions=[main;callback]}
        let profile=CSharpBackendProfile.externalImplementations [sid "id"] |> CSharpBackendProfile.withConcurrentCapabilities [sid "id"]
        check n program profile
        if n<>"ParallelAll" then
            let bytesMain={main with Blocks=main.Blocks |> List.map(fun block -> {block with Parameters=block.Parameters |> List.map(fun v -> {v with Type=TypeRef.Primitive PrimitiveType.Bytes})})}
            check (n+" Bytes operands") {program with Functions=[bytesMain;callback]} profile
    with ex -> failwithf "%s: %s" case.Name ex.Message


// These contract probes deliberately construct Execution IR directly. They test emitter
// formatting, validation precedence and ownership choices; graph execution has its own suite.
let empty : ExecutionProgram={SourceSnapshotId=sid "snapshot";Types=[];Effects=[];Capabilities=[];Functions=[]}
let instruction name typ kind : ExecutionInstruction={Result=value name typ;OriginNodeId=sid name;Kind=kind}
let block name parameters instructions terminator : ExecutionBlock={Id=bid name;Parameters=parameters;Instructions=instructions;Terminator=terminator}
let functionWith name parameters result (blocks:ExecutionBlock list) : ExecutionFunction={SemanticFunctionId=sid name;TypeParameters=[];Parameters=parameters;ResultType=result;RequiredEffects=[];EntryBlockId=(List.head blocks).Id;Blocks=blocks}
let entry instructions result = functionWith "main" [] result [block "entry" [value "input" intType] instructions (ExecutionTerminator.Return(vid "result"))]
let intInstruction kind = instruction "result" intType kind
let external op : CSharpCapabilityBinding={OperationId=sid op;Kind=CSharpCapabilityBindingKind.ExternalImplementation}
let console op : CSharpCapabilityBinding={OperationId=sid op;Kind=CSharpCapabilityBindingKind.SystemConsoleWriteLine}
let op name typ result : ExecutionCapabilityOperation={Id=sid name;Parameters=[{Id=sid "parameter";Type=typ}];ResultType=result;FailureType=None;EffectId=sid "effect"}
let cap name ops : ExecutionCapability={Id=sid name;Operations=ops}
let profile bindings concurrent : CSharpBackendProfile={CapabilityBindings=bindings;ConcurrentCapabilityIds=concurrent |> List.map sid |> Set.ofList}
check "empty" empty CSharpBackendProfile.empty
check "UTF16 symbol ordering and string escaping" {empty with Functions=[for n in ["\U00010000";"\uE000";"z";"é"] -> functionWith n [] stringType [block "é" [] [instruction "result" stringType (ExecutionInstructionKind.StringLiteral "\r\n\t\"\\\u0000\u2028é\U0001F642")] (ExecutionTerminator.Return(vid "result"))]]} CSharpBackendProfile.empty
let typ id kind : ExecutionType={Id=sid id;TypeParameters=[];Kind=kind}
let field id typ : ExecutionField={Id=sid id;Type=typ}
let recursive=typ "recursive" (ExecutionTypeKind.Product[field "self" (TypeRef.BuiltinGeneric(BuiltinGenericType.Option,[TypeRef.Named(sid "recursive",[])]))])
let finite=typ "finite" (ExecutionTypeKind.Product[field "items" (TypeRef.BuiltinGeneric(BuiltinGenericType.List,[TypeRef.Named(sid "finite",[])]))])
let genericType={typ "generic" (ExecutionTypeKind.Union[{Id=sid "variant";Fields=[field "x" (TypeRef.TypeParameter(sid "tp"))]}]) with TypeParameters=[(sid "tp","T")]}
check "recursive products and generic union" {empty with Types=[recursive;finite;genericType]} CSharpBackendProfile.empty
for generic in [BuiltinGenericType.Task;BuiltinGenericType.OneShot;BuiltinGenericType.Exception;BuiltinGenericType.TaskScope] do
    let args=match generic with BuiltinGenericType.Task|BuiltinGenericType.OneShot -> [intType] | _ -> []
    check ("declared runtime type "+string generic) {empty with Types=[typ "runtime" (ExecutionTypeKind.Product[field "value" (TypeRef.BuiltinGeneric(generic,args))])]} CSharpBackendProfile.empty
let par id typ : ExecutionParameter={SemanticParameterId=sid id;Value=value id typ}
let callback={functionWith "callback" [par "z" (TypeRef.TypeParameter(sid "B"));par "a" intType] intType [block "callback" [] [] (ExecutionTerminator.Return(vid "a"))] with TypeParameters=[sid "B","B";sid "A","A"]}
let callMain=entry [intInstruction(ExecutionInstructionKind.ListMap(vid "input",sid "callback",[{TargetParameterId=sid "a";ValueId=vid "input"}]))] intType
check "generic contextual callback declaration order" {empty with Functions=[callMain;callback]} CSharpBackendProfile.empty
let tail=functionWith "tail" [par "z" intType;par "a" intType] intType [block "entry" [] [] (ExecutionTerminator.TailCall(sid "tailcall",[{TargetParameterId=sid "z";ValueId=vid "a"};{TargetParameterId=sid "a";ValueId=vid "z"}]))]
check "tail-call parallel assignment" {empty with Functions=[tail]} CSharpBackendProfile.empty
let jump=functionWith "jump" [] intType [block "entry" [value "a" intType;value "z" intType] [] (ExecutionTerminator.Jump(bid "next",[{TargetValueId=vid "a";ValueId=vid "z"};{TargetValueId=vid "z";ValueId=vid "a"}]));block "next" [] [] (ExecutionTerminator.Return(vid "a"))]
check "jump parallel assignment" {empty with Functions=[jump]} CSharpBackendProfile.empty
let probeCap=cap "cap" [op "a" stringType (TypeRef.Primitive PrimitiveType.Unit);op "z" intType intType]
let probe={empty with Capabilities=[probeCap]}
check "duplicate binding precedence" probe (profile [external "unknown";external "unknown"] [])
check "unknown binding precedence" probe (profile [external "unknown"] [])
check "missing adapter binding" probe (profile [external "a"] [])
check "mixed binding kinds before console signature" probe (profile [console "z";external "a"] [])
check "console compatibility" {probe with Capabilities=[cap "cap" [op "z" intType intType]]} (profile [console "z"] [])
check "unbound concurrent capability" probe (profile [] ["cap"])
let unknown=entry [intInstruction(ExecutionInstructionKind.DirectCall(sid "unknown",[]));intInstruction(ExecutionInstructionKind.CapabilityInvoke(vid "input",sid "unknown",[]))] intType
check "unknown call before unknown operation" {probe with Functions=[unknown]} CSharpBackendProfile.empty
let invoke=entry [intInstruction(ExecutionInstructionKind.CapabilityInvoke(vid "input",sid "a",[]))] intType
check "missing used binding" {probe with Functions=[invoke]} CSharpBackendProfile.empty
check "unknown operation" {empty with Functions=[invoke]} CSharpBackendProfile.empty
let failureOp={op "a" stringType intType with FailureType=Some stringType}
check "unsupported operation failure" {empty with Capabilities=[cap "cap" [failureOp]]} CSharpBackendProfile.empty
let worker=functionWith "worker" [par "item" intType] resultType [block "worker" [value "capval" (TypeRef.Capability(sid "cap"))] [intInstruction(ExecutionInstructionKind.CapabilityInvoke(vid "capval",sid "a",[]))] (ExecutionTerminator.Return(vid "result"))]
let concurrentMain=entry [instruction "result" (TypeRef.BuiltinGeneric(BuiltinGenericType.Result,[TypeRef.BuiltinGeneric(BuiltinGenericType.List,[intType]);stringType])) (ExecutionInstructionKind.ParallelAll(vid "input",sid "worker",[]))] resultType
check "worker concurrent capability required" {probe with Functions=[concurrentMain;worker]} (profile [external "a";external "z"] [])
check "invalid assertion precedes worker requirement" {probe with Functions=[concurrentMain;worker]} (profile [external "a";external "z"] ["unknown"])
let bytesType=TypeRef.Primitive PrimitiveType.Bytes
let update=instruction "updated" bytesType (ExecutionInstructionKind.BytesAppend(vid "bytes",vid "number"))
let read=instruction "length" intType (ExecutionInstructionKind.BytesLength(vid "bytes"))
for after in [false;true] do
    let instructions=if after then [update;read] else [read;update]
    let bytes=functionWith "bytes" [] bytesType [block "entry" [value "bytes" bytesType;value "number" intType] instructions (ExecutionTerminator.Return(vid "updated"))]
    check ("bytes later use "+string after) {empty with Functions=[bytes]} CSharpBackendProfile.empty
let alias=instruction "alias" bytesType (ExecutionInstructionKind.ReadParameter(vid "bytes"))
let useAlias=instruction "length" intType (ExecutionInstructionKind.BytesLength(vid "alias"))
let escaped=instruction "escaped" intType (ExecutionInstructionKind.ListConstruct[vid "bytes"])
let bytesBlocks=[block "entry" [value "bytes" bytesType;value "number" intType] [alias;escaped;update] (ExecutionTerminator.Jump(bid "after",[]));block "after" [] [useAlias] (ExecutionTerminator.Return(vid "updated"))]
check "bytes alias, escape and successor use" {empty with Functions=[functionWith "bytes" [] bytesType bytesBlocks]} CSharpBackendProfile.empty
let byteTail=functionWith "bytes" [par "a" bytesType;par "b" bytesType] bytesType [block "entry" [] [instruction "alias" bytesType (ExecutionInstructionKind.ReadParameter(vid "a"))] (ExecutionTerminator.TailCall(sid "tail",[{TargetParameterId=sid "a";ValueId=vid "a"};{TargetParameterId=sid "b";ValueId=vid "alias"}]))]
check "bytes tail argument alias releases" {empty with Functions=[byteTail]} CSharpBackendProfile.empty

for path in args |> Array.skip 1 do
    let text=File.ReadAllText path
    let snapshot=if path.EndsWith(".nyrec") then RecordCodec.deserialize text |> Result.toOption else SnapshotCodec.deserialize text |> Result.toOption
    let program=snapshot |> Option.bind(TypedSemanticIr.project >> Result.toOption) |> Option.bind(ExecutionIr.lower >> Result.toOption) |> Option.defaultWith(fun () -> failwithf "Could not lower %s" path)
    let ops=program.Capabilities |> List.collect(fun cap -> cap.Operations |> List.map(fun op -> op.Id))
    let caps=program.Capabilities |> List.filter(fun cap -> not cap.Operations.IsEmpty) |> List.map(fun cap -> cap.Id)
    check path program (CSharpBackendProfile.externalImplementations ops |> CSharpBackendProfile.withConcurrentCapabilities caps)
printfn "C# emitter parity: %d source/manifest/failure fixtures passed." count
Directory.Delete(scratch,true)
