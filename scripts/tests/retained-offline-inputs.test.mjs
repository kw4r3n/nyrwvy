import test from 'node:test'
import assert from 'node:assert/strict'
import {cp,mkdtemp,readFile,writeFile,rm,stat} from 'node:fs/promises'
import {join} from 'node:path'
import {tmpdir} from 'node:os'
import {createHash} from 'node:crypto'
import {gzipSync,gunzipSync} from 'node:zlib'
import {materializeInputs,packRoot} from '../../examples/offline-session-replay/retained-inputs.mjs'
const hash=x=>createHash('sha256').update(x).digest('hex')
const proof=JSON.parse(await readFile(new URL('../../examples/offline-session-additional-admission/fixtures/proof.json',import.meta.url),'utf8'))
async function temporary(t){const root=await mkdtemp(join(tmpdir(),'nyrwvy-retained-inputs-'));t.after(()=>rm(root,{recursive:true,force:true}));return root}

test('tracked retained inputs materialize the exact complete historical closures in a fresh location',async t=>{
 const root=await temporary(t),destination=join(root,'inputs with spaces'),receipt=await materializeInputs(destination)
 const expected=Object.entries(proof.closures).flatMap(([bundle,files])=>Object.entries(files).map(([name,digest])=>({path:bundle+'/'+name,sha256:digest})))
 assert.deepEqual(receipt.files.map(({path,sha256})=>({path,sha256})),expected)
 for(const item of receipt.files){const bytes=await readFile(join(destination,item.path));assert.equal(bytes.length,item.bytes);assert.equal(hash(bytes),item.sha256)}
 const sentinel=join(destination,'keep.txt');await writeFile(sentinel,'user-owned')
 await assert.rejects(materializeInputs(destination),/Fresh destination required/)
 assert.equal(await readFile(sentinel,'utf8'),'user-owned')
})

test('missing, corrupt and forged retained packs refuse before output is created',async t=>{
 const root=await temporary(t),source=join(root,'pack');await cp(packRoot,source,{recursive:true})
 const manifestPath=join(source,'manifest.json'),original=JSON.parse(await readFile(manifestPath,'utf8'))
 const entry=original.entries[0],file=join(source,entry.bundle,entry.name+'.gz'),packed=await readFile(file)
 async function refused(label,pattern){const output=join(root,label);await assert.rejects(materializeInputs(output,{source}),pattern);await assert.rejects(stat(output),{code:'ENOENT'})}
 await rm(file);await refused('missing',/ENOENT/);await writeFile(file,packed)
 const corrupt=Buffer.from(packed);corrupt[corrupt.length-1]^=1;await writeFile(file,corrupt);await refused('corrupt',/packed digest/)
 const raw=gunzipSync(packed);raw[0]^=1;const forged=gzipSync(raw),manifest=structuredClone(original)
 manifest.entries[0].packedSha256=hash(forged);manifest.entries[0].packedBytes=forged.length
 await writeFile(file,forged);await writeFile(manifestPath,JSON.stringify(manifest));await refused('forged-bytes',/retained digest/)
 await writeFile(file,packed);const duplicate=structuredClone(original);duplicate.entries[duplicate.entries.length-1]=duplicate.entries[0]
 await writeFile(manifestPath,JSON.stringify(duplicate));await refused('duplicate',/closure manifest differs/)
 const omitted=structuredClone(original);omitted.entries.pop();await writeFile(manifestPath,JSON.stringify(omitted));await refused('omitted',/closure entry count/)
})
