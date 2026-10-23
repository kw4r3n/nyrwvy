// Compile the unedited emitted projection against the installed .NET 10 pack.
// Symbol IDs come from the saved graph and manifest, never mangled-name guesses.
import {mkdtemp,copyFile,writeFile,readFile,readdir,stat,rm} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {join,resolve} from 'node:path'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {compileRuntime} from './compile-runtime.mjs'

if(!process.argv[2])throw new Error('Usage: test-runtime.mjs <emitted-bundle-directory>')
const input=resolve(process.argv[2])
if(!(await stat(input)).isDirectory())throw new Error('Expected a bundle directory with Generated.cs, symbols.json and a graph snapshot')
const hash=bytes=>createHash('sha256').update(bytes).digest('hex')
const authoring=JSON.parse(await readFile(join(input,'authoring.json'),'utf8'))
const base='89787e3376c8612d301ebc5c3396e5df2a713258'
if(authoring.base!==base)throw new Error(`Expected the fixed capture-lifetime base ${base}`)
const generatedSha256=hash(await readFile(join(input,'Generated.cs')))
if(generatedSha256!==authoring.generatedSha256)throw new Error('Generated.cs differs from the authoring.json hash before runtime checks')
const graph=JSON.parse(await readFile(join(input,'declarations.json'),'utf8'))
const manifest=JSON.parse(await readFile(join(input,'symbols.json'),'utf8'))
const exactly=(items,predicate,label)=>{
  const matches=items.filter(predicate)
  if(matches.length!==1)throw new Error(`Expected one ${label}; found ${matches.length}`)
  return matches[0]
}
const symbol=(kind,id)=>exactly(manifest[kind]??[],s=>s.id===id,`${kind} symbol for ${id}`).name
const host=exactly(graph.capabilities,c=>c.displayName==='Host','Host capability')
const variables={HOST:symbol('capabilities',host.id)}
for(const fn of graph.functions){
  variables[fn.displayName.toUpperCase()]=symbol('functions',fn.id)
  for(const p of fn.parameters)variables[`${fn.displayName}_${p.displayName}`.toUpperCase()]=symbol('parameters',p.id)
}
for(const op of host.operations)variables[op.displayName.toUpperCase()]=symbol('capabilityOperations',op.id)
const template=await readFile(new URL('./stream-runtime.template.cs',import.meta.url),'utf8')
const program=template.replace(/@@([A-Z_]+)@@/g,(_,key)=>{
  if(!variables[key])throw new Error(`Missing template symbol ${key}`)
  return variables[key]
})
if(program.includes('@@'))throw new Error('Unresolved runtime template placeholder')
const expected=[
  'normal-small','normal-large','early-done','idle-input','slow-sink','stop-first','send-first',
  'sink-error','sink-exception','source-error','parent-exit','parent-token-cancel',
  'live-gc-normal-small','live-gc-normal-large','live-gc-early-done','bounded-worker-stream',
]
const scratch=await mkdtemp(join(tmpdir(),'nyrwvy-bounded-worker-stream-runtime-'))
let compilation=null,stdout='',stderr='',exitCode=null,failure=null
try{
  await copyFile(join(input,'Generated.cs'),join(scratch,'Generated.cs'))
  await writeFile(join(scratch,'Program.cs'),program)
  const options={cwd:scratch,env:{...process.env,DOTNET_NOLOGO:'1',DOTNET_CLI_TELEMETRY_OPTOUT:'1'},encoding:'utf8',timeout:210000,maxBuffer:16*1024*1024}
  try{
    const compiled=await compileRuntime(scratch,options)
    compilation=compiled.evidence
    try{stdout=execFileSync('dotnet',[compiled.assembly],options).trim();exitCode=0}
    catch(error){
      stdout=String(error.stdout??'').trim();stderr=String(error.stderr??'').trim();exitCode=error.status??null
      throw error
    }
    const labels=stdout.split(/\r?\n/).filter(x=>x.startsWith('PASS:')).map(x=>x.slice(6).split(';')[0])
    for(const label of expected)if(labels.filter(x=>x===label).length!==1)throw new Error(`Missing or repeated runtime PASS label: ${label}`)
    if(stdout.split(/\r?\n/).some(x=>x.startsWith('FAIL:')))throw new Error('Runtime reported a failure')
  }catch(error){
    failure=String(error.message??error)
    if(!stdout&&error.stdout)stdout=String(error.stdout).trim()
    if(!stderr&&error.stderr)stderr=String(error.stderr).trim()
  }
  const sourceUnchanged=hash(await readFile(join(input,'Generated.cs')))===generatedSha256&&hash(await readFile(join(scratch,'Generated.cs')))===generatedSha256
  if(!sourceUnchanged)failure=[failure,'Generated.cs changed during runtime checks'].filter(Boolean).join('\n')
  const checks=stdout.split(/\r?\n/).filter(x=>x.startsWith('PASS:'))
  await writeFile(join(input,'runtime-evidence.json'),JSON.stringify({
    base,generatedSha256,sourceUnchanged,compilation,exitCode,passed:failure===null,expected,checks,
    observations:stdout.split(/\r?\n/).filter(x=>x.startsWith('OBSERVE:')),stdout,stderr,...(failure?{failure}:{}),
  },null,2)+'\n')
  if(stdout)console.log(stdout)
  if(stderr)console.error(stderr)
  if(failure)throw new Error(`Runtime verification failed; evidence saved to runtime-evidence.json\n${failure}`)
}finally{
  await rm(scratch,{recursive:true,force:true})
}
