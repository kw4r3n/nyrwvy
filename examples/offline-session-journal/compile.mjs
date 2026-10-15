import {readFile} from 'node:fs/promises'
import {compile} from '../offline-session-owned-io/compile.mjs'
const inherited=await readFile(new URL('../offline-session-owned-io/pipe.template.cs',import.meta.url),'utf8')
let pipe=await readFile(new URL('../offline-session-bounded-input/pipe.template.cs',import.meta.url),'utf8')
pipe=pipe.replace('int position,available,activeReads;', 'public Exception? Failure;\n int position,available,activeReads;')
pipe=pipe.replace('}finally{Interlocked.Decrement(ref activeReads);','}catch(Exception error){Failure=error;throw;}finally{Interlocked.Decrement(ref activeReads);')
let program=inherited.slice(inherited.indexOf('static class Program'))
program=program.replace('message=error.Message,effects.Opened', 'message=error.Message,readerFaultIdentity=ReferenceEquals(error,pipe.Failure),readerFaultStack=error.StackTrace?.Contains("Pipe.ReadLine")==true,effects.Opened')
const storage=await readFile(new URL('./storage.template.cs',import.meta.url),'utf8')
await compile(process.argv[2],{pipeTemplate:inherited.split('sealed class RecordedInput')[0]+pipe+program,
 hostMethods:{storageRead:'StorageRead',storageWrite:'StorageWrite',digest:'Digest',effectIdentity:'EffectIdentity',effectEvidence:'EffectEvidence'},effectsTransform:source=>source
 .replace(' @@METHODS@@',storage+'\n @@METHODS@@')
 .replace('Ok("commit"+id);','Ok(Setting("snapshotCommit")==""?"commit"+id:Setting("snapshotCommit"));')
 .replace('if(phase=="commit"){commitEntered.Set();Wait(commitReleased,"integration release");}', 'if(phase=="commit"){commitEntered.Set();if(Setting("commitGateFile")=="")Wait(commitReleased,"integration release");else{var clock=System.Diagnostics.Stopwatch.StartNew();while(!System.IO.File.Exists(Setting("commitGateFile"))){Check(clock.ElapsedMilliseconds<10000,"external commit gate timeout");Thread.Sleep(5);}Observe(new {operation="external-gate-released"});}}\n  Observe(new {operation="protected-step",phase,cancelled=NyRuntime.CancellationRequested});\n  if(phase is "commit" or "head" or "save"){PersistEffect(phase,patch.ToString());if(phase=="commit")Checkpoint("after-commit");}')
 .replace('}finally{Interlocked.Decrement(ref Active);}', '}finally{Interlocked.Decrement(ref Active);Observe(new {operation="provider-ended",id,cancelled=NyRuntime.CancellationRequested});}')})
