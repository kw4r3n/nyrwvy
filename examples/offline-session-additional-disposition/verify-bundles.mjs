import assert from 'node:assert/strict'
import {readFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
const hash=x=>createHash('sha256').update(x).digest('hex'),root=resolve(process.argv[2]??'artifacts'),prior=JSON.parse(await readFile(new URL('../offline-session-additional-admission/fixtures/proof.json',import.meta.url))),names=['Generated.cs','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','authoring.json','compilation.json','declarations.json','session.json','symbols.json']
let comparisons=0
for(const dir of ['original-v7','parent','epoch-parent','disposition-parent'])for(const [name,digest] of Object.entries(prior.closures[dir])){assert.equal(hash(await readFile(join(root,dir,name))),digest,dir+'/'+name);comparisons++}
if(process.argv[3]&&process.argv[4])for(const name of names){assert.equal(hash(await readFile(join(resolve(process.argv[3]),name))),hash(await readFile(join(resolve(process.argv[4]),name))),name);comparisons++}
console.log(`PASS: ${comparisons} authenticated ancestor/base or relocated artifact comparisons`)
