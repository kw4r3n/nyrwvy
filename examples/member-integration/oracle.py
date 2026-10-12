"""Differential check against pinned, unchanged Team.integrate with effect stubs.

No real Git commands or providers. Team/get/latest/integrate/lock remain real.
"""
import argparse
import asyncio
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch


async def run_case(c, module):
    with tempfile.TemporaryDirectory(prefix='integration-oracle-') as root:
        team = module.Team(Path(root) / 'source', Path(root) / 'output')
        state = c['candidate']
        a = module.Attempt('member', 'synthetic-workspace', 'base', 'fake', 'low', 'synthetic')
        a.status, a.commit, a.integrated = state['status'], state['commit'] or None, state['integrated'] or None
        a.discarded = state['discarded']
        team.attempts[a.id] = a
        if not state['latest']:
            team.attempts['continuation'] = module.Attempt('continuation', a.workspace, 'base', 'fake', 'low', 'synthetic', previous=a.id)
        # Genuine settled/held task handles, independent from determined status.
        worker = asyncio.create_task(asyncio.sleep(0 if state['settled'] else 3600))
        team.tasks[a.id] = worker
        if state['settled']:
            await worker
        trace, counts = [], {}
        def effect(operation):
            trace.append(operation)
            count = counts.get(operation, 0)
            counts[operation] = count + 1
            if (operation == c.get('fail') and count == 0) or operation == c.get('rollbackFail'):
                raise RuntimeError(operation)
        async def clean(cwd, host):
            effect('clean-source' if cwd == team.repo else 'clean-member')
        async def validate(attempt):
            assert attempt is a
            effect('validate')
        async def close_agent(run_id):
            assert run_id == a.id
            effect('close-agent')
        def save(attempt):
            assert attempt is a
            assert attempt.integrated == ('integrated-head' if c['patch'] else 'source-head')
            effect('save')
        async def git(cwd, *args, **kwargs):
            if args[0] == 'merge-base':
                assert args == ('merge-base', '--is-ancestor', 'base', 'HEAD')
                effect('ancestor')
            elif args[0] == 'diff' and '--name-only' in args:
                operation = 'paths-member' if args[-1] == 'member-commit' else 'paths-leader'
                assert args[:4] == ('diff', '--name-only', '--no-renames', '-z')
                effect(operation)
                return ('\0'.join(c['member' if operation == 'paths-member' else 'leader']) + '\0').encode()
            elif args[0] == 'diff':
                assert args == ('diff', '--binary', '--no-textconv', 'base', 'member-commit')
                effect('patch')
                return c['patch'].encode()
            elif args[0] == 'apply':
                assert cwd == team.repo and kwargs['data'] == c['patch'].encode()
                operation = ('reverse-' if '--reverse' in args else '') + ('check' if '--check' in args else 'apply')
                assert '--index' in args and args[-1] == '-'
                effect(operation)
            elif 'commit' in args:
                assert args[:4] == ('-c', 'core.hooksPath=/dev/null', '-c', 'commit.gpgsign=false')
                effect('commit')
            elif args[0] == 'rev-parse':
                assert args == ('rev-parse', 'HEAD')
                effect('head')
                return b'integrated-head' if c['patch'] else b'source-head'
            else:
                raise AssertionError(args)
            return b''
        team.host_git, team.validate_integration, team.close_member_agent, team.save = git, validate, close_agent, save
        def normalize(exc):
            if isinstance(exc, RuntimeError):
                return str(exc)
            assert isinstance(exc, ValueError)
            for marker, name in [('latest', 'superseded'), ('discarded', 'discarded'), ('finish', 'running'), ('Overlapping', 'overlap')]:
                if marker in str(exc):
                    return name
            return 'unsnapshotted' if a.status == 'completed' else 'status'
        try:
            with patch.object(module, 'clean', clean):
                for _ in range(c['requests']):
                    try:
                        result = await team.integrate(a.id)
                        observed = {'ok': True, 'value': result[0]['integrated']}
                    except (ValueError, RuntimeError) as exc:
                        observed = {'ok': False, 'value': normalize(exc)}
            return {'id': c['id'], 'trace': trace, 'result': observed, 'integrated': a.integrated or ''}
        finally:
            worker.cancel()
            await asyncio.gather(worker, return_exceptions=True)


def compare(expected, actual):
    assert actual == expected, (expected, actual)


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('rrawrlet')
    parser.add_argument('bundle')
    args = parser.parse_args()
    repo, bundle = Path(args.rrawrlet).resolve(), Path(args.bundle).resolve()
    head = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    assert head == '4bab9e030744a0887c1ee13bdd8fc8db3b98334f'
    # Verify working source is the pinned blob, not just a matching HEAD.
    source = repo / 'rrawrlet/team.py'
    pinned = subprocess.check_output(['git', '-C', str(repo), 'show', f'{head}:rrawrlet/team.py'])
    assert source.read_bytes() == pinned
    sys.path.insert(0, str(repo))
    import rrawrlet.team as module
    cases = json.loads((bundle / 'cases.json').read_text())
    virtual_file = bundle / 'evidence.json'
    if not virtual_file.is_file():
        virtual_file = bundle / 'replay-evidence.json'
    virtual = {r['checks']['id']: r['checks'] for r in json.loads(virtual_file.read_text())}
    runtime = {r['id']: r for r in json.loads((bundle / 'runtime-evidence.json').read_text())['results']}
    rows = []
    for c in cases:
        actual = await asyncio.wait_for(run_case(c, module), 10)
        compare(virtual[c['id']], actual)
        compare(runtime[c['id']], actual)
        rows.append(actual)
        print('PASS: unchanged Team.integrate', c['id'])
    controls = []
    mutations = [
        ('reintegrate-does-effects', 'already-integrated', 'trace', ['apply']),
        ('running-determined-as-settled', 'running', 'result', {'ok': True, 'value': 'integrated-head'}),
        ('overlap-accepted', 'overlap', 'result', {'ok': True, 'value': 'integrated-head'}),
        ('commit-failure-without-rollback', 'commit-rollback', 'trace', next(r['trace'][:-2] for r in rows if r['id'] == 'commit-rollback')),
        ('failed-commit-publishes-ledger', 'commit-rollback', 'integrated', 'integrated-head'),
        ('rollback-failure-hidden', 'rollback-check-fails', 'result', {'ok': False, 'value': 'commit'}),
    ]
    for name, case_id, key, mutant in mutations:
        row = next(r for r in rows if r['id'] == case_id)
        try:
            compare(row, row | {key: mutant})
        except AssertionError:
            controls.append({'id': name, 'rejected': True})
        else:
            raise AssertionError('Negative comparison control escaped: ' + name)
    (bundle / 'oracle-evidence.json').write_text(json.dumps({'oracleHead': head, 'teamSha256': hashlib.sha256(pinned).hexdigest(), 'methodsUnmodified': ['integrate', 'get', 'latest'], 'effectStubs': ['clean', 'host_git', 'validate_integration', 'close_member_agent', 'save'], 'results': rows, 'negativeControls': controls}, indent=2) + '\n')
    print(f'PASS: {len(rows)} differential cases; {len(controls)} comparison controls')

if __name__ == '__main__':
    asyncio.run(main())
