import {readFile} from 'node:fs/promises'
import {compile} from '../offline-session-owned-io/compile.mjs'
const inherited=await readFile(new URL('../offline-session-owned-io/pipe.template.cs',import.meta.url),'utf8')
let pipe=await readFile(new URL('../offline-session-bounded-input/pipe.template.cs',import.meta.url),'utf8')
pipe=pipe.replace('int position,available,activeReads;', 'public Exception? Failure;\n int position,available,activeReads;')
pipe=pipe.replace('}finally{Interlocked.Decrement(ref activeReads);','}catch(Exception error){Failure=error;throw;}finally{Interlocked.Decrement(ref activeReads);')
let program=inherited.slice(inherited.indexOf('static class Program'))
program=program.replace('message=error.Message,effects.Opened', 'message=error.Message,readerFaultIdentity=ReferenceEquals(error,pipe.Failure),readerFaultStack=error.StackTrace?.Contains("Pipe.ReadLine")==true,effects.Opened')
let storage=await readFile(new URL('../offline-session-journal/storage.template.cs',import.meta.url),'utf8')
storage+='\n public bool StorageEmpty(){bool empty=!System.IO.Directory.Exists(StorageRoot)||!System.IO.Directory.EnumerateFileSystemEntries(StorageRoot).Any();Observe(new {operation="storage-empty",empty});return empty;}\n'
storage=storage.replace('AtomicWrite(k,v);Observe', 'if(k=="registry/v1")Checkpoint(v.Contains("\\\"phase\\\":\\\"ready\\\"",StringComparison.Ordinal)?"before-registry-publication":v.Contains("\\\"phase\\\":\\\"pending\\\"",StringComparison.Ordinal)?"before-registry-intent":"before-registry-idle");\n  AtomicWrite(k,v);Observe')
storage=storage.replace('if(k.StartsWith("journal/v1/"', 'if(k=="registry/v1")Checkpoint(v.Contains("\\\"phase\\\":\\\"ready\\\"",StringComparison.Ordinal)?"after-registry-publication":v.Contains("\\\"phase\\\":\\\"pending\\\"",StringComparison.Ordinal)?"after-registry-intent":"after-registry-idle");\n  if(k.StartsWith("journal/v1/"')
await compile(process.argv[2],{pipeTemplate:inherited.split('sealed class RecordedInput')[0]+pipe+program,
 hostMethods:{storageRead:'StorageRead',storageWrite:'StorageWrite',storageEmpty:'StorageEmpty',digest:'Digest',effectIdentity:'EffectIdentity',effectEvidence:'EffectEvidence'},effectsTransform:source=>source
 .replace(' @@METHODS@@',storage+'\n @@METHODS@@')
 // Optional deterministic fixture gates. The graph still owns Member policy.
 .replace('this.scenario=scenario;', 'this.scenario=scenario;if(Setting("autoMember")=="true")snapshotReleased.Set();')
 .replace('else if(label.StartsWith("worker-entry:"))Check(', 'else if(label.StartsWith("worker-entry:"))Check(Setting("autoMember")=="true"||')
 .replace('Ok("commit"+id);','Ok(Setting("snapshotCommit")==""?"commit"+id:Setting("snapshotCommit"));')
 .replace('if(phase=="commit"){commitEntered.Set();Wait(commitReleased,"integration release");}', 'if(phase=="commit"){commitEntered.Set();var clock=System.Diagnostics.Stopwatch.StartNew();while(!System.IO.File.Exists(Setting("commitGateFile"))){Check(clock.ElapsedMilliseconds<10000,"external commit gate timeout");Thread.Sleep(5);}Observe(new {operation="external-gate-released"});}\n  Observe(new {operation="protected-step",phase,cancelled=NyRuntime.CancellationRequested});\n  if(phase is "commit" or "head" or "save"){PersistEffect(phase,patch.ToString());if(phase=="commit")Checkpoint("after-commit");}')
 .replace('}finally{Interlocked.Decrement(ref Active);}', '}finally{Interlocked.Decrement(ref Active);Observe(new {operation="provider-ended",id,cancelled=NyRuntime.CancellationRequested});}')})
