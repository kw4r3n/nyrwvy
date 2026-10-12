"""Unchanged pinned Team methods; synthetic external effects and deterministic gates."""
import argparse
import asyncio
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch
PIN = '4bab9e030744a0887c1ee13bdd8fc8db3b98334f'

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
        active, peak, integration, parent = 0, 0, False, None
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
            steps.append({'operation':phase,'patch':payload})
            counts[phase] = counts.get(phase,0)+1
            if phase==c.get('cancel') and counts[phase]==1:
                integration_entered.set()
                await integration_release.wait()
            assert not asyncio.current_task().cancelling(), phase
            if phase==c.get('fail') and counts[phase]==1 or phase==c.get('rollbackFail'):
                raise RuntimeError(phase)
            if c.get('rawFault') and phase=='commit': raise RuntimeError('raw commit fault')
        class GitEffects:
            tampered = []
            def add_worktree(self, *_): pass
            async def discard_worktree(self, workspace): pass
            def restore(self): pass
            def unguarded_git_dirs(self): return []
            def release(self): pass
        team.git_host = GitEffects()
        async def clean(cwd, *_):
            if integration: await effect('clean-source' if str(cwd)==str(team.repo) else 'clean-member')
        async def validate(attempt): await effect('validate')
        team.validate_integration = validate  # External policy hook: one synthetic reply.
        original_close = team.close_member_agent
        async def close_agent(run_id):
            if integration: await effect('close-agent')
            await original_close(run_id)
        team.close_member_agent = close_agent  # Observational wrapper invokes unchanged method.
        def save(attempt):
            if integration and attempt.integrated:
                phase='save';steps.append({'operation':phase,'patch':attempt.integrated});counts[phase]=counts.get(phase,0)+1
                if c.get('cancel')==phase and counts[phase]==1:
                    parent.cancel();parent.cancel();assert not parent.done();reply(current_label+':pending','pending')
                assert not asyncio.current_task().cancelling()
                if c.get('fail')==phase and counts[phase]==1: raise RuntimeError(phase)
        team.save = save
        async def git(cwd, *args, **kwargs):
            if integration:
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
                workspaces[args[3]]=f'w{len(ids)+1}';return b''
            if args[:2]==('rev-parse','--absolute-git-dir'): return str(root/'metadata').encode()
            if args[:2]==('add','-A'):
                i=max(index(a.id) for a in team.attempts.values() if a.workspace==str(cwd))
                event(snapshot_entered,i).set()
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
            async def close(self): self.closed+=1;assert self.closed==1
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
            if c.get('checksGate') and commit=='commit1': checks_entered.set();await checks_release.wait()
            return {'status':'passed','commit':commit,'checks':[]}
        def reject(error):
            message=str(error)
            for key,value in [('Member limit','capacity'),('latest','superseded'),('discarded','discarded'),('terminal','running'),('Stop the Member','running'),('must finish','running'),('closing','closing'),('Unknown','unknown')]:
                if key in message: return value
            if 'completed, snapshotted' in message: return 'status'
            raise AssertionError(message)
        with patch.object(m,'clean',clean),patch.object(m,'build_attempt',build),patch.object(m,'check_submission',checks):
            for r in c['script']:
                op,current_label=r['op'],r['label'];target=raw(r['target'])
                try:
                    if op=='spawn':
                        try: records=await team.spawn('synthetic','fake',effort='low',previous=raw(r['previous']))
                        except ValueError as error:
                            if 'unintegrated' in str(error) and r['previous'] and team.get(raw(r['previous'])).integrated: reply(current_label,'reject:integrated');audit(current_label);continue
                            raise
                        ids[records[0]['id']]=len(ids)+1;reply(current_label,f'spawn:{len(ids)}')
                    elif op=='gate':
                        if current_label.startswith('snapshot'):
                            i=int(current_label[8:]);event(start_release,i).set();event(start_release,2).set();await event(entered,2).wait();await event(snapshot_entered,i).wait()
                        elif current_label.startswith('provider'):
                            i=int(current_label[8:]);event(start_release,i).set();await event(entered,i).wait()
                        elif current_label=='release-checks': checks_release.set()
                        elif current_label.startswith('release'):
                            event(snapshot_release,int(current_label[7:])).set()
                            if c.get('checksGate'): await checks_entered.wait()
                        elif current_label=='checks1': await checks_entered.wait()
                        if r['mode']=='settle': reply(current_label,render(await team.wait([target],seconds=5)))
                    elif op=='wait': reply(current_label,render(await team.wait([target] if target else list(ids),mode=r['mode'],seconds=r['limit']/1000)))
                    elif op=='inspect': reply(current_label,render(team.inspect(target)))
                    elif op=='observe':
                        caller=asyncio.create_task(team.wait(list(ids),seconds=None));await asyncio.sleep(0);caller.cancel()
                        try: await caller
                        except asyncio.CancelledError: reply(current_label,'err:cancelled')
                        assert not team.tasks[raw(2)].cancelling() and not team.tasks[raw(2)].done()
                    elif op=='stop':
                        event(start_release,r['target']).set();reply(current_label,render(await team.stop(target)))
                    elif op=='discard': await team.discard(target);reply(current_label,'discarded')
                    elif op=='integrate':
                        integration=True
                        parent=asyncio.create_task(team.integrate(target))
                        if r['mode']=='cancel' and c.get('cancel')!='save':
                            await integration_entered.wait();parent.cancel();parent.cancel();assert not parent.done();reply(current_label+':pending','pending');integration_release.set()
                        try:
                            records=await parent;reply(current_label,'ok:'+records[0]['integrated'])
                        except asyncio.CancelledError: reply(current_label,'err:cancelled')
                        except ValueError as error: reply(current_label,'err:'+reject(error))
                        except RuntimeError as error: reply(current_label,'err:'+str(error))
                        finally: integration=False
                    elif op=='finish':
                        if c.get('finishCancel'):
                            async def close():
                                closing=asyncio.create_task(team.close())
                                await asyncio.sleep(0)
                                assert team.closing and not closing.done()
                                finish_entered.set();await finish_release.wait();event(snapshot_release,1).set();await closing
                            parent=asyncio.create_task(await_cleanup(close()))
                            await finish_entered.wait();parent.cancel();parent.cancel();assert not parent.done();reply(current_label+':pending','pending');finish_release.set()
                            try: await parent
                            except asyncio.CancelledError: reply(current_label+':outcome','err:cancelled')
                        else:
                            await team.close();reply(current_label+':outcome','ok:drained')
                        reply(current_label,'finished')
                except ValueError as error: reply(current_label,'reject:'+reject(error))
                audit(current_label)
            await team.close()
        assert active==0 and all(a.closed==1 for a in agents.values())
        return {'id':c['id'],'replies':replies,'observations':observations,'steps':steps,'peakActive':peak,'diagnostics':diagnostics}

