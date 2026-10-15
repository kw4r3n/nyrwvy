// Linux offline pipe binding: no pending Console read is abandoned at shutdown.
// The graph owns the reader task and framing handoff; this host owns syscalls.
static class UnixPipe
{
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
 struct PollFd {public int fd;public short events,revents;}
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int poll(ref PollFd fd,uint count,int timeout);
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern nint read(int fd,byte[] bytes,nuint count);
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern nint write(int fd,byte[] bytes,nuint count);
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int fcntl(int fd,int command,int flags);
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int close(int fd);
 public const int TickMs=25,WriteDeadlineMs=250;
 public static void Nonblocking(int fd){int flags=fcntl(fd,3,0);if(flags<0||fcntl(fd,4,flags|2048)<0)throw Fault("fcntl");}
 public static void CloseInput(){if(close(0)<0)throw Fault("close input");}
 static System.IO.IOException Fault(string op)=>new(op+" errno="+System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
 static void Ready(int fd,short events,CancellationToken token,System.Diagnostics.Stopwatch? deadline=null)
 {
  while(true){
   if(token.IsCancellationRequested)throw new System.Threading.Tasks.TaskCanceledException("pipe read cancelled");
   if(deadline?.ElapsedMilliseconds>=WriteDeadlineMs)throw new TimeoutException("pipe write deadline");
   var item=new PollFd{fd=fd,events=events};int n=poll(ref item,1,TickMs);
   if(n>0)return;
   if(n<0&&System.Runtime.InteropServices.Marshal.GetLastPInvokeError()!=4)throw Fault("poll");
  }
 }
 public static int Read(byte[] bytes,int count,CancellationToken token){
  while(true){Ready(0,1,token);int n=(int)read(0,bytes,(nuint)count);if(n>=0)return n;int e=System.Runtime.InteropServices.Marshal.GetLastPInvokeError();if(e!=4&&e!=11)throw Fault("read");}
 }
 public static void Write(string line){
  byte[] bytes=System.Text.Encoding.UTF8.GetBytes(line+"\n");int offset=0;
  var clock=System.Diagnostics.Stopwatch.StartNew();
  while(offset<bytes.Length){
   Ready(1,4,CancellationToken.None,clock);
   // Nonblocking write can be partial; retain exact accepted-byte observations.
   var remaining=bytes.AsSpan(offset).ToArray();int n=(int)write(1,remaining,(nuint)remaining.Length);
   if(n>0){Effects.Observe(new {operation="pipe-write",hex=Convert.ToHexString(remaining.AsSpan(0,n))});offset+=n;continue;}
   int e=System.Runtime.InteropServices.Marshal.GetLastPInvokeError();if(n<0&&(e==4||e==11))continue;throw Fault("write");
  }
 }
}
sealed class RecordedInput : System.IO.Stream
{
 readonly string control;
 bool closed;
 public RecordedInput(string control){this.control=control;UnixPipe.Nonblocking(0);}
 public override bool CanRead=>true;
 public override bool CanWrite=>false;
 public override bool CanSeek=>false;
 public override long Length=>throw new NotSupportedException();
 public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
 public override void Flush()=>throw new NotSupportedException();
 public override long Seek(long offset,System.IO.SeekOrigin origin)=>throw new NotSupportedException();
 public override void SetLength(long value)=>throw new NotSupportedException();
 public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
 public override int Read(byte[] bytes,int offset,int count){
  byte[] buffer=new byte[count];
  int n=UnixPipe.Read(buffer,count,control=="uninterruptible-read"?CancellationToken.None:NyRuntime.Cancellation.Value);
  buffer.AsSpan(0,n).CopyTo(bytes.AsSpan(offset,n));Effects.Observe(new {operation="pipe-read",hex=Convert.ToHexString(buffer.AsSpan(0,n)),synchronous=true});return n;
 }
 protected override void Dispose(bool disposing){if(disposing&&!closed){UnixPipe.CloseInput();closed=true;Effects.Observe(new {operation="input-fd-closed"});}base.Dispose(disposing);}
}
sealed class Pipe : @@TRANSPORT@@, IDisposable
{
 readonly System.IO.StreamReader input;
 readonly Effects effects;readonly string control;int activeReads;
 public Pipe(Effects effects,string control){this.effects=effects;this.control=control;input=new(new RecordedInput(control),new System.Text.UTF8Encoding(false,true),detectEncodingFromByteOrderMarks:false);}
 public NyOption<NyStr> @@READ@@(){
  Interlocked.Increment(ref activeReads);
  try{
  Effects.Observe(new {operation="read-start"});
  if(control!="uninterruptible-read"&&NyRuntime.CancellationRequested)throw new System.Threading.Tasks.TaskCanceledException("pipe read cancelled");
  if(control=="uninterruptible-read")Effects.Observe(new {operation="uninterruptible-read-wait"});
  var line=input.ReadLine();
  if(line==null){effects.InputEnded();return NyOption<NyStr>.None();}
  Effects.Observe(new {operation="readLine",line});
  if(control=="drop-tail-fault"){Effects.Observe(new {operation="tail-control-fault"});throw new NotSupportedException("unrelated tail-control fault");}
  if(control=="drop-tail"&&input.EndOfStream){Effects.Observe(new {operation="tail-dropped",line});effects.InputEnded();return NyOption<NyStr>.None();}
  return NyOption<NyStr>.Some(line);
  }finally{Interlocked.Decrement(ref activeReads);Effects.Observe(new {operation="read-ended",activeReads=Volatile.Read(ref activeReads)});}
 }
 public void Dispose(){Effects.Check(activeReads==0,"graph must join its reader before pipe close");input.Dispose();Effects.Observe(new {operation="transport-close",activeReads});}
}
static class Program
{
 public static void Main(string[] args){
  if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("offline Unix pipe fixture requires Linux");
  ThreadPool.SetMinThreads(16,16);UnixPipe.Nonblocking(1);
  using var scenario=JsonDocument.Parse(System.IO.File.ReadAllText(args[0]));
  string control=args.Length>1?args[1]:"";
  var effects=new Effects(scenario.RootElement,control);var pipe=new Pipe(effects,control);
  using var lifetime=new CancellationTokenSource();NyRuntime.Cancellation.Value=lifetime.Token;
  using var signal=System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,context=>{
   context.Cancel=true;Effects.Observe(new {operation="transport-cancel"});lifetime.Cancel();effects.ReleaseGates();
  });
  try{
   var result=NyFunctions.@@RUN@@(@@RUN_HOST@@:effects,@@RUN_TRANSPORT@@:pipe,@@RUN_WRONG@@:false);
   Effects.Observe(new {operation="outcome",ok=true,isOk=result.IsOk,value=result.IsOk?result.OkValue.ToString():result.ErrorValue.ToString(),effects.Opened,effects.Closed,effects.Active,effects.LocalCompleted,effects.TerminalWritten,effects.OutputFault});
  }catch(Exception error){
   Effects.Observe(new {operation="outcome",ok=false,type=error.GetType().Name,message=error.Message,effects.Opened,effects.Closed,effects.Active,effects.LocalCompleted,effects.TerminalWritten,effects.OutputFault});
  }
  pipe.Dispose();Effects.Observe(new {operation="process-terminal"});
 }
}
