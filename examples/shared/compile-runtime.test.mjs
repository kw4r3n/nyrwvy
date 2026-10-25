import assert from 'node:assert/strict'
import test from 'node:test'
import {mkdtemp,mkdir,writeFile,readFile,rm} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {join} from 'node:path'
import {compileRuntime,parseSdks,parseRuntimes} from './compile-runtime.mjs'

test('SDK discovery restricts stable .NET 10 and sorts numeric versions',()=>{
  assert.deepEqual(parseSdks('9.0.100 [/sdk]\n10.0.112 [/sdk]\n10.0.9 [/sdk]\n11.0.100-preview.1 [/sdk]').map(x=>x.version),['10.0.9','10.0.112'])
  assert.deepEqual(parseRuntimes('Microsoft.NETCore.App 10.0.9 [/runtime]\nMicrosoft.AspNetCore.App 10.0.9 [/runtime]\nMicrosoft.NETCore.App 11.0.0 [/runtime]'),[{version:'10.0.9',directory:'/runtime'}])
})
test('offline compiler uses only installed reference files, with no restore/build step',async()=>{
  const root=await mkdtemp(join(tmpdir(),'collector-compiler-unit-'))
  try {
    const sdk=join(root,'sdk'),compiler=join(sdk,'10.0.112','Roslyn','bincore'),refs=join(root,'packs','Microsoft.NETCore.App.Ref','10.0.9','ref','net10.0')
    await mkdir(compiler,{recursive:true});await mkdir(refs,{recursive:true})
    await writeFile(join(compiler,'csc.dll'),'');await writeFile(join(refs,'System.Runtime.dll'),'');await writeFile(join(refs,'System.Console.dll'),'')
    const calls=[],run=(file,args)=>{calls.push({file,args});if(args[0]==='--list-sdks')return `10.0.112 [${sdk}]`;if(args[0]==='--list-runtimes')return 'Microsoft.NETCore.App 10.0.9 [/installed-runtime]';return 'stub compiler, not real C# execution'}
    const result=await compileRuntime(root,{},run)
    assert.equal(calls.length,3);assert.ok(!calls.some(c=>c.args.includes('restore')||c.args.includes('build')))
    assert.ok(calls[2].args.includes('-nostdlib+'));assert.ok(calls[2].args.includes('-warnaserror+'))
    assert.equal(result.evidence.method,'installed-roslyn-no-restore')
    assert.equal(result.evidence.referenceAssemblyCount,2)
    assert.deepEqual(JSON.parse(await readFile(join(root,'Runtime.runtimeconfig.json'),'utf8')).runtimeOptions,{tfm:'net10.0',framework:{name:'Microsoft.NETCore.App',version:'10.0.9'},rollForward:'Disable'})
  }finally{await rm(root,{recursive:true,force:true})}
})
test('missing installed SDK fails without fetching anything',async()=>{
  await assert.rejects(()=>compileRuntime('/unused',{},()=>''),/Installed stable .NET 10 SDK and runtime are required/)
})
