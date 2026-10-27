namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// NyList appends and concatenations write into spare buffer capacity when the list owns the
/// buffer's used tail, and copy otherwise. Every List value stays immutable.
module ListAppendTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("list-append-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    // Drives the emitted runtime directly: shared buffers are invisible to programs by design.
    let private driver = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nyrwvy.Generated;

static class Runner
{
    static string Show(NyList<NyStr> list) => "[" + NyText.Join(list, ",") + "]";
    static NyList<NyStr> Build(string prefix, int count)
    {
        var list = NyList<NyStr>.Empty();
        for (var i = 0; i < count; i++) list = list.AppendItem(prefix + i);
        return list;
    }
    static List<long> Items(NyList<long> list) => list.Fold(new List<long>(), (items, item) => { items.Add(item); return items; });
    // The callback and search operations against List<T>, and equality with a list of the same items
    // that starts at another trie position (a slice keeps at least half of the positions, so it shares).
    static bool WideFailure(NyList<long> next, List<long> wanted, NyList<long> copy, int skip)
    {
        var padded = NyList<long>.Create(new long[skip]).Concat(copy);
        var shifted = padded.Slice(skip, padded.Length);
        var probe = wanted.Count > 0 ? wanted[wanted.Count / 3] : 0L;
        var even = wanted.Where(v => (v & 1) == 0).ToList();
        bool IsEven(long v) => (v & 1) == 0;
        // The padding is zeros, so a search for 0 that looks before the start finds one there.
        Func<NyOption<long>, int, bool> differs = (found, expected) => found.HasValue != (expected >= 0) || (found.HasValue && found.Value != expected);
        return !next.Equals(shifted) || !shifted.Equals(next) || next.GetHashCode() != shifted.GetHashCode()
            || differs(shifted.IndexOf(0L), wanted.IndexOf(0L)) || differs(shifted.LastIndexOf(0L), wanted.LastIndexOf(0L))
            || next.IndexOf(probe).HasValue != wanted.Contains(probe)
            || (wanted.Contains(probe) && (next.IndexOf(probe).Value != wanted.IndexOf(probe) || next.LastIndexOf(probe).Value != wanted.LastIndexOf(probe)))
            || next.FindIndex(IsEven).HasValue != (even.Count > 0) || (even.Count > 0 && next.FindIndex(IsEven).Value != wanted.FindIndex(v => IsEven(v)))
            || next.MinBy((l, r) => l < r).HasValue != (wanted.Count > 0) || (wanted.Count > 0 && next.MinBy((l, r) => l < r).Value != wanted.Min())
            || next.Any(IsEven) != (even.Count > 0) || next.All(IsEven) != (even.Count == wanted.Count)
            || !Items(next.Filter(IsEven)).SequenceEqual(even) || !Items(next.Map(v => v + 1)).SequenceEqual(wanted.Select(v => v + 1))
            || !Items(next.Reverse()).SequenceEqual(Enumerable.Reverse(wanted)) || !Items(next.SortBy((l, r) => l < r)).SequenceEqual(wanted.OrderBy(v => v));
    }
    // Random operation sequences against List<T>, at sizes that cross 32, 1,024 and 32,768 items, so
    // pushes, pops, slices and dequeues reach every trie boundary; every earlier list must stay unchanged.
    static int RandomSequences()
    {
        var random = new Random(52);
        var failures = 0;
        foreach (var limit in new[] { 40, 1100, 33000, 70000 })
        {
            var lists = new List<(NyList<long> List, List<long> Expected)> { (NyList<long>.Empty(), new List<long>()) };
            for (var step = 0; step < 3000; step++)
            {
                var (list, expected) = lists[random.Next(lists.Count)];
                NyList<long> next;
                List<long> wanted;
                switch (random.Next(11))
                {
                    case < 4: var item = random.NextInt64(); next = list.AppendItem(item); wanted = new List<long>(expected) { item }; break;
                    case < 6: next = list.DropLast(); wanted = expected.Take(Math.Max(expected.Count - 1, 0)).ToList(); break;
                    case 6:
                        var a = random.Next(-2, expected.Count + 3); var b = random.Next(-2, expected.Count + 3);
                        var from = Math.Clamp(a, 0, expected.Count); var to = Math.Clamp(b, 0, expected.Count);
                        next = list.Slice(a, b); wanted = to <= from ? new List<long>() : expected.GetRange(from, to - from); break;
                    case 7: var k = random.Next(0, expected.Count + 1); next = list.Slice(0, k); wanted = expected.Take(k).ToList(); break;
                    case 9:
                        var dequeues = random.Next(1, 80); next = list; wanted = new List<long>(expected);
                        for (var i = 0; i < dequeues; i++) { next = next.Slice(1, next.Length); if (wanted.Count > 0) wanted.RemoveAt(0); }
                        break;
                    case 8: var other = lists[random.Next(lists.Count)]; next = list.Concat(other.List); wanted = expected.Concat(other.Expected).ToList(); break;
                    default:
                        var count = random.Next(0, limit); next = NyList<long>.Empty(); wanted = new List<long>();
                        for (var i = 0; i < count; i++) { next = next.AppendItem(i); wanted.Add(i); }
                        break;
                }
                if (wanted.Count > limit) continue;
                var copy = NyList<long>.Create(wanted.ToArray());
                // The wider checks run on every small list and on a sample of the large ones.
                var wide = wanted.Count <= 1100 || step % 50 == 0;
                var index = wanted.Count / 3;
                if (next.Length != wanted.Count || !Items(next).SequenceEqual(wanted) || !next.Equals(copy) || next.GetHashCode() != copy.GetHashCode()
                    || (wanted.Count > 0 && (next.ItemAt(index).Value != wanted[index] || next.Last().Value != wanted[^1])) || next.ItemAt(wanted.Count).HasValue
                    || (wide && WideFailure(next, wanted, copy, random.Next(1, Math.Clamp(wanted.Count + 1, 2, 40)))))
                    failures++;
                lists.Add((next, wanted));
                if (lists.Count > 60) lists.RemoveAt(1);
            }
            foreach (var (list, expected) in lists) if (!Items(list).SequenceEqual(expected)) failures++;
        }
        return failures;
    }
    static void Main()
    {
        var b = NyList<NyStr>.Empty().AppendItem("a").AppendItem("b");
        var x = b.AppendItem("c");
        var y = b.AppendItem("d");
        Console.WriteLine(Show(b) + Show(x) + Show(y));
        var z = x.AppendItem("e");
        var w = b.AppendItem("f");
        Console.WriteLine(Show(b) + Show(x) + Show(y) + Show(z) + Show(w));
        Console.WriteLine(b.Length + " " + b.ItemAt(2).HasValue + " " + b.Fold(0, (n, _) => n + 1) + " " + b.Map(s => s + "!").Length + " " + b.Filter(_ => true).Length + " " + b.All(s => s != "c"));
        var abc = NyList<NyStr>.Create("a", "b", "c");
        Console.WriteLine(x.Equals(abc) + " " + abc.Equals(x) + " " + (x.GetHashCode() == abc.GetHashCode()) + " " + x.Equals(y) + " " + b.Equals(x.Slice(0, 2)) + " " + NyEquality.Equal(z.Slice(0, 3), x));
        var s = x.Slice(0, 2).AppendItem("g");
        var m = x.Slice(1, 3).AppendItem("h");
        Console.WriteLine(Show(s) + Show(m) + Show(x));
        var c1 = b.Concat(NyList<NyStr>.Create("p"));
        var c2 = b.Concat(NyList<NyStr>.Create("q", "r"));
        Console.WriteLine(Show(c1) + Show(c2) + Show(b.Concat(NyList<NyStr>.Empty())) + Show(NyList<NyStr>.Empty().Concat(b)));
        var t = NyList<NyStr>.Empty().AppendItem("a").AppendItem("b");
        var tt = t.Concat(t);
        var t2 = t.AppendItem("z");
        var tt2 = tt.Concat(t);
        Console.WriteLine(Show(t) + Show(tt) + Show(t2) + Show(tt2) + Show(tt.AppendItem("y")));
        // DropLast shares the tail; appending to the shorter list must copy it, not rewrite the slot.
        var st = Build("k", 3);
        var popped = st.DropLast();
        var pushed = popped.AppendItem("n");
        var again = popped.AppendItem("m");
        Console.WriteLine(Show(st) + Show(popped) + Show(pushed) + Show(again) + " " + st.Last().Value + " " + pushed.Last().Value + " " + NyList<NyStr>.Empty().Last().HasValue + " " + Show(NyList<NyStr>.Empty().DropLast()) + Show(NyList<NyStr>.Create("q").DropLast()) + " " + popped.Equals(st.Slice(0, 2)));
        // Many threads extend one base concurrently; exactly one may take the spare slot, and
        // every result must still hold the base followed by its own items.
        var failures = 0;
        for (var round = 0; round < 200; round++)
        {
            var shared = Build("s", 1 + round % 70);
            var before = Show(shared);
            var results = new NyList<NyStr>[32];
            Parallel.For(0, results.Length, i =>
            {
                var next = (i % 2 == 0 ? shared.AppendItem("x" + i) : shared.Concat(NyList<NyStr>.Create("x" + i))).AppendItem("y" + i);
                results[i] = next.Concat(next);
            });
            for (var i = 0; i < results.Length; i++)
            {
                var half = before.Substring(1, before.Length - 2) + ",x" + i + ",y" + i;
                if (Show(results[i]) != "[" + half + "," + half + "]") failures++;
            }
            if (Show(shared) != before) failures++;
        }
        Console.WriteLine("concurrent failures " + failures);
        Console.WriteLine("random sequence failures " + RandomSequences());
    }
}
"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``appends and concatenations keep every list value independent`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-append-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let execution = Snapshot.empty (id<SnapshotTag> "runtime") |> TypedSemanticIr.project |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            let assembly = GeneratedBuild.compile root [ "Generated.cs", artifact.Source; "Program.cs", driver ] 30000 |> unwrap "build"
            let info = ProcessStartInfo("dotnet", RedirectStandardOutput = true)
            info.ArgumentList.Add(assembly)
            use child = Process.Start(info)
            let output = child.StandardOutput.ReadToEnd()
            child.WaitForExit()
            Assert.Equal(0, child.ExitCode)
            let expected =
                [ "[a,b][a,b,c][a,b,d]"
                  "[a,b][a,b,c][a,b,d][a,b,c,e][a,b,f]"
                  "2 False 2 2 2 True"
                  "True True True False True True"
                  "[a,b,g][b,c,h][a,b,c]"
                  "[a,b,p][a,b,q,r][a,b][a,b]"
                  "[a,b][a,b,a,b][a,b,z][a,b,a,b,a,b][a,b,a,b,y]"
                  "[k0,k1,k2][k0,k1][k0,k1,n][k0,k1,m] k2 n False [][] True"
                  "concurrent failures 0"
                  "random sequence failures 0" ]
            Assert.Equal(String.concat "\n" expected, output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``text rebuilt one code point at a time by appending is linear under runPure`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-list-append-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let findFunction name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let create name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            let slot = ExpressionRecipe.ParameterSlot
            let text value = ExpressionRecipe.StringLiteral value
            let append list item = ExpressionRecipe.ListAppendItem(list, item)
            create "push" [ "acc", listOf stringType; "item", stringType ] (listOf stringType) (append (slot 0) (slot 1))
            create "rebuild" [ "text", stringType ] stringType
                (ExpressionRecipe.StringJoin(
                    ExpressionRecipe.ListFold(ExpressionRecipe.StringCodePoints(slot 0), ExpressionRecipe.ListEmpty stringType, (findFunction "push").Id, []),
                    text ""))
            // Arguments are limited to 64K UTF-16 units, so the long text is built in the program.
            let rebuild = findFunction "rebuild"
            let sixteen = ExpressionRecipe.StringJoin(ExpressionRecipe.ListConstruct(stringType, List.replicate 16 (slot 0)), text "")
            create "roundTrip" [ "chunk", stringType ] (TypeRef.Primitive PrimitiveType.Bool)
                (ExpressionRecipe.StringEqual(ExpressionRecipe.DirectCall(rebuild.Id, [ rebuild.Parameters.Head.Id, sixteen ]), sixteen))
            // Branches of one base: each must see only its own items.
            create "branches" [ "base", listOf stringType ] (listOf (listOf stringType))
                (ExpressionRecipe.ListConstruct(
                    listOf stringType,
                    [ append (slot 0) (text "c")
                      append (slot 0) (text "d")
                      slot 0
                      ExpressionRecipe.ListConcat(slot 0, ExpressionRecipe.ListConstruct(stringType, [ text "e" ]))
                      append (ExpressionRecipe.ListSlice(append (slot 0) (text "f"), ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 1L)) (text "g") ]))
            let branches = findFunction "branches"
            create "fromAppends" [] (listOf (listOf stringType))
                (ExpressionRecipe.DirectCall(
                    branches.Id,
                    [ branches.Parameters.Head.Id, append (append (ExpressionRecipe.ListEmpty stringType) (text "a")) (text "b") ]))
            let result = PureRun.run session (findFunction "fromAppends").Id [] None |> unwrap "branches"
            let expected =
                PureRunValue.ListValue(
                    listOf stringType,
                    [ PureRunValue.StringList [ "a"; "b"; "c" ]
                      PureRunValue.StringList [ "a"; "b"; "d" ]
                      PureRunValue.StringList [ "a"; "b" ]
                      PureRunValue.StringList [ "a"; "b"; "e" ]
                      PureRunValue.StringList [ "a"; "g" ] ])
            Assert.Equal(expected, result.Actual)
            // 128K characters exceeded the 5 s limit when every append copied the whole list.
            // Sixteen copies of a 16,384-code-point chunk: 262,144 appends, 327,680 UTF-16 units.
            let chunk = String.replicate 4096 "ab😀c"
            let rebuilt = PureRun.run session (findFunction "roundTrip").Id [ PureRunValue.String chunk ] None |> unwrap "rebuild"
            Assert.Equal(PureRunValue.Bool true, rebuilt.Actual)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
