namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Display-name renames of fields, variants, parameters, effects and capabilities, and the removal
/// of a function parameter or a Union variant, as ordinary exact-ID `simulateOperations` intents
/// over the maintained JSONL protocol (decision structural-renames-and-removals).
module StructuralRenameAndRemovalTests =
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
        $"""{{"id":"ops","op":"simulateOperations","detail":"full","operations":[{String.concat "," items}]}}"""

    let private operationsByName (items: string list) =
        $"""{{"id":"ops","op":"simulateOperationsByName","operations":[{String.concat "," items}]}}"""

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private int64Type = primitive "int64"
    let private shapeType = """{"kind":"named","name":"Shape"}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private literal (value: int64) = $"""{{"kind":"int64Literal","value":{value}}}"""
    let private int64Value (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""

    let private shapeValue variant (fields: (string * int64) list) =
        let items = fields |> List.map (fun (field, value) -> $"""{{"field":"{field}","value":{int64Value value}}}""") |> String.concat ","
        $"""{{"kind":"union","type":"Shape","variant":"{variant}","fields":[{items}]}}"""

    let private fieldOf variant field =
        $"""{{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"{variant}","field":"{field}"}}"""

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

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    /// Shape = Circle { radius } | Square { side } | Dot; Point = { x, y }; effect io; capability
    /// Host { step(count) }; `area` matches Shape with a Circle arm and a shared Square/Dot arm;
    /// `add(a, unused)` is called by `useAdd`.
    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-rename-remove-" + Guid.NewGuid().ToString("N"))
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
                    $"""{{"id":"host","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Host","operations":[{{"displayName":"step","parameters":[{{"displayName":"count","type":{int64Type}}},{{"displayName":"limit","type":{int64Type}}}],"resultType":{primitive "unit"},"effect":"io"}},{{"displayName":"stop","parameters":[],"resultType":{primitive "unit"},"effect":"io"}}]}}"""
                |> ignore
                let areaBody =
                    $"""{{"kind":"matchUnion","type":"Shape","input":{parameter "shape"},"arms":[{{"variant":"Circle","body":{fieldOf "Circle" "radius"}}},{{"variants":["Square","Dot"],"body":{literal 0}}}]}}"""
                define session "area" [ "shape", shapeType; "scale", int64Type ] int64Type [] areaBody
                define session "add" [ "a", int64Type; "unused", int64Type ] int64Type [] (parameter "a")
                define session "useAdd" [ "v", int64Type ] int64Type [] (call "add" [ "a", parameter "v"; "unused", literal 9 ])
                run session path
            finally
                (session :> IDisposable).Dispose()
        finally
            Directory.Delete(root, true)

    let private declarations (session: AuthoringSession) = session.Declarations()
    let private fn session name = (declarations session).Functions |> List.find (fun item -> item.DisplayName = name)
    let private typeNamed session name = (declarations session).Types |> List.find (fun item -> item.DisplayName = name)
    let private variants session =
        match (typeNamed session "Shape").Kind with
        | SemanticTypeKind.Union variants -> variants
        | SemanticTypeKind.Product _ -> failwith "Shape is not a Union."
    let private variant session name = variants session |> List.find (fun item -> item.DisplayName = name)
    let private productFields session =
        match (typeNamed session "Point").Kind with
        | SemanticTypeKind.Product fields -> fields
        | SemanticTypeKind.Union _ -> failwith "Point is not a Product."
    let private parameterOf session functionName parameterName =
        (fn session functionName).Parameters |> List.find (fun item -> item.DisplayName = parameterName)
    let private capability session = (declarations session).Capabilities |> List.exactlyOne
    let private operation session name = (capability session).Operations |> List.find (fun item -> item.DisplayName = name)
    let private effect session = (declarations session).Effects |> List.exactlyOne
    let private idText (id: StableId<'Kind>) = StableId.value id

    let private matchArms (fn: SemanticFunction) =
        ValueGraph.collectNodes fn.Body
        |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.MatchUnion(_, arms) -> Some arms
            | _ -> None)

    let private reopen path =
        match AuthoringSession.openExisting path with
        | Ok value -> value
        | Error error -> failwithf "Session reopen failed: %A" error

    [<Fact>]
    let ``every rename kind keeps stable IDs, reports its owner in the review and persists`` () =
        withFixture (fun session path ->
            let x = productFields session |> List.find (fun item -> item.DisplayName = "x")
            let radius = (variant session "Circle").Fields |> List.exactlyOne
            let square = variant session "Square"
            let scale = parameterOf session "area" "scale"
            let io = effect session
            let host = capability session
            let step = operation session "step"
            let count = step.Parameters |> List.find (fun item -> item.DisplayName = "count")
            let before = declarations session
            let renames =
                [ $"""{{"kind":"renameField","fieldId":"{idText x.Id}","displayName":"left"}}"""
                  $"""{{"kind":"renameField","fieldId":"{idText radius.Id}","displayName":"r"}}"""
                  $"""{{"kind":"renameVariant","variantId":"{idText square.Id}","displayName":"Box"}}"""
                  $"""{{"kind":"renameParameter","parameterId":"{idText scale.Id}","displayName":"factor"}}"""
                  $"""{{"kind":"renameEffect","effectId":"{idText io.Id}","displayName":"console"}}"""
                  $"""{{"kind":"renameCapability","capabilityId":"{idText host.Id}","displayName":"Machine"}}"""
                  $"""{{"kind":"renameCapabilityOperation","operationId":"{idText step.Id}","displayName":"advance"}}"""
                  $"""{{"kind":"renameCapabilityParameter","parameterId":"{idText count.Id}","displayName":"amount"}}""" ]
            let proposal = result session (operations renames)
            let review = proposal.GetProperty("review")
            let entries =
                review.GetProperty("diff").GetProperty("renames").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("kind").GetString(), item)
                |> Map.ofSeq
            Assert.Equal(8, review.GetProperty("diff").GetProperty("renames").GetArrayLength())
            let owner kind key = entries[kind].GetProperty(key: string).GetString()
            Assert.Equal(idText square.Id, entries["variant"].GetProperty("id").GetString())
            Assert.Equal(idText (typeNamed session "Shape").Id, owner "variant" "ownerTypeId")
            Assert.Equal("Square", owner "variant" "oldDisplayName")
            Assert.Equal("Box", owner "variant" "newDisplayName")
            Assert.Equal(idText (fn session "area").Id, owner "parameter" "ownerFunctionId")
            Assert.Equal(idText host.Id, owner "capabilityOperation" "ownerCapabilityId")
            Assert.Equal(idText host.Id, owner "capabilityParameter" "ownerCapabilityId")
            Assert.Equal(idText step.Id, owner "capabilityParameter" "ownerOperationId")
            Assert.Equal("console", owner "effect" "newDisplayName")
            Assert.Equal("Machine", owner "capability" "newDisplayName")
            let fieldEntries =
                review.GetProperty("diff").GetProperty("renames").EnumerateArray()
                |> Seq.filter (fun item -> item.GetProperty("kind").GetString() = "field")
                |> Seq.toList
            Assert.Equal(2, fieldEntries.Length)
            let variantField = fieldEntries |> List.find (fun item -> item.GetProperty("id").GetString() = idText radius.Id)
            Assert.Equal(idText (variant session "Circle").Id, variantField.GetProperty("ownerVariantId").GetString())
            let productField = fieldEntries |> List.find (fun item -> item.GetProperty("id").GetString() = idText x.Id)
            Assert.False(productField.TryGetProperty("ownerVariantId") |> fst)
            let affectedTypes = review.GetProperty("affectedTypeIds").EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Set.ofSeq
            Assert.Contains(idText (typeNamed session "Point").Id, affectedTypes)
            Assert.Contains(idText (typeNamed session "Shape").Id, affectedTypes)
            commit session proposal

            let check (current: AuthoringDeclarations) =
                Assert.Equal<StableId<TypeTag> list>(before.Types |> List.map (fun item -> item.Id) |> List.sort, current.Types |> List.map (fun item -> item.Id) |> List.sort)
                let currentPoint = current.Types |> List.find (fun item -> item.Id = (before.Types |> List.find (fun t -> t.DisplayName = "Point")).Id)
                match currentPoint.Kind with
                | SemanticTypeKind.Product fields -> Assert.Equal("left", (fields |> List.find (fun item -> item.Id = x.Id)).DisplayName)
                | SemanticTypeKind.Union _ -> failwith "Point changed kind."
                let shape = current.Types |> List.find (fun item -> item.DisplayName = "Shape")
                match shape.Kind with
                | SemanticTypeKind.Union items ->
                    Assert.Equal("Box", (items |> List.find (fun item -> item.Id = square.Id)).DisplayName)
                    Assert.Equal("r", (items |> List.collect (fun item -> item.Fields) |> List.find (fun item -> item.Id = radius.Id)).DisplayName)
                | SemanticTypeKind.Product _ -> failwith "Shape changed kind."
                let area = current.Functions |> List.find (fun item -> item.DisplayName = "area")
                Assert.Equal("factor", (area.Parameters |> List.find (fun item -> item.Id = scale.Id)).DisplayName)
                Assert.Equal("console", (current.Effects |> List.exactlyOne).DisplayName)
                let machine = current.Capabilities |> List.exactlyOne
                Assert.Equal(host.Id, machine.Id)
                Assert.Equal("Machine", machine.DisplayName)
                let advance = machine.Operations |> List.find (fun item -> item.Id = step.Id)
                Assert.Equal("advance", advance.DisplayName)
                Assert.Equal("amount", (advance.Parameters |> List.find (fun item -> item.Id = count.Id)).DisplayName)
                Assert.Equal(io.Id, advance.EffectId)
            check (declarations session)
            // The match still names the renamed variant by ID and runs.
            expectRun session "area" [ shapeValue "Circle" [ "r", 4L ]; int64Value 2L ] (int64Value 4L)
            expectRun session "area" [ shapeValue "Box" [ "side", 3L ]; int64Value 2L ] (int64Value 0L)
            (session :> IDisposable).Dispose()
            use reopened = reopen path
            check (reopened.Declarations()))

    [<Fact>]
    let ``blank and scoped duplicate rename names reject, and other scopes may share a name`` () =
        withFixture (fun session _ ->
            let x = productFields session |> List.find (fun item -> item.DisplayName = "x")
            let radius = (variant session "Circle").Fields |> List.exactlyOne
            let square = variant session "Square"
            let scale = parameterOf session "area" "scale"
            let step = operation session "step"
            let count = step.Parameters |> List.find (fun item -> item.DisplayName = "count")
            let rejectsWith expectedCode (scope: string) item =
                let code, text = rejection session (operations [ item ])
                Assert.Equal(expectedCode, code)
                Assert.Contains(scope, text)
            rejectsWith "InvalidDisplayName" "blank" $"""{{"kind":"renameField","fieldId":"{idText x.Id}","displayName":" "}}"""
            rejectsWith "InvalidDisplayName" "blank" $"""{{"kind":"renameCapabilityParameter","parameterId":"{idText count.Id}","displayName":""}}"""
            rejectsWith "DuplicateDisplayName" "of Product 'Point'" $"""{{"kind":"renameField","fieldId":"{idText x.Id}","displayName":"y"}}"""
            rejectsWith "DuplicateDisplayName" "of Union 'Shape'" $"""{{"kind":"renameVariant","variantId":"{idText square.Id}","displayName":"Dot"}}"""
            rejectsWith "DuplicateDisplayName" "of function 'area'" $"""{{"kind":"renameParameter","parameterId":"{idText scale.Id}","displayName":"shape"}}"""
            rejectsWith "DuplicateDisplayName" "of Capability 'Host'" $"""{{"kind":"renameCapabilityOperation","operationId":"{idText step.Id}","displayName":"stop"}}"""
            rejectsWith "DuplicateDisplayName" "of operation 'step'" $"""{{"kind":"renameCapabilityParameter","parameterId":"{idText count.Id}","displayName":"limit"}}"""
            rejectsWith "TargetNotFound" "not found" """{"kind":"renameVariant","variantId":"missing","displayName":"Other"}"""
            // Global names: a second effect and capability make the names collide.
            publish session """{"id":"net","op":"simulateCreateEffect","exported":true,"displayName":"net"}""" |> ignore
            publish session $"""{{"id":"other","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Other","operations":[{{"displayName":"ping","parameters":[],"resultType":{primitive "unit"},"effect":"net"}}]}}""" |> ignore
            let io = (declarations session).Effects |> List.find (fun item -> item.DisplayName = "io")
            let host = (declarations session).Capabilities |> List.find (fun item -> item.DisplayName = "Host")
            rejectsWith "DuplicateDisplayName" "effect display names must be unique" $"""{{"kind":"renameEffect","effectId":"{idText io.Id}","displayName":"net"}}"""
            rejectsWith "DuplicateDisplayName" "capability display names must be unique" $"""{{"kind":"renameCapability","capabilityId":"{idText host.Id}","displayName":"Other"}}"""
            // The same name in another scope is accepted: a variant field may share a Product field's
            // name, and parameters of different functions may share a name.
            let accepted = result session (operations [ $"""{{"kind":"renameField","fieldId":"{idText radius.Id}","displayName":"x"}}"""; $"""{{"kind":"renameParameter","parameterId":"{idText scale.Id}","displayName":"a"}}""" ])
            Assert.Equal(2, accepted.GetProperty("review").GetProperty("diff").GetProperty("renames").GetArrayLength())
            // Existing type and function rename wording is unchanged.
            let addId = (fn session "add").Id
            let code, text = rejection session (operations [ $"""{{"kind":"renameFunction","functionId":"{idText addId}","displayName":"area"}}""" ])
            Assert.Equal("DuplicateDisplayName", code)
            Assert.Contains("Another function is already named 'area'; function display names must be unique.", text))

    [<Fact>]
    let ``removeFunctionParameter drops caller bindings, retires obligations and keeps callers running`` () =
        withFixture (fun session path ->
            let add = fn session "add"
            let unused = parameterOf session "add" "unused"
            let useAdd = fn session "useAdd"
            let callNodeId =
                ValueGraph.collectNodes useAdd.Body
                |> List.pick (fun node -> SemanticNodeShape.tryDirectCall node |> Option.map (fun _ -> node.Id))
            // A further parameter left missing at the call gives an open obligation for that parameter.
            let pending =
                result session (operations [ $"""{{"kind":"addFunctionParameter","functionId":"{idText add.Id}","displayName":"extra","type":{int64Type},"callSites":[{{"kind":"leaveMissing","callNodeId":"{idText callNodeId}"}}]}}""" ])
            commitIncomplete session pending
            let extra = parameterOf session "add" "extra"
            let removal = result session (operations [ $"""{{"kind":"removeFunctionParameter","parameterId":"{idText extra.Id}"}}"""; $"""{{"kind":"removeFunctionParameter","parameterId":"{idText unused.Id}"}}""" ])
            let diff = removal.GetProperty("review").GetProperty("diff")
            let removed = diff.GetProperty("parametersRemoved").EnumerateArray() |> Seq.toList
            Assert.Equal(2, removed.Length)
            let entry name = removed |> List.find (fun item -> item.GetProperty("displayName").GetString() = name)
            Assert.Equal(idText add.Id, (entry "unused").GetProperty("ownerFunctionId").GetString())
            Assert.Equal(idText unused.Id, (entry "unused").GetProperty("parameterId").GetString())
            Assert.Equal(idText callNodeId, (entry "unused").GetProperty("affectedCallNodeIds").Item(0).GetString())
            Assert.Equal(0, (entry "unused").GetProperty("retiredObligations").GetArrayLength())
            let retired = (entry "extra").GetProperty("retiredObligations")
            Assert.Equal(1, retired.GetArrayLength())
            Assert.Equal("open", retired[0].GetProperty("status").GetProperty("kind").GetString())
            Assert.Equal(idText extra.Id, retired[0].GetProperty("parameterId").GetString())
            Assert.Equal("semanticallyReady", removal.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
            let affected = removal.GetProperty("review").GetProperty("affectedFunctionIds").EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Set.ofSeq
            Assert.Equal<Set<string>>(set [ idText add.Id; idText useAdd.Id ], affected)
            commit session removal

            let current = fn session "add"
            Assert.Equal(add.Id, current.Id)
            Assert.Equal<string list>([ "a" ], current.Parameters |> List.map (fun item -> item.DisplayName))
            let call =
                ValueGraph.collectNodes (fn session "useAdd").Body |> List.pick SemanticNodeShape.tryDirectCall |> snd
            Assert.Equal<StableId<ParameterTag> list>([ (parameterOf session "add" "a").Id ], call |> List.map (fun item -> item.TargetParameterId))
            // The literal that was bound to `unused` stays in the caller graph.
            Assert.Contains(ValueGraph.collectNodes (fn session "useAdd").Body, fun node -> node.Kind = SemanticNodeKind.Int64Literal 9L)
            expectRun session "useAdd" [ int64Value 5L ] (int64Value 5L)
            (session :> IDisposable).Dispose()
            use reopened = reopen path
            let persisted = reopened.Declarations().Functions |> List.find (fun item -> item.Id = add.Id)
            Assert.Equal(1, persisted.Parameters.Length))

    [<Fact>]
    let ``removeFunctionParameter rejects references, non-call uses, effectful callers and new parameters`` () =
        withFixture (fun session _ ->
            let a = parameterOf session "add" "a"
            let unused = parameterOf session "add" "unused"
            let remove (parameter: SemanticParameter) = $"""{{"kind":"removeFunctionParameter","parameterId":"{idText parameter.Id}"}}"""
            let code, text = rejection session (operations [ remove a ])
            Assert.Equal("ParameterStillReferenced", code)
            Assert.Contains("'a'", text)
            Assert.Equal("TargetNotFound", rejection session (operations [ """{"kind":"removeFunctionParameter","parameterId":"missing"}""" ]) |> fst)
            // A parameter added in the same transaction has a system-owned ID the request cannot name;
            // that rejection is covered through the semantic core below.
            // An effectful caller would lose an argument binding.
            define session "effectfulUse" [ "v", int64Type ] int64Type [ "io" ] (call "add" [ "a", parameter "v"; "unused", parameter "v" ])
            let code, text = rejection session (operations [ remove unused ])
            Assert.Equal("ParameterRemovalMayDiscardEffects", code)
            Assert.Contains("effectfulUse", text)
            publish session (operations [ $"""{{"kind":"deleteFunction","functionId":"{idText (fn session "effectfulUse").Id}"}}""" ]) |> ignore
            // A function value fixes the signature.
            define session "asValue" [] $"""{{"kind":"function","parameters":[{int64Type},{int64Type}],"resultType":{int64Type}}}""" [] """{"kind":"functionReference","function":"add"}"""
            let code, text = rejection session (operations [ remove unused ])
            Assert.Equal("ParameterRemovalNonCallUse", code)
            Assert.Contains("function value", text))

    [<Fact>]
    let ``a parameter added and removed in one transaction is rejected`` () =
        let snapshotId = StableId.createForTesting<SnapshotTag> "rr-base"
        let functionId = StableId.createForTesting<FunctionTag> "rr-f"
        let keep = StableId.createForTesting<ParameterTag> "rr-keep"
        let late = StableId.createForTesting<ParameterTag> "rr-late"
        let result = StableId.createForTesting<GraphNodeTag> "rr-result"
        let snapshot =
            { Snapshot.empty snapshotId with
                Functions =
                    [ { Exported = true; Id = functionId
                        DisplayName = "f"
                        Parameters = [ { Id = keep; DisplayName = "keep"; Type = TypeRef.Primitive PrimitiveType.Int64 } ]
                        ResultType = TypeRef.Primitive PrimitiveType.Int64
                        RequiredEffects = []
                        Body = { Nodes = [ { Id = result; Kind = SemanticNodeKind.ParameterReference keep } ]; ResultNodeId = result } } ] }
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "rr-tx"
              BaseSnapshotId = snapshotId
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "rr-next"
              Operations =
                [ AddFunctionParameter
                      { TargetFunctionId = functionId
                        NewParameterId = late
                        NewParameterDisplayName = "late"
                        NewParameterType = TypeRef.Primitive PrimitiveType.Int64
                        CallSitePlans = [] }
                  RemoveFunctionParameter late ] }
        match Transaction.simulate snapshot request with
        | Rejected [ diagnostic ] -> Assert.Equal("ParameterCreatedInTransaction", diagnostic.Code)
        | other -> failwithf "Expected ParameterCreatedInTransaction: %A" other

    [<Fact>]
    let ``removeUnionVariant prunes a single-variant arm, narrows a shared arm and keeps matches running`` () =
        withFixture (fun session path ->
            let shape = typeNamed session "Shape"
            let circle = variant session "Circle"
            let dot = variant session "Dot"
            let area = fn session "area"
            let removeCircle = result session (operations [ $"""{{"kind":"removeUnionVariant","variantId":"{idText circle.Id}"}}""" ])
            let entry = removeCircle.GetProperty("review").GetProperty("diff").GetProperty("unionVariantsRemoved")[0]
            Assert.Equal(idText shape.Id, entry.GetProperty("ownerTypeId").GetString())
            Assert.Equal(idText circle.Id, entry.GetProperty("variantId").GetString())
            Assert.Equal("Circle", entry.GetProperty("displayName").GetString())
            Assert.Equal(idText (circle.Fields |> List.exactlyOne).Id, entry.GetProperty("removedFieldIds").Item(0).GetString())
            Assert.Equal(1, entry.GetProperty("affectedMatchNodeIds").GetArrayLength())
            let review = removeCircle.GetProperty("review")
            Assert.Contains(idText area.Id, review.GetProperty("affectedFunctionIds").EnumerateArray() |> Seq.map (fun item -> item.GetString()))
            Assert.Contains(idText shape.Id, review.GetProperty("affectedTypeIds").EnumerateArray() |> Seq.map (fun item -> item.GetString()))
            commit session removeCircle
            let arms = matchArms (fn session "area")
            Assert.Equal(1, arms.Length)
            Assert.Equal(2, arms.Head.VariantIds.Length)
            expectRun session "area" [ shapeValue "Square" [ "side", 3L ]; int64Value 1L ] (int64Value 0L)

            // Removing Dot narrows the shared arm to Square alone, which then binds Square's field.
            let removeDot = publish session (operationsByName [ """{"kind":"removeUnionVariant","type":"Shape","variant":"Dot"}""" ])
            Assert.Equal(idText dot.Id, removeDot.GetProperty("review").GetProperty("diff").GetProperty("unionVariantsRemoved").Item(0).GetProperty("variantId").GetString())
            let narrowed = matchArms (fn session "area") |> List.exactlyOne
            Assert.Equal<StableId<VariantTag> list>([ (variant session "Square").Id ], narrowed.VariantIds)
            Assert.Equal(1, narrowed.FieldBindings.Length)
            expectRun session "area" [ shapeValue "Square" [ "side", 3L ]; int64Value 1L ] (int64Value 0L)
            Assert.Equal(area.Id, (fn session "area").Id)
            (session :> IDisposable).Dispose()
            use reopened = reopen path
            match (reopened.Declarations().Types |> List.find (fun item -> item.Id = shape.Id)).Kind with
            | SemanticTypeKind.Union remaining -> Assert.Equal<string list>([ "Square" ], remaining |> List.map (fun item -> item.DisplayName))
            | SemanticTypeKind.Product _ -> failwith "Shape changed kind.")

    [<Fact>]
    let ``removeUnionVariant rejects constructed, last and effectful-arm variants`` () =
        withFixture (fun session _ ->
            let remove (item: SemanticVariant) = $"""{{"kind":"removeUnionVariant","variantId":"{idText item.Id}"}}"""
            define session "makeDot" [] shapeType [] """{"kind":"unionConstruct","type":"Shape","variant":"Dot","fields":[]}"""
            let code, text = rejection session (operations [ remove (variant session "Dot") ])
            Assert.Equal("UnionVariantStillConstructed", code)
            Assert.Contains("makeDot", text)
            Assert.Equal("TargetNotFound", rejection session (operations [ """{"kind":"removeUnionVariant","variantId":"missing"}""" ]) |> fst)
            // An effectful function whose arm would be dropped.
            define session "effectfulArea" [ "shape", shapeType ] int64Type [ "io" ]
                $"""{{"kind":"matchUnion","type":"Shape","input":{parameter "shape"},"arms":[{{"variant":"Circle","body":{literal 1}}},{{"variants":["Square","Dot"],"body":{literal 0}}}]}}"""
            let code, text = rejection session (operations [ remove (variant session "Circle") ])
            Assert.Equal("UnionVariantRemovalMayDiscardEffects", code)
            Assert.Contains("effectfulArea", text)
            // Narrowing a shared arm drops no body, so the effectful function does not block it,
            // but the remaining construction still does.
            Assert.Equal("UnionVariantStillConstructed", rejection session (operations [ remove (variant session "Dot") ]) |> fst)
            publish session (operations [ $"""{{"kind":"deleteFunction","functionId":"{idText (fn session "makeDot").Id}"}}""" ]) |> ignore
            publish session (operations [ remove (variant session "Dot") ]) |> ignore
            publish session (operations [ $"""{{"kind":"deleteFunction","functionId":"{idText (fn session "effectfulArea").Id}"}}""" ]) |> ignore
            publish session (operations [ remove (variant session "Circle") ]) |> ignore
            let code, text = rejection session (operations [ remove (variant session "Square") ])
            Assert.Equal("UnionVariantLast", code)
            Assert.Contains("'Square'", text))

    [<Fact>]
    let ``renames and removals of imported declarations are rejected`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-rename-import-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let libraryPath = Path.Combine(root, "library.json")
            withFixture (fun session path -> (session :> IDisposable).Dispose(); File.Copy(path, libraryPath))
            use consumer =
                match AuthoringSession.createNewWithDependency (Path.Combine(root, "consumer.json")) (Some libraryPath) with
                | Ok value -> value
                | Error error -> failwithf "Dependency creation failed: %A" error
            let imported = consumer.Declarations()
            let shape = imported.Types |> List.find (fun item -> item.DisplayName = "Shape")
            let variants = match shape.Kind with SemanticTypeKind.Union items -> items | SemanticTypeKind.Product _ -> []
            let point = imported.Types |> List.find (fun item -> item.DisplayName = "Point")
            let x = match point.Kind with SemanticTypeKind.Product fields -> fields.Head | SemanticTypeKind.Union _ -> failwith "Point"
            let add = imported.Functions |> List.find (fun item -> item.DisplayName = "add")
            let host = imported.Capabilities.Head
            let step = host.Operations |> List.find (fun item -> item.DisplayName = "step")
            let attempts =
                [ $"""{{"kind":"renameField","fieldId":"{idText x.Id}","displayName":"other"}}"""
                  $"""{{"kind":"renameVariant","variantId":"{idText variants.Head.Id}","displayName":"Other"}}"""
                  $"""{{"kind":"renameParameter","parameterId":"{idText add.Parameters.Head.Id}","displayName":"other"}}"""
                  $"""{{"kind":"renameEffect","effectId":"{idText imported.Effects.Head.Id}","displayName":"other"}}"""
                  $"""{{"kind":"renameCapability","capabilityId":"{idText host.Id}","displayName":"Other"}}"""
                  $"""{{"kind":"renameCapabilityOperation","operationId":"{idText step.Id}","displayName":"other"}}"""
                  $"""{{"kind":"renameCapabilityParameter","parameterId":"{idText step.Parameters.Head.Id}","displayName":"other"}}"""
                  $"""{{"kind":"removeFunctionParameter","parameterId":"{idText (add.Parameters |> List.find (fun p -> p.DisplayName = "unused")).Id}"}}"""
                  $"""{{"kind":"removeUnionVariant","variantId":"{idText (variants |> List.find (fun v -> v.DisplayName = "Dot")).Id}"}}""" ]
            for attempt in attempts do
                let _, text = rejection consumer (operations [ attempt ])
                Assert.Contains("ImportedDeclarationModified", text)
        finally
            Directory.Delete(root, true)
