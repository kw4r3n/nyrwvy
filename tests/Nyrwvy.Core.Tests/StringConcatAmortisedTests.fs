namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// NyStr concatenation writes into spare store capacity when the left String ends at the store's
/// used count, and copies otherwise. Every String value stays immutable.
module StringConcatAmortisedTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-concat-amortised-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    // Drives the emitted runtime directly: shared stores are invisible to programs by design.
    let private driver = """
using System;
using System.Threading.Tasks;
using Nyrwvy.Generated;

static class Runner
{
    static NyStr Build(string prefix, int count)
    {
        NyStr text = "";
        for (var i = 0; i < count; i++) text = NyStr.Concat(text, prefix + i);
        return text;
    }
    static void Main()
    {
        // Long enough to be store-backed, so the branches below share one store.
        var b = NyStr.Concat(new string('a', 300), "b");
        var x = NyStr.Concat(b, "c");
        var y = NyStr.Concat(b, "d");
        var z = NyStr.Concat(x, "e");
        var w = NyStr.Concat(b, "f");
        Console.WriteLine(string.Join(" ", new[] { b, x, y, z, w }.Select(s => ((string)s).Substring(299))));
        Console.WriteLine(b.Length + " " + NyText.CodePointLength(z) + " " + ((string)x).Contains("bc", StringComparison.Ordinal));
        NyStr plain = new string('a', 300) + "bc";
        Console.WriteLine(x.Equals(plain) + " " + plain.Equals(x) + " " + (x.GetHashCode() == plain.GetHashCode()) + " " + x.Equals(y) + " " + (x == plain) + " " + (x != y) + " " + NyEquality.Equal(z, NyStr.Concat(plain, "e")));
        var self = NyStr.Concat(x, x);
        var again = NyStr.Concat(x, "g");
        var selfAgain = NyStr.Concat(self, x);
        Console.WriteLine(self.Length + " " + ((string)again).Substring(299) + " " + (selfAgain == NyStr.Concat(NyStr.Concat(plain, plain), plain)) + " " + (x == plain));
        NyStr empty = "";
        Console.WriteLine(ReferenceEquals(NyStr.Concat(x, empty), x) + " " + ReferenceEquals(NyStr.Concat(empty, x), x) + " " + NyStr.Concat("ab", "cd") + " " + NyText.Join(NyList<NyStr>.Create(x, "!", y), "-").Length);
        // Many threads extend one base concurrently; exactly one may take the spare chars, and
        // every result must still hold the base followed by its own text.
        var failures = 0;
        for (var round = 0; round < 200; round++)
        {
            var shared = Build("s", 40 + round % 40);
            var before = shared.Text;
            var results = new NyStr[32];
            Parallel.For(0, results.Length, i =>
            {
                var next = NyStr.Concat(NyStr.Concat(shared, "x" + i), "y" + i);
                results[i] = NyStr.Concat(next, next);
            });
            for (var i = 0; i < results.Length; i++)
            {
                var half = before + "x" + i + "y" + i;
                if (results[i].Text != half + half) failures++;
            }
            if (shared.Text != before) failures++;
        }
        Console.WriteLine("concurrent failures " + failures);
    }
}
"""

    [<Fact>]
    let ``concatenations keep every String value independent`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-concat-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let execution = Snapshot.empty (id<SnapshotTag> "runtime") |> TypedSemanticIr.project |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
            let assembly = GeneratedBuild.compile root [ "Generated.cs", artifact.Source; "Program.cs", "using System.Linq;\n" + driver ] 30000 |> unwrap "build"
            let info = ProcessStartInfo("dotnet", RedirectStandardOutput = true)
            info.ArgumentList.Add(assembly)
            use child = Process.Start(info)
            let output = child.StandardOutput.ReadToEnd()
            child.WaitForExit()
            Assert.Equal(0, child.ExitCode)
            let expected =
                [ "ab abc abd abce abf"
                  "301 303 True"
                  "True True True False True True True"
                  "604 abcg True True"
                  "True True abcd 607"
                  "concurrent failures 0" ]
            Assert.Equal(String.concat "\n" expected, output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``text rebuilt one code point at a time by StringConcat is linear under runPure`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-concat-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let findFunction name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            let create name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
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
            create "push" [ "acc", stringType; "item", stringType ] stringType (ExpressionRecipe.StringConcat(slot 0, slot 1))
            create "rebuild" [ "text", stringType ] stringType
                (ExpressionRecipe.ListFold(ExpressionRecipe.StringCodePoints(slot 0), text "", (findFunction "push").Id, []))
            // Arguments are limited to 64K UTF-16 units, so the long text is built in the program.
            let rebuild = findFunction "rebuild"
            let sixteen = ExpressionRecipe.StringJoin(ExpressionRecipe.ListConstruct(stringType, List.replicate 16 (slot 0)), text "")
            create "roundTrip" [ "chunk", stringType ] (TypeRef.Primitive PrimitiveType.Bool)
                (ExpressionRecipe.StringEqual(ExpressionRecipe.DirectCall(rebuild.Id, [ rebuild.Parameters.Head.Id, sixteen ]), sixteen))
            // Branches of one base: each must see only its own text.
            create "branches" [ "base", stringType ] (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]))
                (ExpressionRecipe.ListConstruct(
                    stringType,
                    [ ExpressionRecipe.StringConcat(slot 0, text "c")
                      ExpressionRecipe.StringConcat(slot 0, text "d")
                      slot 0
                      ExpressionRecipe.StringConcat(ExpressionRecipe.StringConcat(slot 0, text "e"), slot 0) ]))
            let branches = findFunction "branches"
            let longBase = String.replicate 300 "a"
            create "fromConcats" [] (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]))
                (ExpressionRecipe.DirectCall(branches.Id, [ branches.Parameters.Head.Id, ExpressionRecipe.StringConcat(text longBase, text "b") ]))
            let result = PureRun.run session (findFunction "fromConcats").Id [] None |> unwrap "branches"
            let b = longBase + "b"
            Assert.Equal(PureRunValue.StringList [ b + "c"; b + "d"; b; b + "e" + b ], result.Actual)
            // 195,000 code points exceeded the 5 s limit when every concatenation copied the text.
            // Sixteen copies of a 16,384-code-point chunk: 262,144 concatenations, 327,680 UTF-16 units.
            let chunk = String.replicate 4096 "ab😀c"
            let rebuilt = PureRun.run session (findFunction "roundTrip").Id [ PureRunValue.String chunk ] None |> unwrap "rebuild"
            Assert.Equal(PureRunValue.Bool true, rebuilt.Actual)
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
