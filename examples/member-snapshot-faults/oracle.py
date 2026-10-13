"""Independent actual pinned Team.execute/stop/close and await_cleanup.

Only external provider/Git/save effects are synthetic. No method policy copy.
"""
import argparse, asyncio, hashlib, json, subprocess, sys, tempfile
from pathlib import Path
from unittest.mock import patch
sys.dont_write_bytecode=True
PIN='4bab9e030744a0887c1ee13bdd8fc8db3b98334f'

async def run_case(c,m):
    from rrawrlet.backend import AttemptOutcome
    from rrawrlet.codex import await_cleanup
    initial=set(asyncio.all_tasks())
    with tempfile.TemporaryDirectory(prefix='determined-oracle-') as directory:
        root=Path(directory);(root/'output').mkdir()
        team=m.Team(root/'source',root/'output',max_members=2,attempt_seconds=5,reuse_member_threads=False)
        team.catalog=[{'model':'fake','isDefault':True,'defaultReasoningEffort':'low','supportedReasoningEfforts':[{'reasoningEffort':'low'}]}]
        team.member_checks=[{'argv':['fake'],'required':True}];team.git_dir=str(root/'metadata')
        held,released,parked=asyncio.Event(),asyncio.Event(),asyncio.Event()
        effects=[];agents=[];releases=0;cancel_seen=[];diagnostics=[];saves=[]
        asyncio.get_running_loop().set_exception_handler(lambda _,ctx:diagnostics.append({'message':ctx.get('message'),'exception':str(ctx.get('exception'))}))
        class Git:
            tampered=[]
            def add_worktree(self,*_):pass
            def restore(self):pass
            def unguarded_git_dirs(self):return []
            def release(self):
                nonlocal releases
                releases+=1;effects.append('release')
                assert releases==1 and all(t.done() for t in team.tasks.values()) and all(a.closed==1 for a in agents)
                if c.get('releaseFault'):raise RuntimeError('release fault')
        team.git_host=Git();team.save=lambda a:saves.append(a.status)
        async def clean(*_):pass
        async def git(cwd,*args,**kwargs):
            if args[:2]==('rev-parse','--absolute-git-dir'):return str(root/'metadata').encode()
            if args[:2]==('add','-A'):
                effects.append('snapshot');cancel_seen.append({'effect':'snapshot','cancelled':bool(asyncio.current_task().cancelling())})
                if c['boundary']=='snapshot':held.set();await released.wait()
                if c.get('snapshotFault')=='oce':raise asyncio.CancelledError('explicit snapshot cancellation')
                if c.get('snapshotFault'):raise RuntimeError('raw snapshot fault' if c['snapshotFault']=='raw' else 'snapshot failure')
                return b''
            if args[:2]==('rev-parse','HEAD'):return b'base' if str(cwd)==str(team.repo) else b'commit1'
            return b''
        team.host_git=git
        class Agent:
            reusable=False;cleanup_errors=[];thread_id='fake';turn_id='fake'
            def __init__(self):self.closed=0;agents.append(self)
            async def start(self):
                if c['boundary']=='entry':held.set();await released.wait()
            async def close(self):
                self.closed+=1;assert self.closed==1;effects.append('memberClose');cancel_seen.append({'effect':'memberClose','cancelled':bool(asyncio.current_task().cancelling())})
                if c['boundary']=='cleanup':held.set();await released.wait()
            def final_fields(self,_):return {}
        def build(*args,**kwargs):
            agent=Agent()
            async def provider(_):
                effects.append('provider')
                if c['boundary']=='provider':
                    held.set()
                    try:await asyncio.Event().wait()
                    except asyncio.CancelledError:
                        if not c.get('completedAfterCancel'):raise
                return AttemptOutcome('failed' if c.get('providerFailure') else 'completed')
            return agent,provider
        async def checks(*args,**kwargs):
            effects.append('checks')
            if c['boundary']=='checks':held.set();await released.wait()
            if c.get('checksFault'):raise RuntimeError('raw checks fault')
            return {'status':'passed','commit':'commit1','checks':[]}
        with patch.object(m,'clean',clean),patch.object(m,'build_attempt',build),patch.object(m,'check_submission',checks):
            async def own():
                close=None
                try:
                    await team.spawn('synthetic','fake',effort='low')
                    await held.wait()
                    if c['order']=='admitted':
                        # Close is requested behind the real lock, then admitted
                        # after owner cancellation; await_cleanup drains it.
                        await team.lock.acquire()
                        close=asyncio.create_task(team.close())
                        await asyncio.sleep(0);assert not team.closing
                        parked.set();await await_cleanup(close)
                    elif c.get('function'):
                        parked.set();await released.wait()
                        await team.wait(list(team.tasks),mode='all',seconds=None)
                    else:parked.set();await asyncio.Event().wait()
                finally:await await_cleanup(close if close is not None else team.close())
            owner=asyncio.create_task(own())
            await parked.wait()
            attempt=next(iter(team.attempts.values()))
            boundary_status=attempt.status
            assert boundary_status==('running' if c['boundary'] in ['provider','entry'] else 'failed' if c.get('providerFailure') else 'completed')
            if c.get('function'):released.set()
            else:
                owner.cancel();await asyncio.sleep(0);owner.cancel()
                if team.lock.locked():team.lock.release()
                while not team.closing:await asyncio.sleep(0)
                released.set()
            try:await owner;outcome='ok'
            except asyncio.CancelledError:outcome='cancelled'
            except RuntimeError as exc:outcome='error:'+str(exc)
            assert all(t.done() for t in team.tasks.values())
            task=next(iter(team.tasks.values()))
            task_fault='CancelledError' if task.cancelled() else type(task.exception()).__name__ if task.exception() else None
            record=team.inspect()[0]
        assert not(set(asyncio.all_tasks())-initial)
        expected='stopped' if c['boundary']=='provider' else 'failed' if c.get('snapshotFault') in ['raw','typed'] else 'completed'
        assert record['status']==expected,(c,record)
        assert record['commit']==('commit1' if expected=='completed' and c.get('snapshotFault')!='oce' else None)
        assert outcome==('error:release fault' if c.get('releaseFault') else 'ok' if c.get('function') else 'cancelled')
        assert effects.count('memberClose')==1 and releases==1
        if c['boundary'] not in ['provider','entry']:assert not any(x['cancelled'] for x in cancel_seen)
        assert task_fault==('CancelledError' if c.get('snapshotFault')=='oce' else 'RuntimeError' if c.get('checksFault') else None)
        assert effects.count('checks')==(1 if c.get('function') and not c.get('snapshotFault') else 0)
        return {'id':c['id'],'boundaryStatus':boundary_status,'status':record['status'],'error':record.get('error'),'commit':record['commit'],'checks':(record.get('checks')or{}).get('status',''),'outcome':outcome,'effects':effects,'tokens':cancel_seen,'released':releases,'membersDrained':True,'agentsClosed':len(agents),'taskFault':task_fault,'savedStatuses':saves,'diagnostics':diagnostics}

async def main():
    p=argparse.ArgumentParser();p.add_argument('rrawrlet');p.add_argument('cases');p.add_argument('output');a=p.parse_args()
    repo=Path(a.rrawrlet).resolve();assert subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip()==PIN
    proofs={}
    for name in ['team.py','codex.py','runner.py','team_tools.py']:
        data=(repo/'rrawrlet'/name).read_bytes();assert data==subprocess.check_output(['git','-C',str(repo),'show',f'{PIN}:rrawrlet/{name}']);proofs[name]=hashlib.sha256(data).hexdigest()
    sys.path.insert(0,str(repo));import rrawrlet.team as m
    rows=[]
    for c in json.loads(Path(a.cases).read_text()):rows.append(await asyncio.wait_for(run_case(c,m),15));print('PASS:',c['id'])
    Path(a.output).write_text(json.dumps({'pin':PIN,'sourceProofs':proofs,'results':rows},indent=2)+'\n')
if __name__=='__main__':asyncio.run(main())
