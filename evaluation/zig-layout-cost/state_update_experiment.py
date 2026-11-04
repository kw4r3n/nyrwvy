"""Use public authoring operations on copies to compare ProductConstruct and ProductUpdate.
Usage: python state_update_experiment.py /path/to/workspace.json /tmp/output-directory
The original snapshot is never written; generated sources are never patched.
"""
import json, pathlib, shutil, subprocess, sys
repo=pathlib.Path(__file__).resolve().parents[2]
snapshot=pathlib.Path(sys.argv[1]).resolve(); out=pathlib.Path(sys.argv[2]).resolve()
if out == snapshot.parent or snapshot.parent in out.parents: raise ValueError('output must be outside the input snapshot directory')
out.mkdir(parents=True,exist_ok=False)
original=json.loads(snapshot.read_text()); functions={f['id']:f for f in original['functions']}; types={t['id']:t for t in original['types']}
fields={f['id']:(t['displayName'],f['displayName']) for t in original['types'] if t['kind']=='product' for f in t['fields']}
parameters={p['id']:p['displayName'] for f in original['functions'] for p in f['parameters']}
def edits(update):
 steps=[]
 for fn in original['functions']:
  if fn['displayName'] not in ['segStep','alignStep']:continue
  nodes={};scopes={}
  def visit(graph,stack):
   for node in graph['nodes']:
    nodes[node['id']]=node; scopes[node['id']]=stack
    if node['kind']=='conditional':
     visit(node['whenTrue'],stack);visit(node['whenFalse'],stack)
    elif node['kind']=='bind':visit(node['boundBody']['body'],[node['boundBody']['valueBinding']['id']]+stack)
  visit(fn['body'],[])
  def recipe(node_id,stack):
   n=nodes[node_id];k=n['kind']; r={'kind':k}
   if k=='parameterReference':return {'kind':'parameter','name':parameters[n['parameterId']]}
   if k=='patternReference':return {'kind':k,'scopeDepth':stack.index(n['patternBindingId']),'slot':'value'}
   if k=='int64Literal':return {**r,'value':n['value']}
   if k=='fieldAccess':return {**r,'source':recipe(n['sourceNodeId'],stack),'type':fields[n['fieldId']][0],'field':fields[n['fieldId']][1]}
   if k=='directCall':return {**r,'function':functions[n['targetFunctionId']]['displayName'],'arguments':[{'parameter':parameters[a['targetParameterId']],'value':recipe(a['valueNodeId'],stack)} for a in n['arguments']]}
   keys={'int64WrappingAdd': [('left','leftNodeId'),('right','rightNodeId')], 'int64Max':[('left','leftNodeId'),('right','rightNodeId')], 'listAppendItem':[('list','listNodeId'),('item','itemNodeId')], 'listItemAt':[('list','listNodeId'),('index','indexNodeId')], 'optionGetOrDefault':[('option','optionNodeId'),('default','defaultNodeId')]}
   if k in keys:return {**r,**{key:recipe(n[ref],stack) for key,ref in keys[k]}}
   raise ValueError(k)
  for node in list(nodes.values()):
   if node['kind']!='productConstruct':continue
   acc=next(p for p in fn['parameters'] if p['displayName']=='acc')
   assert node['typeId']==acc['type']['typeId']
   replacement={'kind':'productUpdate' if update else 'productConstruct','type':types[node['typeId']]['displayName'],'fields':[{'field':fields[f['targetFieldId']][1],'value':recipe(f['valueNodeId'],scopes[node['id']])} for f in node['fields']]}
   if update:replacement['source']={'kind':'parameter','name':'acc'}
   steps.append({'kind':'replaceTarget','function':fn['displayName'],'targetNodeId':node['id'],'replacement':replacement})
 assert len(steps)==4
 return steps
for name,update in [('original',None),('construct-control',False),('explicit-update',True)]:
 folder=out/name
 shutil.copytree(snapshot.parent,folder,ignore=shutil.ignore_patterns('zig','*.lock','*.lease'))
 target=folder/snapshot.name
 pathlib.Path(str(target)+'.backends.json').write_text(json.dumps({'targets':['csharp'],'exports':['layoutFrame']}))
 host=subprocess.Popen([str(repo/'tools/zig/host/bin/nyrwvy-host'),'open','--snapshot',str(target)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 def call(op,**args):
  host.stdin.write(json.dumps({'id':op,'op':op,**args})+'\n');host.stdin.flush()
  reply=json.loads(host.stdout.readline());assert reply['ok'],reply
  return reply['result']
 try:
  if update is not None:
   steps=edits(update); (folder/'steps.json').write_text(json.dumps(steps,indent=2))
   review=call('simulateBatchByName',steps=steps,detail='full');(folder/'review.json').write_text(json.dumps(review,indent=2))
   # The full review is saved before committing the isolated copy.
   assert review.get('proposalId'),review
   committed=call('commit',proposalId=review['proposalId']);(folder/'commit.json').write_text(json.dumps(committed,indent=2))
  emitted=call('emitZigLibrary',output='layout.zig');assert any(x['symbol']=='ny_layoutFrame' for x in emitted['exports'])
 finally:
  host.stdin.close();host.wait(timeout=20)
 subprocess.run(['zig','build-lib',str(folder/'layout.zig'),'-dynamic','-lc','-O','ReleaseSafe','-femit-bin='+str(folder/'liblayout.so')],check=True)
 print(name,folder/'liblayout.so',flush=True)