async def main():
    parser=argparse.ArgumentParser();parser.add_argument('rrawrlet');parser.add_argument('bundle');args=parser.parse_args()
    repo,bundle=Path(args.rrawrlet).resolve(),Path(args.bundle).resolve()
    assert subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip()==PIN
    proofs={}
    for name in ['team.py','codex.py','runner.py']:
        data=(repo/'rrawrlet'/name).read_bytes();assert data==subprocess.check_output(['git','-C',str(repo),'show',f'{PIN}:rrawrlet/{name}']);proofs[name]=hashlib.sha256(data).hexdigest()
    sys.path.insert(0,str(repo));import rrawrlet.team as module
    virtual={r['checks']['id']:r['checks'] for r in json.loads((bundle/('replay-evidence.json' if (bundle/'replay-evidence.json').exists() else 'evidence.json')).read_text())}
    runtime={r['id']:r['checks'] for r in json.loads((bundle/'runtime-evidence.json').read_text())['results']}
    rows=[]
    for c in json.loads((bundle/'cases.json').read_text()):
        row=await asyncio.wait_for(run_case(c,module),20)
        if c.get('rawFault'):
            assert row['steps'][-2:]==[{'operation':'reverse-check','patch':c['patch']},{'operation':'reverse-apply','patch':c['patch']}]
            row['comparison']='gap: raw host fault bypasses typed rollback and faults Nyrwvy owner'
        else:
            for source in [virtual[c['id']],runtime[c['id']]]:
                assert row['observations']==source['observations'],(c['id'],'observations',row['observations'],source['observations'])
                assert row['steps']==source['steps'],(c['id'],'steps',row['steps'],source['steps'])
                actual=[x for x in source['replies'] if not x['label'].endswith(':state')]
                # Finish acknowledgement is delayed until provider resource release in Nyrwvy.
                assert sorted(row['replies'],key=lambda x:x['label'])==sorted(actual,key=lambda x:x['label']),(c['id'],'replies',row['replies'],actual)
            row['comparison']='exact normalized observations/effects/replies'
        rows.append(row);print('PASS:',c['id'],row['comparison'])
    # Comparison-sensitivity controls, not compiled graph mutants.
    reference=next(r for r in rows if r['id']=='ledger-patch')
    controls=[]
    for name, mutate in [
        ('lost-ledger',lambda r:r['observations'][-1].update(state=r['observations'][-1]['state'].replace('integrated-head',''))),
        ('hidden-first-save-error',lambda r:next(x for x in r['replies'] if x['label']=='integration').update(record='ok:integrated-head')),
        ('duplicate-save',lambda r:r['steps'].append({'operation':'save','patch':'integrated-head'})),
        ('integrated-continuation-admitted',lambda r:next(x for x in r['replies'] if x['label']=='integrated-previous').update(record='spawn:3')),
        ('observer-cancels-peer',lambda r:r['observations'][4].update(state=r['observations'][4]['state'].replace('2,running','2,stopped'))),
    ]:
        mutant=json.loads(json.dumps(reference));mutate(mutant);assert mutant!=reference;controls.append({'id':name,'rejected':True})
    for name, case_id, label, record in [('premature-settlement','checks-boundary','checks-running','ok:integrated-head'),('cancel-hides-rollback-error','rollback-error-reverse-apply','integration','err:cancelled'),('finish-loses-cancellation','finish-cancel-snapshot','finish:outcome','ok:drained')]:
        reference=next(r for r in rows if r['id']==case_id);mutant=json.loads(json.dumps(reference));next(x for x in mutant['replies'] if x['label']==label)['record']=record;assert mutant!=reference;controls.append({'id':name,'rejected':True})
    evidence={'negativeControls':controls,'oracle':PIN,'sourceProofs':proofs,'python':sys.version,'results':rows}
    (bundle/'oracle-evidence.json').write_text(json.dumps(evidence,indent=2)+'\n')
if __name__=='__main__': asyncio.run(main())
