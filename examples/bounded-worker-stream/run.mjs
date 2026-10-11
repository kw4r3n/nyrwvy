import assert from 'node:assert/strict'
import {mkdir,mkdtemp,readdir,readFile,writeFile} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from './lib.mjs'
import {author} from './author.mjs'
import {makeCases,checkCase} from './cases.mjs'
const replay=process.argv[2]==='--replay',output=process.argv[replay?3:2]
if(replay&&!output)throw new Error('Usage: run.mjs [--replay] <bundle-directory>')
const dir=output?resolve(output):await mkdtemp(join(tmpdir(),'nyrwvy-bounded-stream-'))
await mkdir(dir,{recursive:true});if(!replay)assert.deepEqual(await readdir(dir),[],'use an empty output directory')
const s=await session('stream',dir)
try{
  if(!replay){
    const {artifact,nodes}=await author(s)
    await writeFile(join(dir,'Generated.cs'),artifact.source)
    await writeFile(join(dir,'symbols.json'),JSON.stringify(artifact.symbols,null,2))
    await writeFile(join(dir,'authoring.json'),JSON.stringify({base:'89787e3376c8612d301ebc5c3396e5df2a713258',nodes,generatedSha256:createHash('sha256').update(artifact.source).digest('hex')},null,2))
    await writeFile(join(dir,'cases.json'),JSON.stringify(makeCases(),null,2));await s.call('close')
  }
  await s.call('open',{mode:'open',snapshot:s.snapshot})
  const evidence=[]
  for(const c of JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))){
    const response=await s.exchange('request',{op:'runPureByName',arguments:{function:c.function,arguments:c.arguments}})
    const checked=checkCase(c,response);evidence.push({...checked,response});console.log(`PASS: ${c.id}; ${JSON.stringify(checked)}`)
  }
  await writeFile(join(dir,'evidence.json'),JSON.stringify(evidence,null,2))
}finally{await s.exchange('close')}
