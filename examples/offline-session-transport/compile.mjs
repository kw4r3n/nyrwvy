import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
const dir=resolve(process.argv[2])
const d=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),symbols=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8'))
const find=(items,name)=>items.find(x=>x.displayName===name),symbol=(kind,id)=>symbols[kind].find(x=>x.id===id).name
const host=find(d.capabilities,'Host'),run=find(d.functions,'wireRun'),transport=find(d.capabilities,'Transport')
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',int64:'long',bool:'bool'})[t.primitive]:`${({result:'NyResult',list:'NyList'})[t.generic]}<${t.arguments.map(csType).join(',')}>`
const names={open:'Open',close:'Close',workspaceCreate:'WorkspaceCreate',workspaceDiscard:'WorkspaceDiscard',provider:'Provider',memberClose:'MemberClose',snapshot:'Snapshot',checks:'Checks',gate:'Gate',reply:'Reply',step:'Step'}
const replacements={HOST:symbol('capabilities',host.id),RUN:symbol('functions',run.id),RUN_HOST:symbol('parameters',find(run.parameters,'host').id),RUN_TRANSPORT:symbol('parameters',find(run.parameters,'transport').id),RUN_WRONG:symbol('parameters',find(run.parameters,'wrongBoundary').id),TRANSPORT:symbol('capabilities',transport.id),READ:symbol('capabilityOperations',transport.operations[0].id),
 METHODS:host.operations.map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName==='base'?'basis':p.displayName}`).join(',')}) => ${names[o.displayName]}(${o.parameters.map(p=>p.displayName==='base'?'basis':p.displayName).join(',')});`).join('\n')}
// Reuse the maintained synthetic effects fixture, adding pipe observations and
// output only. No host Team state, tickets, command dispatch or ledger is added.
let effects=(await readFile(new URL('../offline-leader-jsonrpc/runtime.template.cs',import.meta.url),'utf8')).split('static class Program')[0]
effects=effects.replace('public Effects(JsonElement scenario){','readonly string control;\n public Effects(JsonElement scenario,string control){this.control=control;')
effects=effects.replace('void Log(string op,params (string Name,object Value)[] args)=>calls.Enqueue(new {operation=op,request=requestLabel.Value,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()});',`public static void Observe(object value){lock(typeof(Effects)){Console.Error.WriteLine(JsonSerializer.Serialize(value));}}
 void Log(string op,params (string Name,object Value)[] args){var row=new {operation=op,request=requestLabel.Value,arguments=args.Select(a=>new {parameter=a.Name,value=new {value=a.Value}}).ToArray()};calls.Enqueue(row);Observe(row);}
 public void ReleaseGates(){snapshotReleased.Set();commitReleased.Set();spawnReleased.Set();closeReleased.Set();}
 public void InputEnded(){Observe(new {operation="input-eof"});if(Setting("releaseOn")=="eof")ReleaseGates();}`)
effects=effects.replace('Log("close");Closed++;','Log("close");Closed++;if(control=="double-close"){Log("close");Closed++;Observe(new {operation="duplicate-close",count=Closed});}')
effects=effects.replace('Log("reply",("label",label.ToString()),("record",record.ToString()));return NyUnit.Value;',`Log("reply",("label",label.ToString()),("record",record.ToString()));
  if(label.ToString()=="@wire"||label.ToString()=="@wire-terminal"){
   string output=record.ToString();
   if(control=="wrong-id" && label.ToString()=="@wire") {using var json=JsonDocument.Parse(output);var id=json.RootElement.GetProperty("id").GetRawText();output=output.Replace("\\\"id\\\":"+id,"\\\"id\\\":\\\"wrong\\\"");Observe(new {operation="response-id-rewritten",before=record.ToString(),after=output});}
   lock(typeof(Effects)){Console.Out.WriteLine(output);Console.Out.Flush();}
  }return NyUnit.Value;`)
const template=effects+await readFile(new URL('./pipe.template.cs',import.meta.url),'utf8')
const program=template.replace(/@@([A-Z_]+)@@/g,(_,key)=>{assert.ok(replacements[key],key);return replacements[key]})
assert.ok(program.includes('Observe(row)'),'fixture observation insertion')
await writeFile(join(dir,'Program.cs'),program)
const compilation=await compileRuntime(dir,{cwd:dir,encoding:'utf8',timeout:90000}).catch(error=>{throw new Error(error.stdout||error.stderr||error.message)})
await writeFile(join(dir,'compilation.json'),JSON.stringify(compilation.evidence,null,2)+'\n')
console.log('PASS: offline Roslyn compilation with unedited Generated.cs')
