import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {compileRuntime} from '../shared/compile-runtime.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const d=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),symbols=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')),host=d.capabilities.find(c=>c.displayName==='Probe')
const symbol=(kind,id)=>symbols[kind].find(x=>x.id===id).name
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',bool:'bool'})[t.primitive]:`NyResult<${t.arguments.map(csType).join(',')}>`
const replacements={HOST:symbol('capabilities',host.id),METHODS:host.operations.map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName}`).join(',')}) => ${o.displayName}(${o.parameters.map(p=>p.displayName).join(',')});`).join('\n')}
for(const f of d.functions)replacements[f.displayName.toUpperCase()]=symbol('functions',f.id)
// Reuse the maintained generic shield's mock host and its explicit held cleanup gates.
let probe=(await readFile(new URL('../cleanup-shield/runtime.template.cs',import.meta.url),'utf8')).split('static class Program')[0]
probe=probe.replace('public bool Throw;','public bool Throw=false;').replace('public bool Owned, OwnedFault;','public bool Owned, OwnedFault=false;')
probe=probe.replace('using System;','using System;\nusing System.Linq;').replace('    public bool Throw=false;','    public bool Throw=false;\n    public Exception? InjectedFault, CleanupFault;\n    public bool ReleaseFault;')
probe=probe.replace('        if(Owned && name == "owned")','        if(InjectedFault is not null && (name == "fault" || name == "context")) throw InjectedFault;\n        if(Owned && name == "owned")')
probe=probe.replace('            if(OwnedFault) throw','            if(CleanupFault is not null) throw CleanupFault;\n            if(OwnedFault) throw')
probe=probe.replace('Interlocked.Increment(ref Closed); return','Interlocked.Increment(ref Closed); if(ReleaseFault) throw new InvalidOperationException("release fault"); return')
const program=(probe+await readFile(new URL('./runtime.tests.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,key)=>{assert.ok(replacements[key],key);return replacements[key]})
const original=hash(await readFile(join(dir,'Generated.cs')))
await writeFile(join(dir,'Program.cs'),program)
const options={cwd:dir,encoding:'utf8',timeout:90000}
const compilation=await compileRuntime(dir,options).catch(error=>{throw new Error(error.stdout||error.stderr||error.message)})
const stdout=execFileSync('dotnet',[compilation.assembly],options)
const controls=stdout.trim().split(/\r?\n/).map(x=>JSON.parse(x))
assert.equal(controls.length,37);assert.ok(controls.every(c=>c.passed))
assert.equal(hash(await readFile(join(dir,'Generated.cs'))),original)
await writeFile(join(dir,'runtime-evidence.json'),JSON.stringify({controls,sourceUnchanged:true,generatedSha256:original,compilation:compilation.evidence},null,2))
console.log('PASS: 37 explicit generic exception controls in unedited C#')
