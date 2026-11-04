"""Emit current C# read-only, then benchmark layoutFrame with the Zig probe's inputs."""
import hashlib, json, os, pathlib, subprocess, sys, tempfile
root = pathlib.Path(__file__).resolve().parents[2]
snapshot = pathlib.Path(sys.argv[1]).resolve()
graphs = [json.loads(snapshot.read_text())]
pin = snapshot.with_name(snapshot.name + '.dependency.json')
if pin.exists():
 graphs += [json.loads(item['snapshotJson']) for item in json.loads(pin.read_text())['dependencies']]
operations = sorted({op['id'] for graph in graphs for cap in graph['capabilities'] for op in cap['operations']})
request = {'id':'bench','op':'emitCSharp','profile':{'kind':'externalImplementations','operationIds':operations}}
reply = subprocess.run([str(root/'tools/zig/host/bin/nyrwvy-host'),'open','--snapshot',str(snapshot),'--read-only'],input=json.dumps(request)+'\n',capture_output=True,text=True,check=True)
reply = json.loads(reply.stdout)
assert reply['ok'], reply
source = reply['result']['source']
print('snapshot-sha256', hashlib.sha256(snapshot.read_bytes()).hexdigest(), 'source-sha256', hashlib.sha256(source.encode()).hexdigest(), flush=True)
entry = 'NyF_' + '01a171a7-fecf-7469-b7ed-1b6eb5353e4a'.encode().hex().upper()
type_name = 'NyT_' + '01a17734-005d-7577-abbf-e8bddd0d4406'.encode().hex().upper()
program = '''using System;
using System.Diagnostics;
using Nyrwvy.Generated;
class Bench {
static void Main(string[] args) {
 int cols=int.Parse(args[0]), rows=int.Parse(args[1]);
 var data=new NyList<long>[rows];
 for(int r=0;r<rows;r++) { var cells=new long[cols]; Array.Fill(cells,1L); data[r]=NyList<long>.Create(cells); }
 var input=NyList<NyList<long>>.Create(data);
 var widths=new WIDTH(1L,2L);
 var warm=Stopwatch.StartNew();
 while(warm.ElapsedMilliseconds<1000) { GC.KeepAlive(NyFunctions.ENTRY(input,widths)); }
 long elapsed=0, checksum=0, allocated=0;
 int gen0=GC.CollectionCount(0), gen1=GC.CollectionCount(1), gen2=GC.CollectionCount(2);
 for(int i=0;i<1100;i++) {
  long alloc=GC.GetAllocatedBytesForCurrentThread(), start=Stopwatch.GetTimestamp();
  var result=NyFunctions.ENTRY(input,widths);
  long ticks=Stopwatch.GetTimestamp()-start;
  if(i>=100) { elapsed+=ticks; allocated+=GC.GetAllocatedBytesForCurrentThread()-alloc; }
  if(result.Length!=rows) throw new Exception("row count");
  for(int r=0;r<rows;r++) { var row=result.ItemAt(r).Value; if(row.Length!=cols+1) throw new Exception("column count"); for(int c=0;c<=cols;c++) { var x=row.ItemAt(c).Value; if(x!=c) throw new Exception("position"); checksum+=x; } }
  GC.KeepAlive(result);
 }
 if(checksum!=1100L*rows*cols*(cols+1)/2) throw new Exception("checksum");
 Console.WriteLine($"{cols}x{rows}: {elapsed*1e9/Stopwatch.Frequency/1000:F0} ns/frame, {allocated/1000} bytes/frame; GC {GC.CollectionCount(0)-gen0}/{GC.CollectionCount(1)-gen1}/{GC.CollectionCount(2)-gen2}");
}
}
'''.replace('WIDTH',type_name).replace('ENTRY',entry)
with tempfile.TemporaryDirectory(prefix='nyrwvy-csharp-layout-') as d:
 d=pathlib.Path(d)
 (d/'Generated.cs').write_text(source)
 (d/'Program.cs').write_text(program)
 (d/'bench.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup></Project>')
 subprocess.run(['dotnet','build',str(d/'bench.csproj'),'-c','Release','-o',str(d/'out'),'--nologo','-v','quiet'],check=True)
 for tiered in ['1','0']:
  for cols,rows in [(80,24),(300,100)]:
   for repeat in range(3):
    result=subprocess.run(['dotnet',str(d/'out/bench.dll'),str(cols),str(rows)],env=dict(os.environ,DOTNET_TieredCompilation=tiered),capture_output=True,text=True,check=True)
    print('tiered='+tiered,repeat,result.stdout.strip(),flush=True)
