// Exact immutable inputs, not regenerated IDs or a second recovery implementation.
import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,lstat} from 'node:fs/promises'
import {dirname,join,resolve} from 'node:path'
import {fileURLToPath} from 'node:url'
import {createHash} from 'node:crypto'
import {gunzipSync} from 'node:zlib'

export const packRoot=fileURLToPath(new URL('./fixtures/retained-inputs/',import.meta.url))
const proofPath=new URL('../offline-session-additional-admission/fixtures/proof.json',import.meta.url)
const hash=x=>createHash('sha256').update(x).digest('hex')
const component=x=>typeof x==='string'&&/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(x)

export async function readRetainedInputs(source=packRoot){
 const manifest=JSON.parse(await readFile(join(source,'manifest.json'),'utf8'))
 assert.equal(manifest.format,'nyrwvy-retained-offline-inputs/v1')
 const proofBytes=await readFile(proofPath),proof=JSON.parse(proofBytes)
 assert.equal(hash(proofBytes),manifest.sourceProofSha256,'retained proof changed')
 const expected=Object.entries(proof.closures).flatMap(([bundle,files])=>Object.entries(files).map(([name,sha256])=>({bundle,name,sha256})))
 assert.equal(manifest.entries.length,expected.length,'closure entry count')
 const verified=[]
 for(const [index,item] of expected.entries()){
  assert.ok(component(item.bundle)&&component(item.name),'invalid closure path')
  const entry=manifest.entries[index]
  assert.deepEqual({bundle:entry.bundle,name:entry.name,sha256:entry.sha256},item,'closure manifest differs from retained proof')
  assert.ok(Number.isSafeInteger(entry.bytes)&&entry.bytes>0&&entry.bytes<=16*1024*1024,'invalid retained size')
  const packed=await readFile(join(source,item.bundle,item.name+'.gz'))
  assert.equal(packed.length,entry.packedBytes,'packed size: '+item.bundle+'/'+item.name)
  assert.equal(hash(packed),entry.packedSha256,'packed digest: '+item.bundle+'/'+item.name)
  const bytes=gunzipSync(packed,{maxOutputLength:entry.bytes})
  assert.equal(bytes.length,entry.bytes,'retained size: '+item.bundle+'/'+item.name)
  assert.equal(hash(bytes),item.sha256,'retained digest: '+item.bundle+'/'+item.name)
  verified.push({...item,bytes})
 }
 return verified
}

export async function materializeInputs(destination,{source=packRoot}={}){
 const root=resolve(destination)
 try{await lstat(root);throw new Error('Fresh destination required: '+root)}catch(error){if(error.code!=='ENOENT')throw error}
 // Authenticate the complete closure before creating any output or executing a binary.
 const verified=await readRetainedInputs(source)
 await mkdir(dirname(root),{recursive:true})
 await mkdir(root)
 for(const bundle of new Set(verified.map(item=>item.bundle)))await mkdir(join(root,bundle))
 for(const item of verified)await writeFile(join(root,item.bundle,item.name),item.bytes,{flag:'wx'})
 const receipt={format:'nyrwvy-retained-offline-materialization/v1',files:verified.map(({bundle,name,bytes,sha256})=>({path:bundle+'/'+name,bytes:bytes.length,sha256}))}
 await writeFile(join(root,'materialization.json'),JSON.stringify(receipt,null,2)+'\n',{flag:'wx'})
 return receipt
}
