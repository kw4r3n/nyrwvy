"""Executable JSONL acceptance examples; build AuthoringSession first. No extra packages."""
import atexit
import contextlib
import sys
import select
import json
import pathlib
import subprocess
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[2]
DLL = REPO / 'tools/Nyrwvy.AuthoringSession/bin/Debug/net10.0/Nyrwvy.AuthoringSession.dll'

class Session:
    def __init__(self, path, mode='create'):
        self.process = subprocess.Popen(['dotnet', str(DLL), mode, '--snapshot', str(path)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        atexit.register(self.cleanup)
        self.serial = 0
    def request(self, op, ok=True, **fields):
        self.serial += 1
        self.process.stdin.write(json.dumps(dict(id=str(self.serial), op=op, **fields)) + '\n')
        self.process.stdin.flush()
        assert select.select([self.process.stdout], [], [], 90)[0], 'JSONL request timed out'
        reply = json.loads(self.process.stdout.readline())
        assert reply['ok'] == ok, reply
        return reply.get('result', reply.get('error'))
    def publish(self, op, **fields):
        proposal = self.request(op, **fields)
        self.request('commit', proposalId=proposal['proposalId'])
        return proposal
    def cleanup(self):
        if self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=10)
    def close(self):
        self.process.stdin.close()
        assert self.process.wait(timeout=10) == 0

def integer(value): return dict(kind='int64', value=str(value))
def literal(value): return dict(kind='int64Literal', value=str(value))
def parameter(name): return dict(kind='parameter', name=name)
def addition(left, right): return dict(kind='int64WrappingAdd', left=left, right=right)

destination = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else None
if destination: destination.mkdir(parents=True, exist_ok=True)
with contextlib.nullcontext(str(destination)) if destination else tempfile.TemporaryDirectory(prefix='nyrwvy-change-verification-') as directory:
    path = pathlib.Path(directory) / 'workspace.json'
    session = Session(path)
    typ = dict(kind='primitive', primitive='int64')
    session.publish('simulateCreateFunctionByName', displayName='increment', parameters=[dict(displayName='x', type=typ)], resultType=typ, requiredEffects=[], body=addition(parameter('x'), literal(1)))
    function_id = session.request('runPureByName', function='increment', arguments=[integer(10)])['functionId']
    session.publish('simulateCreateFunctionByName', displayName='wrapper', parameters=[dict(displayName='x', type=typ)], resultType=typ, requiredEffects=[], body=dict(kind='directCall', function='increment', arguments=[dict(parameter='x', value=parameter('x'))]))
    wrapper_id = next(fn['id'] for fn in session.request('declarations')['functions'] if fn['displayName'] == 'wrapper')
    baseline = path.read_bytes()
    def propose(body): return session.request('simulateReplaceBodyByName', function='increment', replacement=body)['proposalId']
    def verify(proposal, expected=None, args=None):
        case = dict(label='increment 10 preserves 11' if expected is None else 'intended new increment result', functionId=function_id, arguments=args or [integer(10)])
        if expected is not None: case['expectedCandidate'] = integer(expected)
        return session.request('verifyChange', proposalId=proposal, cases=[case], output=f'reviews/check-{session.serial + 1}.html')
    safe = propose(addition(literal(1), parameter('x')))
    safe_result = verify(safe)
    assert safe_result['evidence']['cases'][0]['status'] == 'tested-agreement', safe_result
    assert function_id in safe_result['evidence']['affectedFunctionIds']
    assert wrapper_id in safe_result['evidence']['affectedFunctionIds']
    assert 'Experimental change verification' in pathlib.Path(safe_result['reviewPath']).read_text()
    assert path.read_bytes() == baseline
    changed = propose(addition(parameter('x'), literal(2)))
    caught = verify(changed)
    assert caught['evidence']['cases'][0]['status'] == 'counterexample', caught
    intended = verify(changed, 12)
    assert intended['evidence']['cases'][0]['status'] == 'intended-change-tested', intended
    wrong = verify(changed, 99)
    assert wrong['evidence']['cases'][0]['status'] == 'counterexample', wrong
    unknown = verify(changed, args=[dict(kind='string', value='unsupported input')])
    assert unknown['evidence']['cases'][0]['status'] == 'unknown', unknown
    session.request('reject', proposalId=changed)
    session.request('verifyChange', ok=False, proposalId=changed, cases=[dict(label='rejected cannot pass', functionId=function_id, arguments=[integer(10)])])
    assert path.read_bytes() == baseline
    # Publishing another proposal makes the formerly successful handle stale.
    newer = propose(addition(parameter('x'), literal(3)))
    session.request('commit', proposalId=newer)
    session.request('verifyChange', ok=False, proposalId=safe, cases=[dict(label='stale cannot pass', functionId=function_id, arguments=[integer(10)])])
    session.close()
    session = Session(path, 'open')
    actual = session.request('runPureByName', function='increment', arguments=[integer(10)])
    assert actual['actual'] == integer(13), actual
    session.request('verifyChange', ok=False, proposalId=safe, cases=[dict(label='reopened handle cannot pass', functionId=function_id, arguments=[integer(10)])])
    loop = propose(dict(kind='directCall', function='increment', arguments=[dict(parameter='x', value=parameter('x'))]))
    timeout = verify(loop)
    assert timeout['evidence']['cases'][0]['status'] == 'unknown', timeout
    assert 'RunTimeout' in timeout['evidence']['cases'][0]['candidate']['unknown'], timeout
    session.request('reject', proposalId=loop)
    # Same returned value but different effect arguments must be a counterexample.
    session.publish('simulateCreateEffect', displayName='io')
    session.publish('simulateCreateCapabilityByName', displayName='Provider', operations=[dict(displayName='read', parameters=[dict(displayName='key', type=typ)], resultType=typ, effect='io')])
    def invoke(key): return dict(kind='capabilityInvoke', capabilityValue=parameter('provider'), capability='Provider', operation='read', arguments=[dict(parameter='key', value=literal(key))])
    session.publish('simulateCreateFunctionByName', displayName='query', parameters=[dict(displayName='provider', type=dict(kind='capability', name='Provider'))], resultType=typ, requiredEffects=['io'], body=invoke(1))
    declarations = session.request('declarations')
    cap = next(cap for cap in declarations['capabilities'] if cap['displayName'] == 'Provider')
    query_id = next(fn['id'] for fn in declarations['functions'] if fn['displayName'] == 'query')
    fake = dict(kind='fakeCapability', capabilityId=cap['id'], operations=[dict(operationId=cap['operations'][0]['id'], responses=[dict(kind='return', value=integer(7))])])
    effect_change = session.request('simulateReplaceBodyByName', function='query', replacement=invoke(2))['proposalId']
    effect_case = dict(label='preserve effect argument and returned value', functionId=query_id, arguments=[fake])
    effect = session.request('verifyChange', proposalId=effect_change, cases=[effect_case], output='reviews/effect-counterexample.html')
    item = effect['evidence']['cases'][0]
    assert item['baseline']['actual'] == item['candidate']['actual']
    assert item['status'] == 'counterexample', effect
    failing = dict(kind='fakeCapability', capabilityId=cap['id'], operations=[dict(operationId=cap['operations'][0]['id'], responses=[dict(kind='throw', message='unavailable')])])
    effect_case['arguments'] = [failing]
    failure = session.request('verifyChange', proposalId=effect_change, cases=[effect_case], output='reviews/failure-unknown.html')
    assert failure['evidence']['cases'][0]['status'] == 'unknown', failure
    session.request('reject', proposalId=effect_change)
    if destination:
        (destination / 'evidence.json').write_text(json.dumps(dict(safe=safe_result, caught=caught, intended=intended, wrong=wrong, unsupported=unknown, timeout=timeout, effect=effect, failure=failure), indent=2))
    session.close()
    print(json.dumps({'safe_refactor': 'tested-agreement', 'behavior_change': 'counterexample', 'explicit_change': 'intended-change-tested', 'wrong_intent': 'counterexample', 'unsupported': 'unknown', 'rejection': 'baseline bytes unchanged', 'stale_and_reopened': 'rejected', 'save_reopen': 'committed result 13', 'caller_impact': 'wrapper affected', 'timeout': 'unknown', 'effect_arguments': 'counterexample', 'failure': 'unknown'}, indent=2))
