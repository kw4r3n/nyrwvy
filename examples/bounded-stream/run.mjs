import assert from 'node:assert/strict'
import {mkdir,mkdtemp,readdir,readFile,writeFile} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from './lib.mjs'
import {author} from './author.mjs'
const replay=process.argv[2]==='--replay',output=process.argv[replay?3:2]
if(replay&&!output)throw new Error('Usage: run.mjs [--replay] <bundle-directory>')
const dir=output?resolve(output):await mkdtemp(join(tmpdir(),'nyrwvy-cursor-retention-'))
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'use an empty output directory')
const s=await session('cursor-retention',dir)
try {
  if(!replay){
    const {artifact,declarations,nodes}=await author(s)
    await writeFile(join(dir,'Generated.cs'),artifact.source)
    await writeFile(join(dir,'declarations.json'),JSON.stringify(declarations,null,2))
    await writeFile(join(dir,'symbols.json'),JSON.stringify(artifact.symbols,null,2))
    await writeFile(join(dir,'authoring.json'),JSON.stringify({base:'90e495c75dd6915440ea22c96222aefcf04f723e',nodes,generatedSha256:createHash('sha256').update(artifact.source).digest('hex')},null,2))
    await writeFile(join(dir,'cases.json'),JSON.stringify([4,256].map(count=>({count,function:'run',arguments:[{kind:'fakeCapability',capability:'Host',operations:[{operation:'checkpoint',responses:[{kind:'return',value:{kind:'unit'}},{kind:'return',value:{kind:'unit'}}]}]},{kind:'int64',value:String(count)}]})),null,2))
    await s.call('close')
  }
  await s.call('open',{mode:'open',snapshot:s.snapshot})
  const evidence=[]
  for(const c of JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))){
    const response=await s.exchange('request',{op:'runPureByName',arguments:{function:c.function,arguments:c.arguments}})
    assert.equal(response.ok,true,JSON.stringify(response))
    const result=response.result
    assert.equal(result.actual.kind,'result');assert.equal(result.actual.isOk,true)
    const calls=result.calls.filter(x=>x.operation==='checkpoint')
    assert.equal(calls.length,2)
    for(const call of calls)assert.equal(String(call.arguments.find(a=>a.parameter==='count').value.value),String(c.count))
    assert.deepEqual(calls.map(x=>String(x.arguments.find(a=>a.parameter==='worker').value.value)).sort(),['1','2'])
    evidence.push({count:c.count,completedWorkers:2,response})
    console.log(`PASS: ${c.count} cursor advances per worker; saved graph replay`)
  }
  await writeFile(join(dir,'evidence.json'),JSON.stringify(evidence,null,2))
}finally{await s.exchange('close')}
