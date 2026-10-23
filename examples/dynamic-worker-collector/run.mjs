import assert from 'node:assert/strict'
import {mkdir,mkdtemp,readdir,readFile,writeFile} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from './lib.mjs'
import {author} from './author.mjs'
import {makeCases,checkCase} from './cases.mjs'
const replay=process.argv[2]==='--replay'
const output=process.argv[replay?3:2]
if(replay&&!output)throw new Error('Usage: run.mjs [--replay] <bundle-directory>')
const directory=output?resolve(output):await mkdtemp(join(tmpdir(),'nyrwvy-dynamic-'))
await mkdir(directory,{recursive:true})
if(!replay)assert.deepEqual(await readdir(directory),[],'use an empty output directory')
const s=await session('collector',directory)
try {
  if(!replay) {
    const {artifact,declarations,nodes}=await author(s)
    await writeFile(join(directory,'Generated.cs'),artifact.source)
    await writeFile(join(directory,'declarations.json'),JSON.stringify(declarations,null,2))
    await writeFile(join(directory,'symbols.json'),JSON.stringify(artifact.symbols,null,2))
    await writeFile(join(directory,'authoring.json'),JSON.stringify({base:'181eb040f6103b2c37900c397efd0a309b2b6d21',nodes,generatedSha256:createHash('sha256').update(artifact.source).digest('hex')},null,2))
    await writeFile(join(directory,'cases.json'),JSON.stringify(makeCases(),null,2))
    await s.call('close')
  }
  await s.call('open',{mode:'open',snapshot:s.snapshot})
  const evidence=[]
  for(const c of JSON.parse(await readFile(join(directory,'cases.json'),'utf8'))) {
    const response=await s.exchange('request',{op:'runPureByName',arguments:{function:c.function,arguments:c.arguments}})
    const checks=checkCase(c,response)
    evidence.push({...checks,response});console.log(`PASS: ${c.id}; accepted events and cleanup checked`)
  }
  await writeFile(join(directory,'evidence.json'),JSON.stringify(evidence,null,2))
  console.log(`PASS: dynamic worker/collector virtual-time cases. ${directory}`)
} finally {await s.exchange('close')}
