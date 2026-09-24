namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ExternalCapabilityBindingTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    let private current (session: AuthoringSession) =
        (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)
        |> unwrap "commit" |> ignore

    let private createType session name fieldName =
        let request: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = current session
              DisplayName = name
              Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = fieldName; Type = stringType } ] }
        session.SimulateCreateType request |> unwrap "create type" |> publish session
        let entity = session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)
        match entity.Kind with
        | SemanticTypeKind.Product [ field ] -> entity, field
        | _ -> failwith "Expected one product field."

    let private createFixture session =
        let requestType, key = createType session "LookupRequest" "key"
        let responseType, value = createType session "LookupResponse" "value"
        let effectRequest: AgentCreateEffectDeclarationRequest =
            { ExpectedBaseSnapshotId = current session; DisplayName = "ExternalRead" }
        session.SimulateCreateEffect effectRequest |> unwrap "create effect" |> publish session
        let effect = session.Declarations().Effects |> List.exactlyOne
        let resultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.Named responseType.Id; stringType ])
        let capabilityRequest: AgentCreateCapabilityDeclarationRequest =
            { ExpectedBaseSnapshotId = current session
              DisplayName = "LookupProvider"
              Operations =
                [ { DisplayName = "lookup"
                    Parameters = [ { DisplayName = "request"; Type = TypeRef.Named requestType.Id } ]
                    ResultType = resultType
                    FailureType = None
                    EffectId = effect.Id }
                  { DisplayName = "fallback"
                    Parameters = []
                    ResultType = resultType
                    FailureType = None
                    EffectId = effect.Id } ] }
        session.SimulateCreateCapability capabilityRequest |> unwrap "create capability" |> publish session
        let capability = session.Declarations().Capabilities |> List.exactlyOne
        let lookup = capability.Operations |> List.find (fun operation -> operation.DisplayName = "lookup")
        let fallback = capability.Operations |> List.find (fun operation -> operation.DisplayName = "fallback")
        let argument = List.exactlyOne lookup.Parameters
        let body =
            ExpressionRecipe.MatchResult(
                ExpressionRecipe.CapabilityInvoke(
                    ExpressionRecipe.ParameterSlot 1,
                    lookup.Id,
                    [ argument.Id, ExpressionRecipe.ParameterSlot 0 ]),
                ExpressionRecipe.FieldAccess(
                    ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value },
                    value.Id),
                ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Error })
        let functionRequest: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = current session
              DisplayName = "query"
              Parameters =
                [ { DisplayName = "request"; Type = TypeRef.Named requestType.Id }
                  { DisplayName = "provider"; Type = TypeRef.Capability capability.Id } ]
              ResultType = stringType
              RequiredEffects = [ effect.Id ]
              Body = body }
        session.SimulateCreateFunction functionRequest |> unwrap "create query" |> publish session
        let query = session.Declarations().Functions |> List.exactlyOne
        requestType, key, responseType, value, capability, lookup, fallback, query

    let private runDotnet root args =
        let info = ProcessStartInfo("dotnet")
        info.WorkingDirectory <- root
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        info.Environment["DOTNET_NOLOGO"] <- "1"
        for arg in args do info.ArgumentList.Add(arg)
        use child = Process.Start(info)
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        if child.ExitCode <> 0 then
            failwithf "dotnet %A failed:\n%s\n%s" args stdout.Result stderr.Result
        stdout.Result

    [<Fact>]
    let ``external interface binds product values and typed Result failure without hiding Nyrwvy control`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-external-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let requestType, key, responseType, value, capability, lookup, fallback, query =
                use session = AuthoringSession.createNew path |> unwrap "session"
                let entities = createFixture session
                session.Close()
                entities
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            reopened.ProjectExecution() |> unwrap "lower" |> ignore
            match reopened.EmitCSharp CSharpBackendProfile.empty with
            | Error(AuthoringSessionError.BackendFailure(CSharpBackendError.MissingCapabilityBinding id)) ->
                Assert.Equal(lookup.Id, id)
            | result -> failwithf "Expected missing binding: %A" result
            match reopened.EmitCSharp(CSharpBackendProfile.externalImplementations [ lookup.Id ]) with
            | Error(AuthoringSessionError.BackendFailure(CSharpBackendError.MissingCapabilityBinding id)) ->
                Assert.Equal(fallback.Id, id)
            | result -> failwithf "Expected exhaustive capability binding: %A" result
            let mixed: CSharpBackendProfile =
                { CapabilityBindings =
                    [ { OperationId = lookup.Id; Kind = CSharpCapabilityBindingKind.ExternalImplementation }
                      { OperationId = fallback.Id; Kind = CSharpCapabilityBindingKind.SystemConsoleWriteLine } ] }
            match reopened.EmitCSharp mixed with
            | Error(AuthoringSessionError.BackendFailure(CSharpBackendError.MixedCapabilityBindingKinds id)) ->
                Assert.Equal(capability.Id, id)
            | result -> failwithf "Expected mixed binding rejection: %A" result
            let profile = CSharpBackendProfile.externalImplementations [ lookup.Id; fallback.Id ]
            let artifact = reopened.EmitCSharp profile |> unwrap "emit external profile"
            Assert.Empty(artifact.Symbols.CapabilityAdapters)
            Assert.DoesNotContain(CSharpBackendNames.capabilityAdapterName capability.Id, artifact.Source)
            let profileJson =
                $"""{{"id":"emit","op":"emitCSharp","profile":{{"kind":"externalImplementations","operationIds":["{StableId.value lookup.Id}","{StableId.value fallback.Id}"]}}}}"""
            use response = AuthoringProtocol.executeLine reopened profileJson |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            Assert.Equal(artifact.Source, response.RootElement.GetProperty("result").GetProperty("source").GetString())
            let requestName = CSharpBackendNames.typeName requestType.Id
            let responseName = CSharpBackendNames.typeName responseType.Id
            let capabilityName = CSharpBackendNames.capabilityName capability.Id
            let lookupName = CSharpBackendNames.capabilityOperationName lookup.Id
            let fallbackName = CSharpBackendNames.capabilityOperationName fallback.Id
            let argName = CSharpBackendNames.capabilityParameterName (List.exactlyOne lookup.Parameters).Id
            let keyName = CSharpBackendNames.fieldName key.Id
            let entryName = CSharpBackendNames.functionName query.Id
            let host =
                $"""using Nyrwvy.Generated;

public sealed class FakeProvider : {capabilityName}
{{
    public NyResult<{responseName}, string> {lookupName}({requestName} {argName}) =>
        {argName}.{keyName} == "yes"
            ? NyResult<{responseName}, string>.Ok(new {responseName}("found"))
            : NyResult<{responseName}, string>.Err("missing");
    public NyResult<{responseName}, string> {fallbackName}() => NyResult<{responseName}, string>.Err("fallback");
}}
"""
            let program =
                $"""using System;
using Nyrwvy.Generated;
var provider = new FakeProvider();
Console.WriteLine(NyFunctions.{entryName}(new {requestName}("yes"), provider));
Console.WriteLine(NyFunctions.{entryName}(new {requestName}("no"), provider));
"""
            File.WriteAllText(Path.Combine(root, "Generated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            File.WriteAllText(Path.Combine(root, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(root, "FakeProvider.cs"), host)
            File.WriteAllText(Path.Combine(root, "Program.cs"), program)
            runDotnet root [ "build"; "Generated.csproj"; "-v:q" ] |> ignore
            let output = runDotnet root [ Path.Combine(root, "bin", "Debug", "net10.0", "Generated.dll") ]
            Assert.Equal("found\nmissing", output.Replace("\r\n", "\n").Trim())
        finally
            if Directory.Exists root then Directory.Delete(root, true)
