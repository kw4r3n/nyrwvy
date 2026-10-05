import { mkdtemp,copyFile,writeFile,readFile,stat,rm } from 'node:fs/promises'
import { tmpdir,homedir } from 'node:os'
import { join,resolve } from 'node:path'
import { execFileSync } from 'node:child_process'
const scratch=await mkdtemp(join(tmpdir(),'nyrwvy-one-shot-runtime-'))
try {
 const input=resolve(process.argv[2]),bundle=(await stat(input)).isDirectory()
 await copyFile(bundle?join(input,'Generated.cs'):input,join(scratch,'Generated.cs'))
 let program=await readFile(new URL('./runtime-tests.cs',import.meta.url),'utf8')
 if(bundle) {
  const graph=JSON.parse(await readFile(join(input,'broker.json'),'utf8')),manifest=JSON.parse(await readFile(join(input,'symbols.json'),'utf8'))
  const symbol=(kind,id)=>manifest[kind].find(s=>s.id===id).name
  const fn=name=>graph.functions.find(f=>f.displayName===name)
  const fnName=name=>symbol('functions',fn(name).id)
  const param=(name,parameter)=>symbol('parameters',fn(name).parameters.find(p=>p.displayName===parameter).id)
  const wire=graph.capabilities.find(c=>c.displayName==='Wire'),control=graph.capabilities.find(c=>c.displayName==='Control'),response=graph.types.find(t=>t.displayName==='Response')
  const vars={WIRE:symbol('capabilities',wire.id),CONTROL:symbol('capabilities',control.id),RESPONSE:symbol('types',response.id),RESPONSE_ID:symbol('fields',response.fields.find(f=>f.displayName==='id').id),RESPONSE_PAYLOAD:symbol('fields',response.fields.find(f=>f.displayName==='payload').id),RUN_CANCELLED:fnName('runCancelled'),CANCEL_WIRE:param('runCancelled','wire'),CANCEL_CONTROL:param('runCancelled','control'),RUN_BROKER:fnName('runBroker'),BROKER_WIRE:param('runBroker','wire'),LIMIT_A:param('runBroker','limitA'),LIMIT_B:param('runBroker','limitB'),REQUEST_WORKER:fnName('requestWorker'),REQUEST_CELL:param('requestWorker','cell'),REQUEST_ID:param('requestWorker','id'),REQUEST_LIMIT:param('requestWorker','limit')}
  for(const op of [...wire.operations,...control.operations])vars[op.displayName.toUpperCase()]=symbol('capabilityOperations',op.id)
  const host=(await readFile(new URL('./broker-runtime.template.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,key)=>{if(!vars[key])throw new Error(key);return vars[key]})
  program=program.replace('        Console.WriteLine("PASS:', '        BrokerChecks.Run();\n        Console.WriteLine("PASS:')+'\n'+host
 }
 await writeFile(join(scratch,'Program.cs'),program)
 await writeFile(join(scratch,'Runtime.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>')
 const options={cwd:scratch,env:{...process.env,DOTNET_CLI_HOME:join(scratch,'dotnet-home'),DOTNET_NOLOGO:'1'},encoding:'utf8',timeout:60000}
 execFileSync('dotnet',['restore','Runtime.csproj','--source',process.env.NUGET_PACKAGES??join(homedir(),'.nuget/packages'),'-p:NuGetAudit=false'],options)
 execFileSync('dotnet',['build','Runtime.csproj','-c','Release','--no-restore','-v:q'],options)
 console.log(execFileSync('dotnet',[join(scratch,'bin/Release/net10.0/Runtime.dll')],options).trim())
} finally {await rm(scratch,{recursive:true,force:true})}
