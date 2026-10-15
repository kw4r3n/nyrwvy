import assert from 'node:assert/strict'
import {cp,mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {dirname,join,resolve} from 'node:path'
import {fileURLToPath} from 'node:url'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {materializeInputs} from './retained-inputs.mjs'

const kinds=['epoch','epoch-disposition','additional-admission','additional-disposition']
const [selection,destination]=process.argv.slice(2)
assert.ok(destination&&(selection==='all'||kinds.includes(selection)),`Usage: node examples/offline-session-replay/reproduce.mjs <${kinds.join('|')}|all> <fresh-output-directory>`)
const cwd=fileURLToPath(new URL('../../',import.meta.url)),root=resolve(destination)
await mkdir(dirname(root),{recursive:true})
await mkdir(root,{recursive:false})
assert.deepEqual(await readdir(root),[])
const inputs=join(root,'inputs'),materialization=await materializeInputs(inputs)
const config=JSON.parse(await readFile(join(inputs,'parent','Runtime.runtimeconfig.json'),'utf8'))
const framework=config.runtimeOptions.framework
const runtimes=execFileSync('dotnet',['--list-runtimes'],{encoding:'utf8'}).split(/\r?\n/)
assert.ok(runtimes.some(line=>line.startsWith(framework.name+' '+framework.version+' ')),`Retained inputs require ${framework.name} ${framework.version}; install that runtime without changing retained bytes`)
const hash=x=>createHash('sha256').update(x).digest('hex'),runs=[]
const run=(script,...args)=>execFileSync(process.execPath,[join(cwd,'examples',script+'.mjs'),...args],{cwd,stdio:'inherit',timeout:600000})
for(const kind of selection==='all'?kinds:[selection]){
 const name='offline-session-'+kind,output=join(root,kind),bundle=join(output,'bundle'),moved=join(output,'moved')
 await mkdir(output)
 run(name+'/run',bundle)
 run(name+'/compile',bundle)
 const parents=[join(inputs,'parent')]
 if(kind!=='epoch')parents.push(join(inputs,'epoch-parent'))
 if(kind.startsWith('additional-'))parents.push(join(inputs,'disposition-parent'))
 const evidence=join(output,'evidence')
 run(name+'/test-runtime',bundle,...parents,evidence,...(kind==='additional-disposition'?[join(inputs,'original-v7')]:[]))
 await cp(bundle,moved,{recursive:true})
 run(name+'/run','--replay',moved)
 run(name+'/compile',moved)
 const comparisons=[]
 // Source/snapshot/symbol identity, not SDK/path-dependent binary identity.
 for(const file of ['Generated.cs','session.json','symbols.json','declarations.json','authoring.json']){
  const expected=hash(await readFile(join(bundle,file))),actual=hash(await readFile(join(moved,file)))
  assert.equal(actual,expected,kind+'/'+file);comparisons.push({file,sha256:actual})
 }
 const proof=JSON.parse(await readFile(join(evidence,'proof.json'),'utf8'))
 assert.ok(proof.contracts.length&&proof.contracts.every(item=>item.passed),'oracle contracts must pass')
 runs.push({kind,contracts:proof.contracts.length,comparisons,evidence:kind+'/evidence/proof.json',relocation:'exact reopen/re-emission and offline compilation; runtime matrix executed once'})
 await writeFile(join(root,'reproduction.json'),JSON.stringify({materialization,runs,complete:false},null,2)+'\n')
}
await writeFile(join(root,'reproduction.json'),JSON.stringify({materialization,runs,complete:true},null,2)+'\n')
console.log(`PASS: ${runs.length} offline oracle(s) reproduced from tracked inputs; originals and moved bundles retained at ${root}`)
