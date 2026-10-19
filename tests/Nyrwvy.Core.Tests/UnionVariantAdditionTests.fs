namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The plain `addUnionVariant` operation of `simulateOperations` (and `…ByName`): a new variant
/// with arbitrary fields, with one arm recipe per exhaustive `matchUnion` over the Union
/// (decision authoring-surface-queries-and-parity, option 1).
module UnionVariantAdditionTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private commit session (proposal: JsonElement) =
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

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
    let private stringType = primitive "string"
    let private named name = $"""{{"kind":"named","name":"{name}"}}"""
    let private namedWith name (arguments: string list) = $"""{{"kind":"named","name":"{name}","arguments":[{String.concat "," arguments}]}}"""
    let private typeParameter name = $"""{{"kind":"typeParameter","name":"{name}"}}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private literal (value: int64) = $"""{{"kind":"int64Literal","value":{value}}}"""
    let private multiply left right = $"""{{"kind":"int64WrappingMultiply","left":{left},"right":{right}}}"""
    let private subtract left right = $"""{{"kind":"int64WrappingSubtract","left":{left},"right":{right}}}"""
    let private add left right = $"""{{"kind":"int64WrappingAdd","left":{left},"right":{right}}}"""
    let private field name typeRef = $"""{{"displayName":"{name}","type":{typeRef}}}"""
    let private strings (items: JsonElement seq) = items |> Seq.map (fun item -> item.GetString()) |> Set.ofSeq
    let private idText (id: StableId<'Kind>) = StableId.value id

    let private define session name (parameters: (string * string) list) resultType body =
        let signature = parameters |> List.map (fun (displayName, typeRef) -> field displayName typeRef) |> String.concat ","
        publish
            session
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{signature}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""
        |> ignore

    /// Shape = Circle { radius } | Square { side } | Dot. `area(shape, scale)` matches Shape with a
    /// Circle arm and a shared Square|Dot arm; `sides(shape)` matches it with three one-variant arms.
    /// Maybe<T> = Just { value: T } | Nothing; `orDefault(m: Maybe<Int64>, d)` matches it.
    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-add-variant-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            try
                publish
                    session
                    $"""{{"id":"shape","op":"simulateCreateTypeByName","exported":true,"displayName":"Shape","typeKind":{{"kind":"union","variants":[{{"displayName":"Circle","fields":[{field "radius" int64Type}]}},{{"displayName":"Square","fields":[{field "side" int64Type}]}},{{"displayName":"Dot","fields":[]}}]}}}}"""
                |> ignore
                publish
                    session
                    $"""{{"id":"maybe","op":"simulateCreateTypeByName","exported":true,"displayName":"Maybe","typeParameters":["T"],"typeKind":{{"kind":"union","variants":[{{"displayName":"Just","fields":[{field "value" (typeParameter "T")}]}},{{"displayName":"Nothing","fields":[]}}]}}}}"""
                |> ignore
                let radius = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"Circle","field":"radius"}"""
                define session "area" [ "shape", named "Shape"; "scale", int64Type ] int64Type
                    $"""{{"kind":"matchUnion","type":"Shape","input":{parameter "shape"},"arms":[{{"variant":"Circle","body":{multiply radius (parameter "scale")}}},{{"variants":["Square","Dot"],"body":{literal 0}}}]}}"""
                define session "sides" [ "shape", named "Shape" ] int64Type
                    $"""{{"kind":"matchUnion","type":"Shape","input":{parameter "shape"},"arms":[{{"variant":"Circle","body":{literal 0}}},{{"variant":"Square","body":{literal 4}}},{{"variant":"Dot","body":{literal 0}}}]}}"""
                let justValue = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Maybe","variant":"Just","field":"value"}"""
                define session "orDefault" [ "m", namedWith "Maybe" [ int64Type ]; "d", int64Type ] int64Type
                    $"""{{"kind":"matchUnion","type":"Maybe","input":{parameter "m"},"arms":[{{"variant":"Just","body":{justValue}}},{{"variant":"Nothing","body":{parameter "d"}}}]}}"""
                run session path
            finally
                (session :> IDisposable).Dispose()
        finally
            Directory.Delete(root, true)

    let private declarations (session: AuthoringSession) = session.Declarations()
    let private fn session name = (declarations session).Functions |> List.find (fun item -> item.DisplayName = name)
    let private typeNamed session name = (declarations session).Types |> List.find (fun item -> item.DisplayName = name)
    let private variants (typeEntity: SemanticType) =
        match typeEntity.Kind with
        | SemanticTypeKind.Union variants -> variants
        | SemanticTypeKind.Product _ -> failwith "Expected a Union."
    let private variantNamed session typeName name = variants (typeNamed session typeName) |> List.find (fun item -> item.DisplayName = name)
    let private matchNode (fn: SemanticFunction) =
        ValueGraph.collectNodes fn.Body
        |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.MatchUnion(_, arms) -> Some(node.Id, arms)
            | _ -> None)
    let private parameterOf (fn: SemanticFunction) name = fn.Parameters |> List.find (fun item -> item.DisplayName = name)
    let private snapshotOf path =
        match SnapshotCodec.deserialize (File.ReadAllText path) with
        | Ok value -> value
        | Error error -> failwithf "%A" error
    let private reopen path =
        match AuthoringSession.openExisting path with
        | Ok value -> value
        | Error error -> failwithf "Session reopen failed: %A" error

    /// Exact-ID recipes of the arm bodies: a new field by `field` name, an existing parameter by ID.
    let private newField name = $"""{{"kind":"patternReference","scopeDepth":0,"slot":"field","field":"{name}"}}"""
    let private parameterId (id: ParameterId) = $"""{{"kind":"parameter","parameterId":"{idText id}"}}"""

    let private ringRequest session (arms: string list) =
        let shape = typeNamed session "Shape"
        $"""{{"kind":"addUnionVariant","typeId":"{idText shape.Id}","displayName":"Ring","fields":[{field "inner" int64Type},{field "outer" int64Type}],"arms":[{String.concat "," arms}]}}"""

    let private arm (matchNodeId: GraphNodeId) body = $"""{{"matchNodeId":"{idText matchNodeId}","body":{body}}}"""

    /// `area`'s Ring arm: (outer - inner) * scale; `sides`'s Ring arm: 0.
    let private ringArms session =
        let area = fn session "area"
        let sides = fn session "sides"
        [ arm (fst (matchNode area)) (multiply (subtract (newField "outer") (newField "inner")) (parameterId (parameterOf area "scale").Id))
          arm (fst (matchNode sides)) (literal 0) ]

    let private checkRing (current: AuthoringDeclarations) =
        let shape = current.Types |> List.find (fun item -> item.DisplayName = "Shape")
        let ring = variants shape |> List.find (fun item -> item.DisplayName = "Ring")
        // Persisted field order is canonical (by ID), so the fields are compared as a set.
        Assert.Equal<Set<string>>(Set.ofList [ "inner"; "outer" ], ring.Fields |> List.map (fun item -> item.DisplayName) |> Set.ofList)
        for name in [ "area"; "sides" ] do
            let owner = current.Functions |> List.find (fun item -> item.DisplayName = name)
            let _, arms = matchNode owner
            let ringArm = arms |> List.find (fun item -> item.VariantIds = [ ring.Id ])
            Assert.Equal<Set<FieldId>>(ring.Fields |> List.map (fun item -> item.Id) |> Set.ofList, ringArm.FieldBindings |> List.map (fun item -> item.SourceFieldId) |> Set.ofList)
            Assert.Equal<Set<string option>>(Set.ofList [ Some "inner"; Some "outer" ], ringArm.FieldBindings |> List.map (fun item -> item.Binding.DisplayName) |> Set.ofList)
        // The shared Square|Dot arm of `area` is untouched: the new variant has its own arm.
        let _, areaArms = matchNode (current.Functions |> List.find (fun item -> item.DisplayName = "area"))
        Assert.Equal(3, areaArms.Length)
        Assert.Contains(areaArms, fun item -> item.VariantIds.Length = 2)
        ring

    [<Fact>]
    let ``addUnionVariant adds a variant with fields and one planned arm per match in one reviewed change`` () =
        withFixture (fun session path ->
            let shape = typeNamed session "Shape"
            let area = fn session "area"
            let sides = fn session "sides"
            let proposal = result session (operations [ ringRequest session (ringArms session) ])
            let review = proposal.GetProperty("review")
            Assert.Equal("operations", review.GetProperty("kind").GetString())
            let diff = review.GetProperty("diff")
            let added = diff.GetProperty("unionVariantsAdded")
            Assert.Equal(1, added.GetArrayLength())
            Assert.Equal(idText shape.Id, added[0].GetProperty("ownerTypeId").GetString())
            let variant = added[0].GetProperty("variant")
            Assert.Equal("Ring", variant.GetProperty("displayName").GetString())
            Assert.Equal<string list>([ "inner"; "outer" ], variant.GetProperty("fields").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("displayName").GetString()) |> List.ofSeq)
            let arms = diff.GetProperty("unionMatchArmsAdded")
            Assert.Equal(2, arms.GetArrayLength())
            let armOwners = arms.EnumerateArray() |> Seq.map (fun item -> item.GetProperty("ownerFunctionId").GetString()) |> Set.ofSeq
            Assert.Equal<Set<string>>(Set.ofList [ idText area.Id; idText sides.Id ], armOwners)
            for item in arms.EnumerateArray() do
                Assert.Equal(variant.GetProperty("id").GetString(), item.GetProperty("variantId").GetString())
                Assert.Equal(2, item.GetProperty("patternBindingIds").GetArrayLength())
            Assert.Equal<Set<string>>(Set.ofList [ idText area.Id; idText sides.Id ], strings (review.GetProperty("affectedFunctionIds").EnumerateArray()))
            Assert.Contains(idText shape.Id, strings (review.GetProperty("affectedTypeIds").EnumerateArray()))
            // Reviews of other operations keep their shape: the new lists appear only when non-empty.
            let rename = result session (operations [ $"""{{"kind":"renameType","typeId":"{idText (typeNamed session "Maybe").Id}","displayName":"Option2"}}""" ])
            Assert.False(rename.GetProperty("review").GetProperty("diff").TryGetProperty("unionVariantsAdded") |> fst)
            // The pending listing summarizes the operation by display names.
            let listing = result session """{"id":"proposals","op":"proposals"}"""
            let summary =
                listing.GetProperty("proposals").EnumerateArray()
                |> Seq.find (fun item -> item.GetProperty("proposalId").GetString() = proposal.GetProperty("proposalId").GetString())
                |> fun item -> item.GetProperty("summary").GetString()
            Assert.Equal("addUnionVariant 'Ring' to 'Shape'", summary)
            commit session proposal
            let ring = checkRing (declarations session)
            Assert.Equal(shape.Id, (typeNamed session "Shape").Id)
            Assert.Empty(SnapshotValidator.validate (snapshotOf path))
            // The new arm runs: area(Ring { inner 2, outer 5 }, 3) = (5 - 2) * 3.
            let ringValue = $"""{{"kind":"union","type":"Shape","variant":"Ring","fields":[{{"field":"inner","value":{{"kind":"int64","value":"2"}}}},{{"field":"outer","value":{{"kind":"int64","value":"5"}}}}]}}"""
            let run = result session $"""{{"id":"run","op":"runPureByName","function":"area","arguments":[{ringValue},{{"kind":"int64","value":"3"}}],"expected":{{"kind":"int64","value":"9"}}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText())
            (session :> IDisposable).Dispose()
            use reopened = reopen path
            Assert.Equal(ring.Id, (checkRing (reopened.Declarations())).Id))

    [<Fact>]
    let ``addUnionVariant rejects missing, stray, duplicate and mistyped arm plans and duplicate names`` () =
        withFixture (fun session _ ->
            let area = fn session "area"
            let sides = fn session "sides"
            let areaArm, sidesArm =
                match ringArms session with
                | [ first; second ] -> first, second
                | other -> failwithf "Unexpected arms %A" other
            // No plan for `sides`: the rejection lists every match over Shape by owner.
            let code, text = rejection session (operations [ ringRequest session [ areaArm ] ])
            Assert.Equal("UnionMatchArmPlanMismatch", code)
            Assert.Contains("area", text)
            Assert.Contains("sides", text)
            Assert.Contains(idText (fst (matchNode sides)), text)
            // A plan for a node that is not a match, for a match over another Union, and for no node at all.
            let parameterNode =
                ValueGraph.collectNodes area.Body
                |> List.pick (fun node -> match node.Kind with SemanticNodeKind.ParameterReference _ -> Some node.Id | _ -> None)
            Assert.Equal("UnionMatchArmPlanTargetMismatch", rejection session (operations [ ringRequest session [ areaArm; sidesArm; arm parameterNode (literal 0) ] ]) |> fst)
            Assert.Equal("UnionMatchArmPlanTargetMismatch", rejection session (operations [ ringRequest session [ areaArm; sidesArm; arm (fst (matchNode (fn session "orDefault"))) (literal 0) ] ]) |> fst)
            Assert.Equal("UnionMatchArmPlanTargetNotFound", rejection session (operations [ ringRequest session [ areaArm; sidesArm; arm (StableId.createForTesting<GraphNodeTag> "missing-match") (literal 0) ] ]) |> fst)
            Assert.Equal("UnionMatchArmPlanMismatch", rejection session (operations [ ringRequest session [ areaArm; sidesArm; sidesArm ] ]) |> fst)
            // An arm body must produce the match's type, in the arm's scope.
            let code, text = rejection session (operations [ ringRequest session [ areaArm; arm (fst (matchNode sides)) """{"kind":"stringLiteral","value":"ring"}""" ] ])
            Assert.Equal("UnionMatchArmTypeMismatch", code)
            Assert.Contains("sides", text)
            Assert.Contains("ProtocolError", rejection session (operations [ ringRequest session [ areaArm; arm (fst (matchNode sides)) (newField "diameter") ] ]) |> snd)
            // An existing variant name, a blank field name, a Product target and a field type naming a type parameter the Union lacks.
            Assert.Equal("DuplicateUnionVariantDisplayName", rejection session (operations [ (ringRequest session [ areaArm; sidesArm ]).Replace("\"displayName\":\"Ring\"", "\"displayName\":\"Dot\"") ]) |> fst)
            Assert.Equal("InvalidDisplayName", rejection session (operations [ (ringRequest session [ areaArm; sidesArm ]).Replace("\"displayName\":\"Ring\"", "\"displayName\":\" \"") ]) |> fst)
            Assert.Contains("only the Union's own type parameters", rejection session (operations [ (ringRequest session [ areaArm; sidesArm ]).Replace(field "outer" int64Type, field "outer" (typeParameter "T")) ]) |> snd)
            // Nothing was published by the rejected batches.
            Assert.Equal(3, (variants (typeNamed session "Shape")).Length))

    [<Fact>]
    let ``addUnionVariant by name resolves the Union, field types, parameters and new fields in arm bodies`` () =
        withFixture (fun session path ->
            let area = fn session "area"
            let sides = fn session "sides"
            let inner = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"Ring","field":"inner"}"""
            let outer = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"Ring","field":"outer"}"""
            let request =
                $"""{{"kind":"addUnionVariant","type":"Shape","displayName":"Ring","fields":[{field "inner" int64Type},{field "outer" int64Type}],"arms":[{arm (fst (matchNode area)) (multiply (subtract outer inner) (parameter "scale"))},{arm (fst (matchNode sides)) (literal 0)}]}}"""
            let proposal = result session (operationsByName [ request ])
            Assert.Equal(2, proposal.GetProperty("review").GetProperty("diff").GetProperty("unionMatchArmsAdded").GetArrayLength())
            commit session proposal
            checkRing (declarations session) |> ignore
            Assert.Empty(SnapshotValidator.validate (snapshotOf path))
            Assert.Contains("Unknown type", rejection session (operationsByName [ request.Replace("\"type\":\"Shape\",\"displayName\":\"Ring\"", "\"type\":\"Nobody\",\"displayName\":\"Ring\"") ]) |> snd)
            // The arm bodies name Ring's fields, which exist now but are not in scope of a Halo arm.
            Assert.Contains("outer", rejection session (operationsByName [ request.Replace("\"displayName\":\"Ring\"", "\"displayName\":\"Halo\"") ]) |> snd))

    [<Fact>]
    let ``addUnionVariant on a generic Union binds the new fields at the match input's type arguments`` () =
        withFixture (fun session path ->
            let maybe = typeNamed session "Maybe"
            let orDefault = fn session "orDefault"
            let matchId, _ = matchNode orDefault
            let request =
                $"""{{"kind":"addUnionVariant","typeId":"{idText maybe.Id}","displayName":"Pair","fields":[{field "first" (typeParameter "T")},{field "second" (typeParameter "T")}],"arms":[{arm matchId (add (newField "first") (newField "second"))}]}}"""
            let proposal = result session (operations [ request ])
            commit session proposal
            let pair = variantNamed session "Maybe" "Pair"
            let declaredParameter = maybe.TypeParameters |> List.exactlyOne |> fst
            // The declaration keeps the Union's own type parameter; the arm binds Int64, the input's argument.
            Assert.All(pair.Fields, fun item -> Assert.Equal(TypeRef.TypeParameter(declaredParameter, "T"), item.Type))
            let _, arms = matchNode (fn session "orDefault")
            let pairArm = arms |> List.find (fun item -> item.VariantIds = [ pair.Id ])
            Assert.All(pairArm.FieldBindings, fun item -> Assert.Equal(TypeRef.Primitive PrimitiveType.Int64, item.Binding.Type))
            Assert.Empty(SnapshotValidator.validate (snapshotOf path))
            let pairValue = $"""{{"kind":"union","type":"Maybe","variant":"Pair","typeArguments":[{int64Type}],"fields":[{{"field":"first","value":{{"kind":"int64","value":"2"}}}},{{"field":"second","value":{{"kind":"int64","value":"5"}}}}]}}"""
            let run = result session $"""{{"id":"run","op":"runPureByName","function":"orDefault","arguments":[{pairValue},{{"kind":"int64","value":"0"}}],"expected":{{"kind":"int64","value":"7"}}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText()))

    [<Fact>]
    let ``addUnionVariant composes with other operations in one batch`` () =
        withFixture (fun session path ->
            let shape = typeNamed session "Shape"
            let dot = variantNamed session "Shape" "Dot"
            // Add Ring, retire Dot and rename the Union in one reviewed change: Dot leaves the shared
            // arm of `area` (which keeps Square) and loses its own arm in `sides`.
            let batch =
                [ ringRequest session (ringArms session)
                  $"""{{"kind":"removeUnionVariant","variantId":"{idText dot.Id}"}}"""
                  $"""{{"kind":"renameType","typeId":"{idText shape.Id}","displayName":"Figure"}}""" ]
            let proposal = result session (operations batch)
            let diff = proposal.GetProperty("review").GetProperty("diff")
            Assert.Equal(1, diff.GetProperty("unionVariantsAdded").GetArrayLength())
            Assert.Equal(1, diff.GetProperty("unionVariantsRemoved").GetArrayLength())
            Assert.Equal(1, diff.GetProperty("renames").GetArrayLength())
            let listing = result session """{"id":"proposals","op":"proposals"}"""
            let summary = listing.GetProperty("proposals").EnumerateArray() |> Seq.head |> fun item -> item.GetProperty("summary").GetString()
            Assert.Equal("addUnionVariant 'Ring' to 'Shape'; removeUnionVariant 'Dot'; renameType 'Shape' to 'Figure'", summary)
            commit session proposal
            let figure = typeNamed session "Figure"
            Assert.Equal(shape.Id, figure.Id)
            Assert.Equal<string list>([ "Circle"; "Square"; "Ring" ], variants figure |> List.map (fun item -> item.DisplayName))
            let _, areaArms = matchNode (fn session "area")
            Assert.Equal(3, areaArms.Length)
            Assert.All(areaArms, fun item -> Assert.Equal(1, item.VariantIds.Length))
            let _, sidesArms = matchNode (fn session "sides")
            Assert.Equal(3, sidesArms.Length)
            Assert.Empty(SnapshotValidator.validate (snapshotOf path)))

    // ----- F# surface: prepared plans, raw operations and their rejections -----

    let private workspaceOf (session: AuthoringSession) =
        match AgentWorkspace.create (snapshotOf session.Path) with
        | Ok value -> value
        | Error error -> failwithf "%A" error

    let private int64 = TypeRef.Primitive PrimitiveType.Int64
    let private newRing (): SemanticVariant =
        AgentUnionVariantArmPlans.allocateVariant "Ring" [ { DisplayName = "inner"; Type = int64 }; { DisplayName = "outer"; Type = int64 } ]

    let private simulateRaw (ws: AgentWorkspace) (operations: SemanticOperation list) =
        let snapshot = AgentWorkspace.snapshotForStructuralEditing ws
        Transaction.simulate
            snapshot
            { Id = StableIdAllocator.newTransactionId ()
              BaseSnapshotId = snapshot.Id
              ProposedSnapshotId = StableIdAllocator.newSnapshotId ()
              Operations = operations }

    let private rejectedCode result =
        match result with
        | Rejected [ diagnostic ] -> diagnostic.Code
        | other -> failwithf "Expected one rejection, got %A" other

    [<Fact>]
    let ``prepared arm plans simulate through AgentWorkflow and the raw operation enforces its plan rules`` () =
        withFixture (fun session _ ->
            let ws = workspaceOf session
            let shape = typeNamed session "Shape"
            let area = fn session "area"
            let sides = fn session "sides"
            let ring = newRing ()
            let inner = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Field ring.Fields[0].Id }
            let outer = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Field ring.Fields[1].Id }
            let recipes: AgentUnionVariantArmRecipe list =
                [ { MatchNodeId = fst (matchNode area); Body = ExpressionRecipe.Int64WrappingMultiply(ExpressionRecipe.Int64WrappingSubtract(outer, inner), ExpressionRecipe.Parameter (parameterOf area "scale").Id) }
                  { MatchNodeId = fst (matchNode sides); Body = ExpressionRecipe.Int64Literal 0L } ]
            let intent =
                match AgentUnionVariantArmPlans.prepare ws shape.Id ring recipes with
                | Ok value -> value
                | Error error -> failwithf "prepare: %A" error
            Assert.Equal(2, intent.ArmPlans.Length)
            // Every plan binds both fields, by name, at Int64; the bodies refer to those bindings.
            for plan in intent.ArmPlans do
                Assert.Equal<FieldId list>(ring.Fields |> List.map (fun item -> item.Id), plan.FieldBindings |> List.map (fun item -> item.SourceFieldId))
                Assert.All(plan.FieldBindings, fun item -> Assert.Equal(int64, item.Binding.Type))
            let areaPlan = intent.ArmPlans |> List.find (fun plan -> plan.MatchNodeId = fst (matchNode area))
            let referenced =
                ValueGraph.collectNodes areaPlan.Body
                |> List.choose (fun node -> match node.Kind with SemanticNodeKind.PatternReference id -> Some id | _ -> None)
                |> Set.ofList
            Assert.Equal<Set<PatternBindingId>>(areaPlan.FieldBindings |> List.map (fun item -> item.Binding.Id) |> Set.ofList, referenced)
            let proposal =
                match AgentWorkflow.simulate ws [ AgentOperationIntent.AddUnionVariant intent ] with
                | Ok value -> value
                | Error error -> failwithf "simulate: %A" error
            let review = AgentChangeProposal.review proposal
            Assert.Equal<FunctionId list>([ area.Id; sides.Id ] |> List.sortBy StableId.value, review.ResultingChange.AffectedFunctionIds)
            Assert.Equal<TypeId list>([ shape.Id ], review.ResultingChange.AffectedTypeIds)
            Assert.Equal(ring, (review.ResultingChange.Diff.UnionVariantsAdded |> List.exactlyOne).Variant)
            let next =
                match AgentWorkflow.commit ws proposal with
                | AgentCommitResult.Committed(next, _) -> next
                | other -> failwithf "Commit failed: %A" other
            Assert.Empty(SnapshotValidator.validate (AgentWorkspace.snapshotForStructuralEditing next))
            // The raw operation: a plan short of the match set, fields out of order, a binding at
            // another type, and removing the variant added in the same transaction.
            let request: AddUnionVariantRequest = { TargetTypeId = shape.Id; Variant = ring; ArmPlans = intent.ArmPlans }
            Assert.Equal("UnionMatchArmPlanMismatch", rejectedCode (simulateRaw ws [ SemanticOperation.AddUnionVariant { request with ArmPlans = [ areaPlan ] } ]))
            let partial = { areaPlan with FieldBindings = [ areaPlan.FieldBindings.Head ] }
            Assert.Equal("UnionMatchArmPlanFieldMismatch", rejectedCode (simulateRaw ws [ SemanticOperation.AddUnionVariant { request with ArmPlans = intent.ArmPlans |> List.map (fun plan -> if plan.MatchNodeId = areaPlan.MatchNodeId then partial else plan) } ]))
            let retyped = { areaPlan with FieldBindings = areaPlan.FieldBindings |> List.map (fun item -> { item with Binding = { item.Binding with Type = TypeRef.Primitive PrimitiveType.String } }) }
            Assert.Equal("UnionMatchArmBindingTypeMismatch", rejectedCode (simulateRaw ws [ SemanticOperation.AddUnionVariant { request with ArmPlans = intent.ArmPlans |> List.map (fun plan -> if plan.MatchNodeId = areaPlan.MatchNodeId then retyped else plan) } ]))
            Assert.Equal("UnionVariantCreatedInTransaction", rejectedCode (simulateRaw ws [ SemanticOperation.AddUnionVariant request; SemanticOperation.RemoveUnionVariant ring.Id ]))
            Assert.Equal("DuplicateUnionVariantId", rejectedCode (simulateRaw ws [ SemanticOperation.AddUnionVariant { request with Variant = { ring with Id = (variantNamed session "Shape" "Dot").Id } } ]))
            Assert.Equal("TargetNotFound", rejectedCode (simulateRaw ws [ SemanticOperation.AddUnionVariant { request with TargetTypeId = StableId.createForTesting<TypeTag> "missing" } ]))
            // The same request is re-simulated identically: the plans carry every ID.
            match simulateRaw ws [ SemanticOperation.AddUnionVariant request ], simulateRaw ws [ SemanticOperation.AddUnionVariant request ] with
            | Proposed first, Proposed second -> Assert.Equal<SemanticFunction list>(first.ProposedSnapshot.Functions, second.ProposedSnapshot.Functions)
            | other -> failwithf "Expected two proposals, got %A" other)

    [<Fact>]
    let ``prepare rejects an arm body that does not type-check and a match over another Union`` () =
        withFixture (fun session _ ->
            let ws = workspaceOf session
            let shape = typeNamed session "Shape"
            let ring = newRing ()
            let plans (body: ExpressionRecipe) (matchNodeId: GraphNodeId) =
                AgentUnionVariantArmPlans.prepare ws shape.Id ring [ { MatchNodeId = matchNodeId; Body = body } ]
            let code result =
                match result with
                | Error(diagnostic: TransactionDiagnostic) -> diagnostic.Code
                | Ok _ -> failwith "Expected a rejection."
            Assert.Equal("UnionMatchArmTypeMismatch", code (plans (ExpressionRecipe.StringLiteral "ring") (fst (matchNode (fn session "sides")))))
            Assert.Equal("UnionMatchArmPlanTargetMismatch", code (plans (ExpressionRecipe.Int64Literal 0L) (fst (matchNode (fn session "orDefault")))))
            Assert.Equal("UnionMatchArmPlanTargetNotFound", code (plans (ExpressionRecipe.Int64Literal 0L) (StableId.createForTesting<GraphNodeTag> "missing")))
            // A body may already construct the new variant: it is bound against the extended snapshot.
            let construct = ExpressionRecipe.UnionConstruct(ring.Id, [], ring.Fields |> List.map (fun item -> item.Id, ExpressionRecipe.Int64Literal 1L))
            let sidesMatch = fst (matchNode (fn session "sides"))
            let intent =
                match AgentUnionVariantArmPlans.prepare ws shape.Id ring [ { MatchNodeId = sidesMatch; Body = ExpressionRecipe.MatchUnion(construct, [ [ ring.Id ], ExpressionRecipe.Int64Literal 2L; [], ExpressionRecipe.Int64Literal 0L ]) } ] with
                | Ok value -> value
                | Error error -> failwithf "prepare: %A" error
            Assert.Equal(1, intent.ArmPlans.Length))
