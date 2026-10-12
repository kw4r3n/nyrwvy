// Restore the frozen synthetic graph, then emit through ordinary tools.
import assert from 'node:assert/strict'
import {mkdir,readdir,copyFile,readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {fileURLToPath} from 'node:url'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
const dir=resolve(process.argv[2]),fixture=fileURLToPath(new URL('./fixtures/',import.meta.url))
await mkdir(dir,{recursive:true});assert.deepEqual(await readdir(dir),[],'use an empty output directory')
const summary=JSON.parse(await readFile(join(fixture,'verification-summary.json'),'utf8'))
const hash=value=>createHash('sha256').update(value).digest('hex')
for(const name of ['integration.json','cases.json','symbols.json','authoring.json']){
 assert.equal(hash(await readFile(join(fixture,name))),summary.committedSha256[name])
 await copyFile(join(fixture,name),join(dir,name))
}
const s=await session('integration',dir)
try{
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:[]}}})
 assert.equal(hash(artifact.source),summary.identity.generatedSha256)
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 await writeFile(join(dir,'Generated.cs'),artifact.source)
 console.log('PASS: exact frozen integration graph and C# projection')
}finally{await s.exchange('close')}
