"""Pinned Team.integrate and await_cleanup; external effects only, no Git/provider calls."""
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


async def run_case(c, module):
    with tempfile.TemporaryDirectory(prefix='shield-oracle-') as root:
        team = module.Team(Path(root)/'source', Path(root)/'output')
        a = module.Attempt('member', 'workspace', 'base', 'fake', 'low', 'synthetic')
        a.status, a.commit = 'completed', 'member-commit'
        team.attempts[a.id] = a
        member = asyncio.create_task(asyncio.sleep(0))
        await member
        team.tasks[a.id] = member
        steps, reports = [], []
        entered, release = asyncio.Event(), asyncio.Event()
        parent = None

        async def effect(phase):
            steps.append(phase)
            if phase == c.get('cancel'):
                entered.set()
                await release.wait()
            assert not asyncio.current_task().cancelling(), phase
            reports.append(phase+'=clear')
            if phase in (c.get('fail'), c.get('rollbackFail')):
                raise RuntimeError(phase)

        async def clean(*_):
            pass

        async def validate(*_):
            pass

        async def close_agent(*_):
            pass

        def save(attempt):
            assert attempt is a
            steps.append('save')
            if c.get('cancel') == 'save':
                # Python save is synchronous; cancellation is requested here,
                # before return/throw. No asynchronous interruption of save is claimed.
                parent.cancel()
                parent.cancel()
                assert not parent.done()
                reports.append('parent-pending')
            assert not asyncio.current_task().cancelling()
            reports.append('save=clear')
            if c.get('fail') == 'save':
                raise RuntimeError('save')

        async def git(cwd, *args, **kwargs):
            if args[0] == 'merge-base':
                return b''
            if args[0] == 'diff':
                return b'' if '--name-only' in args or not c['patch'] else b'patch'
            if args[0] == 'apply':
                assert kwargs['data'] == b'patch'
                phase = ('reverse-' if '--reverse' in args else '')+('check' if '--check' in args else 'apply')
                if phase != 'check':
                    await effect(phase)
                return b''
            if 'commit' in args:
                await effect('commit')
                return b''
            if args == ('rev-parse', 'HEAD'):
                await effect('head')
                return b'integrated-head' if c['patch'] else b'source-head'
            raise AssertionError(args)

        team.host_git, team.validate_integration = git, validate
        team.close_member_agent, team.save = close_agent, save
        with patch.object(module, 'clean', clean):
            parent = asyncio.create_task(team.integrate(a.id))
            if c.get('cancel') and c['cancel'] != 'save':
                await entered.wait()
                parent.cancel()
                await asyncio.sleep(0)  # process the first request; no wall-time delay
                parent.cancel()
                await asyncio.sleep(0)
                assert not parent.done()
                reports.append('parent-pending')
                release.set()
            try:
                result = await parent
                final = 'ok:'+result[0]['integrated']
            except asyncio.CancelledError:
                final = 'err:cancelled'
            except RuntimeError as exc:
                final = 'err:'+str(exc)
        reports.append((a.integrated or '')+'|'+final)
        return {'id':c['id'], 'steps':steps, 'reports':reports}


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('rrawrlet')
    parser.add_argument('bundle')
    parser.add_argument('--baseline',action='store_true')
    args = parser.parse_args()
    repo, bundle = Path(args.rrawrlet).resolve(), Path(args.bundle).resolve()
    assert subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip() == PIN
    hashes = {}
    for path in ['rrawrlet/team.py', 'rrawrlet/codex.py']:
        pinned = subprocess.check_output(['git','-C',str(repo),'show',f'{PIN}:{path}'])
        assert (repo/path).read_bytes() == pinned
        hashes[path] = hashlib.sha256(pinned).hexdigest()
    sys.path.insert(0,str(repo))
    import rrawrlet.team as module
    virtual = json.loads((bundle/'evidence.json').read_text())
    runtime = json.loads((bundle/'runtime-evidence.json').read_text())
    replay = json.loads((bundle/'replay-evidence.json').read_text()) if (bundle/'replay-evidence.json').exists() else virtual
    cases = json.loads((bundle/'cases.json').read_text())
    diagnostics = []
    loop = asyncio.get_running_loop()
    def diagnostic_handler(loop, context):
        if context.get('message') == 'RuntimeError exception in shielded future' and isinstance(context.get('exception'), RuntimeError):
            diagnostics.append(str(context['exception']))
        else:
            loop.default_exception_handler(context)
    loop.set_exception_handler(diagnostic_handler)
    rows = []
    for c in cases:
        row = await asyncio.wait_for(run_case(c,module),10)
        if not args.baseline:
            for source in [virtual,runtime,replay]:
                assert next(x for x in source['results'] if x['id']==c['id']) == row, (c['id'],source,row)
        else:
            assert virtual == replay
            assert runtime['results'] == virtual['results']
        rows.append(row)
        print('PASS: unchanged Python',c['id'])
    disagreements = [row['id'] for row in rows if row != next(x for x in virtual['baseline'] if x['id']==row['id'])]
    controls = []
    for name,case,key,mutant in [
        ('cancel-propagates-early','cancel-commit','reports',['parent-settled']),
        ('no-rollback','rollback-cancel-commit','steps',['apply','commit']),
        ('pending-cancel-hides-commit-error','rollback-cancel-commit','reports',['|err:cancelled']),
        ('lost-ledger','cancel-save','reports',['|err:cancelled']),
        ('cancel-hides-save-error','save-failure','reports',['integrated-head|err:cancelled']),
    ]:
        row = next(x for x in rows if x['id']==case)
        assert row != row | {key:mutant}
        controls.append({'id':name,'rejected':True})
    assert len(disagreements) == 12, disagreements
    evidence = {'oracleHead':PIN,'sourceHashes':hashes,'results':rows,'unshieldedDisagreements':disagreements,'negativeControls':controls,'shieldedFutureDiagnostics':diagnostics}
    (bundle/'oracle-evidence.json').write_text(json.dumps(evidence,indent=2)+'\n')
    print(f'PASS: {len(rows)} oracle scenarios; {len(disagreements)} unshielded failures; {len(controls)} controls rejected; baseline={args.baseline}')

if __name__=='__main__':
    asyncio.run(main())
