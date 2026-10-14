// Disposable fixtures only. Never discovers or operates on a user checkout.
import {mkdir,writeFile,readFile,readdir,lstat} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
export const hash=x=>createHash('sha256').update(x).digest('hex')
export function gitEnv(){
 const env=Object.fromEntries(Object.entries(process.env).filter(([k])=>!k.startsWith('GIT_')))
 return {...env,GIT_CONFIG_NOSYSTEM:'1',GIT_CONFIG_GLOBAL:'/dev/null',GIT_TERMINAL_PROMPT:'0',GIT_OPTIONAL_LOCKS:'0',
  GIT_AUTHOR_NAME:'Offline fixture',GIT_AUTHOR_EMAIL:'fixture@invalid',GIT_COMMITTER_NAME:'Offline fixture',GIT_COMMITTER_EMAIL:'fixture@invalid',
  GIT_AUTHOR_DATE:'2000-01-01T00:00:00Z',GIT_COMMITTER_DATE:'2000-01-01T00:00:00Z'}
}
export const git=(root,args,input)=>execFileSync('git',['-c','core.hooksPath='+join(root,'disabled-hooks'),'-c','commit.gpgSign=false','-c','core.fsmonitor=false','-c','core.untrackedCache=false','-C',root,...args],{env:gitEnv(),input,encoding:'utf8'}).trim()
export async function fixture(root){
 root=resolve(root);await mkdir(root,{recursive:true});if((await readdir(root)).length)throw new Error('fresh disposable repository required')
 git(root,['init','--initial-branch=main']);await mkdir(join(root,'disabled-hooks'));await writeFile(join(root,'.git','offline-fixture-id'),hash(root))
 await writeFile(join(root,'file.txt'),'base\n');git(root,['add','file.txt']);git(root,['commit','-m','base'])
 const base=git(root,['rev-parse','HEAD']);await writeFile(join(root,'file.txt'),'selected\n');git(root,['add','file.txt'])
 const tree=git(root,['write-tree']),selected=git(root,['commit-tree',tree,'-p',base], 'selected\n')
 git(root,['read-tree','--reset','-u',base]);return {root,base,selected,tree,patch:git(root,['diff',base,selected,'--','file.txt'])}
}
export async function inspect(root){
 const file=join(root,'file.txt'),info=await lstat(file),regular=info.isFile()
 return {head:git(root,['rev-parse','HEAD']),refs:git(root,['show-ref']),objects:git(root,['cat-file','--batch-all-objects','--batch-check=%(objectname) %(objecttype)']).split('\n').sort(),
  index:hash(await readFile(join(root,'.git','index'))),worktree:regular?await readFile(file,'utf8'):null,worktreeType:regular?'file':info.isSymbolicLink()?'symlink':'other',worktreeMode:info.mode&0o777,gitMode:regular?(info.mode&0o100?'100755':'100644'):null,status:git(root,['status','--porcelain=v1','--untracked-files=all'])}
}
