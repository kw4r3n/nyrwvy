// Reconstitute a saved synthetic fixture bundle without committing generated C#
// or machine-local runtime logs. Ordinary tools emit the unchanged projection.
import assert from 'node:assert/strict'
import {mkdir,readdir,copyFile,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {fileURLToPath} from 'node:url'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
const variant=process.argv[2],output=process.argv[3]
assert.ok(['baseline','followup'].includes(variant)&&output,'Usage: materialize.mjs baseline|followup <empty-output-directory>')
const directory=resolve(output),label=variant==='baseline'?'members':'followup'
const fixture=fileURLToPath(new URL(variant==='baseline'?'./fixtures/':'../member-lifecycle-followup/fixtures/',import.meta.url))
await mkdir(directory,{recursive:true});assert.deepEqual(await readdir(directory),[],'use an empty output directory')
for(const name of [`${label}.json`,'cases.json','symbols.json','authoring.json'])await copyFile(join(fixture,name),join(directory,name))
const metadata=JSON.parse(await readFile(join(directory,'authoring.json'),'utf8')),s=await session(label,directory)
try{
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(createHash('sha256').update(artifact.source).digest('hex'),metadata.generatedSha256)
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(directory,'symbols.json'),'utf8')))
 await writeFile(join(directory,'Generated.cs'),artifact.source)
 console.log(`PASS: materialized ${variant}, exact saved projection SHA-256 ${metadata.generatedSha256}`)
}finally{await s.exchange('close')}
