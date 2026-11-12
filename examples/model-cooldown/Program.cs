// Drives the emitted coolModel against the real OS through NyHost.cs, mirroring run-zig.mjs's scenarios.
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nyrwvy.Generated;
using NyrHost;

int failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok) failures++;
}
var fs = new FsHost();
var clock = new ClockHost();
var locks = new LocksHost();
long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
string Cool(string dir, string model, long seconds, string reason)
{
    var r = NyFacade.coolModel(fs, clock, locks, dir, model, seconds, reason);
    return r.IsOk ? r.OkValue.Text : throw new Exception("coolModel failed: " + r.ErrorValue.Text);
}
JsonObject Entries(string dir)
{
    var text = File.ReadAllText(Path.Combine(dir, "model-cooldown.json"));
    return JsonNode.Parse(text) as JsonObject ?? throw new Exception("not an object: " + text);
}
long Field(JsonObject entry, string name) => entry[name]!.GetValue<long>();
var root = Directory.CreateTempSubdirectory("nyr-cooldown-");
string Dir(string name) => Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName;

// From no file: the entry appears with until = now + seconds and since = now.
var d1 = Dir("s1");
var before = Now();
Check("from empty returns no note", Cool(d1, "m1", 600, "first") == "");
var after = Now();
var e1 = (JsonObject)Entries(d1)["m1"]!;
Check("until is now + seconds", Field(e1, "until") >= before + 600 && Field(e1, "until") <= after + 600);
Check("since is now", Field(e1, "since") >= before && Field(e1, "since") <= after);
Check("reason stored", e1["reason"]!.GetValue<string>() == "first");
Check("integers have no fraction", !File.ReadAllText(Path.Combine(d1, "model-cooldown.json")).Contains(".0"));
// A shorter request does not lower until and keeps since; the reason is replaced.
Cool(d1, "m1", 10, "second");
var e1b = (JsonObject)Entries(d1)["m1"]!;
Check("until not lowered", Field(e1b, "until") == Field(e1, "until"));
Check("since kept", Field(e1b, "since") == Field(e1, "since"));
Check("reason replaced", e1b["reason"]!.GetValue<string>() == "second");
// Control runs in the reason collapse to one space.
Cool(d1, "m1", 600, "a\n\nb\rc\u001fd");
Check("reason controls collapsed", ((JsonObject)Entries(d1)["m1"]!)["reason"]!.GetValue<string>() == "a b c d");
// Removing an entry that is not there writes nothing.
var d2 = Dir("s2");
var seeded = "{\"keep\":{\"until\":9999999999,\"since\":5,\"reason\":\"x\"}}";
File.WriteAllText(Path.Combine(d2, "model-cooldown.json"), seeded);
Cool(d2, "absent", 0, "");
Check("absent removal writes nothing", File.ReadAllText(Path.Combine(d2, "model-cooldown.json")) == seeded);
Cool(d2, "keep", 0, "");
Check("removal drops the entry", !Entries(d2).ContainsKey("keep"));
// Expired entries drop on the next write; an active one survives untouched.
var d3 = Dir("s3");
var past = Now() - 100;
File.WriteAllText(Path.Combine(d3, "model-cooldown.json"),
    $"{{\"old\":{{\"until\":{past},\"since\":{past},\"reason\":\"x\"}},\"live\":{{\"until\":9999999999,\"since\":7,\"reason\":\"y\"}}}}");
Cool(d3, "new", 60, "n");
Check("expired dropped", !Entries(d3).ContainsKey("old"));
Check("live entry untouched", Field((JsonObject)Entries(d3)["live"]!, "since") == 7);
Cool(d3, "old", 60, "n");
Check("expired same-model since restarts", Field((JsonObject)Entries(d3)["old"]!, "since") >= past + 100);
// Corrupt JSON moves to .bad and starts empty; the result says so.
var d4 = Dir("s4");
File.WriteAllText(Path.Combine(d4, "model-cooldown.json"), "not json{");
Check("corrupt reported", Cool(d4, "m1", 60, "r").Contains(".bad"));
Check("corrupt kept in .bad", File.ReadAllText(Path.Combine(d4, "model-cooldown.json.bad")) == "not json{");
Check("corrupt starts empty", Entries(d4).ContainsKey("m1") && Entries(d4).Count == 1);
// Valid JSON that is not an object is treated as empty, without a .bad move.
var d5 = Dir("s5");
File.WriteAllText(Path.Combine(d5, "model-cooldown.json"), "[1,2]");
Cool(d5, "m1", 60, "r");
Check("non-object treated as empty", Entries(d5).ContainsKey("m1"));
Check("non-object makes no .bad", !File.Exists(Path.Combine(d5, "model-cooldown.json.bad")));
// A held lock is retried until it frees, then the update goes through.
var d6 = Dir("s6");
var held = locks.acquire(Path.Combine(d6, "model-cooldown.json.lock"));
var task = Task.Run(() => Cool(d6, "m1", 60, "r"));
Thread.Sleep(400);
locks.release(held.OkValue);
Check("held lock retried then proceeds", task.Wait(10000) && Entries(d6).ContainsKey("m1"));

root.Delete(true);
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} checks failed");
return failures == 0 ? 0 : 1;
