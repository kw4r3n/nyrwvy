namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The request JSON depth allowance is derived from the recipe node limit, so any recipe within the
/// node limit parses and is judged by the recipe bounds; only deeper JSON is a protocol error.
/// Responses can render any graph the snapshot codec persists.
module JsonDepthBoundTests =
    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-json-depth-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> function Ok value -> value | Error error -> failwithf "%A" error
            run session
        finally
            Directory.Delete(root, true)

    /// `depth` nested selfCalls ending in ParameterSlot 0: depth + 1 recipe nodes, three JSON levels per selfCall.
    let private selfCallChain depth =
        let text = StringBuilder()
        for _ in 1..depth do
            text.Append("""{"kind":"selfCall","arguments":[{"slot":0,"value":""") |> ignore
        text.Append("""{"kind":"parameterSlot","index":0}""") |> ignore
        for _ in 1..depth do
            text.Append("}]}") |> ignore
        text.ToString()

    /// The probe-3 shape: a left-deep chain of `adds` Int64WrappingAdd nodes.
    let private leftDeepAdds adds =
        let text = StringBuilder()
        for _ in 1..adds do
            text.Append("""{"kind":"int64WrappingAdd","left":""") |> ignore
        text.Append("""{"kind":"parameterSlot","index":0}""") |> ignore
        for _ in 1..adds do
            text.Append(""","right":{"kind":"int64Literal","value":1}}""") |> ignore
        text.ToString()

    let private createFunction body =
        sprintf
            """{"id":"create","op":"simulateCreateFunction","displayName":"deep","parameters":[{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}"""
            int64Type int64Type body

    let private errorOf (session: AuthoringSession) line =
        use reply = JsonDocument.Parse(AuthoringProtocol.executeLine session line, JsonDocumentOptions(MaxDepth = 4096))
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
        reply.RootElement.GetProperty("error").Clone()

    let private diagnosticOf (error: JsonElement) =
        Assert.Equal("SimulationRejected", error.GetProperty("code").GetString())
        let diagnostic = error.GetProperty("details").GetProperty("diagnostics").[0]
        diagnostic.GetProperty("code").GetString(), diagnostic.GetProperty("message").GetString()

    /// Envelope (3) + three levels per node for every node the limit allows + leaf TypeRef margin (64).
    let private requestBound = 3 + 3 * ExpressionRecipeBounds.maxNodes + 64

    [<Fact>]
    let ``a recipe at the depth limit with three JSON levels per node is accepted`` () =
        withSession (fun session ->
            use reply = AuthoringProtocol.executeLine session (createFunction (selfCallChain (ExpressionRecipeBounds.maxDepth - 1))) |> JsonDocument.Parse
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString()))

    [<Fact>]
    let ``the probe's 140-deep add chain gets the recipe depth diagnostic, not invalid JSON`` () =
        withSession (fun session ->
            let code, message = errorOf session (createFunction (leftDeepAdds 140)) |> diagnosticOf
            Assert.Equal("StructuralRecipeDepthLimitExceeded", code)
            Assert.Equal("The structural recipe is deeper than 32 nodes (it is 141 nodes deep).", message))

    [<Fact>]
    let ``a recipe as deep as the node limit parses in the deepest envelope`` () =
        withSession (fun session ->
            // 255 selfCalls and a leaf: 256 nodes nested 256 deep, three JSON levels each.
            let chain = selfCallChain (ExpressionRecipeBounds.maxNodes - 1)
            let code, message = errorOf session (createFunction chain) |> diagnosticOf
            Assert.Equal("StructuralRecipeDepthLimitExceeded", code)
            Assert.EndsWith("(it is 256 nodes deep).", message)
            // functionRewrites[0].replacement is the deepest recipe envelope; the request fails on its
            // unknown field, after the whole recipe was read.
            let migration =
                sprintf
                    """{"id":"migrate","op":"simulateProductFieldTypeMigration","fieldId":"missing-field","newFieldType":%s,"functionRewrites":[{"functionId":"missing-function","replacement":%s}]}"""
                    int64Type chain
            let error = errorOf session migration
            Assert.NotEqual<string>("ProtocolError", error.GetProperty("code").GetString()))

    [<Fact>]
    let ``JSON deeper than the bound is a clear protocol error`` () =
        withSession (fun session ->
            let error = errorOf session (createFunction (selfCallChain 300))
            Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
            let message = error.GetProperty("message").GetString()
            Assert.StartsWith("Invalid JSON: ", message)
            Assert.Equal(835, requestBound)
            Assert.Contains($"maximum configured depth of {requestBound} has been exceeded", message))

    let mutable private counter = 0
    let private fresh<'tag> () =
        counter <- counter + 1
        StableId.createForTesting<'tag> (sprintf "json-depth-%06d" counter)

    [<Fact>]
    let ``readFunction renders a graph nested as deep as the snapshot codec persists`` () =
        let int64 = TypeRef.Primitive PrimitiveType.Int64
        // 240 nested Binds are 966 snapshot levels (4 per Bind) and about 1200 response levels (5 per Bind).
        let rec nested remaining: ValueGraph =
            let literal = fresh<GraphNodeTag> ()
            if remaining = 0 then
                { Nodes = [ { Id = literal; Kind = SemanticNodeKind.Int64Literal 1L } ]; ResultNodeId = literal }
            else
                let bind = fresh<GraphNodeTag> ()
                let binding: PatternBinding = { Id = fresh<PatternBindingTag> (); DisplayName = None; Type = int64 }
                { Nodes =
                    [ { Id = literal; Kind = SemanticNodeKind.Int64Literal 1L }
                      { Id = bind; Kind = SemanticNodeKind.Bind(literal, { ValueBinding = binding; Body = nested (remaining - 1) }) } ]
                  ResultNodeId = bind }
        let functionId = fresh<FunctionTag> ()
        let deep: SemanticFunction =
            { Id = functionId; DisplayName = "deep"; Parameters = []; ResultType = int64; RequiredEffects = []; Body = nested 240 }
        let snapshot = { Snapshot.empty (fresh<SnapshotTag> ()) with Functions = [ deep ] }
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-json-depth-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> function Ok json -> json | Error error -> failwithf "%A" error)
            use session = AuthoringSession.openExisting path |> function Ok value -> value | Error error -> failwithf "%A" error
            let line = AuthoringProtocol.executeLine session (sprintf """{"id":"read","op":"readFunction","functionId":"%s"}""" (StableId.value functionId))
            use reply = JsonDocument.Parse(line, JsonDocumentOptions(MaxDepth = 4096))
            Assert.True(reply.RootElement.GetProperty("ok").GetBoolean())
        finally
            Directory.Delete(root, true)
