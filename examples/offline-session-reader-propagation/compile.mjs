import {readFile} from 'node:fs/promises'
import {compile} from '../offline-session-owned-io/compile.mjs'
const inherited=await readFile(new URL('../offline-session-owned-io/pipe.template.cs',import.meta.url),'utf8')
let pipe=await readFile(new URL('../offline-session-bounded-input/pipe.template.cs',import.meta.url),'utf8')
pipe=pipe.replace('int position,available,activeReads;', 'public Exception? Failure;\n int position,available,activeReads;')
pipe=pipe.replace('}finally{Interlocked.Decrement(ref activeReads);','}catch(Exception error){Failure=error;throw;}finally{Interlocked.Decrement(ref activeReads);')
let program=inherited.slice(inherited.indexOf('static class Program'))
program=program.replace('message=error.Message,effects.Opened', 'message=error.Message,readerFaultIdentity=ReferenceEquals(error,pipe.Failure),readerFaultStack=error.StackTrace?.Contains("Pipe.ReadLine")==true,effects.Opened')
// Test-only external gate: the peer releases a held effect via a marker file.
// It never reacts to reader failure or cancellation. Both graphs use this host.
// Provider exit and protected token observations are evidence, not policy.
const pipeTemplate=inherited.split('sealed class RecordedInput')[0]+pipe+program
await compile(process.argv[2],{pipeTemplate,effectsTransform:source=>source
 .replace('if(phase=="commit"){commitEntered.Set();Wait(commitReleased,"integration release");}', 'if(phase=="commit"){commitEntered.Set();if(Setting("commitGateFile")=="")Wait(commitReleased,"integration release");else{var clock=System.Diagnostics.Stopwatch.StartNew();while(!System.IO.File.Exists(Setting("commitGateFile"))){Check(clock.ElapsedMilliseconds<10000,"external commit gate timeout");Thread.Sleep(5);}Observe(new {operation="external-gate-released"});}}\n  Observe(new {operation="protected-step",phase,cancelled=NyRuntime.CancellationRequested});')
 .replace('}finally{Interlocked.Decrement(ref Active);}', '}finally{Interlocked.Decrement(ref Active);Observe(new {operation="provider-ended",id,cancelled=NyRuntime.CancellationRequested});}')})
