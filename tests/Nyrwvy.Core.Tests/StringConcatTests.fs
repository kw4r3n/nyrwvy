namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module StringConcatTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("concat-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private parameter name: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
        { DisplayName = name; Type = stringType }

    [<Fact>]
    let ``StringConcat rejects mistyped operands in recipes and persisted graphs`` () =
        let snapshot = Snapshot.empty (id<SnapshotTag> "base")
        let ws = AgentWorkspace.create snapshot |> function
            | Ok value -> value
            | Error errors -> failwithf "Invalid fixture: %A" errors
        let request body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { Exported = true; ExpectedBaseSnapshotId = snapshot.Id
              TypeParameters = None
              DisplayName = "join"
              Parameters = [ parameter "left"; parameter "right" ]
              ResultType = stringType
              RequiredEffects = []
              Body = body }
        let invalid =
            request (ExpressionRecipe.StringConcat(ExpressionRecipe.BoolLiteral true, ExpressionRecipe.ParameterSlot 1))
        match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws invalid with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("PureExpressionOperandTypeMismatch", diagnostic.Code)
        | other -> failwithf "Mistyped concatenation was accepted: %A" other
        let valid = request (ExpressionRecipe.StringConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
        let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws valid |> function
            | Ok result -> result
            | Error error -> failwithf "Creation failed: %A" error
        let committed = AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal |> function
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next
            | other -> failwithf "Commit failed: %A" other
        let created = AgentWorkspace.snapshotForStructuralEditing committed
        let broken =
            { created with
                Functions = created.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference parameterId when parameterId = fn.Parameters.Head.Id ->
                                        { node with Kind = SemanticNodeKind.BoolLiteral true }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated C# runs JSONL-authored StringConcat preserving order empties and Unicode`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-concat-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let initial = Snapshot.empty (id<SnapshotTag> "session")
            File.WriteAllText(path, SnapshotCodec.serialize initial |> function
                | Ok json -> json
                | Error error -> failwithf "Fixture serialization failed: %A" error)
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let request =
                """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"join","parameters":[{"displayName":"left","type":{"kind":"primitive","primitive":"string"}},{"displayName":"right","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"kind":"stringConcat","left":{"kind":"parameter","name":"left"},"right":{"kind":"parameter","name":"right"}}}"""
            use proposed = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean(), proposed.RootElement.ToString())
            let proposalId = proposed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use commit =
                AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
                |> JsonDocument.Parse
            Assert.True(commit.RootElement.GetProperty("ok").GetBoolean(), commit.RootElement.ToString())
            session.Close()
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> function
                | Ok value -> value
                | Error error -> failwithf "Reopen failed: %A" error
            Assert.Empty(SnapshotValidator.validate snapshot)
            let fn = List.exactlyOne snapshot.Functions
            Assert.Contains(fn.Body.Nodes, fun node -> match node.Kind with | SemanticNodeKind.StringConcat _ -> true | _ -> false)
            let nyason = NyasonCodec.serialize snapshot |> function
                | Ok text -> text
                | Error error -> failwithf "Nyason encoding failed: %A" error
            Assert.Contains("string-concat", nyason)
            let restored = NyasonCodec.deserialize nyason |> function
                | Ok value -> value
                | Error error -> failwithf "Nyason decoding failed: %A" error
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> function
                | Ok value -> value
                | Error error -> failwithf "Typed projection failed: %A" error
            let execution = ExecutionIr.lower typed |> function
                | Ok value -> value
                | Error error -> failwithf "Lowering failed: %A" error
            Assert.Contains("\"stringConcat\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"stringConcat\"", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> function
                | Ok value -> value
                | Error error -> failwithf "Emission failed: %A" error
            File.WriteAllText(Path.Combine(root, "Generated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            File.WriteAllText(Path.Combine(root, "Generated.cs"), artifact.Source)
            let name = CSharpBackendNames.functionName fn.Id
            let program =
                [ "using System; using System.Text; using Nyrwvy.Generated;"
                  $"Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(NyFunctions.{name}(\"a\", \"bc\"))));"
                  $"Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(NyFunctions.{name}(\"\", \"\"))));"
                  $"Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(NyFunctions.{name}(\"\", \"x\"))));"
                  $"Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(NyFunctions.{name}(\"🍀\", \"e\\u0301\"))));"
                  $"Console.WriteLine(Convert.ToHexString(Encoding.UTF8.GetBytes(NyFunctions.{name}(\"a\\r\\n\", \"b\"))));" ]
                |> String.concat "\n"
            File.WriteAllText(Path.Combine(root, "Program.cs"), program)
            let run args =
                let start = ProcessStartInfo("dotnet")
                start.WorkingDirectory <- root
                start.RedirectStandardOutput <- true
                start.RedirectStandardError <- true
                for arg in args do start.ArgumentList.Add(arg)
                use child = Process.Start(start)
                let output = child.StandardOutput.ReadToEndAsync()
                let errors = child.StandardError.ReadToEndAsync()
                child.WaitForExit()
                if child.ExitCode <> 0 then failwithf "dotnet %A failed: %s\n%s" args output.Result errors.Result
                output.Result
            run [ "build"; "Generated.csproj"; "-v:q" ] |> ignore
            let output = run [ Path.Combine(root, "bin", "Debug", "net10.0", "Generated.dll") ]
            let hex (text: string) = Convert.ToHexString(Encoding.UTF8.GetBytes(text))
            let expected = [ "abc"; ""; "x"; "🍀e\u0301"; "a\r\nb" ] |> List.map hex |> String.concat "\n"
            Assert.Equal(expected, output.Replace("\r\n", "\n").TrimEnd('\r', '\n'))
        finally
            if Directory.Exists root then Directory.Delete(root, true)
