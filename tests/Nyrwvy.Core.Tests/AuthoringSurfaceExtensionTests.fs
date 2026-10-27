namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The authoring-surface extensions of decision authoring-surface-queries-and-parity over the
/// maintained JSONL protocol: `addCapabilityOperation`, the `references` and `proposals` queries,
/// the pending count in `status`, the `…ByName` parity operations, and the renamed Union variant
/// evolution workflow.
module AuthoringSurfaceExtensionTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private commit session (proposal: JsonElement) =
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private commitIncomplete session (proposal: JsonElement) =
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}","allowIncomplete":true}}""" |> ignore

    let private publish session line =
        let proposal = result session line
        commit session proposal
        proposal

    /// The error code of a rejected request, or of the diagnostic that rejected a simulation, and its
    /// message followed by the whole reply.
    let private rejection session line =
        use reply = execute session line
        let root = reply.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.GetRawText())
        let error = root.GetProperty("error")
        let mutable details = Unchecked.defaultof<JsonElement>
        let mutable diagnostics = Unchecked.defaultof<JsonElement>
        let source =
            if error.TryGetProperty("details", &details) && details.ValueKind = JsonValueKind.Object
               && details.TryGetProperty("diagnostics", &diagnostics) && diagnostics.GetArrayLength() > 0 then
                diagnostics[0]
            else error
        source.GetProperty("code").GetString(), source.GetProperty("message").GetString() + "\n" + root.GetRawText()

    let private operations (items: string list) =
        $"""{{"id":"ops","op":"simulateOperations","operations":[{String.concat "," items}]}}"""

    let private operationsByName (items: string list) =
        $"""{{"id":"ops","op":"simulateOperationsByName","operations":[{String.concat "," items}]}}"""

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private int64Type = primitive "int64"
    let private unitType = primitive "unit"
    let private stringType = primitive "string"
    let private named name = $"""{{"kind":"named","name":"{name}"}}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private literal (value: int64) = $"""{{"kind":"int64Literal","value":{value}}}"""
    let private int64Value (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private stringValue (value: string) = $"""{{"kind":"string","value":"{value}"}}"""
    let private strings (items: JsonElement seq) = items |> Seq.map (fun item -> item.GetString()) |> Set.ofSeq

    let private define session name (parameters: (string * string) list) resultType (effects: string list) body =
        let signature =
            parameters |> List.map (fun (displayName, typeRef) -> $"""{{"displayName":"{displayName}","type":{typeRef}}}""") |> String.concat ","
        let required = effects |> List.map (fun effect -> "\"" + effect + "\"") |> String.concat ","
        publish
            session
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{signature}],"resultType":{resultType},"requiredEffects":[{required}],"body":{body}}}"""
        |> ignore

    let private call name (arguments: (string * string) list) =
        let items = arguments |> List.map (fun (target, value) -> $"""{{"parameter":"{target}","value":{value}}}""") |> String.concat ","
        $"""{{"kind":"directCall","function":"{name}","arguments":[{items}]}}"""

    /// Shape = Circle { radius } | Square { side } | Dot; Point = { x, y }; effect io; capability
    /// Host { step(count, limit), stop() }; `area(shape, scale)` matches Shape; `add(a, unused)` is
    /// called by `useAdd(v)`; `pointX(p)` reads `x`; `origin()` constructs a Point; `stepImpl` and
    /// `stopImpl` implement Host's operations, and `makeHost()` constructs Host from them.
    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-surface-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            try
                let field name = $"""{{"displayName":"{name}","type":{int64Type}}}"""
                publish
                    session
                    $"""{{"id":"shape","op":"simulateCreateTypeByName","exported":true,"displayName":"Shape","typeKind":{{"kind":"union","variants":[{{"displayName":"Circle","fields":[{field "radius"}]}},{{"displayName":"Square","fields":[{field "side"}]}},{{"displayName":"Dot","fields":[]}}]}}}}"""
                |> ignore
                publish
                    session
                    $"""{{"id":"point","op":"simulateCreateTypeByName","exported":true,"displayName":"Point","typeKind":{{"kind":"product","fields":[{field "x"},{field "y"}]}}}}"""
                |> ignore
                publish session """{"id":"io","op":"simulateCreateEffect","exported":true,"displayName":"io"}""" |> ignore
                publish
                    session
                    $"""{{"id":"host","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Host","operations":[{{"displayName":"step","parameters":[{{"displayName":"count","type":{int64Type}}},{{"displayName":"limit","type":{int64Type}}}],"resultType":{unitType},"effect":"io"}},{{"displayName":"stop","parameters":[],"resultType":{unitType},"effect":"io"}}]}}"""
                |> ignore
                let areaBody =
                    $"""{{"kind":"matchUnion","type":"Shape","input":{parameter "shape"},"arms":[{{"variant":"Circle","body":{{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"Circle","field":"radius"}}}},{{"variants":["Square","Dot"],"body":{literal 0}}}]}}"""
                define session "area" [ "shape", named "Shape"; "scale", int64Type ] int64Type [] areaBody
                define session "add" [ "a", int64Type; "unused", int64Type ] int64Type [] (parameter "a")
                define session "useAdd" [ "v", int64Type ] int64Type [] (call "add" [ "a", parameter "v"; "unused", literal 9 ])
                define session "pointX" [ "p", named "Point" ] int64Type [] $"""{{"kind":"fieldAccess","source":{parameter "p"},"type":"Point","field":"x"}}"""
                define session "origin" [] (named "Point") [] $"""{{"kind":"productConstruct","type":"Point","fields":[{{"field":"x","value":{literal 0}}},{{"field":"y","value":{literal 0}}}]}}"""
                define session "stepImpl" [ "count", int64Type; "limit", int64Type ] unitType [] """{"kind":"unitLiteral"}"""
                define session "stopImpl" [] unitType [] """{"kind":"unitLiteral"}"""
                define session "makeHost" [] """{"kind":"capability","name":"Host"}""" []
                    """{"kind":"capabilityConstruct","capability":"Host","implementations":[{"operation":"step","function":"stepImpl"},{"operation":"stop","function":"stopImpl"}]}"""
                run session path
            finally
                (session :> IDisposable).Dispose()
        finally
            Directory.Delete(root, true)

    let private declarations (session: AuthoringSession) = session.Declarations()
    let private fn session name = (declarations session).Functions |> List.find (fun item -> item.DisplayName = name)
    let private typeNamed session name = (declarations session).Types |> List.find (fun item -> item.DisplayName = name)
    let private capability session = (declarations session).Capabilities |> List.exactlyOne
    let private effect session = (declarations session).Effects |> List.exactlyOne
    let private idText (id: StableId<'Kind>) = StableId.value id
    let private constructNode session =
        ValueGraph.collectNodes (fn session "makeHost").Body
        |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.CapabilityConstruct _ -> Some node.Id
            | _ -> None)

    let private reopen path =
        match AuthoringSession.openExisting path with
        | Ok value -> value
        | Error error -> failwithf "Session reopen failed: %A" error

    let private references session (request: string) =
        result session $"""{{"id":"refs","op":"references",{request}}}"""

    let private sites (reply: JsonElement) =
        reply.GetProperty("references").EnumerateArray()
        |> Seq.map (fun item ->
            let text key = match item.TryGetProperty(key: string) with | true, value -> value.GetString() | _ -> ""
            text "ownerKind", text "ownerDisplayName", text "site", text "role")
        |> Set.ofSeq

    // ----- B1: addCapabilityOperation -----

    [<Fact>]
    let ``addCapabilityOperation extends a Capability and every program implementation in one reviewed change`` () =
        withFixture (fun session path ->
            let host = capability session
            let io = effect session
            let construct = constructNode session
            let stopImpl = fn session "stopImpl"
            let request =
                $"""{{"kind":"addCapabilityOperation","capabilityId":"{idText host.Id}","displayName":"reset","parameters":[],"resultType":{unitType},"effectId":"{idText io.Id}","implementations":[{{"constructNodeId":"{idText construct}","functionId":"{idText stopImpl.Id}"}}]}}"""
            let proposal = result session (operations [ request ])
            let review = proposal.GetProperty("review")
            Assert.Equal("operations", review.GetProperty("kind").GetString())
            let diff = review.GetProperty("diff")
            let added = diff.GetProperty("capabilityOperationsAdded")
            Assert.Equal(1, added.GetArrayLength())
            Assert.Equal(idText host.Id, added[0].GetProperty("ownerCapabilityId").GetString())
            let operation = added[0].GetProperty("operation")
            Assert.Equal("reset", operation.GetProperty("displayName").GetString())
            Assert.Equal(idText io.Id, operation.GetProperty("effectId").GetString())
            let bound = diff.GetProperty("capabilityImplementationsBound")
            Assert.Equal(1, bound.GetArrayLength())
            Assert.Equal(idText (fn session "makeHost").Id, bound[0].GetProperty("ownerFunctionId").GetString())
            Assert.Equal(idText construct, bound[0].GetProperty("constructNodeId").GetString())
            Assert.Equal(operation.GetProperty("id").GetString(), bound[0].GetProperty("operationId").GetString())
            Assert.Equal(idText stopImpl.Id, bound[0].GetProperty("functionId").GetString())
            Assert.Contains(idText (fn session "makeHost").Id, strings (review.GetProperty("affectedFunctionIds").EnumerateArray()))
            // Reviews of other operations keep their shape: the new lists appear only when non-empty.
            let rename = result session (operations [ $"""{{"kind":"renameType","typeId":"{idText (typeNamed session "Point").Id}","displayName":"Coordinate"}}""" ])
            Assert.False(rename.GetProperty("review").GetProperty("diff").TryGetProperty("capabilityOperationsAdded") |> fst)
            commit session proposal
            let check (current: AuthoringDeclarations) =
                let machine = current.Capabilities |> List.exactlyOne
                Assert.Equal(host.Id, machine.Id)
                Assert.Equal<string list>([ "reset"; "step"; "stop" ], machine.Operations |> List.map (fun item -> item.DisplayName) |> List.sort)
                let reset = machine.Operations |> List.find (fun item -> item.DisplayName = "reset")
                let implementations =
                    current.Functions |> List.find (fun item -> item.DisplayName = "makeHost")
                    |> fun item -> ValueGraph.collectNodes item.Body
                    |> List.pick (fun node ->
                        match node.Kind with
                        | SemanticNodeKind.CapabilityConstruct(_, implementations) -> Some implementations
                        | _ -> None)
                Assert.Contains((reset.Id, stopImpl.Id), implementations)
                Assert.Equal(3, implementations.Length)
            check (declarations session)
            Assert.Empty(SnapshotValidator.validate (SnapshotCodec.deserialize (File.ReadAllText path) |> function Ok value -> value | Error error -> failwithf "%A" error))
            (session :> IDisposable).Dispose()
            use reopened = reopen path
            check (reopened.Declarations()))

    [<Fact>]
    let ``addCapabilityOperation rejects incomplete, mismatched, duplicate and same-transaction-deleted operations`` () =
        withFixture (fun session _ ->
            let host = capability session
            let io = effect session
            let construct = constructNode session
            let request implementations displayName parameters effectId =
                $"""{{"kind":"addCapabilityOperation","capabilityId":"{idText host.Id}","displayName":"{displayName}","parameters":[{parameters}],"resultType":{unitType},"effectId":"{effectId}","implementations":[{implementations}]}}"""
            let plan (name: string) = $"""{{"constructNodeId":"{idText construct}","functionId":"{idText (fn session name).Id}"}}"""
            let code, text = rejection session (operations [ request "" "reset" "" (idText io.Id) ])
            Assert.Equal("CapabilityImplementationPlanMismatch", code)
            Assert.Contains("makeHost", text)
            // `stepImpl` takes two Int64 parameters; the new operation takes none.
            let code, text = rejection session (operations [ request (plan "stepImpl") "reset" "" (idText io.Id) ])
            Assert.Equal("CapabilityImplementationSignatureMismatch", code)
            Assert.Contains("makeHost", text)
            Assert.Equal("DuplicateCapabilityOperationDisplayName", rejection session (operations [ request (plan "stopImpl") "stop" "" (idText io.Id) ]) |> fst)
            Assert.Equal("InvalidDisplayName", rejection session (operations [ request (plan "stopImpl") " " "" (idText io.Id) ]) |> fst)
            Assert.Equal("EffectNotFound", rejection session (operations [ request (plan "stopImpl") "reset" "" "missing-effect" ]) |> fst)
            Assert.Equal("TargetNotFound", rejection session (operations [ (request (plan "stopImpl") "reset" "" (idText io.Id)).Replace(idText host.Id, "missing-capability") ]) |> fst)
            // An implementation must be pure: `useAdd` is, but its signature differs; `area` declares no effects but takes Shape.
            Assert.Equal("CapabilityImplementationSignatureMismatch", rejection session (operations [ request (plan "area") "reset" "" (idText io.Id) ]) |> fst)
            // The operation ID is system-owned, so a same-transaction deletion needs the review to name it;
            // the batch cannot, and the semantic core rejects it with its own code.
            let ws = AgentWorkspace.create (SnapshotCodec.deserialize (File.ReadAllText session.Path) |> function Ok value -> value | Error error -> failwithf "%A" error) |> function Ok value -> value | Error error -> failwithf "%A" error
            let intent: AgentAddCapabilityOperationIntent =
                { TargetCapabilityId = host.Id
                  Operation = { MayWait = false; DisplayName = "reset"; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = None; EffectId = io.Id }
                  ImplementationPlans = [ { ConstructNodeId = construct; ImplementationFunctionId = (fn session "stopImpl").Id } ] }
            let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
            let operationId = StableIdAllocator.newCapabilityOperationId ()
            let request: TransactionRequest =
                { Id = StableIdAllocator.newTransactionId ()
                  BaseSnapshotId = snapshot.Id
                  ProposedSnapshotId = StableIdAllocator.newSnapshotId ()
                  Operations =
                    [ SemanticOperation.AddCapabilityOperation
                          { TargetCapabilityId = host.Id
                            Operation = { MayWait = false; Id = operationId; DisplayName = "reset"; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = None; EffectId = io.Id }
                            ImplementationPlans = [ { ConstructNodeId = construct; ImplementationFunctionId = (fn session "stopImpl").Id } ] }
                      SemanticOperation.DeleteCapabilityOperation operationId ] }
            match Transaction.simulate snapshot request with
            | Rejected [ diagnostic ] -> Assert.Equal("CapabilityOperationCreatedInTransaction", diagnostic.Code)
            | other -> failwithf "Expected a rejection, got %A" other
            Assert.True(Result.isOk (AgentWorkflow.simulate ws [ AgentOperationIntent.AddCapabilityOperation intent ])))

    [<Fact>]
    let ``addCapabilityOperation by name resolves the capability, the effect, types and implementation functions`` () =
        withFixture (fun session _ ->
            let construct = constructNode session
            let proposal =
                result session (operationsByName
                    [ $"""{{"kind":"addCapabilityOperation","capability":"Host","displayName":"locate","parameters":[{{"displayName":"origin","type":{named "Point"}}}],"resultType":{int64Type},"effect":"io","implementations":[{{"constructNodeId":"{idText construct}","function":"pointX"}}]}}""" ])
            let operation = proposal.GetProperty("review").GetProperty("diff").GetProperty("capabilityOperationsAdded").[0].GetProperty("operation")
            Assert.Equal("locate", operation.GetProperty("displayName").GetString())
            Assert.Equal(idText (typeNamed session "Point").Id, operation.GetProperty("parameters").[0].GetProperty("type").GetProperty("typeId").GetString())
            commit session proposal
            Assert.Equal(3, (capability session).Operations.Length)
            Assert.Contains("Unknown capability", rejection session (operationsByName [ """{"kind":"addCapabilityOperation","capability":"Nobody","displayName":"x","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"effect":"io","implementations":[]}""" ]) |> snd))

    // ----- B2: references -----

    [<Fact>]
    let ``references lists every referencing site of a type, effect, capability, operation, variant, field and function`` () =
        withFixture (fun session _ ->
            let shape = references session """"kind":"type","type":"Shape" """
            Assert.Equal("Shape", shape.GetProperty("subject").GetProperty("displayName").GetString())
            Assert.Equal(idText (typeNamed session "Shape").Id, shape.GetProperty("subject").GetProperty("id").GetString())
            let shapeSites = sites shape
            Assert.Contains(("function", "area", "parameterType", ""), shapeSites)
            Assert.Contains(("function", "area", "node", "match"), shapeSites)
            Assert.Equal(shape.GetProperty("references").GetArrayLength(), shape.GetProperty("count").GetInt32())
            // The exact-ID form gives the same sites.
            Assert.Equal<Set<string * string * string * string>>(shapeSites, sites (references session $""""kind":"type","typeId":"{idText (typeNamed session "Shape").Id}" """))
            let point = sites (references session """"kind":"type","type":"Point" """)
            Assert.Contains(("function", "pointX", "parameterType", ""), point)
            Assert.Contains(("function", "pointX", "node", "fieldAccess"), point)
            Assert.Contains(("function", "origin", "resultType", ""), point)
            Assert.Contains(("function", "origin", "node", "construct"), point)
            let x = references session """"kind":"field","type":"Point","field":"x" """
            Assert.Equal("Point", x.GetProperty("subject").GetProperty("ownerDisplayName").GetString())
            Assert.Equal<Set<string * string * string * string>>(set [ "function", "pointX", "node", "fieldAccess"; "function", "origin", "node", "construct" ], sites x)
            let radius = sites (references session """"kind":"field","type":"Shape","variant":"Circle","field":"radius" """)
            Assert.Equal<Set<string * string * string * string>>(set [ "function", "area", "node", "patternBinding" ], radius)
            let circle = references session """"kind":"variant","type":"Shape","variant":"Circle" """
            Assert.Equal("Circle", circle.GetProperty("subject").GetProperty("displayName").GetString())
            Assert.Equal<Set<string * string * string * string>>(set [ "function", "area", "node", "match" ], sites circle)
            let io = sites (references session """"kind":"effect","effect":"io" """)
            Assert.Equal<Set<string * string * string * string>>(set [ "capability", "Host", "operationEffect", "" ], io)
            let host = sites (references session """"kind":"capability","capability":"Host" """)
            Assert.Contains(("function", "makeHost", "resultType", ""), host)
            Assert.Contains(("function", "makeHost", "node", "capabilityConstruct"), host)
            Assert.Contains(("function", "makeHost", "node", "implementation"), host)
            let step = references session """"kind":"capabilityOperation","capability":"Host","operation":"step" """
            Assert.Equal("Host", step.GetProperty("subject").GetProperty("ownerDisplayName").GetString())
            Assert.Equal<Set<string * string * string * string>>(set [ "function", "makeHost", "node", "implementation" ], sites step)
            let add = references session """"kind":"function","function":"add" """
            Assert.Equal<Set<string * string * string * string>>(set [ "function", "useAdd", "node", "directCall" ], sites add)
            Assert.Equal<Set<string * string * string * string>>(set [ "function", "makeHost", "node", "implementation" ], sites (references session """"kind":"function","function":"stopImpl" """))
            Assert.Equal(0, (references session """"kind":"function","function":"useAdd" """).GetProperty("count").GetInt32())
            // Unknown subjects and malformed selectors.
            Assert.Equal("SemanticQueryFailure", rejection session """{"id":"refs","op":"references","kind":"type","typeId":"missing"}""" |> fst)
            Assert.Equal("ProtocolError", rejection session """{"id":"refs","op":"references","kind":"type","type":"Nothing"}""" |> fst)
            Assert.Equal("ProtocolError", rejection session $"""{{"id":"refs","op":"references","kind":"type","type":"Shape","typeId":"{idText (typeNamed session "Shape").Id}"}}""" |> fst)
            Assert.Equal("ProtocolError", rejection session """{"id":"refs","op":"references","kind":"type"}""" |> fst)
            Assert.Equal("ProtocolError", rejection session """{"id":"refs","op":"references","kind":"module","type":"Shape"}""" |> fst)
            // The session query itself answers the same way.
            match session.References(ReferenceSubject.Effect (effect session).Id) with
            | Ok items -> Assert.Equal(2, items.Length)
            | Error error -> failwithf "%A" error)

    [<Fact>]
    let ``a rejected deletion lists every blocking reference`` () =
        withFixture (fun session _ ->
            let code, text = rejection session (operationsByName [ """{"kind":"deleteType","type":"Point"}""" ])
            Assert.Equal("TypeStillReferenced", code)
            Assert.StartsWith("Repair all references before deleting this type (", text)
            Assert.Contains("blocking issues", text)
            Assert.Contains(" ; ", text)
            let count = (references session """"kind":"type","type":"Point" """).GetProperty("count").GetInt32()
            Assert.True(count >= 3, $"{count}"))

    // ----- B3: proposals and the pending count -----

    [<Fact>]
    let ``status counts pending proposals and proposals lists them with kind, base and summary`` () =
        withFixture (fun session _ ->
            let status () = result session """{"id":"status","op":"status"}"""
            Assert.Equal(0, (status ()).GetProperty("pendingProposalCount").GetInt32())
            let listing () = result session """{"id":"proposals","op":"proposals"}"""
            Assert.Equal(0, (listing ()).GetProperty("count").GetInt32())
            let rename = result session (operationsByName [ """{"kind":"renameType","type":"Point","displayName":"Coordinate"}"""; """{"kind":"deleteFunction","function":"useAdd"}""" ])
            let evolution = result session """{"id":"evolve","op":"simulateAddUnionVariantLikeByName","type":"Shape","sourceVariant":"Dot","displayName":"Blank"}"""
            Assert.Equal(2, (status ()).GetProperty("pendingProposalCount").GetInt32())
            let listed = listing ()
            Assert.Equal(2, listed.GetProperty("count").GetInt32())
            let entries = listed.GetProperty("proposals").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("proposalId").GetString(), item) |> Map.ofSeq
            let renameEntry = entries[rename.GetProperty("proposalId").GetString()]
            Assert.Equal("operations", renameEntry.GetProperty("kind").GetString())
            Assert.True(renameEntry.GetProperty("baseIsCurrent").GetBoolean())
            Assert.Equal(idText session.SnapshotId, renameEntry.GetProperty("baseSnapshotId").GetString())
            Assert.Equal("renameType 'Point' to 'Coordinate'; deleteFunction 'useAdd'", renameEntry.GetProperty("summary").GetString())
            let evolutionEntry = entries[evolution.GetProperty("proposalId").GetString()]
            Assert.Equal("unionVariantEvolution", evolutionEntry.GetProperty("kind").GetString())
            Assert.Equal("addUnionVariantLike 'Blank' on 'Shape' (1 matches repaired)", evolutionEntry.GetProperty("summary").GetString())
            commit session evolution
            Assert.Equal(1, (status ()).GetProperty("pendingProposalCount").GetInt32())
            let remaining = (listing ()).GetProperty("proposals")
            Assert.Equal(1, remaining.GetArrayLength())
            Assert.False(remaining[0].GetProperty("baseIsCurrent").GetBoolean())
            result session $"""{{"id":"reject","op":"reject","proposalId":"{rename.GetProperty("proposalId").GetString()}"}}""" |> ignore
            Assert.Equal(0, (status ()).GetProperty("pendingProposalCount").GetInt32()))

    // ----- B4: ByName parity -----

    [<Fact>]
    let ``simulateOperationsByName renames every kind and adds parameters, fields and effects by name`` () =
        withFixture (fun session path ->
            let useAddCall =
                ValueGraph.collectNodes (fn session "useAdd").Body
                |> List.pick (fun node -> SemanticNodeShape.tryDirectCall node |> Option.map (fun _ -> node.Id))
            let originConstruct =
                ValueGraph.collectNodes (fn session "origin").Body
                |> List.pick (fun node ->
                    match node.Kind with
                    | SemanticNodeKind.ProductConstruct _ -> Some node.Id
                    | _ -> None)
            let originZero =
                ValueGraph.collectNodes (fn session "origin").Body
                |> List.pick (fun node -> if node.Kind = SemanticNodeKind.Int64Literal 0L then Some node.Id else None)
            let renames =
                result session (operationsByName
                    [ """{"kind":"renameType","type":"Point","displayName":"Coordinate"}"""
                      """{"kind":"renameFunction","function":"pointX","displayName":"coordinateX"}"""
                      """{"kind":"renameField","type":"Point","field":"x","displayName":"left"}"""
                      """{"kind":"renameField","type":"Shape","variant":"Circle","field":"radius","displayName":"r"}"""
                      """{"kind":"renameVariant","type":"Shape","variant":"Square","displayName":"Box"}"""
                      """{"kind":"renameParameter","function":"area","parameter":"scale","displayName":"factor"}"""
                      """{"kind":"renameEffect","effect":"io","displayName":"console"}"""
                      """{"kind":"renameCapability","capability":"Host","displayName":"Machine"}"""
                      """{"kind":"renameCapabilityOperation","capability":"Host","operation":"step","displayName":"advance"}"""
                      """{"kind":"renameCapabilityParameter","capability":"Host","operation":"step","parameter":"count","displayName":"amount"}""" ])
            Assert.Equal(10, renames.GetProperty("review").GetProperty("diff").GetProperty("renames").GetArrayLength())
            commit session renames
            Assert.Equal("Coordinate", (typeNamed session "Coordinate").DisplayName)
            Assert.Equal("Machine", (capability session).DisplayName)
            Assert.Equal<string list>([ "advance"; "stop" ], (capability session).Operations |> List.map (fun item -> item.DisplayName) |> List.sort)
            Assert.Equal<string list>([ "amount"; "limit" ], ((capability session).Operations |> List.find (fun item -> item.DisplayName = "advance")).Parameters |> List.map (fun item -> item.DisplayName))
            Assert.Equal("console", (effect session).DisplayName)
            Assert.Equal<string list>([ "shape"; "factor" ], (fn session "area").Parameters |> List.map (fun item -> item.DisplayName))
            // Additions by name: a parameter left missing at the one call, a field planned at the one
            // construction, a required effect, then the obligation resolved and the effect removed again.
            let additions =
                result session (operationsByName
                    [ $"""{{"kind":"addFunctionParameter","function":"add","displayName":"extra","type":{int64Type},"callSites":[{{"kind":"leaveMissing","callNodeId":"{idText useAddCall}"}}]}}"""
                      $"""{{"kind":"addProductField","type":"Coordinate","displayName":"z","fieldType":{int64Type},"constructions":[{{"constructNodeId":"{idText originConstruct}","valueNodeId":"{idText originZero}"}}]}}"""
                      """{"kind":"addRequiredEffect","function":"useAdd","effect":"console"}""" ])
            let diff = additions.GetProperty("review").GetProperty("diff")
            Assert.Equal("extra", diff.GetProperty("parametersAdded").[0].GetProperty("displayName").GetString())
            Assert.Equal("z", diff.GetProperty("productFieldsAdded").[0].GetProperty("displayName").GetString())
            Assert.Equal(1, diff.GetProperty("productFieldsBound").GetArrayLength())
            Assert.Equal("added", diff.GetProperty("requiredEffectsChanged").[0].GetProperty("kind").GetString())
            let obligationId = diff.GetProperty("obligationsCreated").[0].GetProperty("obligationId").GetString()
            Assert.Equal("incomplete", additions.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
            commitIncomplete session additions
            let nine =
                ValueGraph.collectNodes (fn session "useAdd").Body
                |> List.pick (fun node -> if node.Kind = SemanticNodeKind.Int64Literal 9L then Some node.Id else None)
            let repairs =
                result session (operationsByName
                    [ $"""{{"kind":"resolveMissingArgument","obligationId":"{obligationId}","valueNodeId":"{idText nine}"}}"""
                      """{"kind":"removeRequiredEffect","function":"useAdd","effect":"console"}"""
                      """{"kind":"removeProductField","type":"Coordinate","field":"z"}""" ])
            let diff = repairs.GetProperty("review").GetProperty("diff")
            Assert.Equal(obligationId, diff.GetProperty("obligationsResolved").[0].GetProperty("obligationId").GetString())
            Assert.Equal("removed", diff.GetProperty("requiredEffectsChanged").[0].GetProperty("kind").GetString())
            Assert.Equal("z", diff.GetProperty("productFieldsRemoved").[0].GetProperty("displayName").GetString())
            Assert.Equal("semanticallyReady", repairs.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
            commit session repairs
            Assert.Equal<string list>([ "a"; "unused"; "extra" ], (fn session "add").Parameters |> List.map (fun item -> item.DisplayName))
            Assert.Empty((fn session "useAdd").RequiredEffects)
            let run = result session $"""{{"id":"run","op":"runPureByName","function":"useAdd","arguments":[{int64Value 5L}],"expected":{int64Value 5L}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText())
            // Unknown names and unsupported kinds are protocol errors that name the accepted kinds.
            Assert.Contains("Unknown function", rejection session (operationsByName [ """{"kind":"renameFunction","function":"nobody","displayName":"x"}""" ]) |> snd)
            Assert.Contains("renameCapabilityParameter", rejection session (operationsByName [ """{"kind":"frobnicate","function":"add"}""" ]) |> snd)
            Assert.Contains("fieldType", rejection session (operationsByName [ """{"kind":"addProductField","type":"Coordinate","displayName":"w","constructions":[]}""" ]) |> snd)
            (session :> IDisposable).Dispose()
            use reopened = reopen path
            Assert.Equal(3, (reopened.Declarations().Functions |> List.find (fun item -> item.DisplayName = "add")).Parameters.Length))

    [<Fact>]
    let ``exact-ID required-effect, missing-argument and rename-type operations have the documented JSON shapes`` () =
        withFixture (fun session _ ->
            let add = fn session "add"
            let useAdd = fn session "useAdd"
            let io = effect session
            let point = typeNamed session "Point"
            let callNodeId =
                ValueGraph.collectNodes useAdd.Body
                |> List.pick (fun node -> SemanticNodeShape.tryDirectCall node |> Option.map (fun _ -> node.Id))
            let proposal =
                result session (operations
                    [ $"""{{"kind":"addRequiredEffect","functionId":"{idText useAdd.Id}","effectId":"{idText io.Id}"}}"""
                      $"""{{"kind":"renameType","typeId":"{idText point.Id}","displayName":"Coordinate"}}"""
                      $"""{{"kind":"addFunctionParameter","functionId":"{idText add.Id}","displayName":"extra","type":{int64Type},"callSites":[{{"kind":"leaveMissing","callNodeId":"{idText callNodeId}"}}]}}""" ])
            let diff = proposal.GetProperty("review").GetProperty("diff")
            let effectChange = diff.GetProperty("requiredEffectsChanged")[0]
            Assert.Equal("added", effectChange.GetProperty("kind").GetString())
            Assert.Equal(idText useAdd.Id, effectChange.GetProperty("functionId").GetString())
            Assert.Equal(idText io.Id, effectChange.GetProperty("effectId").GetString())
            let rename = diff.GetProperty("renames")[0]
            Assert.Equal("type", rename.GetProperty("kind").GetString())
            Assert.Equal("Point", rename.GetProperty("oldDisplayName").GetString())
            Assert.Equal("Coordinate", rename.GetProperty("newDisplayName").GetString())
            let obligation = diff.GetProperty("obligationsCreated")[0]
            Assert.Equal(idText callNodeId, obligation.GetProperty("callNodeId").GetString())
            let code, _ = rejection session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}"""
            Assert.Equal("BlockingObligations", code)
            commitIncomplete session proposal
            let nine =
                ValueGraph.collectNodes (fn session "useAdd").Body
                |> List.pick (fun node -> if node.Kind = SemanticNodeKind.Int64Literal 9L then Some node.Id else None)
            let repaired =
                result session (operations
                    [ $"""{{"kind":"resolveMissingArgument","obligationId":"{obligation.GetProperty("obligationId").GetString()}","valueNodeId":"{idText nine}"}}"""
                      $"""{{"kind":"removeRequiredEffect","functionId":"{idText useAdd.Id}","effectId":"{idText io.Id}"}}""" ])
            let diff = repaired.GetProperty("review").GetProperty("diff")
            let resolved = diff.GetProperty("obligationsResolved")[0]
            Assert.Equal(idText nine, resolved.GetProperty("valueNodeId").GetString())
            Assert.Equal(idText callNodeId, diff.GetProperty("argumentsAttached").[0].GetProperty("callNodeId").GetString())
            Assert.Equal("removed", diff.GetProperty("requiredEffectsChanged").[0].GetProperty("kind").GetString())
            commit session repaired
            Assert.Equal("ObligationNotOpen", rejection session (operations [ $"""{{"kind":"resolveMissingArgument","obligationId":"{obligation.GetProperty("obligationId").GetString()}","valueNodeId":"{idText nine}"}}""" ]) |> fst)
            Assert.Equal("FunctionRequiredEffectNotPresent", rejection session (operations [ $"""{{"kind":"removeRequiredEffect","functionId":"{idText useAdd.Id}","effectId":"{idText io.Id}"}}""" ]) |> fst)
            Assert.Equal("EffectNotFound", rejection session (operations [ $"""{{"kind":"addRequiredEffect","functionId":"{idText useAdd.Id}","effectId":"missing"}}""" ]) |> fst))

    [<Fact>]
    let ``parameter and result type migrations by name resolve the target, its parameter and every caller`` () =
        withFixture (fun session _ ->
            // `add(a: Int64, unused)` becomes `add(a: String, unused)` returning the text's length.
            let stringLength value = $"""{{"kind":"stringLength","value":{value}}}"""
            let int64ToString value = $"""{{"kind":"int64ToString","value":{value}}}"""
            let migration =
                result session
                    $"""{{"id":"migrate","op":"simulateParameterTypeMigrationByName","function":"add","parameter":"a","newParameterType":{stringType},"targetReplacement":{stringLength (parameter "a")},"callerRewrites":[{{"function":"useAdd","replacement":{call "add" [ "a", int64ToString (parameter "v"); "unused", literal 9 ]}}}]}}"""
            Assert.Equal("parameterTypeMigration", migration.GetProperty("review").GetProperty("kind").GetString())
            Assert.Equal("string", migration.GetProperty("review").GetProperty("diff").GetProperty("parameterChange").GetProperty("newType").GetProperty("primitive").GetString())
            commit session migration
            Assert.Equal(TypeRef.Primitive PrimitiveType.String, (fn session "add").Parameters.Head.Type)
            let run = result session $"""{{"id":"run","op":"runPureByName","function":"useAdd","arguments":[{int64Value 12345L}],"expected":{int64Value 5L}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText())
            // `add` then returns the text itself; `useAdd` keeps returning its length.
            let resultMigration =
                result session
                    $"""{{"id":"result","op":"simulateResultTypeMigrationByName","function":"add","newResultType":{stringType},"targetReplacement":{parameter "a"},"callerRewrites":[{{"function":"useAdd","replacement":{stringLength (call "add" [ "a", int64ToString (parameter "v"); "unused", literal 9 ])}}}]}}"""
            Assert.Equal("resultTypeMigration", resultMigration.GetProperty("review").GetProperty("kind").GetString())
            commit session resultMigration
            Assert.Equal(TypeRef.Primitive PrimitiveType.String, (fn session "add").ResultType)
            let run = result session $"""{{"id":"run","op":"runPureByName","function":"add","arguments":[{stringValue "abc"},{int64Value 1L}],"expected":{stringValue "abc"}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText())
            let code, text = rejection session $"""{{"id":"bad","op":"simulateParameterTypeMigrationByName","function":"add","parameter":"nobody","newParameterType":{stringType},"targetReplacement":{parameter "a"},"callerRewrites":[]}}"""
            Assert.Equal("ProtocolError", code)
            Assert.Contains("Unknown parameter on add", text))

    [<Fact>]
    let ``verifyChangeByName resolves case functions and values by name against the committed and candidate snapshots`` () =
        withFixture (fun session _ ->
            let proposal = result session $"""{{"id":"edit","op":"simulateReplaceBodyByName","function":"add","replacement":{parameter "unused"}}}"""
            let verified =
                result session
                    $"""{{"id":"verify","op":"verifyChangeByName","proposalId":"{proposal.GetProperty("proposalId").GetString()}","cases":[{{"label":"changed","function":"add","arguments":[{int64Value 1L},{int64Value 2L}],"expectedCandidate":{int64Value 2L}}},{{"label":"caller","function":"useAdd","arguments":[{int64Value 7L}]}}]}}"""
            let cases = verified.GetProperty("evidence").GetProperty("cases")
            Assert.Equal("intended-change-tested", cases[0].GetProperty("status").GetString())
            Assert.Equal("add", cases[0].GetProperty("function").GetString())
            Assert.Equal(idText (fn session "add").Id, cases[0].GetProperty("functionId").GetString())
            Assert.Equal("counterexample", cases[1].GetProperty("status").GetString())
            Assert.Equal("9", cases[1].GetProperty("candidate").GetProperty("actual").GetProperty("value").GetString())
            Assert.Equal("ProtocolError", rejection session $"""{{"id":"verify","op":"verifyChangeByName","proposalId":"{proposal.GetProperty("proposalId").GetString()}","cases":[{{"label":"x","functionId":"{idText (fn session "add").Id}","arguments":[]}}]}}""" |> fst)
            // A created type is visible to the candidate expectation of a name-resolved case.
            let created =
                result session
                    $"""{{"id":"wrap","op":"simulateCreateTypeByName","exported":true,"displayName":"Wrapper","typeKind":{{"kind":"product","fields":[{{"displayName":"value","type":{int64Type}}}]}}}}"""
            commit session created
            define session "wrap" [ "n", int64Type ] (named "Wrapper") [] $"""{{"kind":"productConstruct","type":"Wrapper","fields":[{{"field":"value","value":{parameter "n"}}}]}}"""
            let edit = result session $"""{{"id":"edit","op":"simulateReplaceBodyByName","function":"wrap","replacement":{{"kind":"productConstruct","type":"Wrapper","fields":[{{"field":"value","value":{literal 1}}}]}}}}"""
            let verified =
                result session
                    $"""{{"id":"verify","op":"verifyChangeByName","proposalId":"{edit.GetProperty("proposalId").GetString()}","cases":[{{"label":"wrapped","function":"wrap","arguments":[{int64Value 5L}],"expectedCandidate":{{"kind":"product","type":"Wrapper","fields":[{{"field":"value","value":{int64Value 1L}}}]}}}}]}}"""
            Assert.Equal("intended-change-tested", verified.GetProperty("evidence").GetProperty("cases").[0].GetProperty("status").GetString()))

    // ----- B5: Union variant evolution by name -----

    [<Fact>]
    let ``simulateAddUnionVariantLikeByName names the Union, the source variant and additional field types`` () =
        withFixture (fun session _ ->
            let proposal =
                result session
                    $"""{{"id":"ring","op":"simulateAddUnionVariantLikeByName","type":"Shape","sourceVariant":"Circle","displayName":"Ring","additionalFields":[{{"displayName":"inner","type":{int64Type}}}]}}"""
            let review = proposal.GetProperty("review")
            Assert.Equal("unionVariantEvolution", review.GetProperty("kind").GetString())
            Assert.Equal("Ring", review.GetProperty("addedVariantDisplayName").GetString())
            Assert.Equal(1, review.GetProperty("repairedMatchCount").GetInt32())
            let variants = review.GetProperty("updatedType").GetProperty("typeKind").GetProperty("variants")
            Assert.Equal(4, variants.GetArrayLength())
            Assert.Equal(2, variants[3].GetProperty("fields").GetArrayLength())
            commit session proposal
            let shape = typeNamed session "Shape"
            match shape.Kind with
            | SemanticTypeKind.Union variants -> Assert.Equal<string list>([ "Circle"; "Dot"; "Ring"; "Square" ], variants |> List.map (fun item -> item.DisplayName) |> List.sort)
            | SemanticTypeKind.Product _ -> failwith "Shape changed kind."
            let run =
                result session
                    $"""{{"id":"run","op":"runPureByName","function":"area","arguments":[{{"kind":"union","type":"Shape","variant":"Ring","fields":[{{"field":"radius","value":{int64Value 3L}}},{{"field":"inner","value":{int64Value 1L}}}]}},{int64Value 1L}],"expected":{int64Value 3L}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText())
            Assert.Contains("Unknown variant on Shape", rejection session """{"id":"bad","op":"simulateAddUnionVariantLikeByName","type":"Shape","sourceVariant":"Nothing","displayName":"X"}""" |> snd))

    [<Fact>]
    let ``failureType is retired on new capability operations`` () =
        withFixture (fun session _ ->
            let failing = $"""{{"displayName":"read","parameters":[],"resultType":{int64Type},"failureType":{stringType},"effect":"io"}}"""
            for line in
                [ $"""{{"id":"c1","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Reader","operations":[{failing}]}}"""
                  operationsByName [ $"""{{"kind":"addCapabilityOperation","capability":"Host","displayName":"read","parameters":[],"resultType":{int64Type},"failureType":{stringType},"effect":"io","implementations":[]}}""" ] ] do
                let code, message = rejection session line
                Assert.Equal("ProtocolError", code)
                Assert.Contains("failureType is retired", message)
            // Absent or null keeps the old request shape.
            publish
                session
                $"""{{"id":"c2","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Reader","operations":[{{"displayName":"read","parameters":[],"resultType":{int64Type},"failureType":null,"effect":"io"}}]}}"""
            |> ignore)
