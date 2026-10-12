import assert from 'node:assert/strict'
import {mkdtemp,copyFile,readFile,writeFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {createHash} from 'node:crypto'
import {execFileSync} from 'node:child_process'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
import {validate} from './cases.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const graph=JSON.parse(await readFile(join(dir,'integration.json'),'utf8')),symbols=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8'))
const meta=JSON.parse(await readFile(join(dir,'authoring.json'),'utf8')),cases=JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))
assert.equal(hash(await readFile(join(dir,'Generated.cs'))),meta.generatedSha256)
const find=(xs,name)=>xs.find(x=>x.displayName===name),sym=(kind,id)=>symbols[kind].find(x=>x.id===id).name
const host=find(graph.capabilities,'Git'),run=find(graph.functions,'run'),entry=find(graph.types,'Candidate'),out=find(graph.types,'Outcome')
const vars={HOST:sym('capabilities',host.id),STEP:sym('capabilityOperations',host.operations[0].id),RUN:sym('functions',run.id),CANDIDATE:sym('types',entry.id),
 CANDIDATE_ARGS:entry.fields.map(f=>`${sym('fields',f.id)}: ${f.type.primitive==='bool'?`e.GetProperty("${f.displayName}").GetBoolean()`:`(NyStr)e.GetProperty("${f.displayName}").GetString()!`}`).join(','),
 ENTRY_FIELDS:entry.fields.map(f=>`new {field="${f.displayName}",value=${f.type.primitive==='bool'?`new {kind="bool",value=entry.${sym('fields',f.id)}}`:`S(entry.${sym('fields',f.id)}.ToString())`}}`).join(','),OUT_RESULT:sym('fields',find(out.fields,'result').id),OUT_CANDIDATE:sym('fields',find(out.fields,'candidate').id),
 ...Object.fromEntries(run.parameters.map(p=>['RUN_'+p.displayName.toUpperCase(),sym('parameters',p.id)]))}
const program=(await readFile(new URL('./runtime.template.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,k)=>{assert.ok(vars[k],k);return vars[k]})
const scratch=await mkdtemp(join(tmpdir(),'integration-runtime-'))
try{
 await copyFile(join(dir,'Generated.cs'),join(scratch,'Generated.cs'));await writeFile(join(scratch,'Program.cs'),program)
 const opts={cwd:scratch,encoding:'utf8',env:{...process.env,DOTNET_NOLOGO:'1',DOTNET_CLI_TELEMETRY_OPTOUT:'1'},timeout:90000}
 const {assembly,evidence:compilation}=await compileRuntime(scratch,opts)
 const rows=execFileSync('dotnet',[assembly,join(dir,'cases.json')],opts).trim().split(/\r?\n/).map(x=>JSON.parse(x))
 assert.equal(rows.length,cases.length)
 const results=rows.map(row=>{const check=validate(cases.find(c=>c.id===row.id),row.response);console.log('PASS: unedited C# '+row.id);return check})
 assert.equal(hash(await readFile(join(scratch,'Generated.cs'))),meta.generatedSha256)
 assert.equal(hash(await readFile(join(dir,'Generated.cs'))),meta.generatedSha256)
 await writeFile(join(dir,'runtime-evidence.json'),JSON.stringify({compilation,generatedSha256:meta.generatedSha256,sourceUnchanged:true,results},null,2))
}finally{await rm(scratch,{recursive:true,force:true})}
