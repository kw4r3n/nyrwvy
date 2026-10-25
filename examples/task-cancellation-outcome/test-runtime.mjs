import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {compileRuntime} from '../shared/compile-runtime.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const d=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),symbols=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')),host=d.capabilities.find(c=>c.displayName==='Probe')
const symbol=(kind,id)=>symbols[kind].find(x=>x.id===id).name
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',bool:'bool'})[t.primitive]:t.kind==='capability'?symbol('capabilities',t.capabilityId):t.generic==='taskScope'?'NyTaskScope':t.generic==='exception'?'System.Runtime.ExceptionServices.ExceptionDispatchInfo':`${{result:'NyResult',task:'NyTask'}[t.generic]}<${t.arguments.map(csType).join(',')}>`
const replacements={HOST:symbol('capabilities',host.id),METHODS:host.operations.map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName}`).join(',')}) => ${o.displayName}(${o.parameters.map(p=>p.displayName).join(',')});`).join('\n'),CALLS:d.functions.map(f=>`public static ${csType(f.resultType)} ${f.displayName}(${f.parameters.map(p=>`${csType(p.type)} ${p.displayName}`).join(',')}) => NyFunctions.${symbol('functions',f.id)}(${f.parameters.map(p=>`${symbol('parameters',p.id)}: ${p.displayName}`).join(',')});`).join('\n')}
const program=(await readFile(new URL('./runtime.tests.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,key)=>{assert.ok(replacements[key],key);return replacements[key]})
const original=hash(await readFile(join(dir,'Generated.cs')))
await writeFile(join(dir,'Program.cs'),program)
const options={cwd:dir,encoding:'utf8',timeout:90000}
const compilation=await compileRuntime(dir,options).catch(error=>{throw new Error(error.stdout||error.stderr||error.message)})
const stdout=execFileSync('dotnet',[compilation.assembly],options)
const controls=stdout.trim().split(/\r?\n/).map(x=>JSON.parse(x))
assert.ok(controls.length>0&&controls.every(c=>c.passed))
assert.equal(hash(await readFile(join(dir,'Generated.cs'))),original)
await writeFile(join(dir,'runtime-evidence.json'),JSON.stringify({controls,sourceUnchanged:true,generatedSha256:original,compilation:compilation.evidence},null,2)+'\n')
console.log(`PASS: ${controls.length} real compiled task composition controls in unedited C#`)
