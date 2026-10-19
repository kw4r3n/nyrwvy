namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ImportProvenanceSelectorTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("selector-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private unwrap = function Ok v -> v | Error e -> failwithf "%A" e
    let private sharedType = { TypeParameters = []; Id = id<TypeTag> "shared"; DisplayName = "Shared"; Kind = SemanticTypeKind.Product [] }
    let private library version name number =
        let node = id<GraphNodeTag> name
        let measure = { TypeParameters = []; Id = id<TypeTag> name; DisplayName = "Measure"; Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> name; DisplayName = "value"; Type = intType } ] }
        let effect = { Id = id<EffectTag> name; DisplayName = "Io" }
        let capability = { Id = id<CapabilityTag> name; DisplayName = "Clock"
                           Operations = [ { Id = id<CapabilityOperationTag> name; DisplayName = "read"; Parameters = []; ResultType = intType; FailureType = None; EffectId = effect.Id } ] }
        { Snapshot.empty (id<SnapshotTag> version) with
            Types = [ sharedType; measure ]
            Effects = [ effect ]
            Capabilities = [ capability ]
            Functions =
                [ { Id = id<FunctionTag> name; DisplayName = "render"; Parameters = []; ResultType = intType; RequiredEffects = []
                    TypeParameters = []
                    Body = { Nodes = [ { Id = node; Kind = SemanticNodeKind.Int64Literal number } ]; ResultNodeId = node } } ] }
    let private withApp test =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-selector-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let a, b = library "a" "a" 1L, library "b" "b" 2L
            let write name graph = let path = Path.Combine(root, name + ".json") in File.WriteAllText(path, SnapshotCodec.serialize graph |> unwrap); path
            let aPath, bPath, nextPath = write "a" a, write "b" b, write "a-new" (library "a-new" "a" 10L)
            let path = Path.Combine(root, "app.json")
            use session = AuthoringSession.createNewWithDependencies path [ aPath; bPath ] |> unwrap
            test session path a b aPath bPath nextPath
        finally Directory.Delete(root, true)
    let private json value = JsonSerializer.Serialize value
    let private element text = use doc = JsonDocument.Parse(text: string) in doc.RootElement.Clone()
    let private simulate (session: AuthoringSession) name body =
        AuthoringProtocol.executeLine session (json {| id = "test"; op = "simulateCreateFunctionByName"; displayName = name
                                                       parameters = Array.empty<obj>; resultType = {| kind = "primitive"; primitive = "int64" |}
                                                       requiredEffects = Array.empty<obj>; body = element body |}) |> element
    let private direct selector = json {| kind = "directCall"; ``function`` = element selector; arguments = Array.empty<obj> |}
    let private selected snapshot = json {| name = "render"; dependencySnapshotId = StableId.value snapshot |}
    let private publish (session: AuthoringSession) (response: JsonElement) =
        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
        let proposal = response.GetProperty("result").GetProperty("proposalId").GetString()
        session.Commit(proposal, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore

    [<Fact>]
    let ``conflicting imported identity names only its actual providing pins`` () =
        withApp (fun session path a b aPath bPath nextPath ->
            let before = File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")
            let change = AuthoringDependencyChangeRequest.Selection { Sources = [ aPath; bPath; nextPath ]; Replaces = [ StableId.value a.Id; StableId.value b.Id ] }
            match session.SimulateDependencyUpgrade(change, { Fields = []; Functions = []; Operations = [] }, session.SnapshotId) with
            | Error(AuthoringSessionError.DependencyFailure message) ->
                Assert.Contains("render", message)
                Assert.Contains(StableId.value a.Id, message)
                Assert.Contains(StableId.value (id<SnapshotTag> "a-new"), message)
                Assert.DoesNotContain(StableId.value b.Id, message)
                Assert.Contains("providing pin", message)
            | other -> failwithf "Expected import conflict: %A" other
            Assert.Equal(before, (File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json"))))

    [<Fact>]
    let ``failed pin and ownership guards advise without resolving or falling back`` () =
        withApp (fun session path a b _ _ _ ->
            let before = File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")
            for selector, guard in
                [ json {| name = "render"; dependencySnapshotId = "old-pin" |}, "dependencySnapshotId"
                  json {| id = StableId.value a.Functions.Head.Id; name = "render"; ownership = "local" |}, "ownership"
                  json {| id = StableId.value a.Functions.Head.Id; name = "wrong-name" |}, "name" ] do
                let response = simulate session "refused" (direct selector)
                Assert.False(response.GetProperty("ok").GetBoolean())
                let message = response.GetProperty("error").GetProperty("message").GetString()
                Assert.Contains("advice only", message)
                Assert.Contains(StableId.value a.Functions.Head.Id, message)
                Assert.Contains(StableId.value a.Id, message)
                Assert.Contains(guard, message)
            let unknown = simulate session "unknown" (direct (json {| id = "unknown"; name = "render" |}))
            Assert.False(unknown.GetProperty("ok").GetBoolean())
            Assert.DoesNotContain("Current candidates", unknown.GetProperty("error").GetProperty("message").GetString())
            Assert.Equal(before, (File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")))
            simulate session "allowed" (direct (json {| id = StableId.value a.Functions.Head.Id; name = "render"; ownership = "imported" |})) |> publish session)

    [<Fact>]
    let ``selector advice is bounded and deterministic for many equal names`` () =
        withApp (fun session path _ _ aPath bPath _ ->
            let sources =
                [ 0 .. 5 ] |> List.map (fun index ->
                    let source = Path.Combine(Path.GetDirectoryName path, $"extra-{index}.json")
                    File.WriteAllText(source, SnapshotCodec.serialize (library $"extra-{index}" $"extra-{index}" (int64 index)) |> unwrap)
                    source)
            use larger = AuthoringSession.createNewWithDependencies (Path.Combine(Path.GetDirectoryName path, "larger.json")) ([ aPath; bPath ] @ sources) |> unwrap
            let selector = json {| name = "render"; dependencySnapshotId = "obsolete" |}
            let first = simulate larger "refused" (direct selector)
            let second = simulate larger "refused" (direct selector)
            Assert.False(first.GetProperty("ok").GetBoolean())
            let message = first.GetProperty("error").GetProperty("message").GetString()
            Assert.Equal(message, second.GetProperty("error").GetProperty("message").GetString())
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(message, "ownership=imported").Count)
            Assert.Contains("+3 more candidates", message))

    [<Fact>]
    let ``discovery identifies local declarations and all providing pins for a diamond`` () =
        withApp (fun session _ a b _ _ _ ->
            let p = session.Declarations().Provenance
            let shared = p |> List.find (fun x -> x.Kind = "type" && x.Id = StableId.value sharedType.Id)
            Assert.False(shared.IsLocal)
            Assert.Equal<Set<string>>(set [ StableId.value a.Id; StableId.value b.Id ], shared.Sources |> List.map (fun s -> s.SnapshotId) |> Set.ofList)
            let selector = selected a.Id
            simulate session "local" (direct selector) |> publish session
            let declaration = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "local")
            let local = session.Declarations().Provenance |> List.find (fun p -> p.Id = StableId.value declaration.Id)
            Assert.True(local.IsLocal)
            Assert.Empty local.Sources
            let response = AuthoringProtocol.executeLine session "{\"id\":\"list\",\"op\":\"declarations\"}" |> element
            Assert.Equal(session.Declarations().Provenance.Length, response.GetProperty("result").GetProperty("provenance").GetArrayLength()))

    [<Fact>]
    let ``qualified and exact-ID selectors call both same-named functions without guessing`` () =
        withApp (fun session _ a b _ _ _ ->
            Assert.False((simulate session "ambiguous" (direct (json "render"))).GetProperty("ok").GetBoolean())
            let left = direct (selected a.Id)
            let right = direct (json {| id = StableId.value b.Functions.Head.Id; name = "render" |})
            let body = json {| kind = "int64WrappingAdd"; left = element left; right = element right |}
            simulate session "both" body |> publish session
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "both")
            let actual = PureRun.run session fn.Id [] (Some(PureRunValue.Int64 3L)) |> unwrap
            Assert.Equal(Some true, actual.Matches))

    [<Fact>]
    let ``explicit ID name pin and ownership guards never fall back`` () =
        withApp (fun session path a _ _ _ _ ->
            let before = File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")
            let invalid =
                [ json {| id = "unknown"; name = "render"; dependencySnapshotId = StableId.value a.Id |}
                  json {| id = StableId.value a.Functions.Head.Id; name = "wrong" |}
                  json {| id = StableId.value a.Functions.Head.Id; dependencySnapshotId = "unknown" |}
                  json {| id = StableId.value a.Functions.Head.Id; ownership = "local" |}
                  json {| dependencySnapshotId = StableId.value a.Id |} ]
            for selector in invalid do
                Assert.False((simulate session "invalid" (direct selector)).GetProperty("ok").GetBoolean())
                Assert.Equal(before, (File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json"))))

    [<Fact>]
    let ``exact owner selector supports recursive replacement including keepUnlisted`` () =
        withApp (fun session path a _ aPath _ _ ->
            let create = session.SimulateCreateFunction
                            { ExpectedBaseSnapshotId = session.SnapshotId; DisplayName = "countdown"
                              TypeParameters = None
                              Parameters = [ { DisplayName = "remaining"; Type = intType }; { DisplayName = "total"; Type = intType } ]
                              ResultType = intType; RequiredEffects = []; Body = ExpressionRecipe.Int64Literal 0L } |> unwrap
            session.Commit(create.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "countdown")
            let selector = {| id = StableId.value fn.Id; name = "countdown"; ownership = "local" |}
            for keep in [ false; true ] do
                let arguments =
                    [ {| parameter = "remaining"; value = element """{"kind":"int64WrappingSubtract","left":{"kind":"parameter","name":"remaining"},"right":{"kind":"int64Literal","value":"1"}}""" |} ]
                    @ (if keep then [] else [ {| parameter = "total"; value = element """{"kind":"parameter","name":"total"}""" |} ])
                let call = {| kind = "directCall"; ``function`` = selector; arguments = arguments; keepUnlisted = keep |}
                let body = {| kind = "conditional"; condition = element """{"kind":"int64Equal","left":{"kind":"parameter","name":"remaining"},"right":{"kind":"int64Literal","value":"0"}}"""
                              whenTrue = element """{"kind":"parameter","name":"total"}"""; whenFalse = call |}
                let response = AuthoringProtocol.executeLine session (json {| id = "replace"; op = "simulateReplaceBodyByName"; ``function`` = selector; replacement = body |}) |> element
                publish session response
                let actual = PureRun.run session fn.Id [ PureRunValue.Int64 3L; PureRunValue.Int64 9L ] (Some(PureRunValue.Int64 9L)) |> unwrap
                Assert.Equal(Some true, actual.Matches)
            // Evolving a separate library can legitimately introduce the local owner's
            // display name: collision policy preserves distinct declaration identities.
            use upstream = AuthoringSession.openExisting aPath |> unwrap
            let rename = upstream.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function a.Functions.Head.Id, "countdown") ] |> unwrap
            upstream.Commit(rename.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            upstream.Close()
            let change = AuthoringDependencyChangeRequest.Upgrades [ { Source = aPath; Replaces = StableId.value a.Id } ]
            let proposal = session.SimulateDependencyUpgrade(change, { Fields = []; Functions = []; Operations = [] }, session.SnapshotId) |> unwrap
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let other = {| id = StableId.value a.Functions.Head.Id; name = "countdown" |}
            let replace keep = AuthoringProtocol.executeLine session (json {| id = "replace"; op = "simulateReplaceBodyByName"; ``function`` = {| id = StableId.value fn.Id |}
                                                                              replacement = {| kind = "directCall"; ``function`` = other; arguments = Array.empty<obj>; keepUnlisted = keep |} |}) |> element
            replace false |> publish session
            let actual = PureRun.run session fn.Id [ PureRunValue.Int64 3L; PureRunValue.Int64 9L ] (Some(PureRunValue.Int64 1L)) |> unwrap
            Assert.Equal(Some true, actual.Matches)
            let before = File.ReadAllBytes path
            Assert.False((replace true).GetProperty("ok").GetBoolean())
            Assert.Equal<byte>(before, File.ReadAllBytes path))

    [<Fact>]
    let ``type capability and effect qualifiers lower to exact owned declarations`` () =
        withApp (fun session _ a _ _ _ _ ->
            let selector name = element(json {| name = name; dependencySnapshotId = StableId.value a.Id |})
            let capType = element(json {| kind = "capability"; name = selector "Clock" |})
            let body = element(json {| kind = "capabilityInvoke"; capability = selector "Clock"; operation = "read";
                                      capabilityValue = {| kind = "parameter"; name = "clock" |}; arguments = Array.empty<obj> |})
            let response = AuthoringProtocol.executeLine session (json {| id = "invoke"; op = "simulateCreateFunctionByName"; displayName = "invokeA"
                                                                          parameters = [ {| displayName = "clock"; ``type`` = capType |} ]; resultType = {| kind = "primitive"; primitive = "int64" |}
                                                                          requiredEffects = [ selector "Io" ]; body = body |}) |> element
            publish session response
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "invokeA")
            Assert.Equal<TypeRef>(TypeRef.Capability a.Capabilities.Head.Id, fn.Parameters.Head.Type)
            Assert.Equal<EffectId list>([ a.Effects.Head.Id ], fn.RequiredEffects)
            let request = json {| kind = "named"; name = selector "Measure" |} |> element
            Assert.Equal<TypeRef>(TypeRef.Named(a.Types.Tail.Head.Id, []), WireSymbolic.typeRef (session.Declarations()) request))

    [<Fact>]
    let ``updated candidate provenance and moved source-free reopen retain explicit selection`` () =
        withApp (fun session path a _ aPath bPath nextPath ->
            simulate session "local" (direct (selected a.Id)) |> publish session
            let change = AuthoringDependencyChangeRequest.Selection { Sources = [ nextPath ]; Replaces = [ StableId.value a.Id ] }
            let proposal = session.SimulateDependencyUpgrade(change, { Fields = []; Functions = []; Operations = [] }, session.SnapshotId) |> unwrap
            let candidate = session.ExecutionContext(Some proposal.ProposalId) |> unwrap
            let provenance = candidate.Declarations.Provenance |> List.find (fun p -> p.Kind = "function" && p.Id = StableId.value a.Functions.Head.Id)
            let nextId = StableId.value (library "a-new" "a" 10L).Id
            Assert.Equal<string list>([ nextId ], provenance.Sources |> List.map (fun s -> s.SnapshotId))
            session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            Assert.False((simulate session "stalePin" (direct (selected a.Id))).GetProperty("ok").GetBoolean())
            session.Close()
            File.Delete aPath; File.Delete bPath; File.Delete nextPath
            let moved = Path.Combine(Path.GetDirectoryName path, "moved.json")
            File.Move(path, moved); File.Move(path + ".dependency.json", moved + ".dependency.json")
            use reopened = AuthoringSession.openExisting moved |> unwrap
            let request = json {| id = StableId.value a.Functions.Head.Id; dependencySnapshotId = nextId |}
            simulate reopened "movedCall" (direct request) |> publish reopened
            let fn = reopened.Declarations().Functions |> List.find (fun f -> f.DisplayName = "movedCall")
            Assert.Equal(Some true, (PureRun.run reopened fn.Id [] (Some(PureRunValue.Int64 10L)) |> unwrap).Matches))
