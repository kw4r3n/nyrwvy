namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Nyrwvy.Core
open Xunit

module ZigHostStructuralMigrationsParityTests =
    open ZigHostParityTests
    let private sid<'T> name = StableId.createForTesting<'T> ("sm-" + name)
    let private intT = TypeRef.Primitive PrimitiveType.Int64
    let private stringT = TypeRef.Primitive PrimitiveType.String
    let private intJson = """{"kind":"primitive","primitive":"int64"}"""
    let private stringJson = """{"kind":"primitive","primitive":"string"}"""
    let private literal = """{"kind":"int64Literal","value":9}"""
    let private fixture () =
        let point: SemanticType =
            { Id = sid<TypeTag> "point"; DisplayName = "Point"; Exported = true; TypeParameters = []
              Kind = SemanticTypeKind.Product [{ Id = sid<FieldTag> "x"; DisplayName = "x"; Type = intT }] }
        let choice: SemanticType =
            { Id = sid<TypeTag> "choice"; DisplayName = "Choice"; Exported = true; TypeParameters = []
              Kind = SemanticTypeKind.Union [ for name in ["A"; "B"] -> { Id = sid<VariantTag> name; DisplayName = name; Fields = [{ Id = sid<FieldTag> (name + "-value"); DisplayName = "value"; Type = intT }] } ] }
        let effect: SemanticEffect = { Id = sid<EffectTag> "io"; DisplayName = "io"; Exported = true }
        let operation: SemanticCapabilityOperation =
            { Id = sid<CapabilityOperationTag> "read"; DisplayName = "read"; Parameters = [{ Id = sid<CapabilityParameterTag> "input"; DisplayName = "input"; Type = intT }]
              ResultType = intT; FailureType = None; EffectId = effect.Id; MayWait = false }
        let capability: SemanticCapability =
            { Id = sid<CapabilityTag> "cap"; DisplayName = "Values"; Exported = true; TaskBound = false; Operations = [operation] }
        let node name kind : SemanticNode = { Id = sid<GraphNodeTag> name; Kind = kind }
        let paramRef fnName pname = SemanticNodeKind.ParameterReference(sid<ParameterTag> (fnName + "-" + pname))
        let makeFn name parameters result effects nodes resultNode : SemanticFunction =
            { Id = sid<FunctionTag> name; DisplayName = name; Exported = true; MustNotWait = false; TypeParameters = []
              Parameters = parameters |> List.map (fun (pname, ptype) -> { Id = sid<ParameterTag> (name + "-" + pname); DisplayName = pname; Type = ptype })
              ResultType = result; RequiredEffects = effects; Body = { Nodes = nodes; ResultNodeId = resultNode } }
        let make =
            let p = node "make-input" (paramRef "make" "input")
            let c = node "make-body" (SemanticNodeKind.ProductConstruct(point.Id, [], [{ TargetFieldId = sid<FieldTag> "x"; ValueNodeId = p.Id }]))
            makeFn "make" ["input", intT] (TypeRef.Named(point.Id, [])) [] [p; c] c.Id
        let readPoint =
            let p = node "readPoint-point" (paramRef "readPoint" "point")
            let a = node "readPoint-body" (SemanticNodeKind.FieldAccess(p.Id, sid<FieldTag> "x"))
            makeFn "readPoint" ["point", TypeRef.Named(point.Id, [])] intT [] [p; a] a.Id
        let implementation =
            let p = node "implementation-input" (paramRef "implementation" "input")
            makeFn "implementation" ["input", intT] intT [] [p] p.Id
        let impl = implementation
        let construct =
            let c = node "construct-body" (SemanticNodeKind.CapabilityConstruct(capability.Id, [operation.Id, impl.Id]))
            makeFn "construct" [] (TypeRef.Capability capability.Id) [] [c] c.Id
        let invoke =
            let p0 = node "invoke-values" (paramRef "invoke" "values")
            let p1 = node "invoke-input" (paramRef "invoke" "input")
            let v = node "invoke-body" (SemanticNodeKind.CapabilityInvoke(p0.Id, operation.Id, [{ TargetCapabilityParameterId = operation.Parameters.Head.Id; ValueNodeId = p1.Id }]))
            makeFn "invoke" ["values", TypeRef.Capability capability.Id; "input", intT] intT [effect.Id] [p0; p1; v] v.Id
        let reference =
            let r = node "reference-body" (SemanticNodeKind.FunctionReference impl.Id)
            makeFn "reference" [] (TypeRef.Function([intT], intT)) [] [r] r.Id
        let matchFn =
            let arm variantName =
                let bindingId = sid<PatternBindingTag> ("match-" + variantName)
                let refNode = node ("match-" + variantName + "-ref") (SemanticNodeKind.PatternReference bindingId)
                { VariantIds = [sid<VariantTag> variantName]
                  FieldBindings = [{ SourceFieldId = sid<FieldTag> (variantName + "-value"); Binding = { Id = bindingId; DisplayName = None; Type = intT } }]
                  Body = { Nodes = [refNode]; ResultNodeId = refNode.Id } }
            let p = node "match-choice" (paramRef "match" "choice")
            let m = node "match-body" (SemanticNodeKind.MatchUnion(p.Id, [arm "A"; arm "B"]))
            makeFn "match" ["choice", TypeRef.Named(choice.Id, [])] intT [] [p; m] m.Id
        let shared =
            let p = node "shared-choice" (paramRef "shared" "choice")
            let lit = node "shared-lit" (SemanticNodeKind.Int64Literal 1L)
            let arm = { VariantIds = [sid<VariantTag> "A"; sid<VariantTag> "B"]; FieldBindings = []; Body = { Nodes = [lit]; ResultNodeId = lit.Id } }
            let m = node "shared-body" (SemanticNodeKind.MatchUnion(p.Id, [arm]))
            makeFn "shared" ["choice", TypeRef.Named(choice.Id, [])] intT [] [p; m] m.Id
        { Snapshot.empty (sid<SnapshotTag> "base") with Types = [point; choice]; Effects = [effect]; Capabilities = [capability]; Functions = [make; readPoint; implementation; construct; invoke; reference; matchFn; shared] }

    let private q (s: string) = System.Text.Json.JsonSerializer.Serialize s
    let private typeJson byName typ =
        match typ with
        | TypeRef.Capability _ -> if byName then """{"kind":"capability","name":"Values"}""" else """{"kind":"capability","capabilityId":"sm-cap"}"""
        | _ -> stringJson
    let private request (snapshot: Snapshot) byName kind =
        let find name = snapshot.Functions |> List.find (fun f -> f.DisplayName = name)
        let functionKey name = if byName then "\"function\":" + q name else "\"functionId\":" + q (StableId.value (find name).Id)
        let parameter (fn: SemanticFunction) index = if byName then $"""{{"kind":"parameter","name":{q fn.Parameters[index].DisplayName}}}""" else $"""{{"kind":"parameter","parameterId":{q (StableId.value fn.Parameters[index].Id)}}}"""
        let suffix = if byName then "ByName" else ""
        match kind with
        | "field" ->
            let target = if byName then "\"type\":\"Point\",\"field\":\"x\"" else "\"fieldId\":\"sm-x\""
            let construction = if byName then """{"kind":"productConstruct","type":"Point","fields":[{"field":"x","value":{"kind":"stringLiteral","value":"new"}}]}""" else """{"kind":"productConstruct","typeId":"sm-point","fields":[{"fieldId":"sm-x","value":{"kind":"stringLiteral","value":"new"}}]}"""
            $"""{{"id":"sm","op":"simulateProductFieldTypeMigration{suffix}",{target},"newFieldType":{stringJson},"functionRewrites":[{{{functionKey "readPoint"},"replacement":{literal}}},{{{functionKey "make"},"replacement":{construction}}}]}}"""
        | "capability" ->
            let target = if byName then "\"capability\":\"Values\",\"operation\":\"read\"" else "\"operationId\":\"sm-read\""
            let operationParameter = if byName then "\"parameter\":\"input\"" else "\"parameterId\":\"sm-input\""
            let impl = find "implementation"
            let invocation = find "invoke"
            let implRef = if byName then "\"function\":\"implementation\"" else "\"functionId\":" + q (StableId.value impl.Id)
            let construct = if byName then $"""{{"kind":"capabilityConstruct","capability":"Values","implementations":[{{"operation":"read",{implRef}}}]}}""" else $"""{{"kind":"capabilityConstruct","capabilityId":"sm-cap","implementations":[{{"operationId":"sm-read",{implRef}}}]}}"""
            let invoke = if byName then $"""{{"kind":"capabilityInvoke","capabilityValue":{parameter invocation 0},"capability":"Values","operation":"read","arguments":[{{"parameter":"input","value":{parameter invocation 1}}}]}}""" else $"""{{"kind":"capabilityInvoke","capabilityValue":{parameter invocation 0},"operationId":"sm-read","arguments":[{{"capabilityParameterId":"sm-input","value":{parameter invocation 1}}}]}}"""
            let repair name result replacement =
                let f = find name
                let ps = f.Parameters |> List.map (fun p -> let key = if byName then "\"parameter\":" + q p.DisplayName else "\"parameterId\":" + q (StableId.value p.Id) in $"""{{{key},"type":{typeJson byName p.Type}}}""") |> String.concat ","
                $"""{{{functionKey name},"parameterTypes":[{ps}],"resultType":{result},"replacement":{replacement}}}"""
            let capT = if byName then """{"kind":"capability","name":"Values"}""" else """{"kind":"capability","capabilityId":"sm-cap"}"""
            let repairs = [repair "reference" $"""{{"kind":"function","parameters":[{stringJson}],"resultType":{stringJson}}}""" $"""{{"kind":"functionReference",{implRef}}}"""; repair "invoke" stringJson invoke; repair "construct" capT construct; repair "implementation" stringJson (parameter impl 0)] |> String.concat ","
            $"""{{"id":"sm","op":"simulateCapabilityOperationSignatureMigration{suffix}",{target},"parameterTypes":[{{{operationParameter},"type":{stringJson}}}],"resultType":{stringJson},"functionRepairs":[{repairs}]}}"""
        | _ ->
            let target = if byName then "\"type\":\"Choice\",\"sourceVariant\":\"A\"" else "\"typeId\":\"sm-choice\",\"sourceVariantId\":\"sm-A\""
            $"""{{"id":"sm","op":"simulateAddUnionVariantLike{suffix}",{target},"displayName":"Added","additionalFields":[{{"displayName":"extra","type":{stringJson}}}]}}"""

    let private mutate (line: string) action =
        let root = JsonNode.Parse line
        action root
        root.ToJsonString()

    [<Fact; Trait("Suite", "Extended")>]
    let ``Structural migration reviews match both surfaces and retain semantic rewrite order`` () =
        withDirectory (fun dir ->
            let snapshot = fixture ()
            let path = Path.Combine(dir, "structural.json")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let lines = [ for byName in [false; true] do for kind in ["field"; "capability"; "union"] do let line = request snapshot byName kind in yield line; yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full"); yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ]
            let actual = seededPair "structural-reviews" path lines
            for line in actual do use parsed = parseDocument line in Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Structural failures preserve diagnostics and subsequent allocation streams`` () =
        withDirectory (fun dir ->
            let snapshot = fixture ()
            let path = Path.Combine(dir, "rejected.json")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let lines =
                [ for kind in ["field"; "capability"; "union"] do
                    for byName in [false; true] do
                        let good = request snapshot byName kind
                        let invalid =
                            [ yield mutate good (fun root -> root["expectedSnapshotId"] <- JsonValue.Create "old")
                              yield good.Replace("\"id\":\"sm\"", "\"id\":\"sm\",\"extra\":0", StringComparison.Ordinal)
                              if kind = "union" then
                                  yield mutate good (fun root -> root["displayName"] <- JsonValue.Create "A")
                                  yield mutate good (fun root -> root["displayName"] <- JsonValue.Create " ")
                                  yield mutate good (fun root -> root["additionalFields"].[0].["displayName"] <- JsonValue.Create "value")
                              else
                                  let key = if kind = "field" then "functionRewrites" else "functionRepairs"
                                  yield mutate good (fun root -> root[key] <- JsonNode.Parse "[]")
                                  yield mutate good (fun root -> root[key].AsArray().Add(root[key].[0].DeepClone()))
                                  yield mutate good (fun root -> root[key].[0].["replacement"] <- JsonNode.Parse """{"kind":"boolLiteral","value":true}""")
                                  yield mutate good (fun root -> root[key].[if kind = "field" then 1 else 3].["replacement"] <- JsonNode.Parse """{"kind":"boolLiteral","value":true}""")
                                  yield mutate good (fun root -> root[key].[0].["replacement"] <- JsonNode.Parse """{"kind":"parameterSlot","slot":99}""") ]
                        for bad in invalid do yield bad; yield good ]
            let actual = seededPair "structural-negative" path lines
            for index, line in List.indexed actual do use parsed = parseDocument line in Assert.Equal(index % 2 = 1, parsed.RootElement.GetProperty("ok").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Structural proposals retain pages views lifecycle and all persistence encodings`` () =
        withDirectory (fun dir ->
            for kind in ["field"; "capability"; "union"] do
                for records, resolved in [false,false; true,false; false,true; true,true] do
                    let case = Path.Combine(dir, kind + string records + string resolved)
                    let input = Path.Combine(case, "input")
                    Directory.CreateDirectory input |> ignore
                    let source = Path.Combine(input, if records then "structural.nyrec" else "structural.json")
                    let snapshot = if records then recordFixture "structural" (fixture ()) else fixture ()
                    let dependency = Snapshot.empty (sid<SnapshotTag> "018bcfe5-6700-7000-8000-000000000091")
                    let json = if resolved then SnapshotCodec.serializeResolved dependency snapshot |> unwrap else SnapshotCodec.serialize snapshot |> unwrap
                    File.WriteAllText(source, if records then RecordCodec.ofJson json else json)
                    if resolved then File.WriteAllText(source + ".dependency.json", (memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json").ToJsonString())
                    for suffix in [".backends.json"; ".previous"; ".dependency.json.previous"] do File.WriteAllText(source + suffix, "sentinel")
                    let ok reply = use parsed = parseDocument reply in Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                    let proposal reply = ok reply; use parsed = parseDocument reply in parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                    let operation op id = $"""{{"id":"life","op":"{op}","proposalId":{q id}}}"""
                    let migration = request snapshot true kind
                    let zig = seededWritablePair "structural-lifecycle" source case (fun path send ->
                        let current = send migration |> proposal
                        let stale = send migration |> proposal
                        let inspect id =
                            send (operation "viewProposal" id) |> ok
                            for structured in ["false"; "true"] do
                                let pointer = if kind = "union" then "/review/rewrittenFunctions" else "/review/diff/bodyRewrites"
                                send $"""{{"id":"page","op":"readProposal","proposalId":{q id},"structuredReview":{structured},"path":"{pointer}","offset":0,"limit":1}}""" |> ok
                            send """{"id":"list","op":"proposals"}""" |> ok
                        inspect current
                        send (operation "commit" current) |> ok
                        inspect stale
                        Assert.Contains("StaleBaseSnapshot", send (operation "commit" stale))
                        send (operation "reject" stale) |> ok
                        Assert.Contains("ProposalNotFound", send (operation "readProposal" stale))
                        let target = if kind = "field" then "readPoint" elif kind = "capability" then "implementation" else "match"
                        let replacement = if kind = "capability" then """{"kind":"stringLiteral","value":"next"}""" else literal
                        let next = send $"""{{"id":"replace","op":"simulateReplaceBodyByName","function":"{target}","replacement":{replacement}}}"""
                        do
                            let id = next |> proposal
                            let before = File.ReadAllBytes path
                            File.WriteAllBytes(path, Array.append before [|byte ' '|])
                            Assert.Contains("ExternalModification", send (operation "commit" id))
                            Assert.Contains("SessionInvalidated", send migration)
                            File.WriteAllBytes(path, before))
                    let path = Path.Combine(zig, Path.GetFileName source)
                    let lines = ["""{"id":"status","op":"status"}"""; """{"id":"types","op":"declarations"}"""]
                    let code, errors, actual = run ["open"; "--snapshot"; path; "--read-only"] lines
                    Assert.True((code = 0), errors)
                    for line in actual do ok line)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Union evolution clones nested pattern frames and instantiates generic extra fields`` () =
        withDirectory (fun dir ->
            let t = sid<TypeParameterTag> "T"
            let original = fixture ()
            let snapshot =
                { original with
                    Types = original.Types |> List.map (fun typ ->
                        if typ.DisplayName <> "Choice" then typ else
                            { typ with TypeParameters = [t, "T"]
                                       Kind = match typ.Kind with SemanticTypeKind.Union variants -> SemanticTypeKind.Union(variants |> List.map (fun v -> { v with Fields = v.Fields |> List.map (fun f -> { f with Type = TypeRef.TypeParameter t }) })) | _ -> typ.Kind })
                    Functions = original.Functions |> List.map (fun f ->
                        if f.DisplayName <> "match" && f.DisplayName <> "shared" then f else
                            let nodes =
                                f.Body.Nodes |> List.map (fun n ->
                                    match n.Kind with
                                    | SemanticNodeKind.MatchUnion(input, arms) when f.DisplayName = "match" ->
                                        let arms = arms |> List.mapi (fun index arm ->
                                            if index <> 0 then arm else
                                                let binding: PatternBinding = { Id = sid<PatternBindingTag> "inner-reference"; DisplayName = Some "inner"; Type = intT }
                                                let value: SemanticNode = { Id = sid<GraphNodeTag> "inner-reference"; Kind = SemanticNodeKind.PatternReference binding.Id }
                                                let list: SemanticNode = { Id = sid<GraphNodeTag> "inner-list"; Kind = SemanticNodeKind.ListConstruct(intT, [value.Id]) }
                                                let length: SemanticNode = { Id = sid<GraphNodeTag> "inner-length"; Kind = SemanticNodeKind.ListLength list.Id }
                                                let unused: SemanticNode = { Id = StableId.createForTesting<GraphNodeTag> (StableId.value f.Parameters.Head.Id); Kind = SemanticNodeKind.ParameterReference f.Parameters.Head.Id }
                                                let bound: SemanticNode = { Id = sid<GraphNodeTag> "bind"; Kind = SemanticNodeKind.Bind(arm.Body.ResultNodeId, { ValueBinding = binding; Body = { Nodes = [value; list; length; unused]; ResultNodeId = length.Id } }) }
                                                { arm with Body = { Nodes = arm.Body.Nodes @ [bound]; ResultNodeId = bound.Id } })
                                        { n with Kind = SemanticNodeKind.MatchUnion(input, arms) }
                                    | _ -> n)
                            let body = { f.Body with Nodes = nodes }
                            { f with Parameters = f.Parameters |> List.map (fun p -> { p with Type = TypeRef.Named(sid<TypeTag> "choice", [intT]) }); Body = body }) }
            Assert.Empty(SnapshotValidator.validate snapshot)
            let path = Path.Combine(dir, "generic-union.json")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let lines =
                [ for byName in [false;true] do
                    let line = request snapshot byName "union" |> fun line -> mutate line (fun root ->
                        root["additionalFields"].[0].["type"] <- JsonNode.Parse(if byName then $"""{{"kind":"typeParameter","name":{q (StableId.value t)}}}""" else $"""{{"kind":"typeParameter","typeParameterId":{q (StableId.value t)}}}"""))
                    yield line
                    yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ]
            let actual = seededPair "structural-generic-union" path lines
            for line in actual do use parsed = parseDocument line in Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    [<Fact; Trait("Suite", "Extended")>]
    let ``All structural migrations preserve imported declaration policy and rejected allocation streams`` () =
        withDirectory (fun dir ->
            let snapshot = fixture ()
            let dependency = { Snapshot.empty (sid<SnapshotTag> "dependency") with Types = snapshot.Types; Effects = snapshot.Effects; Capabilities = snapshot.Capabilities }
            let local = { snapshot with Types = []; Effects = []; Capabilities = [] }
            let path = Path.Combine(dir, "dependent.json")
            File.WriteAllText(path, SnapshotCodec.serializeResolved dependency local |> unwrap)
            File.WriteAllText(path + ".dependency.json", (memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json").ToJsonString())
            let create = $"""{{"id":"good","op":"simulateCreateFunctionByName","displayName":"new","parameters":[],"resultType":{intJson},"requiredEffects":[],"body":{literal}}}"""
            let lines = [ for byName in [false;true] do for kind in ["field";"capability";"union"] do yield request snapshot byName kind; yield create ]
            let actual = seededPair "structural-imported" path lines
            for index, line in List.indexed actual do
                if index % 2 = 0 then Assert.Contains("ImportedDeclarationModified", line)
                else use parsed = parseDocument line in Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Structural proposal summaries resolve current names while reviews remain immutable`` () =
        withDirectory (fun dir ->
            for kind in ["field"; "capability"; "union"] do
                let case = Path.Combine(dir, kind)
                let input = Path.Combine(case, "input")
                Directory.CreateDirectory input |> ignore
                let source = Path.Combine(input, "summary.json")
                let snapshot = fixture ()
                File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
                let ok reply = use parsed = parseDocument reply in Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                let proposal reply = ok reply; use parsed = parseDocument reply in parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                let operations =
                    if kind = "field" then """[{"kind":"renameType","type":"Point","displayName":"RenamedPoint"},{"kind":"renameField","type":"Point","field":"x","displayName":"renamedX"}]"""
                    elif kind = "capability" then """[{"kind":"renameCapability","capability":"Values","displayName":"RenamedValues"},{"kind":"renameCapabilityOperation","capability":"Values","operation":"read","displayName":"renamedRead"}]"""
                    else """[{"kind":"renameType","type":"Choice","displayName":"RenamedChoice"}]"""
                seededWritablePair "structural-summary" source case (fun _ send ->
                    let pending = send (request snapshot true kind) |> proposal
                    let original = send $"""{{"id":"review","op":"readProposal","proposalId":{q pending}}}"""
                    let rename = send $"""{{"id":"rename","op":"simulateOperationsByName","operations":{operations}}}""" |> proposal
                    send $"""{{"id":"commit","op":"commit","proposalId":{q rename}}}""" |> ok
                    let listing = send """{"id":"list","op":"proposals"}"""
                    ok listing
                    Assert.Contains((if kind = "field" then "RenamedPoint" elif kind = "capability" then "RenamedValues" else "Choice"), listing)
                    Assert.DoesNotContain((if kind = "field" then "field \\u0027x\\u0027" elif kind = "capability" then "renamedRead" else "RenamedChoice"), listing)
                    let after = send $"""{{"id":"review","op":"readProposal","proposalId":{q pending}}}"""
                    let reviewed reply =
                        use parsed = parseDocument reply
                        parsed.RootElement.GetProperty("result").GetProperty("value").GetRawText()
                    Assert.Equal(reviewed original, reviewed after)) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``By name structural declaration types resolve names recursively without leaking into exact requests`` () =
        withDirectory (fun dir ->
            let snapshot = fixture ()
            let path = Path.Combine(dir, "symbolic-types.json")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let impl = snapshot.Functions |> List.find (fun f -> f.DisplayName = "implementation")
            let make byName kind shape =
                let choice = if byName then """{"kind":"named","name":"Choice"}""" else """{"kind":"named","typeId":"sm-choice"}"""
                let capability = if byName then """{"kind":"capability","name":"Values"}""" else """{"kind":"capability","capabilityId":"sm-cap"}"""
                let functionType = $"""{{"kind":"function","parameters":[{choice}],"resultType":{capability}}}"""
                let typ = if shape = "named" then choice elif shape = "capability" then capability else $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{functionType}]}}"""
                let line = (request snapshot byName kind).Replace(stringJson, typ, StringComparison.Ordinal)
                if kind <> "field" then
                    if kind = "union" then mutate line (fun root -> root["additionalFields"].[0].["type"] <- JsonNode.Parse typ) else line
                else
                    let value =
                        if shape = "named" then
                            if byName then $"""{{"kind":"unionConstruct","type":"Choice","variant":"A","fields":[{{"field":"value","value":{literal}}}]}}"""
                            else $"""{{"kind":"unionConstruct","variantId":"sm-A","fields":[{{"fieldId":"sm-A-value","value":{literal}}}]}}"""
                        elif shape = "capability" then
                            if byName then """{"kind":"capabilityConstruct","capability":"Values","implementations":[{"operation":"read","function":"implementation"}]}"""
                            else $"""{{"kind":"capabilityConstruct","capabilityId":"sm-cap","implementations":[{{"operationId":"sm-read","functionId":{q (StableId.value impl.Id)}}}]}}"""
                        else $"""{{"kind":"optionNone","itemType":{functionType}}}"""
                    mutate line (fun root -> root["functionRewrites"].[1].["replacement"].["fields"].[0].["value"] <- JsonNode.Parse value)
            let lines =
                [ for kind in ["field"; "capability"; "union"] do
                    for shape in ["named"; "capability"; "nested"] do
                        // Every symbolic request is followed by the equivalent exact surface.
                        for byName in [true;false] do
                            let line = make byName kind shape
                            yield line
                            yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ]
            let actual = seededPair "structural-symbolic-types" path lines
            for line in actual do use parsed = parseDocument line in Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Release users migrate with their operation and union evolution keeps cloned obligations within a node cap`` () =
        withDirectory (fun dir ->
            let write name (json: string) =
                let path = Path.Combine(dir, name, "snapshot.json")
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, json)
                path
            let ok (reply: string) =
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
            let primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
            let result value = sprintf """{"kind":"builtinGeneric","generic":"result","arguments":[%s,%s]}""" value (primitive "string")
            let operation name (output: string) = sprintf """{"id":"%s","displayName":"%s","parameters":[{"id":"%s-v","displayName":"value","type":%s}],"resultType":%s,"effectId":"io"}""" name name name (primitive "string") output
            let body = sprintf """{"id":"body","displayName":"body","parameters":[{"id":"body-h","displayName":"handle","type":%s}],"resultType":%s,"requiredEffects":[],"body":{"nodes":[{"id":"b1","kind":"parameterReference","parameterId":"body-h"},{"id":"b2","kind":"resultOk","errorType":%s,"valueNodeId":"b1"}],"resultNodeId":"b2"}}""" (primitive "string") (result (primitive "string")) (primitive "string")
            // `user` invokes only `open`; it reaches `close` solely as its useResource release.
            let user = sprintf """{"id":"user","displayName":"user","parameters":[{"id":"user-f","displayName":"files","type":{"kind":"capability","capabilityId":"files"}}],"resultType":%s,"requiredEffects":["io"],"body":{"nodes":[{"id":"u1","kind":"parameterReference","parameterId":"user-f"},{"id":"u2","kind":"stringLiteral","value":"x"},{"id":"u3","kind":"capabilityInvoke","capabilityValueNodeId":"u1","operationId":"open","arguments":[{"targetCapabilityParameterId":"open-v","valueNodeId":"u2"}]},{"id":"u4","kind":"parameterReference","parameterId":"user-f"},{"id":"u5","kind":"useResource","acquireNodeId":"u3","releaseCapabilityNodeId":"u4","releaseOperationId":"close","bodyFunctionId":"body"}],"resultNodeId":"u5"}}""" (result (primitive "string"))
            let resource = write "resource" (sprintf """{"schemaVersion":24,"snapshotId":"s","types":[],"effects":[{"id":"io","displayName":"IO"}],"capabilities":[{"id":"files","displayName":"Files","operations":[%s,%s]}],"functions":[%s,%s],"obligations":[]}""" (operation "open" (result (primitive "string"))) (operation "close" (primitive "unit")) body user)
            let migrate repairs = sprintf """{"id":"sm","op":"simulateCapabilityOperationSignatureMigration","operationId":"close","parameterTypes":[{"parameterId":"close-v","type":%s}],"resultType":%s,"functionRepairs":[%s]}""" (primitive "string") (result (primitive "unit")) repairs
            let repair = sprintf """{"functionId":"user","parameterTypes":[{"parameterId":"user-f","type":{"kind":"capability","capabilityId":"files"}}],"resultType":%s,"replacement":{"kind":"resultOk","errorType":%s,"value":{"kind":"stringLiteral","value":"y"}}}""" (result (primitive "string")) (primitive "string")
            match seededPair "structural-release-users" resource [ migrate repair; migrate "" ] with
            | [ repaired; unrepaired ] ->
                ok repaired
                Assert.Contains("OperationSignatureMigrationRepairMismatch", unrepaired)
                Assert.Contains("Missing: [user]", unrepaired)
            | other -> failwithf "%A" other
            let choice = """{"id":"choice","displayName":"Choice","exported":true,"kind":"union","variants":[{"id":"A","displayName":"A","fields":[]},{"id":"B","displayName":"B","fields":[]}]}"""
            let arm variant (graph: string) = sprintf """{"variantId":"%s","fieldBindings":[],"body":%s}""" variant graph
            let leaf id (value: int) = sprintf """{"nodes":[{"id":"%s","kind":"int64Literal","value":%d}],"resultNodeId":"%s"}""" id value id
            let matching id (a: string) = sprintf """{"nodes":[{"id":"p%s","kind":"parameterReference","parameterId":"m-c"},{"id":"m%s","kind":"matchUnion","inputNodeId":"p%s","arms":[%s,%s]}],"resultNodeId":"m%s"}""" id id id (arm "A" a) (arm "B" (leaf ("b" + id) 3)) id
            let union (functions: string) (obligations: string) =
                sprintf """{"schemaVersion":24,"snapshotId":"s","types":[%s],"effects":[],"capabilities":[],"functions":[%s],"obligations":[%s]}""" choice functions obligations
            let matcher graph = sprintf """{"id":"m","displayName":"m","parameters":[{"id":"m-c","displayName":"c","type":{"kind":"named","typeId":"choice"}}],"resultType":%s,"requiredEffects":[],"body":%s}""" (primitive "int64") graph
            // The A arm's call to `f` has an open obligation for `x`, so its copy needs one too.
            let callee = sprintf """{"id":"f","displayName":"f","parameters":[{"id":"f-x","displayName":"x","type":%s}],"resultType":%s,"requiredEffects":[],"body":%s}""" (primitive "int64") (primitive "int64") (leaf "f1" 1)
            let obligation = """{"id":"o","kind":"missingArgument","originTransactionId":"t","callNodeId":"call","parameterId":"f-x","status":"open"}"""
            let call = """{"nodes":[{"id":"call","kind":"directCall","targetFunctionId":"f","arguments":[]}],"resultNodeId":"call"}"""
            let obligated = write "obligated" (union (callee + "," + matcher (matching "1" call)) obligation)
            // Fourteen matches nested in each other's A arm would clone about 2^14 nodes.
            let nested = write "nested" (union (matcher (List.fold (fun graph level -> matching (string level) graph) (leaf "leaf" 0) [ 1 .. 14 ])) "")
            let evolve = """{"id":"u","op":"simulateAddUnionVariantLikeByName","type":"Choice","sourceVariant":"A","displayName":"C"}"""
            seededPair "structural-union-obligations" obligated [ evolve ] |> List.exactlyOne |> ok
            Assert.Contains("UnionVariantEvolutionTooLarge", seededPair "structural-union-cap" nested [ evolve ] |> List.exactlyOne))
