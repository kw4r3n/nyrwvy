"""Compare native layout libraries without modifying sources; checks output and input ownership.
Usage: python compare_libraries.py baseline.so candidate.so [candidate2.so ...]
Timing is separated from parity and excludes Python buffer construction.
"""
import ctypes as c, hashlib, json, pathlib, random, statistics, sys, time
P=c.POINTER
class Library:
 def __init__(self,path):
  self.path=pathlib.Path(path).resolve();self.lib=c.CDLL(str(self.path));self.f=self.lib.ny_layoutFrame
  self.f.argtypes=[P(c.c_int64),P(c.c_size_t),c.c_size_t,c.c_int64,c.c_int64,P(c.c_int64),c.c_size_t,P(c.c_size_t),P(c.c_size_t),c.c_size_t,P(c.c_size_t)];self.f.restype=c.c_int
 def call(self,buffers,narrow,wide,small=False):
  data,lens,output,outlens,olen,rows=buffers
  status=self.f(data,lens,len(lens),narrow,wide,output,0 if small else len(output),c.byref(olen),outlens,0 if small else len(outlens),c.byref(rows))
  if status==3:return status,olen.value,rows.value
  assert status==0,(self.path,status)
  assert olen.value<=len(output) and rows.value<=len(outlens)
  return status,list(output[:olen.value]),list(outlens[:rows.value])
def buffers(rows):
 flat=[v for row in rows for v in row];return ((c.c_int64*len(flat))(*flat),(c.c_size_t*len(rows))(*map(len,rows)),(c.c_int64*(len(flat)+len(rows)+16))(),(c.c_size_t*len(rows))(),c.c_size_t(),c.c_size_t())
libraries=[Library(p) for p in sys.argv[1:]];assert len(libraries)>=2
cases=[[],[[]],[[],[]],[[1]],[[2,0]],[[3]],[[3,3,3]],[[0,0,0]],[[1,3,1],[1,1],[2,0,3,1]],[[3],[1,3,1],[3,1,3,1,3]]]
rng=random.Random(52965)
for _ in range(240):
 rows=[]
 for _ in range(rng.randrange(1,17)):
  row=[]
  for _ in range(rng.randrange(0,81)):
   row.append(rng.choice([0,1,1,1,2,3]))
  rows.append(row)
 cases.append(rows)
checks=0
for case in cases:
 for narrow,wide in [(1,2),(2,3),(3,5),(0,0)]:
  args=buffers(case);before=list(args[0]);expected=libraries[0].call(args,narrow,wide)
  for lib in libraries[1:]:
   c.memset(args[2],0xA5,c.sizeof(args[2]));actual=lib.call(args,narrow,wide)
   assert actual==expected,(lib.path,case,narrow,wide,actual,expected)
   assert list(args[0])==before
   assert lib.call(args,narrow,wide,small=True)==libraries[0].call(args,narrow,wide,small=True)
   checks+=1
print('parity comparisons',checks,flush=True)
patterns={'narrow':[1],'wide':[1,2,0,1,1,2,0,1],'mixed':[1,2,0,1,3,1,1,1]}
for cols,rows in [(80,24),(300,100)]:
 for pattern,p in patterns.items():
  data=[[p[(i+r)%len(p)] for i in range(cols)] for r in range(rows)]
  args=buffers(data);expected=libraries[0].call(args,2,3)
  for lib in libraries:
   assert lib.call(args,2,3)==expected
   # Include only native call in timing, not output list conversion.
   a,l,o,ol,n,nr=args
   def run():return lib.f(a,l,len(l),2,3,o,len(o),c.byref(n),ol,len(ol),c.byref(nr))
   for _ in range(100):assert run()==0
   samples=[]
   for _ in range(500):
    t=time.perf_counter_ns();status=run();samples.append((time.perf_counter_ns()-t)/1000);assert status==0
   print(lib.path.parent.name,cols,rows,pattern,'p50_us',round(statistics.median(samples),2),'p99_us',round(sorted(samples)[494],2),flush=True)
