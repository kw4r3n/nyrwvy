"""Generate a cached-width layout library via public recipes on an isolated copy.
Usage: python segment_width_experiment.py /path/to/workspace.json /tmp/new-directory
"""
import json, pathlib, shutil, subprocess, sys
repo = pathlib.Path(__file__).resolve().parents[2]
snapshot, out = map(lambda x: pathlib.Path(x).resolve(), sys.argv[1:])
if out == snapshot.parent or snapshot.parent in out.parents: raise ValueError('output must be outside the input snapshot directory')
out.mkdir(parents=True, exist_ok=False)
shutil.copytree(snapshot.parent, out / 'cached', ignore=shutil.ignore_patterns('zig', '*.lock', '*.lease'))
folder = out / 'cached'; target = folder / snapshot.name
pathlib.Path(str(target)+'.backends.json').write_text(json.dumps({'targets':['csharp'], 'exports':['layoutFrame']}))
i64={'kind':'primitive','primitive':'int64'}
def lst(t): return {'kind':'builtinGeneric','generic':'list','arguments':[t]}
row=lst(i64); grid={'kind':'named','name':'CellGrid'}; prepared={'kind':'named','name':'PreparedLayoutRow'}
def p(name): return {'kind':'parameter','name':name}
def field(source, typ, name): return {'kind':'fieldAccess','source':source,'type':typ,'field':name}
def call(name, **args): return {'kind':'directCall','function':name,'arguments':[{'parameter':k,'value':v} for k,v in args.items()]}
def fn(name, params, result, body): return {'kind':'createFunction','displayName':name,'parameters':[{'displayName':n,'type':t} for n,t in params], 'resultType':result,'requiredEffects':[],'body':body}
def length(v): return {'kind':'listLength','value':v}
def eq(a,b): return {'kind':'valueEqual','left':a,'right':b}
def cond(c,a,b): return {'kind':'conditional','condition':c,'whenTrue':a,'whenFalse':b}
def empty(t): return {'kind':'listEmpty','itemType':t}
def ref(depth=0): return {'kind':'patternReference','scopeDepth':depth,'slot':'value'}
def bind(value,body): return {'kind':'bind','value':value,'body':body}
def fold(source, seed, name, **ctx): return {'kind':'listFold','source':source,'seed':seed,'function':name,'context':[{'parameter':k,'value':v} for k,v in ctx.items()]}
def map_(source,name,**ctx): return {'kind':'listMap','source':source,'function':name,'context':[{'parameter':k,'value':v} for k,v in ctx.items()]}
def construct(t,**fields): return {'kind':'productConstruct','type':t,'fields':[{'field':k,'value':v} for k,v in fields.items()]}
zero={'kind':'int64Literal','value':0}; cells=field(p('row'),'PreparedLayoutRow','cells'); widths=field(p('row'),'PreparedLayoutRow','widths')
steps=[{'kind':'createType','displayName':'PreparedLayoutRow','typeKind':{'kind':'product','fields':[{'displayName':'cells','type':row},{'displayName':'widths','type':row}]}},
 fn('prepareLayoutRow',[('cells',row),('grid',grid)],prepared,construct('PreparedLayoutRow',cells=p('cells'),widths=call('segmentWidths',cells=p('cells'),grid=p('grid')))),
 fn('mergePreparedTargets',[('acc',row),('row',prepared)],row, cond(eq(length(p('acc')),zero),widths,cond(eq(length(p('acc')),length(widths)),{'kind':'listZipWith','left':p('acc'),'right':widths,'function':'maxInt64'},p('acc'))))]
# Resolve the existing combiner by identity, keeping the experiment resilient to its name.
original=json.loads(snapshot.read_text()); functions={f['id']:f['displayName'] for f in original['functions']}
merge=next(f for f in original['functions'] if f['displayName']=='mergeTargets')
def find_zip(obj):
 if isinstance(obj,dict):
  if obj.get('kind')=='listZipWith': return obj['combinerFunctionId']
  for v in obj.values():
   result=find_zip(v)
   if result:return result
 if isinstance(obj,list):
  for v in obj:
   result=find_zip(v)
   if result:return result
steps[-1]['body']['whenFalse']['whenTrue']['function']=functions[find_zip(merge['body'])]
seed=construct('AlignAcc',segIdx=zero,x=zero,segStart=zero,xs=empty(i64),grid=p('grid'))
result={'kind':'listAppendItem','list':field(ref(),'AlignAcc','xs'),'item':field(ref(),'AlignAcc','x')}
align=cond(eq(length(widths),length(p('targets'))),bind(fold(cells,seed,'alignStep',targets=p('targets')),result),call('rowPositions',cells=cells,grid=p('grid')))
# rowPositions may name its cell argument differently.
rp=next(f for f in original['functions'] if f['displayName']=='rowPositions')
align['whenFalse']['arguments'][0]['parameter']=rp['parameters'][0]['displayName']
steps.append(fn('alignPreparedRow',[('row',prepared),('targets',row),('grid',grid)],row,align))
# Bind the prepared rows once; a second bind supplies the fold result to the map.
prepare=map_(p('block'),'prepareLayoutRow',grid=p('grid'))
targets=fold(ref(),empty(i64),'mergePreparedTargets')
body={'kind':'listConcat','left':p('out'),'right':map_(ref(1),'alignPreparedRow',targets=ref(),grid=p('grid'))}
steps.append({'kind':'replaceBody','function':'flushBlock','replacement':bind(prepare,bind(targets,body))})
(folder/'steps.json').write_text(json.dumps(steps,indent=2))
host=subprocess.Popen([str(repo/'tools/zig/host/bin/nyrwvy-host'),'open','--snapshot',str(target)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
def request(op,**args):
 host.stdin.write(json.dumps({'id':op,'op':op,**args})+'\n');host.stdin.flush();reply=json.loads(host.stdout.readline())
 if not reply['ok']: raise RuntimeError(reply)
 return reply['result']
try:
 review=request('simulateBatchByName',steps=steps,detail='full');(folder/'review.json').write_text(json.dumps(review,indent=2))
 committed=request('commit',proposalId=review['proposalId']);(folder/'commit.json').write_text(json.dumps(committed,indent=2))
 emitted=request('emitZigLibrary',output='layout.zig');assert any(x['symbol']=='ny_layoutFrame' for x in emitted['exports'])
finally:
 host.stdin.close();host.wait(timeout=20)
subprocess.run(['zig','build-lib',str(folder/'layout.zig'),'-dynamic','-lc','-O','ReleaseSafe','-femit-bin='+str(folder/'liblayout.so')],check=True)
print(folder/'liblayout.so')
