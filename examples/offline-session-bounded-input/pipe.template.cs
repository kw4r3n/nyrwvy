// Generic bounded line capability. No command parsing, admission or team state.
sealed class Pipe : @@TRANSPORT@@, IDisposable
{
 const int ReadBufferBytes=1024,MaximumSupportedLimit=2*1024*1024;
 readonly byte[] pending=new byte[ReadBufferBytes];
 readonly System.Text.UTF8Encoding utf8=new(false,true);
 readonly Effects effects;readonly string control;
 int position,available,activeReads;bool skipLf,eof,closed;
 public Pipe(Effects effects,string control){this.effects=effects;this.control=control;UnixPipe.Nonblocking(0);}
 int NextByte(int bufferedBytes){
  if(NyRuntime.CancellationRequested)throw new System.Threading.Tasks.TaskCanceledException("pipe read cancelled");
  if(position==available){
   if(eof)return -1;
   Effects.Observe(new {operation="input-wait",bufferedBytes});
   available=UnixPipe.Read(pending,pending.Length,NyRuntime.Cancellation.Value);position=0;
   Effects.Observe(new {operation="pipe-read",hex=Convert.ToHexString(pending.AsSpan(0,available)),synchronous=true});
   if(available==0){eof=true;return -1;}
  }
  return pending[position++];
 }
 public NyOption<NyStr> @@READ@@(long @@READ_LIMIT@@)=>ReadLine(@@READ_LIMIT@@);
 NyOption<NyStr> ReadLine(long maxBytes){
  Interlocked.Increment(ref activeReads);
  int length=0;
  try{
   Effects.Observe(new {operation="read-start",maxBytes});
   if(maxBytes<1||maxBytes>MaximumSupportedLimit)throw new ArgumentOutOfRangeException(nameof(maxBytes));
   byte[] frame=new byte[(int)maxBytes];
   bool tail=false;
   while(true){
    int value=NextByte(length);
    if(skipLf){skipLf=false;if(value==10)continue;}
    if(value<0){if(length==0){effects.InputEnded();return NyOption<NyStr>.None();}tail=true;break;}
    if(value==13){skipLf=true;break;}
    if(value==10)break;
    if(length==maxBytes){
     Effects.Observe(new {operation="input-oversize",maxBytes,bufferedBytes=length,observedPayloadBytes=(long)length+1,readBufferBytes=ReadBufferBytes});
     throw new System.IO.InvalidDataException("pipe frame exceeds byte limit");
    }
    frame[length++]=(byte)value;
   }
   if(control=="unrelated-input-fault"){Effects.Observe(new {operation="unrelated-input-fault"});throw new NotSupportedException("unrelated input fault");}
   Effects.Observe(new {operation="decode-start",payloadBytes=length,maxBytes,tail});
   string line=utf8.GetString(frame,0,length);
   Effects.Observe(new {operation="readLine",line});
   return NyOption<NyStr>.Some(line);
  }finally{Interlocked.Decrement(ref activeReads);Effects.Observe(new {operation="read-ended",activeReads=Volatile.Read(ref activeReads),bufferedBytes=length});}
 }
 public void Dispose(){
  if(closed)return;
  Effects.Check(activeReads==0,"graph must join its reader before pipe close");
  UnixPipe.CloseInput();closed=true;Effects.Observe(new {operation="input-fd-closed"});Effects.Observe(new {operation="transport-close",activeReads});
 }
}
