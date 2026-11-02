// tools/Nyrwvy.ViewerExport after `dotnet build -c Release` and `npm run build:host`: a focus page
// from an example snapshot, and the output refusals that keep the input snapshot intact.
import test from 'node:test'
import assert from 'node:assert/strict'
import {copyFile,mkdtemp,readFile,rm,symlink} from 'node:fs/promises'
import {join,parse,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {spawnSync} from 'node:child_process'

const root=resolve('.')
const tool=join(root,'tools/Nyrwvy.ViewerExport/bin/Release/net10.0/Nyrwvy.ViewerExport.dll')
const fixture=join(root,'examples/task-cancellation-outcome/fixtures/task-outcomes.nyrec')
const exportFocus=(input,...rest)=>spawnSync('dotnet',[tool,'focus','--input',input,'--format','record',...rest],{cwd:root,encoding:'utf8',timeout:60000})

test('ViewerExport writes a focus page with the function in its data block and refuses aliases of its input',{timeout:180000},async()=>{
  const dir=await mkdtemp(join(tmpdir(),'nyrwvy-viewer-export-'))
  try{
    const snapshot=join(dir,'snapshot.nyrec'),output=join(dir,'focus.html')
    await copyFile(fixture,snapshot)
    const source=await readFile(snapshot,'utf8')
    const written=exportFocus(snapshot,'--function','protectedWork','--output',output)
    assert.equal(written.status,0,written.stderr)
    assert.equal(resolve(written.stdout.trim()),output)
    const html=await readFile(output,'utf8')
    const data=/<script type="application\/json" id="nyrwvy-data">([\s\S]*?)<\/script>/.exec(html)
    assert.ok(data,'the page embeds its viewer data')
    assert.ok(!data[1].includes('@DATA@'))
    assert.match(data[1],/protectedWork/)

    const refused=(input,out,message)=>{
      const result=exportFocus(input,'--function','protectedWork','--output',out)
      assert.equal(result.status,2,`${out}: ${result.stdout}`)
      assert.ok(result.stderr.includes(message),result.stderr)
    }
    for(const out of [snapshot,join(dir,'.','snapshot.nyrec'),join(dir,'pages','..','snapshot.nyrec')])refused(snapshot,out,'must not replace the input snapshot')
    refused(snapshot,parse(snapshot).root,'Output must name a file')
    if(process.platform!=='win32'){
      const linkedDirectory=dir+'-link'
      try{
        await symlink(dir,linkedDirectory,'dir')
        refused(snapshot,join(linkedDirectory,'snapshot.nyrec'),'must not replace the input snapshot')
      }finally{await rm(linkedDirectory,{force:true})}
      const linkedInput=join(dir,'input-link.nyrec')
      await symlink(snapshot,linkedInput)
      refused(linkedInput,snapshot,'must not replace the input snapshot')
      refused(linkedInput,linkedInput,'must not replace the input snapshot')
      const symbolic=join(dir,'symbolic.html')
      await symlink(output,symbolic)
      refused(snapshot,symbolic,'Output must not be a symbolic link')
    }
    assert.equal(await readFile(snapshot,'utf8'),source)

    const both=exportFocus(snapshot,'--function','protectedWork','--function-id','function:x')
    assert.equal(both.status,2)
    assert.ok(both.stderr.includes('exactly one of --function-id and --function'),both.stderr)
    const unknown=exportFocus(snapshot,'--function','protectedWork','--display-name','x')
    assert.equal(unknown.status,2)
    assert.ok(unknown.stderr.includes("Unsupported option '--display-name'"),unknown.stderr)
  }finally{await rm(dir,{recursive:true,force:true})}
})
