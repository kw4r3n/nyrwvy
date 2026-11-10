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
    let private stateType = TypeRef.Named(stateId, [])
    let private field fieldId = ExpressionRecipe.FieldAccess(ExpressionRecipe.ParameterSlot 0, fieldId)
    let private stateWithFound =
        ExpressionRecipe.ProductConstruct(stateId, [], [ keyId, field keyId; foundId, ExpressionRecipe.BoolLiteral true ])

    let private create ws name parameters resultType body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
              TypeParameters = None
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
            { Exported = true; TypeParameters = []; Id = stateId
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
                    ExpressionRecipe.StringEqual(item, field keyId),
                    stateWithFound,
                    ExpressionRecipe.ParameterSlot 0))
        let ws, folder =
            create ws "lookup_step"
                [ parameter "state" stateType; parameter "item" stringType ]
                stateType folderBody
        let seed =
            ExpressionRecipe.ProductConstruct(stateId, [],
                [ keyId, ExpressionRecipe.ParameterSlot 1
                  foundId, ExpressionRecipe.BoolLiteral false ])
        let searchBody =
            ExpressionRecipe.FieldAccess(
                ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, seed, folder.Id, []),
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
                (ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 0L, digitFolder.Id, []))
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
        let stateType = TypeRef.Named(stateId, [])
        let state: SemanticType =
            { Exported = true; TypeParameters = []; Id = stateId
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
                ExpressionRecipe.ProductConstruct(stateId, [],
                    [ remainingId, ExpressionRecipe.Int64Literal -1L
                      itemId, ExpressionRecipe.OptionSome(ExpressionRecipe.ParameterSlot 1) ]),
                ExpressionRecipe.ProductConstruct(stateId, [],
                    [ remainingId,
                        ExpressionRecipe.Int64WrappingSubtract(remaining, ExpressionRecipe.Int64Literal 1L)
                      itemId, previous ]))
        let ws, folder =
            create ws "index_step"
                [ parameter "state" stateType; parameter "item" stringType ] stateType step
        let index = ExpressionRecipe.ParameterSlot 1
        let seed =
            ExpressionRecipe.ProductConstruct(stateId, [],
                [ remainingId, index; itemId, ExpressionRecipe.OptionNone stringType ])
        let lookup =
            ExpressionRecipe.Conditional(
                ExpressionRecipe.Int64LessThan(index, ExpressionRecipe.Int64Literal 0L),
                ExpressionRecipe.OptionNone stringType,
                ExpressionRecipe.FieldAccess(
                    ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, seed, folder.Id, []), itemId))
        let ws, at =
            create ws "item_at"
                [ parameter "items" listType; parameter "index" intType ] optionType lookup
        AgentWorkspace.snapshotForStructuralEditing ws, folder, at

    let private findFixture () =
        let entryId = id<TypeTag> "find-entry"
        let stateId = id<TypeTag> "find-state"
        let entryKeyId = id<FieldTag> "find-entry-key"
        let entryValueId = id<FieldTag> "find-entry-value"
        let stateKeyId = id<FieldTag> "find-state-key"
        let stateFoundId = id<FieldTag> "find-state-found"
        let stateValueId = id<FieldTag> "find-state-value"
        let entryType = TypeRef.Named(entryId, [])
        let stateType = TypeRef.Named(stateId, [])
        let optionType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ])
        let entry: SemanticType =
            { Exported = true; TypeParameters = []; Id = entryId
              DisplayName = "Entry"
              Kind = SemanticTypeKind.Product
                [ { Id = entryKeyId; DisplayName = "key"; Type = stringType }
                  { Id = entryValueId; DisplayName = "value"; Type = stringType } ] }
        let state: SemanticType =
            { Exported = true; TypeParameters = []; Id = stateId
              DisplayName = "FindState"
              Kind = SemanticTypeKind.Product
                [ { Id = stateKeyId; DisplayName = "key"; Type = stringType }
                  { Id = stateFoundId; DisplayName = "found"; Type = boolType }
                  { Id = stateValueId; DisplayName = "value"; Type = optionType } ] }
        let ws =
            { Snapshot.empty (id<SnapshotTag> "find-initial") with Types = [ entry; state ] }
            |> AgentWorkspace.create
            |> function
                | Ok value -> value
                | Error errors -> failwithf "Invalid find fixture: %A" errors
        let current = ExpressionRecipe.ParameterSlot 0
        let item = ExpressionRecipe.ParameterSlot 1
        let field source fieldId = ExpressionRecipe.FieldAccess(source, fieldId)
        let folderBody =
            ExpressionRecipe.Conditional(
                field current stateFoundId,
                current,
                ExpressionRecipe.Conditional(
                    ExpressionRecipe.StringEqual(field item entryKeyId, field current stateKeyId),
                    ExpressionRecipe.ProductConstruct(stateId, [],
                        [ stateKeyId, field current stateKeyId
                          stateFoundId, ExpressionRecipe.BoolLiteral true
                          stateValueId, ExpressionRecipe.OptionSome(field item entryValueId) ]),
                    current))
        let ws, folder =
            create ws "find_step"
                [ parameter "state" stateType; parameter "item" entryType ] stateType folderBody
        let seed =
            ExpressionRecipe.ProductConstruct(stateId, [],
                [ stateKeyId, ExpressionRecipe.ParameterSlot 1
                  stateFoundId, ExpressionRecipe.BoolLiteral false
                  stateValueId, ExpressionRecipe.OptionNone stringType ])
        let body =
            ExpressionRecipe.FieldAccess(
                ExpressionRecipe.ListFold(ExpressionRecipe.ParameterSlot 0, seed, folder.Id, []),
                stateValueId)
        let ws, find =
            create ws "first_value_for_key"
                [ parameter "entries" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ entryType ]))
                  parameter "key" stringType ] optionType body
        AgentWorkspace.snapshotForStructuralEditing ws, folder, find, entryId, entryKeyId, entryValueId

    [<Fact>]
    let ``fold with runtime context authors publishes persists and lowers with stable callback identity`` () =
        let snapshot, folder, search, _ = fixture ()
        Assert.Empty(SnapshotValidator.validate snapshot)
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize JSON"
        let restored = SnapshotCodec.deserialize json |> unwrap "restore JSON"
        let persisted = SnapshotCodec.serialize restored |> unwrap "reserialize"
        let reopened = SnapshotCodec.deserialize persisted |> unwrap "restore"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute reopened)
        let focus = SemanticQuery.focusFunction reopened search.Id |> unwrap "focus"
        Assert.Contains(focus.Callees, fun site -> site.CalleeFunctionId = folder.Id)
        let foldNode =
            search.Body.Nodes
            |> List.find (fun node -> match node.Kind with | SemanticNodeKind.ListFold _ -> true | _ -> false)
        Assert.Equal(Some stateType,
            focus.NodeTypes |> List.find (fun fact -> fact.NodeId = foldNode.Id) |> fun fact -> fact.Type)
        let typed = TypedSemanticIr.project reopened |> unwrap "typed projection"
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"listFold\"", TypedSemanticIrSerializer.serialize typed)
        Assert.Contains("\"listFold\"", ExecutionIrSerializer.serialize execution)
        Assert.Contains("\"stringEqual\"", TypedSemanticIrSerializer.serialize typed)
        Assert.Contains("\"stringEqual\"", ExecutionIrSerializer.serialize execution)
        let emitted = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "C# emission"
        Assert.Contains("public S Fold<S>(S seed, Func<S, T, S> folder)", emitted.Source)
        Assert.Contains(".Fold(", emitted.Source)

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated fold searches an exact ordinal key in input order including empty and absent inputs`` () =
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
                $"Console.WriteLine({call}(NyList<NyStr>.Create(\"a\", \"b\", \"c\"), \"b\"));\n" +
                $"Console.WriteLine({call}(NyList<NyStr>.Create(\"a\", \"b\"), \"z\"));\n" +
                $"Console.WriteLine({call}(NyList<NyStr>.Empty(), \"b\"));\n" +
                $"Console.WriteLine({call}(NyList<NyStr>.Create(\"ab\"), \"a\"));\n" +
                $"Console.WriteLine({call}(NyList<NyStr>.Create(\"ab\"), \"abab\"));\n" +
                $"Console.WriteLine({call}(NyList<NyStr>.Create(\"A\"), \"a\"));\n" +
                $"Console.WriteLine({call}(NyList<NyStr>.Create(\"\"), \"\"));\n" +
                $"Console.WriteLine({digitCall}(NyList<long>.Create(1L, 2L, 3L)));\n" +
                $"Console.WriteLine({digitCall}(NyList<long>.Empty()));\n")
            let start args =
                let code, stdout, stderr = Dotnet.run tempRoot args
                if code <> 0 then failwithf "dotnet failed: %s\n%s\n%s" (String.concat " " args) stdout stderr
                stdout
            start [ "build"; "Generated.csproj"; "--nologo"; "-v:q" ] |> ignore
            let output = start [ Path.Combine(tempRoot, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("True\nFalse\nFalse\nFalse\nFalse\nFalse\nTrue\n123\n0", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated fold retrieves a typed element by runtime index with empty and out of range cases`` () =
        let snapshot, folder, at = indexFixture ()
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize index"
        let restored = SnapshotCodec.deserialize json |> unwrap "reopen index"
        let persisted = SnapshotCodec.serialize restored |> unwrap "reserialize index"
        let reopened = SnapshotCodec.deserialize persisted |> unwrap "reopen index"
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
                [ "NyList<NyStr>.Empty(), 0L"
                  "NyList<NyStr>.Create(\"first\", \"second\", \"third\"), -1L"
                  "NyList<NyStr>.Create(\"first\", \"second\", \"third\"), 0L"
                  "NyList<NyStr>.Create(\"first\", \"second\", \"third\"), 1L"
                  "NyList<NyStr>.Create(\"first\", \"second\", \"third\"), 2L"
                  "NyList<NyStr>.Create(\"first\", \"second\", \"third\"), 3L" ]
            let program =
                [ "using System; using Nyrwvy.Generated;" ]
                @ (inputs |> List.map (fun args ->
                    $"{{ var result = {functionName}({args}); Console.WriteLine(result.HasValue ? result.Value : \"<none>\"); }}"))
                |> String.concat "\n"
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), program)
            let start args =
                let code, stdout, stderr = Dotnet.run tempRoot args
                if code <> 0 then failwithf "dotnet failed: %s\n%s\n%s" (String.concat " " args) stdout stderr
                stdout
            start [ "build"; "Generated.csproj"; "--nologo"; "-v:q" ] |> ignore
            let output = start [ Path.Combine(tempRoot, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("<none>\n<none>\nfirst\nsecond\nthird\n<none>", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated fold returns first typed matching value with an absent and empty result`` () =
        let snapshot, folder, find, entryId, entryKeyId, entryValueId = findFixture ()
        let json = SnapshotCodec.serialize snapshot |> unwrap "serialize find"
        let restored = SnapshotCodec.deserialize json |> unwrap "reopen find"
        let persisted = SnapshotCodec.serialize restored |> unwrap "reserialize find"
        let reopened = SnapshotCodec.deserialize persisted |> unwrap "reopen find"
        Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute reopened)
        let focus = SemanticQuery.focusFunction reopened find.Id |> unwrap "find focus"
        Assert.Contains(focus.Callees, fun site -> site.CalleeFunctionId = folder.Id)
        let execution = TypedSemanticIr.project reopened |> unwrap "typed" |> ExecutionIr.lower |> unwrap "lower"
        let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap "emit"
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-find-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            let functionName = "NyFunctions." + CSharpBackendNames.functionName find.Id
            let entryName = CSharpBackendNames.typeName entryId
            let keyName = CSharpBackendNames.fieldName entryKeyId
            let valueName = CSharpBackendNames.fieldName entryValueId
            let program =
                [ "using System; using Nyrwvy.Generated;"
                  $"var entries = NyList<{entryName}>.Create(new {entryName}({keyName}: \"k\", {valueName}: \"first\"), new {entryName}({keyName}: \"k\", {valueName}: \"second\"), new {entryName}({keyName}: \"ka\", {valueName}: \"other\"));"
                  $"var match = {functionName}(entries, \"k\"); Console.WriteLine(match.HasValue ? match.Value : \"<none>\");"
                  $"var absent = {functionName}(entries, \"a\"); Console.WriteLine(absent.HasValue ? absent.Value : \"<none>\");"
                  $"var empty = {functionName}(NyList<{entryName}>.Empty(), \"k\"); Console.WriteLine(empty.HasValue ? empty.Value : \"<none>\");"
                  $"var other = {functionName}(entries, \"ka\"); Console.WriteLine(other.HasValue ? other.Value : \"<none>\");" ]
                |> String.concat "\n"
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), program)
            let start args =
                let code, stdout, stderr = Dotnet.run tempRoot args
                if code <> 0 then failwithf "dotnet failed: %s\n%s\n%s" (String.concat " " args) stdout stderr
                stdout
            start [ "build"; "Generated.csproj"; "--nologo"; "-v:q" ] |> ignore
            let output = start [ Path.Combine(tempRoot, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("first\n<none>\n<none>\nother", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists tempRoot then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``StringEqual rejects a mistyped operand before publication and during snapshot validation`` () =
        let initial = Snapshot.empty (id<SnapshotTag> "equality-initial")
        let ws = AgentWorkspace.create initial |> unwrap "workspace"
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = initial.Id
              TypeParameters = None
              DisplayName = "same"
              Parameters = [ parameter "left" stringType; parameter "right" stringType ]
              ResultType = boolType
              RequiredEffects = []
              Body = ExpressionRecipe.StringEqual(ExpressionRecipe.Int64Literal 1L, ExpressionRecipe.ParameterSlot 1) }
        match AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
            Assert.Equal("PureExpressionOperandTypeMismatch", diagnostic.Code)
        | other -> failwithf "Mistyped equality was accepted: %A" other
        let ws, fn =
            create ws "same" request.Parameters boolType
                (ExpressionRecipe.StringEqual(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
        let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
        let corrupted =
            { snapshot with
                Functions = snapshot.Functions |> List.map (fun item ->
                    if item.Id <> fn.Id then item else
                        { item with
                            Body =
                                { item.Body with
                                    Nodes = item.Body.Nodes |> List.map (fun node ->
                                        match node.Kind with
                                        | SemanticNodeKind.ParameterReference parameterId when parameterId = item.Parameters.Head.Id ->
                                            { node with Kind = SemanticNodeKind.Int64Literal 1L }
                                        | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate corrupted, fun issue -> issue.Code = "StringIntrinsicTypeMismatch")

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
                folder.Id, []) }
        match AgentExpressionStructuralWorkflow.simulate ws request with
        | Error(AgentStructuralWorkflowError.SimulationRejected errors) ->
            Assert.Contains(errors, fun error -> error.Code = "PureExpressionCallbackInputTypeMismatch")
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
                                                | SemanticNodeKind.ListFold(source, seed, _, _) ->
                                                    { node with Kind = SemanticNodeKind.ListFold(source, seed, search.Id, []) }
                                                | _ -> node) } }) }
        let issues = SnapshotValidator.validate corrupted
        Assert.Contains(issues, fun issue -> issue.Code = "CallbackParameterTypeMismatch")
        Assert.Contains(issues, fun issue -> issue.Code = "CallbackResultTypeMismatch")
