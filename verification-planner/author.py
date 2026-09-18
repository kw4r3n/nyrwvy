"""Authoring intents only; all declaration identities come from session discovery."""
import json
import sys
from session_client import ROOT, request


def call(op, **kw):
    return request({'id': op, 'op': op, **kw})


def commit_last():
    entries = [json.loads(line) for line in (ROOT / 'transcript.jsonl').read_text().splitlines()]
    proposal = next(e['response']['result'] for e in reversed(entries) if e['request']['op'].startswith('simulate') and e['response']['ok'])
    print(json.dumps(call('commit', proposalId=proposal['proposalId']), indent=2))


def discover():
    d = call('declarations')
    return ({t['displayName']: t for t in d['types']}, {f['displayName']: f for f in d['functions']})


def prim(p): return {'kind': 'primitive', 'primitive': p}
def named(t): return {'kind': 'named', 'typeId': t['id']}
def slot(i): return {'kind': 'parameterSlot', 'index': i}
def boolean(v): return {'kind': 'boolLiteral', 'value': v}
def cond(c, t, f): return {'kind': 'conditional', 'condition': c, 'whenTrue': t, 'whenFalse': f}
def variants(t): return {v['displayName']: v for v in t['typeKind']['variants']}
def union(v): return {'kind': 'unionConstruct', 'variantId': v['id'], 'fields': []}
def match(value, vs, bodies): return {'kind': 'matchUnion', 'input': value, 'arms': [{'variantId': v['id'], 'body': bodies[n]} for n, v in vs.items()]}
def field(t, name, source): return {'kind': 'fieldAccess', 'source': source, 'fieldId': next(f['id'] for f in t['typeKind']['fields'] if f['displayName'] == name)}
def direct(f, value): return {'kind': 'directCall', 'functionId': f['id'], 'arguments': [{'parameterId': f['parameters'][0]['id'], 'value': value}]}
def function(name, input_type, output_type, body): return call('simulateCreateFunction', displayName=name, parameters=[{'displayName': 'input', 'type': input_type}], resultType=output_type, requiredEffects=[], body=body)


def propose(step):
    ts, fs = discover()
    if step == 'request':
        fields = [('areas', {'kind':'builtinGeneric','generic':'list','arguments':[named(ts['ChangeArea'])]}), ('agentInteraction',prim('bool')), ('environmentDependent',prim('bool')), ('knownRegression',prim('bool'))]
        return call('simulateCreateType', displayName='ChangeRequest', typeKind={'kind':'product','fields':[{'displayName':n,'type':t} for n,t in fields]})
    if step == 'level':
        return call('simulateCreateType', displayName='VerificationLevel', typeKind={'kind':'union','variants':[{'displayName':n,'fields':[]} for n in ['FastOnly','FastExtended','FastLive','FastExtendedLive']]})
    if step == 'area':
        vs = variants(ts['ChangeArea'])
        return function('area_requires_extended', named(ts['ChangeArea']), prim('bool'), match(slot(0), vs, {n:boolean(n in ['Semantics','Backend','Persistence']) for n in vs}))
    if step == 'live':
        t = ts['ChangeRequest']
        return function('requires_live', named(t), prim('bool'), cond(field(t,'agentInteraction',slot(0)),boolean(True),field(t,'environmentDependent',slot(0))))
    if step == 'plan':
        t = ts['ChangeRequest']; vs = variants(ts['VerificationLevel'])
        live = direct(fs['requires_live'],slot(0))
        ext = {'kind':'listAny','source':field(t,'areas',slot(0)),'functionId':fs['area_requires_extended']['id']}
        return function('plan_verification', named(t), named(ts['VerificationLevel']), cond(ext,cond(live,union(vs['FastExtendedLive']),union(vs['FastExtended'])),cond(live,union(vs['FastLive']),union(vs['FastOnly']))))
    if step == 'label':
        vs = variants(ts['VerificationLevel'])
        labels = dict(zip(vs,['fast tests のみ','fast + extended','fast + live','fast + extended + live']))
        return function('verification_label',named(ts['VerificationLevel']),prim('string'),match(slot(0),vs,{n:{'kind':'stringLiteral','value':labels[n]} for n in vs}))
    raise ValueError(step)

if __name__ == '__main__':
    if sys.argv[1] == 'commit': commit_last()
    else: print(json.dumps(propose(sys.argv[1]),ensure_ascii=False,indent=2))
