// This fixture has no PackageReference. Compile against the installed .NET 10
// targeting pack, without NuGet restore, MSBuild, downloads, or new SDK installs.
import {readdir,stat,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {execFileSync} from 'node:child_process'

const order=(a,b)=>a.version.split('.').map(Number).reduce((n,v,i)=>n||v-Number(b.version.split('.')[i]),0)
const stableTen=version=>/^10\.0\.\d+$/.test(version)
export function parseSdks(output) {
  return output.split(/\r?\n/).flatMap(line=>{
    const match=/^(\d+\.\d+\.\d+) \[(.+)\]$/.exec(line.trim())
    return match&&stableTen(match[1])?[{version:match[1],directory:match[2]}]:[]
  }).sort(order)
}
export function parseRuntimes(output) {
  return output.split(/\r?\n/).flatMap(line=>{
    const match=/^Microsoft\.NETCore\.App (\d+\.\d+\.\d+) \[(.+)\]$/.exec(line.trim())
    return match&&stableTen(match[1])?[{version:match[1],directory:match[2]}]:[]
  }).sort(order)
}
export async function compileRuntime(scratch,options,run=execFileSync) {
  // Listing is handled by the installed dotnet host; unlike restore, it has no
  // dependency resolution or package-source access.
  const sdk=parseSdks(run('dotnet',['--list-sdks'],options)).at(-1)
  const runtime=parseRuntimes(run('dotnet',['--list-runtimes'],options)).at(-1)
  if(!sdk||!runtime)throw new Error('Installed stable .NET 10 SDK and runtime are required; nothing was downloaded')
  const compiler=join(sdk.directory,sdk.version,'Roslyn','bincore','csc.dll')
  if(!(await stat(compiler)).isFile())throw new Error('Installed SDK C# compiler is missing')
  const packRoot=resolve(sdk.directory,'..','packs','Microsoft.NETCore.App.Ref')
  const pack=(await readdir(packRoot)).filter(stableTen).map(version=>({version})).sort(order).at(-1)
  if(!pack)throw new Error('Installed .NET 10 reference pack is required; no restore or download was attempted')
  const references=join(packRoot,pack.version,'ref','net10.0')
  const assemblies=(await readdir(references)).filter(name=>name.endsWith('.dll')).sort()
  if(!assemblies.includes('System.Runtime.dll'))throw new Error('Incomplete installed .NET 10 reference pack')
  const assembly=join(scratch,'Runtime.dll')
  const compilerOutput=run('dotnet',[compiler,'-noconfig','-nostdlib+','-target:exe','-langversion:14.0','-nullable:enable',
    '-warnaserror+','-deterministic+','-optimize+',`-out:${assembly}`,
    ...assemblies.map(name=>`-r:${join(references,name)}`),join(scratch,'Generated.cs'),join(scratch,'Program.cs')],options).trim()
  await writeFile(join(scratch,'Runtime.runtimeconfig.json'),JSON.stringify({runtimeOptions:{tfm:'net10.0',framework:{name:'Microsoft.NETCore.App',version:runtime.version},rollForward:'Disable'}},null,2)+'\n')
  return {assembly,evidence:{method:'installed-roslyn-no-restore',sdkVersion:sdk.version,referencePackVersion:pack.version,runtimeVersion:runtime.version,referenceAssemblyCount:assemblies.length,compilerOutput}}
}
