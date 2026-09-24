namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Xunit

module ListFoldTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("fold-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ])
    let private stateId = id<TypeTag> "state"
    let private keyId = id<FieldTag> "key"
    let private foundId = id<FieldTag> "found"
    let private stateType = TypeRef.Named stateId
    let private field fieldId = ExpressionRecipe.FieldAccess(ExpressionRecipe.ParameterSlot 0, fieldId)
    let private stateWithFound =
        ExpressionRecipe.ProductConstruct(stateId, [ keyId, field keyId; foundId, ExpressionRecipe.BoolLiteral true ])

    let private create ws name parameters resultType body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              DisplayName = name
              Parameters = parameters
              ResultType = resultType
              RequiredEffects = []
              Body = body }
        let proposal =
            match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
            | Ok value -> value
            | Error error -> failwithf "Cannot author %s: %A" name error
        let created =
            proposal
            |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
            |> fun review -> review.CreatedFunction
        match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next, created
        | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected errors ->
            failwithf "Cannot publish %s: %A" name errors

    let private parameter name typeRef: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
        { DisplayName = name; Type = typeRef }

    let private fixture () =
        let state: SemanticType =
            { Id = stateId
              DisplayName = "LookupState"
              Kind = SemanticTypeKind.Product
                [ { Id = keyId; DisplayName = "key"; Type = stringType }
                  { Id = foundId; DisplayName = "found"; Type = boolType } ] }
        let initial = { Snapshot.empty (id<SnapshotTag> "initial") with Types = [ state ] }
        let ws =
            match AgentWorkspace.create initial with
            | Ok value -> value
            | Error errors -> failwithf "Invalid fixture: %A" errors
        // Accumulator carries the runtime key. No host search function or closure is needed.
        let item = ExpressionRecipe.ParameterSlot 1
        let folderBody =
            ExpressionRecipe.Conditional(
                field foundId,
                ExpressionRecipe.ParameterSlot 0,
                ExpressionRecipe.Conditional(
                    ExpressionRecipe.StringContains(item, field keyId),
                    ExpressionRecipe.Conditional(
                        ExpressionRecipe.StringContains(field keyId, item),
                        stateWithFound,
                        ExpressionRecipe.ParameterSlot 0),
                    ExpressionRecipe.ParameterSlot 0))
        let ws, folder =
            create ws "lookup_step"
                [ parameter "state" stateType; parameter "item" stringType ]
                stateType folderBody
        let seed =
            ExpressionRecipe.ProductConstruct(stateId,
                [ keyId, ExpressionRecipe.ParameterSlot 1
                  foundId, ExpressionRecipe.BoolLiteral false ])
        let searchBody =
            ExpressionRecipe.FieldAccess(
                ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, seed, folder.Id),
                foundId)
        let ws, search =
            create ws "contains_key"
                [ parameter "items" listType; parameter "key" stringType ]
                boolType searchBody
        let intType = TypeRef.Primitive PrimitiveType.Int64
        let nextDigit =
            ExpressionRecipe.Int64WrappingAdd(
                ExpressionRecipe.Int64WrappingMultiply(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 10L),
                ExpressionRecipe.ParameterSlot 1)
        let ws, digitFolder =
            create ws "append_digit"
                [ parameter "accumulator" intType; parameter "digit" intType ]
                intType nextDigit
        let ws, digits =
            create ws "digits"
                [ parameter "items" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ])) ]
                intType
                (ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 0L, digitFolder.Id))
        AgentWorkspace.snapshotForStructuralEditing ws, folder, search, digits

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private indexFixture () =
        let intType = TypeRef.Primitive PrimitiveType.Int64
        let optionType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])
        let stateId = id<TypeTag> "index-state"
        let remainingId = id<FieldTag> "remaining"
        let itemId = id<FieldTag> "item"
        let stateType = TypeRef.Named stateId
        let state: SemanticType =
            { Id = stateId
              DisplayName = "IndexState"
              Kind = SemanticTypeKind.Product
                [ { Id = remainingId; DisplayName = "remaining"; Type = intType }
                  { Id = itemId; DisplayName = "item"; Type = optionType } ] }
        let ws =
            { Snapshot.empty (id<SnapshotTag> "index-initial") with Types = [ state ] }
            |> AgentWorkspace.create
            |> function
                | Ok value -> value
                | Error errors -> failwithf "Invalid index fixture: %A" errors
        let acc = ExpressionRecipe.ParameterSlot 0
        let remaining = ExpressionRecipe.FieldAccess(acc, remainingId)
        let previous = ExpressionRecipe.FieldAccess(acc, itemId)
        let step =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.Int64Equal(remaining, ExpressionRecipe.Int64Literal 0L),
                ExpressionRecipe.ProductConstruct(stateId,
                    [ remainingId, ExpressionRecipe.Int64Literal -1L
                      itemId, ExpressionRecipe.OptionSome(ExpressionRecipe.ParameterSlot 1) ]),
                ExpressionRecipe.ProductConstruct(stateId,
                    [ remainingId,
                        ExpressionRecipe.Int64WrappingSubtract(remaining, ExpressionRecipe.Int64Literal 1L)
                      itemId, previous ]))
        let ws, folder =
            create ws "index_step"
                [ parameter "state" stateType; parameter "item" stringType ] stateType step
        let index = ExpressionRecipe.ParameterSlot 1
        let seed =
            ExpressionRecipe.ProductConstruct(stateId,
                [ remainingId, index; itemId, ExpressionRecipe.OptionNone stringType ])
        let lookup =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.Int64LessThan(index, ExpressionRecipe.Int64Literal 0L),
                ExpressionRecipe.OptionNone stringType,
                ExpressionRecipe.FieldAccess(
                    ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, seed, folder.Id), itemId))
        let ws, at =
            create ws "item_at"
                [ parameter "items" listType; parameter "index" intType ] optionType lookup
        AgentWorkspace.snapshotForStructuralEditing ws, folder, at

    [<Fact>]
    let ``fold with runtime context authors publishes persists and lowers with stable callback identity`` () =
        let snapshot, folder, search, _ = fixture ()
        Assert.Empty(SnapshotValidator.validate snapshot)
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize JSON"
        let restored = SnapshotCodec.deserialize json |> unwrap "restore JSON"
        let nyason = NyasonCodec.serialize restored |> unwrap "serialize Nyason"
        let reopened = NyasonCodec.deserialize nyason |> unwrap "restore Nyason"
        Assert.Contains("list-fold", nyason)
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute reopened)
        let focus = SemanticQuery.focusFunction reopened search.Id |> unwrap "focus"
        Assert.Contains(focus.Callees, fun site -> site.CalleeFunctionId = folder.Id)
        let foldNode =
            search.Body.Nodes
            |> List.find (fun node -> match node.Kind with | SemanticNodeKind.ListFold _ -> true | _ -> false)
        Assert.Equal(Some stateType,
            focus.NodeTypes |> List.find (fun fact -> fact.NodeId = foldNode.Id) |> fun fact -> fact.Type)
        let viewer = ViewerProjection.focus focus
        let foldView = viewer.Graph.Nodes |> List.find (fun node -> node.Id = foldNode.Id)
        Assert.Equal(ViewerNodeRole.ListFold, foldView.Role)
        Assert.Contains(foldView.Relations, fun relation ->
            relation.Kind = ViewerRelationKind.CallsFunction && relation.Target = ViewerTarget.Function folder.Id)
        let typed = TypedSemanticIr.project reopened |> unwrap "typed projection"
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"listFold\"", TypedSemanticIrCodec.serialize typed)
        Assert.Contains("\"listFold\"", ExecutionIrCodec.serialize execution)
        let emitted = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "C# emission"
        Assert.Contains("public S Fold<S>(S seed, Func<S, T, S> folder)", emitted.Source)
        Assert.Contains(".Fold(", emitted.Source)

    [<Fact>]
    let ``generated fold searches an arbitrary key in input order including empty and absent inputs`` () =
        let snapshot, _, search, digits = fixture ()
        let execution = TypedSemanticIr.project snapshot |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-fold-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            let call = "NyFunctions." + CSharpBackendNames.functionName search.Id
            let digitCall = "NyFunctions." + CSharpBackendNames.functionName digits.Id
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"),
                "using System; using Nyrwvy.Generated;\n" +
                $"Console.WriteLine({call}(NyList<string>.Create(\"a\", \"b\", \"c\"), \"b\"));\n" +
                $"Console.WriteLine({call}(NyList<string>.Create(\"a\", \"b\"), \"z\"));\n" +
                $"Console.WriteLine({call}(NyList<string>.Empty(), \"b\"));\n" +
                $"Console.WriteLine({call}(NyList<string>.Create(\"ab\"), \"a\"));\n" +
                $"Console.WriteLine({digitCall}(NyList<long>.Create(1L, 2L, 3L)));\n" +
                $"Console.WriteLine({digitCall}(NyList<long>.Empty()));\n")
            let start args =
                let info = ProcessStartInfo("dotnet")
                info.WorkingDirectory <- tempRoot
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                for arg in args do info.ArgumentList.Add(arg)
                use child = Process.Start(info)
                let stdout = child.StandardOutput.ReadToEndAsync()
                let stderr = child.StandardError.ReadToEndAsync()
                child.WaitForExit()
                if child.ExitCode <> 0 then failwithf "dotnet failed: %s\n%s\n%s" (String.concat " " args) stdout.Result stderr.Result
                stdout.Result
            start [ "build"; "Generated.csproj"; "--nologo"; "-v:q" ] |> ignore
            let output = start [ Path.Combine(tempRoot, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("True\nFalse\nFalse\nFalse\n123\n0", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``generated fold retrieves a typed element by runtime index with empty and out of range cases`` () =
        let snapshot, folder, at = indexFixture ()
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize index"
        let restored = SnapshotCodec.deserialize json |> unwrap "reopen index"
        let nyason = NyasonCodec.serialize restored |> unwrap "nyason index"
        let reopened = NyasonCodec.deserialize nyason |> unwrap "reopen nyason index"
        let focus = SemanticQuery.focusFunction reopened at.Id |> unwrap "index focus"
        Assert.Contains(focus.Callees, fun site -> site.CalleeFunctionId = folder.Id)
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute reopened)
        let execution = TypedSemanticIr.project reopened |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-index-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            let functionName = "NyFunctions." + CSharpBackendNames.functionName at.Id
            let inputs =
                [ "NyList<string>.Empty(), 0L"
                  "NyList<string>.Create(\"first\", \"second\", \"third\"), -1L"
                  "NyList<string>.Create(\"first\", \"second\", \"third\"), 0L"
                  "NyList<string>.Create(\"first\", \"second\", \"third\"), 1L"
                  "NyList<string>.Create(\"first\", \"second\", \"third\"), 2L"
                  "NyList<string>.Create(\"first\", \"second\", \"third\"), 3L" ]
            let program =
                [ "using System; using Nyrwvy.Generated;" ]
                @ (inputs |> List.map (fun args ->
                    $"{{ var result = {functionName}({args}); Console.WriteLine(result.HasValue ? result.Value : \"<none>\"); }}"))
                |> String.concat "\n"
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), program)
            let start args =
                let info = ProcessStartInfo("dotnet")
                info.WorkingDirectory <- tempRoot
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                for arg in args do info.ArgumentList.Add(arg)
                use child = Process.Start(info)
                let stdout = child.StandardOutput.ReadToEndAsync()
                let stderr = child.StandardError.ReadToEndAsync()
                child.WaitForExit()
                if child.ExitCode <> 0 then failwithf "dotnet failed: %s\n%s\n%s" (String.concat " " args) stdout.Result stderr.Result
                stdout.Result
            start [ "build"; "Generated.csproj"; "--nologo"; "-v:q" ] |> ignore
            let output = start [ Path.Combine(tempRoot, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("<none>\n<none>\nfirst\nsecond\nthird\n<none>", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``fold rejects a folder with the wrong accumulator type before publication`` () =
        let snapshot, folder, search, _ = fixture ()
        let ws = AgentWorkspace.create snapshot |> unwrap "workspace"
        let target = snapshot.Functions |> List.find (fun item -> item.Id = search.Id)
        let request: AgentReplaceFunctionBodyWithExpressionRecipe =
            { TargetFunctionId = search.Id
              ExpectedBaseSnapshotId = snapshot.Id
              ExpectedCurrentResultNodeId = target.Body.ResultNodeId
              Replacement = ExpressionRecipe.ListFold(
                ExpressionRecipe.Parameter target.Parameters[0].Id,
                ExpressionRecipe.BoolLiteral false,
                folder.Id) }
        match AgentExpressionStructuralWorkflow.simulate ws request with
        | Error(AgentStructuralWorkflowError.SimulationRejected errors) ->
            Assert.Contains(errors, fun error -> error.Code = "PureExpressionListCallbackInputTypeMismatch")
        | other -> failwithf "Expected typed folder rejection, got %A" other

        let corrupted =
            { snapshot with
                Functions =
                    snapshot.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id <> search.Id then functionEntity
                        else
                            { functionEntity with
                                Body =
                                    { functionEntity.Body with
                                        Nodes =
                                            functionEntity.Body.Nodes
                                            |> List.map (fun node ->
                                                match node.Kind with
                                                | SemanticNodeKind.ListFold(source, seed, _) ->
                                                    { node with Kind = SemanticNodeKind.ListFold(source, seed, search.Id) }
                                                | _ -> node) } }) }
        let issues = SnapshotValidator.validate corrupted
        Assert.Contains(issues, fun issue -> issue.Code = "ListCallbackParameterTypeMismatch")
        Assert.Contains(issues, fun issue -> issue.Code = "ListCallbackResultTypeMismatch")
