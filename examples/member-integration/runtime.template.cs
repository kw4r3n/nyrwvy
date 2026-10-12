using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Nyrwvy.Generated;

// Deterministic fake effect responses and recording only; graph owns policy.
sealed class Effects : @@HOST@@
{
 readonly JsonElement scenario;
 readonly Dictionary<string,int> counts=new();
 public readonly List<object> Calls=new();
 public Effects(JsonElement scenario){this.scenario=scenario;}
 public NyResult<NyList<NyStr>,NyStr> @@STEP@@(NyStr operation,NyStr patch)
 {
  string op=operation.ToString();
  Calls.Add(new {operation="step",arguments=new[]{new {parameter="operation",value=new {value=op}},new {parameter="patch",value=new {value=patch.ToString()}}}});
  int count=counts.GetValueOrDefault(op);counts[op]=count+1;
  if((scenario.TryGetProperty("fail",out var fail)&&fail.GetString()==op&&count==0)||
     (scenario.TryGetProperty("rollbackFail",out var reverse)&&reverse.GetString()==op))
   return NyResult<NyList<NyStr>,NyStr>.Err(op);
  string[] values=op switch {
   "paths-member"=>scenario.GetProperty("member").EnumerateArray().Select(x=>x.GetString()!).ToArray(),
   "paths-leader"=>scenario.GetProperty("leader").EnumerateArray().Select(x=>x.GetString()!).ToArray(),
   "patch"=>new[]{scenario.GetProperty("patch").GetString()!},
   "head"=>new[]{scenario.GetProperty("patch").GetString()==""?"source-head":"integrated-head"},
   _=>Array.Empty<string>()};
  return NyResult<NyList<NyStr>,NyStr>.Ok(NyList<NyStr>.Create(values.Select(x=>(NyStr)x).ToArray()));
 }
}
static class Program
{
 static object S(string value)=>new {kind="string",value};
 public static void Main(string[] args)
 {
  foreach(var c in JsonDocument.Parse(System.IO.File.ReadAllText(args[0])).RootElement.EnumerateArray()) {
   var e=c.GetProperty("candidate");var host=new Effects(c);
   var candidate=new @@CANDIDATE@@(@@CANDIDATE_ARGS@@);
   var result=NyFunctions.@@RUN@@(@@RUN_HOST@@:host,@@RUN_CANDIDATE@@:candidate,
      @@RUN_REQUESTS@@:NyList<NyUnit>.Create(Enumerable.Repeat(NyUnit.Value,c.GetProperty("requests").GetInt32()).ToArray()));
   var outcome=result.@@OUT_RESULT@@;
   var entry=result.@@OUT_CANDIDATE@@;
   // Serialize the projection's actual outcome/ledger; nothing recomputes policy.
   var value=new {kind="product",fields=new object[]{
    new {field="candidate",value=new {kind="product",fields=new object[]{@@ENTRY_FIELDS@@}}},
    new {field="result",value=new {kind="result",isOk=outcome.IsOk,value=outcome.IsOk?outcome.OkValue.ToString():outcome.ErrorValue.ToString()}}}};
   Console.WriteLine(JsonSerializer.Serialize(new {id=c.GetProperty("id").GetString(),response=new {ok=true,result=new {actual=value,calls=host.Calls}}}));
  }
 }
}
