import assert from 'node:assert/strict'
import {mkdtemp,copyFile,readFile,writeFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {createHash} from 'node:crypto'
import {execFileSync} from 'node:child_process'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
import {validate} from './cases.mjs'
const input=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const graph=JSON.parse(await readFile(join(input,'followup.json'),'utf8')),manifest=JSON.parse(await readFile(join(input,'symbols.json'),'utf8')),authoring=JSON.parse(await readFile(join(input,'authoring.json'),'utf8'))
assert.equal(hash(await readFile(join(input,'Generated.cs'))),authoring.generatedSha256)
const symbol=(kind,id)=>manifest[kind].find(x=>x.id===id).name,find=(items,name)=>items.find(x=>x.displayName===name)
const host=find(graph.capabilities,'Host'),provider=find(graph.capabilities,'TaggedProvider'),run=find(graph.functions,'followupRun'),observation=find(graph.types,'ProviderObservation')
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',int64:'long',bool:'bool'})[t.primitive]:t.kind==='named'?symbol('types',t.typeId):`NyResult<${t.arguments.map(csType).join(',')}>`
const names={open:'Open',close:'Close',workspaceCreate:'WorkspaceCreate',workspaceDiscard:'WorkspaceDiscard',provider:'Provider',memberClose:'MemberClose',snapshot:'Snapshot',checks:'Checks',gate:'Gate',reply:'Reply',run:'TaggedRun'}
const variables={HOST:symbol('capabilities',host.id),TAGGED:symbol('capabilities',provider.id),OBSERVATION:symbol('types',observation.id),OBS_KIND:symbol('fields',find(observation.fields,'kind').id),OBS_CAUSE:symbol('fields',find(observation.fields,'cause').id),RUN:symbol('functions',run.id),
 METHODS:[...host.operations,...provider.operations].map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName==='base'?'basis':p.displayName}`).join(',')}) => ${names[o.displayName]}(${o.parameters.map(p=>p.displayName==='base'?'basis':p.displayName).join(',')});`).join('\n')}
for(const parameter of run.parameters)variables[`RUN_${parameter.displayName.toUpperCase()}`]=symbol('parameters',parameter.id)
const program=(await readFile(new URL('./runtime.template.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,k)=>{assert.ok(variables[k],k);return variables[k]})
const scratch=await mkdtemp(join(tmpdir(),'member-followup-runtime-'))
try{
 await copyFile(join(input,'Generated.cs'),join(scratch,'Generated.cs'));await writeFile(join(scratch,'Program.cs'),program)
 const options={cwd:scratch,encoding:'utf8',env:{...process.env,DOTNET_NOLOGO:'1',DOTNET_CLI_TELEMETRY_OPTOUT:'1'},timeout:90000}
 const {assembly,evidence:compilation}=await compileRuntime(scratch,options),stdout=execFileSync('dotnet',[assembly,join(input,'cases.json')],options)
 const results=stdout.trim().split(/\r?\n/).map(x=>JSON.parse(x)),cases=JSON.parse(await readFile(join(input,'cases.json'),'utf8'))
 assert.equal(results.length,cases.length)
 for(const row of results){const c=cases.find(x=>x.id===row.id);row.checks=validate(c,row.response);assert.equal(row.MaxActive,2);assert.equal(row.Opened,1);assert.equal(row.Closed,1);if(!c.fault)assert.equal(row.MonitorPublished,1,'monitor joined even after caller or parent cancellation');console.log(`PASS: real follow-up ${row.id}`)}
 assert.equal(hash(await readFile(join(scratch,'Generated.cs'))),authoring.generatedSha256);assert.equal(hash(await readFile(join(input,'Generated.cs'))),authoring.generatedSha256)
 await writeFile(join(input,'runtime-evidence.json'),JSON.stringify({generatedSha256:authoring.generatedSha256,sourceUnchanged:true,compilation,results},null,2));await writeFile(join(input,'Runtime.Program.cs'),program)
}finally{await rm(scratch,{recursive:true,force:true})}
