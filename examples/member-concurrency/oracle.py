"""Actual pinned Team/asyncio.Lock/await_cleanup; effects and gates only."""
import argparse, asyncio, hashlib, json, subprocess, sys, tempfile
from pathlib import Path
from unittest.mock import patch
from contextvars import ContextVar
PIN='4bab9e030744a0887c1ee13bdd8fc8db3b98334f'
async def run_case(c, m):
    from rrawrlet.backend import AttemptOutcome
    from rrawrlet.codex import await_cleanup
    with tempfile.TemporaryDirectory(prefix='controller-oracle-') as directory:
        root = Path(directory)
        (root/'output').mkdir()
        team = m.Team(root/'source', root/'output', max_members=2, attempt_seconds=5, reuse_member_threads=False)
        team.catalog = [{'model':'fake', 'isDefault':True, 'defaultReasoningEffort':'low', 'supportedReasoningEfforts':[{'reasoningEffort':'low'}]}]
        team.member_checks = [{'argv':['fake'],'required':True}]
        team.git_dir = str(root/'metadata')
        ids, workspaces, agents = {}, {}, {}
        entered, start_release, snapshot_entered, snapshot_release = {}, {}, {}, {}
        checks_entered, checks_release = asyncio.Event(), asyncio.Event()
        integration_entered, integration_release = asyncio.Event(), asyncio.Event()
        finish_entered, finish_release = asyncio.Event(), asyncio.Event()
        replies, observations, steps, counts = [], [], [], {}
        active, peak = 0, 0
        request_context = ContextVar("request", default=("worker", "worker"))
        callers = {}
        spawn_entered, spawn_release = asyncio.Event(), asyncio.Event()
        close_entered, close_release = asyncio.Event(), asyncio.Event()
        events = []
        def current(): return request_context.get()[0]
        def integrating(): return request_context.get()[1]=="integrate"
        def trace(operation, payload=""): events.append({"request":current(),"operation":operation,"patch":payload})
        diagnostics = []
        asyncio.get_running_loop().set_exception_handler(lambda _, context: diagnostics.append(str(context.get('exception'))))
        def event(table, i): return table.setdefault(i, asyncio.Event())
        def reply(label, record): replies.append({'label':label,'record':record})
        def index(raw): return ids.get(raw,0)
        def raw(i): return next((k for k,v in ids.items() if v==i),None)
        def render(records):
            result = ''
            for a in records:
                if 'workspace' not in a: result += f"{index(a['id'])},running;"
                else: result += f"{index(a['id'])},{a['status']},{workspaces[a['workspace']]},base,{index(a['previous'])},{str(a['discarded']).lower()},{a['commit'] or ''},{(a.get('checks') or {}).get('status') or ''},{a.get('integrated') or ''};"
            return result
        def audit(label): observations.append({'label':label,'state':render(team.inspect()),'steps':list(steps)})
        async def effect(phase, payload=''):
            steps.append({'operation':phase,'patch':payload});trace(phase,payload)
            counts[phase] = counts.get(phase,0)+1
            if phase=='commit' and c['kind'] in ['duplicate','wait-stop','close'] and counts[phase]==1:
                integration_entered.set()
                try:
                    await integration_release.wait()
                except asyncio.CancelledError:
                    # Only this bounded throwing check host survives cancellation.
                    # Cooperative check exits here without producing a raw fault.
                    if phase!='check' or c.get('rawStepFault')!='check' or c.get('cooperativeCheck'): raise
                    await integration_release.wait()
                    raise RuntimeError('raw check fault')
            assert not asyncio.current_task().cancelling(), phase
            if phase==c.get('rawStepFault') and counts[phase]==1: raise RuntimeError(f'raw {phase} fault')
            if phase==c.get('fail') and counts[phase]==1 or phase==c.get('rollbackFail'):
                raise RuntimeError(phase)
            if c.get('rawFault') and phase=='commit': raise RuntimeError('raw commit fault')
            if c.get('rawHeadFault') and phase=='head' and counts[phase]==1: raise RuntimeError('raw head fault')
        class GitEffects:
            tampered = []
            released = 0
            def add_worktree(self, *_): pass
            async def discard_worktree(self, workspace): pass
            def restore(self): pass
            def unguarded_git_dirs(self): return []
            def release(self):
                self.released+=1
                assert self.released==1 and active==0
                assert all(task.done() for task in team.tasks.values())
                assert all(agent.closed==1 for agent in agents.values())
                events.append({'request':'owner','operation':'release','patch':''})
        team.git_host = GitEffects()
        async def clean(cwd, *_):
            if integrating(): await effect('clean-source' if str(cwd)==str(team.repo) else 'clean-member')
        async def validate(attempt): await effect('validate')
        team.validate_integration = validate  # External policy hook: one synthetic reply.
        original_close = team.close_member_agent
        async def close_agent(run_id):
            if integrating(): await effect('close-agent')
            await original_close(run_id)
        team.close_member_agent = close_agent  # Observational wrapper invokes unchanged method.
        def save(attempt):
            if integrating() and attempt.integrated:
                phase='save';steps.append({'operation':phase,'patch':attempt.integrated});trace(phase,attempt.integrated);counts[phase]=counts.get(phase,0)+1
                assert not asyncio.current_task().cancelling()
                if c.get('rawSaveFault') and counts[phase]==1: raise RuntimeError('raw save fault')
                if c.get('fail')==phase and counts[phase]==1: raise RuntimeError(phase)
        team.save = save
        original_stop=team.stop
        async def stop(run_id):
            if current()=='closing' and index(run_id)==2:
                assert team.closing
                close_entered.set();await close_release.wait()
            return await original_stop(run_id)
        team.stop=stop
        async def git(cwd, *args, **kwargs):
            if integrating():
                if args[0]=='merge-base': await effect('ancestor');return b''
                if args[0]=='diff':
                    phase='paths-member' if '--name-only' in args and args[-1]!='HEAD' else 'paths-leader' if '--name-only' in args else 'patch'
                    await effect(phase)
                    return b'a\0b\0' if phase=='paths-member' else b'c\0' if phase=='paths-leader' else c['patch'].encode()
                if args[0]=='apply':
                    phase=('reverse-' if '--reverse' in args else '')+('check' if '--check' in args else 'apply')
                    assert kwargs['data']==c['patch'].encode();await effect(phase,c['patch']);return b''
                if 'commit' in args: await effect('commit');return b''
                if args==('rev-parse','HEAD'): await effect('head');return (b'integrated-head' if c['patch'] else b'source-head')
                raise AssertionError(args)
            if args[:2]==('worktree','add'):
                workspaces[args[3]]=f'w{len(ids)+1}';trace('workspaceCreate',workspaces[args[3]])
                if current()=='winner': spawn_entered.set();await spawn_release.wait()
                return b''
            if args[:2]==('rev-parse','--absolute-git-dir'): return str(root/'metadata').encode()
            if args[:2]==('add','-A'):
                i=max(index(a.id) for a in team.attempts.values() if a.workspace==str(cwd))
                trace('snapshot',str(i));event(snapshot_entered,i).set()
                if c.get('finishCancel') and i==1:
                    # finish starts only after this snapshot boundary is established.
                    pass
                await event(snapshot_release,i).wait()
                return b''
            if args[:2]==('rev-parse','HEAD'):
                if str(cwd)==str(team.repo): return b'base'
                i=max(index(a.id) for a in team.attempts.values() if a.workspace==str(cwd));return f'commit{i}'.encode()
            if args[:2]==('rev-parse','--verify'): return b'base'
            return b''
        team.host_git = git
        class Agent:
            reusable=False
            cleanup_errors=[]
            def __init__(self, attempt): self.attempt=attempt;self.closed=0;self.thread_id='fresh';self.turn_id='first'
            async def start(self): pass
            async def close(self):
                self.closed+=1;assert self.closed==1;trace('memberClose',str(index(self.attempt.id)))
            def final_fields(self, _): return {}
        def build(_, attempt, **kwargs):
            agent=Agent(attempt);agents[attempt.id]=agent
            async def provider(_):
                nonlocal active,peak
                i=index(attempt.id)
                await event(start_release,i).wait()
                active+=1;peak=max(peak,active);event(entered,i).set()
                try:
                    if c['providers'].get(str(i),'hold')=='hold': await asyncio.Event().wait()
                    if c.get('providerError') and i==1: raise TimeoutError('inner timeout')
                    return AttemptOutcome('completed')
                finally: active-=1
            return agent,provider
        async def checks(_,workspace,commit,*args,**kwargs):
            trace('checks',str(int(commit[6:])))
            if c.get('checksGate') and commit=='commit1': checks_entered.set();await checks_release.wait()
            return {'status':'passed','commit':commit,'checks':[]}
        def reject(error):
            message=str(error)
            for key,value in [('Member limit','capacity'),('latest','superseded'),('discarded','discarded'),('terminal','running'),('Stop the Member','running'),('must finish','running'),('closing','closing'),('Unknown','unknown')]:
                if key in message: return value
            if 'completed, snapshotted' in message: return 'status'
            raise AssertionError(message)
        async def request(r, op=None):
            op=op or r['op'];label=r['label'];target=raw(r['previous'] if r['op']=='begin' else r['target'])
            token=request_context.set((label,op))
            try:
                if op=='spawn':
                    records=await team.spawn('synthetic','fake',effort='low')
                    ids[records[0]['id']]=len(ids)+1
                    record=f"spawn:{index(records[0]['id'])}"
                elif op=='integrate':
                    records=await team.integrate(target);record='ok:'+records[0]['integrated']
                elif op=='wait': record=render(await team.wait([target],mode='all',seconds=None))
                elif op=='stop': record=render(await team.stop(target))
                elif op=='finish': await await_cleanup(team.close());record='ok:drained'
                elif op=='inspect': record=render(team.inspect(target))
                else: raise AssertionError(op)
            except asyncio.CancelledError: record='err:cancelled'
            except ValueError as error: record=('err:' if op=='integrate' else 'reject:')+reject(error)
            reply(label,record)
            trace('reply',record)
            request_context.reset(token)
            return record
        with patch.object(m,'clean',clean),patch.object(m,'build_attempt',build),patch.object(m,'check_submission',checks):
            for r in c['script']:
                op,label=r['op'],r['label']
                if op=='begin':
                    assert sum(not t.done() for t in callers.values())<3
                    callers[r['target']]=asyncio.create_task(request(r,r['mode']))
                    await asyncio.sleep(0)
                elif op=='join': await callers[r['target']]
                elif op=='cancel': callers[r['target']].cancel()
                elif op=='gate':
                    if label=='spawn-held': await spawn_entered.wait()
                    elif label=='spawn-release': spawn_release.set()
                    elif label=='commit-held': await integration_entered.wait()
                    elif label=='commit-release': integration_release.set()
                    elif label=='close-held': await close_entered.wait();assert team.closing
                    elif label=='close-release': close_release.set()
                    elif label.startswith('queued:'):
                        await asyncio.sleep(0)
                        assert team.lock.locked() and len(team.lock._waiters or ())>=1
                        if label=='queued:closing': assert not team.closing
                    elif label.startswith('snapshot'):
                        i=int(label[8:]);event(start_release,i).set();await event(snapshot_entered,i).wait()
                    elif label.startswith('release'):
                        i=int(label[7:]);event(snapshot_release,i).set();await team.wait([raw(i)],mode='all',seconds=None)
                    elif label.startswith('provider'):
                        i=int(label[8:]);event(start_release,i).set();await event(entered,i).wait()
                    else: raise AssertionError(label)
                    events.append({'request':'coordinator','operation':'gate','patch':label})
                else: await request(r)
            await asyncio.gather(*callers.values())
            close_release.set()
            if not team.closing: await team.close()
        assert active==0 and peak<=2 and len(ids)<=2 and all(a.closed==1 for a in agents.values())
        assert not diagnostics,diagnostics
        assert team.git_host.released==1
        return {'id':c['id'],'replies':replies,'state':render(team.inspect()),'events':events,'peakActive':peak,'released':team.git_host.released,'diagnostics':diagnostics}

async def main():
    p=argparse.ArgumentParser();p.add_argument('rrawrlet');p.add_argument('cases');p.add_argument('output');args=p.parse_args()
    repo=Path(args.rrawrlet).resolve()
    assert subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip()==PIN
    proofs={}
    for name in ['team.py','codex.py','runner.py']:
        data=(repo/'rrawrlet'/name).read_bytes()
        assert data==subprocess.check_output(['git','-C',str(repo),'show',f'{PIN}:rrawrlet/{name}'])
        proofs[name]=hashlib.sha256(data).hexdigest()
    sys.path.insert(0,str(repo));import rrawrlet.team as module
    rows=[]
    for c in json.loads(Path(args.cases).read_text()):
        rows.append(await asyncio.wait_for(run_case(c,module),15));print('PASS:',c['id'])
    Path(args.output).write_text(json.dumps({'pin':PIN,'sourceProofs':proofs,'results':rows},indent=2)+'\n')
if __name__=='__main__': asyncio.run(main())
