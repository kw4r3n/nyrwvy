// Separate mechanical binding probe. It does not add an application command or
// edit Generated.cs. Reflect the actual compiled binding after validated release.
import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,readdir,cp,stat} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {execFileSync} from 'node:child_process'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
import {store,slot} from './continuation-fixture.mjs'
import {inspect,hash,git} from './git-fixture.mjs'
const [bundle,seed,out]=process.argv.slice(2,5).map(x=>resolve(x)),control=process.argv.includes('--control')
await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[])
const storage=join(seed,'firewall-'+hash(out).slice(0,12));assert.equal(await stat(storage).then(()=>true,error=>{if(error.code==='ENOENT')return false;throw error}),false);await cp(join(seed,'state'),storage,{recursive:true})
const original=await store(join(seed,'state')),before=await store(storage),physicalBefore=await inspect(join(seed,'git'))
const row=JSON.parse(await readFile(join(seed,'row-1.json'))),scenario={...row.scenario,storageDir:storage,crashWindow:''}
const intent=JSON.stringify({commit:scenario.snapshotCommit,integrated:git(join(seed,'git'),['rev-parse','HEAD'])}),value=JSON.stringify({phase:'pending',token:'firewall-probe',intent})
await writeFile(join(out,'scenario.json'),JSON.stringify(scenario));await writeFile(join(out,'write.json'),value)
const source=`using System;using System.IO;using System.Text.Json;using System.Reflection;using System.Runtime.Loader;
static class Program{static void Main(string[] args){
 var context=new AssemblyLoadContext("binding-probe",true);var assembly=context.LoadFromAssemblyPath(args[0]);var effects=assembly.GetType("Effects")!;var text=assembly.GetType("Nyrwvy.Generated.NyStr")!;var convert=text.GetMethod("op_Implicit",new[]{typeof(string)})!;
 object Str(string value)=>convert.Invoke(null,new object[]{value})!;
 using var document=JsonDocument.Parse(File.ReadAllText(args[1]));var host=Activator.CreateInstance(effects,new object[]{document.RootElement,""})!;
 effects.GetMethod("StorageRead")!.Invoke(host,new[]{Str("registry/v1")});
 string error="";try{effects.GetMethod("StorageWrite")!.Invoke(host,new[]{Str("registry/v1"),Str(File.ReadAllText(args[2]))});}catch(TargetInvocationException failure){error=failure.InnerException!.Message;}
 Console.WriteLine(JsonSerializer.Serialize(new{error}));
}}`
await writeFile(join(out,'Generated.cs'),'// Separate reflection probe; no application-generated source is edited.\n');await writeFile(join(out,'Program.cs'),source)
const build=await compileRuntime(out,{cwd:out,encoding:'utf8',timeout:90000})
const execution=execFileSync('dotnet',[join(out,'Runtime.dll'),join(bundle,'Runtime.dll'),join(out,'scenario.json'),join(out,'write.json')],{encoding:'utf8',timeout:30000,stdio:['ignore','pipe','pipe']})
const result=JSON.parse(execution.trim()),after=await store(storage),physicalAfter=await inspect(join(seed,'git'));assert.match(result.error,/released observation admission cannot write/);assert.deepEqual(physicalAfter,physicalBefore);assert.deepEqual(await store(join(seed,'state')),original)
const changed=Object.keys({...before,...after}).filter(k=>before[k]!==after[k]);assert.equal(changed.length,control?1:0);if(control)assert.equal(changed[0],slot('physical-plan/v1/firewall-probe'))
const proof={control,mechanicalProcesses:1,disposableDomainsReused:1,newGitDomains:0,applicationGraphRuns:0,bindingHash:hash(await readFile(join(bundle,'Runtime.dll'))),sourceHash:hash(await readFile(new URL('./test-admission-firewall.mjs',import.meta.url))),reflectionSourceHash:hash(source),compilation:build.evidence,result,before,after,physicalBefore,physicalAfter,originalSeedPreserved:true,changed};await writeFile(join(out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(control?'PASS: retained late-firewall control writes one new plan before refusal':'PASS: admission firewall refuses before any application plan/storage effect')
