import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const declarations=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),symbols=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8'))
const probe=declarations.capabilities.find(c=>c.displayName==='Probe')
const symbol=(kind,id)=>symbols[kind].find(x=>x.id===id).name
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',bool:'bool'})[t.primitive]:`NyResult<${t.arguments.map(csType).join(',')}>`
const replacements={HOST:symbol('capabilities',probe.id),METHODS:probe.operations.map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName}`).join(',')}) => ${o.displayName}(${o.parameters.map(p=>p.displayName).join(',')});`).join('\n')}
for(const f of declarations.functions) replacements[f.displayName.toUpperCase()]=symbol('functions',f.id)
const source=await readFile(join(dir,'Generated.cs'),'utf8'),before=hash(source)
const program=(await readFile(new URL('runtime.template.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,key)=>{assert.ok(replacements[key],key);return replacements[key]})
await writeFile(join(dir,'Program.cs'),program)
const options={cwd:dir,encoding:'utf8',timeout:60000,env:{...process.env,DOTNET_NOLOGO:'1',DOTNET_CLI_TELEMETRY_OPTOUT:'1'}}
const compiled=await compileRuntime(dir,options).catch(error=>{throw new Error(error.stdout||error.stderr||error.message)})
const rows=execFileSync('dotnet',[compiled.assembly,join(dir,'cases.json'),...(process.argv.includes('--baseline')?['--baseline']:[])],options).trim().split(/\r?\n/).map(x=>JSON.parse(x))
const results=rows.filter(x=>x.protect).map(({protect,...row})=>row),baseline=rows.filter(x=>x.protect===false).map(({protect,...row})=>row),controls=rows.filter(x=>x.control)
const virtual=JSON.parse(await readFile(join(dir,'evidence.json'),'utf8'))
assert.deepEqual(results,virtual.results);assert.deepEqual(baseline,virtual.baseline)
assert.equal(hash(await readFile(join(dir,'Generated.cs'))),before)
await writeFile(join(dir,'runtime-evidence.json'),JSON.stringify({results,baseline,controls,generatedSourceUnchanged:true,generatedSha256:before,compilation:compiled.evidence},null,2))
console.log(`PASS: ${results.length} protected + ${baseline.length} unshielded; ${controls.length} restoration controls; unedited C#`)
