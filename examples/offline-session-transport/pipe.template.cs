sealed class RecordedInput : System.IO.Stream
{
 readonly System.IO.Stream inner=Console.OpenStandardInput();
 public override bool CanRead=>true;
 public override bool CanWrite=>false;
 public override bool CanSeek=>false;
 public override long Length=>throw new NotSupportedException();
 public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
 public override void Flush()=>throw new NotSupportedException();
 public override long Seek(long offset,System.IO.SeekOrigin origin)=>throw new NotSupportedException();
 public override void SetLength(long value)=>throw new NotSupportedException();
 public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
 public override int Read(byte[] buffer,int offset,int count)
 {
  // StreamReader.EndOfStream may probe synchronously in the tail-loss control.
  int n=inner.Read(buffer,offset,count);
  Effects.Observe(new {operation="pipe-read",hex=Convert.ToHexString(buffer.AsSpan(offset,n)),synchronous=true});
  return n;
 }
 public override async System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
 {
  int n=await inner.ReadAsync(buffer,token);
  Effects.Observe(new {operation="pipe-read",hex=Convert.ToHexString(buffer.Span[..n])});
  return n;
 }
 protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
}
sealed class Pipe : @@TRANSPORT@@, IDisposable
{
 readonly System.IO.StreamReader input = new(new RecordedInput(),new System.Text.UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:false);
 readonly Effects effects;
 readonly string control;
 System.Threading.Tasks.Task<string?>? pending;
 public Pipe(Effects effects,string control){this.effects=effects;this.control=control;}
 public NyOption<NyStr> @@READ@@()
 {
  Effects.Observe(new {operation="read-start"});
  // Console's OS read can stay blocked after cancellation on Linux. Cancel
  // this wait, retain the read, and join it in Dispose after peer stdin EOF.
  pending=input.ReadLineAsync(CancellationToken.None).AsTask();
  if(control=="uninterruptible-read")Effects.Observe(new {operation="uninterruptible-read-wait"});
  var line=(control=="uninterruptible-read"?pending:pending.WaitAsync(NyRuntime.Cancellation.Value)).GetAwaiter().GetResult();
  pending=null;
  if(line==null){effects.InputEnded();return NyOption<NyStr>.None();}
  Effects.Observe(new {operation="readLine",line});
  // Compiled negative control: a final unterminated frame is read by the
  // same platform reader, then incorrectly dropped. Acceptance detects loss.
  // Verification guard recreates the former unrelated exception; it must be
  // refused as tail-loss evidence, even though positive acceptance rejects it.
  if(control=="drop-tail-fault"){Effects.Observe(new {operation="tail-control-fault"});throw new NotSupportedException("unrelated tail-control fault");}
  if(control=="drop-tail" && input.EndOfStream){Effects.Observe(new {operation="tail-dropped",line});effects.InputEnded();return NyOption<NyStr>.None();}
  return NyOption<NyStr>.Some(line);
 }
 public void Dispose()
 {
  Effects.Observe(new {operation="input-drain-start"});
  try{pending?.GetAwaiter().GetResult();}
  catch(Exception error){Effects.Observe(new {operation="input-drain-fault",type=error.GetType().Name});}
  finally{input.Dispose();Effects.Observe(new {operation="transport-close"});}
 }
}
static class Program
{
 public static void Main(string[] args)
 {
  ThreadPool.SetMinThreads(16,16);
  using var scenario=JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));
  string control=args.Length>1?args[1]:"";
  var effects=new Effects(scenario.RootElement,control);
  var pipe=new Pipe(effects,control);
  using var lifetime=new CancellationTokenSource();
  NyRuntime.Cancellation.Value=lifetime.Token;
  using var signal=System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,context=>{
   context.Cancel=true;
   Effects.Observe(new {operation="transport-cancel"});
   lifetime.Cancel();effects.ReleaseGates();
  });
  try{
   var result=NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_TRANSPORT@@:pipe,@@RUN_WRONG@@:false);
   Effects.Observe(new {operation="outcome",ok=true,isOk=result.IsOk,value=result.IsOk?result.OkValue.ToString():result.ErrorValue.ToString(),effects.Opened,effects.Closed,effects.Active});
  }catch(Exception error){
   Effects.Observe(new {operation="outcome",ok=false,type=error.GetType().Name,message=error.Message,effects.Opened,effects.Closed,effects.Active});
  }
  pipe.Dispose();
  Effects.Observe(new {operation="process-terminal"});
 }
}
