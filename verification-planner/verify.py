"""Execute an unmodified public backend projection; never read the snapshot."""
import json
import subprocess
import sys
from author import call
from session_client import ROOT

stage = sys.argv[1]
d = call('declarations')
(ROOT / (stage + '-declarations.json')).write_text(json.dumps(d, indent=2))
artifact = call('emitCSharp', profile={'kind':'empty'})
source = artifact['source']
symbols = artifact['symbols']
work = ROOT / 'runner'
work.mkdir(exist_ok=True)
(work / 'Generated.cs').write_text(source)
ts = {t['displayName']:t for t in d['types']}
fs = {f['displayName']:f for f in d['functions']}
symbol_maps = {
    kind: {item['id']: item['name'] for item in symbols[kind]}
    for kind in ['types', 'fields', 'variants', 'functions']
}
def symbol(kind, identity):
    result = symbol_maps[kind][identity]
    assert result in source, result
    return result
area = symbol('types', ts['ChangeArea']['id'])
request_type = symbol('types', ts['ChangeRequest']['id'])
plan = symbol('functions', fs['plan_verification']['id'])
label = symbol('functions', fs['verification_label']['id'])
variants = ts['ChangeArea']['typeKind']['variants']
constructors = ', '.join('new '+symbol('variants', v['id'])+'()' for v in variants)
names = ', '.join(json.dumps(v['displayName']) for v in variants)
fields = {'areas':'NyList<'+area+'>.Create(selected)', 'agentInteraction':'agent', 'environmentDependent':'environment', 'knownRegression':'regression'}
args = ', '.join(symbol('fields', f['id'])+': '+fields[f['displayName']] for f in ts['ChangeRequest']['typeKind']['fields'])
harness = '''using System;
using System.Linq;
using NAMESPACE;
internal static class Program {
    static readonly AREA[] Areas = [CONSTRUCTORS];
    static readonly string[] Names = [NAMES];
    static string Run(int mask, bool agent, bool environment, bool regression) {
        var selected = Areas.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        var input = new REQUEST(ARGS);
        return FUNCTION_CONTAINER.LABEL(FUNCTION_CONTAINER.PLAN(input));
    }
    static void Main(string[] args) {
        if (args.Length > 0 && args[0] != "--test") {
            int mask = 0;
            foreach (var name in args[0].Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                int index = Array.FindIndex(Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new ArgumentException("Unknown area: " + name);
                mask |= 1 << index;
            }
            Console.WriteLine(Run(mask, args.Contains("--agent"), args.Contains("--environment"), args.Contains("--regression")));
            return;
        }
        bool maintained = args.Contains("--maintained");
        int count = 0;
        for (int mask=0; mask<128; mask++)
        for (int flags=0; flags<8; flags++) {
            bool agent=(flags&1)!=0, environment=(flags&2)!=0, regression=(flags&4)!=0;
            bool extended = Names.Where((_,i)=>(mask&(1<<i))!=0).Any(n=>n is "Semantics" or "Backend" or "Persistence" || (maintained && n=="EvaluationHarness"));
            bool live=agent||environment;
            string expected = (extended,live) switch { (true,true)=>"fast + extended + live", (true,false)=>"fast + extended", (false,true)=>"fast + live", _=>"fast tests のみ" };
            string actual=Run(mask,agent,environment,regression);
            if(actual!=expected) throw new Exception($"mask={mask}, flags={flags}: {actual} != {expected}");
            count++;
        }
        Console.WriteLine($"PASS {count} exhaustive cases; maintained={maintained}");
    }
}
'''
for key,value in [('CONSTRUCTORS',constructors),('NAMES',names),('REQUEST',request_type),('ARGS',args),('LABEL',label),('PLAN',plan),('AREA',area),('NAMESPACE',symbols['namespace']),('FUNCTION_CONTAINER',symbols['functionContainer'])]:
    harness=harness.replace(key,value)
(work/'Program.cs').write_text(harness)
(work/'Runner.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>\n')
command=['dotnet','run','--project',str(work/'Runner.csproj'),'--','--test']
if stage=='maintained': command.append('--maintained')
result=subprocess.run(command,text=True,capture_output=True,timeout=120)
(ROOT/(stage+'-execution.log')).write_text(result.stdout+result.stderr)
print(result.stdout+result.stderr)
result.check_returncode()
print(call('executionReadiness'))
