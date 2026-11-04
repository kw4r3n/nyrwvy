"""Read-only ABI pattern sweep and scratch per-function allocation instrumentation.
Usage: python library_probe.py /path/to/layout.zig /path/to/liblayout.so /path/to/workspace.json
Instrumented timings are deliberately not reported.
"""
import ctypes as c, hashlib, json, pathlib, re, statistics, subprocess, sys, tempfile, time
source_path, library_path, snapshot_path = map(pathlib.Path, sys.argv[1:4])
source = source_path.read_text()
graph = json.loads(snapshot_path.read_text())
names = {'f_' + f['id'].replace('-', '_2d_'): f['displayName'] for f in graph['functions']}
patterns = {'narrow':[1], 'wide':[1,2,0,1,1,2,0,1], 'separators':[1,1,1,1,3,1,1,1], 'mixed':[1,2,0,1,3,1,1,1]}
def load(path):
 lib=c.CDLL(str(path.resolve())); f=lib.ny_layoutFrame; P=c.POINTER
 f.argtypes=[P(c.c_int64),P(c.c_size_t),c.c_size_t,c.c_int64,c.c_int64,P(c.c_int64),c.c_size_t,P(c.c_size_t),P(c.c_size_t),c.c_size_t,P(c.c_size_t)];f.restype=c.c_int
 return lib,f
def case(f,w,h,pattern,runs,expected_output=None):
 row=[pattern[i%len(pattern)] for i in range(w)]
 a=(c.c_int64*(w*h))(*(row*h)); lens=(c.c_size_t*h)(*([w]*h)); out=(c.c_int64*(h*(w+1)))(); ol=(c.c_size_t*h)(); n=c.c_size_t(); nr=c.c_size_t(); times=[]
 for i in range(runs+20):
  if expected_output is not None: c.memset(out,0xA5,c.sizeof(out))
  t=time.perf_counter_ns(); status=f(a,lens,h,1,2,out,len(out),c.byref(n),ol,h,c.byref(nr));dt=time.perf_counter_ns()-t
  assert status==0 and n.value==len(out) and nr.value==h and list(ol)==[w+1]*h
  if expected_output is not None: assert list(out)==expected_output
  if i>=20:times.append(dt/1000)
 return statistics.median(times),list(out)
lib,f=load(library_path)
print('source',hashlib.sha256(source.encode()).hexdigest(),flush=True)
expected={}
for w,h in [(80,24),(300,100),(600,100),(300,200)]:
 for name,p in patterns.items():
  us,values=case(f,w,h,p,100); expected[w,h,name]=values
  lib.ny_arena_capacity.restype=c.c_size_t
  print('ABI',w,h,name,round(us,2),'us', 'retained',lib.ny_arena_capacity(),flush=True)
# Wrappers attribute allocations to the innermost generated function. They preserve ownership.
signatures=list(re.finditer(r'^fn (f_\w+)\((.*)\) ([^{\n]+) \{',source,re.M))
wrappers=[]
for index,m in enumerate(signatures,1):
 name,params,result=m.groups(); args=[p.split(':',1)[0].strip() for p in params.split(', ') if p]
 wrappers.append(f'fn {name}({params}) {result} {{ const before = probe_current; probe_current = {index}; probe_stats[{index}][0] += 1; defer probe_current = before; return impl_{name}({", ".join(args)}); }}')
text=re.sub(r'^fn (f_\w+)\(',r'fn impl_\1(',source,flags=re.M)
count=len(signatures)+1
text+=f'\nvar probe_current: usize = 0; var probe_stats: [{count}][7]u64 = @splat(@splat(0));\n'+ '\n'.join(wrappers)
text+='\nexport fn probe_read(i: usize, metric: usize) u64 { return probe_stats[i][metric]; }\nexport fn probe_reset() void { probe_stats = @splat(@splat(0)); }\n'
needle='if (count == 0) return &.{};';assert text.count(needle)==1
text=text.replace(needle,needle+' probe_stats[probe_current][1] += 1; probe_stats[probe_current][2] += @sizeOf(NyHeader) + count * @sizeOf(T);')
needle='const bytes = nyRaw().realloc(';assert text.count(needle)==2
text=text.replace(needle,'probe_stats[probe_current][3] += 1; '+needle)
for needle,metric in [('const slot = &nyAlloc(T, 1)[0];',4), ('const header = nyHeader(ptr);\n    if (header.count != ny_static)',5), ('const header = nyHeader(ptr);\n    if (header.count == ny_static)',6)]:
 assert text.count(needle)==1
 text=text.replace(needle,f'probe_stats[probe_current][{metric}] += 1; '+needle)
with tempfile.TemporaryDirectory(prefix='nyrwvy-library-probe-') as d:
 d=pathlib.Path(d);p=d/'probe.zig';p.write_text(text);so=d/'probe.so'
 subprocess.run(['zig','build-lib',str(p),'-dynamic','-lc','-O','ReleaseSafe','-femit-bin='+str(so)],check=True)
 instrumented,call=load(so);instrumented.probe_read.argtypes=[c.c_size_t,c.c_size_t];instrumented.probe_read.restype=c.c_uint64
 for name,pattern in patterns.items():
  instrumented.probe_reset();_,actual=case(call,300,100,pattern,1,expected[300,100,name]);assert actual==expected[300,100,name]
  print('PROFILE',name,'calls/nyAlloc/requested bytes/realloc/boxes/retains/drops per frame',flush=True)
  for i,m in enumerate(signatures,1):
   values=[instrumented.probe_read(i,k)/21 for k in range(7)]
   if values[0]:print(names.get(m.group(1),m.group(1)),values,flush=True)
  print('ABI outside functions',[instrumented.probe_read(0,k)/21 for k in range(7)],flush=True)
