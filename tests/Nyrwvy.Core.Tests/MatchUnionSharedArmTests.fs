namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// MatchUnion arms that name several variants (post-M2 order 41), and the recipe's `otherwise` arm,
/// which materialization expands to an explicit variant list, over the maintained JSONL protocol.
module MatchUnionSharedArmTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore
        proposal

    /// The error code of a rejected request, or of the diagnostic that rejected a simulation.
    let private rejectionCode session line =
        use reply = execute session line
        let root = reply.RootElement
        Assert.False(root.GetProperty("ok").GetBoolean(), root.GetRawText())
        let error = root.GetProperty("error")
        let mutable details = Unchecked.defaultof<JsonElement>
        let mutable diagnostics = Unchecked.defaultof<JsonElement>
        if error.TryGetProperty("details", &details) && details.ValueKind = JsonValueKind.Object
           && details.TryGetProperty("diagnostics", &diagnostics) && diagnostics.GetArrayLength() > 0 then
            diagnostics[0].GetProperty("code").GetString()
        else error.GetProperty("code").GetString()

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private json = """{"kind":"named","name":"Json"}"""
    let private variantNames = [ "Null"; "Bool"; "Int"; "Big"; "Float"; "Str"; "Arr"; "Obj" ]

    let private fieldsOf variant =
        match variant with
        | "Bool" -> [ "flag", "bool" ]
        | "Int" -> [ "i", "int64" ]
        | "Big" -> [ "digits", "string" ]
        | "Str" -> [ "s", "string" ]
        | _ -> []

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-shared-arms-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            let variants =
                variantNames
                |> List.map (fun name ->
                    let fields =
                        fieldsOf name
                        |> List.map (fun (field, typeName) -> $"""{{"displayName":"{field}","type":{primitive typeName}}}""")
                        |> String.concat ","
                    $"""{{"displayName":"{name}","fields":[{fields}]}}""")
                |> String.concat ","
            publish session $"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"Json","typeKind":{{"kind":"union","variants":[{variants}]}}}}""" |> ignore
            run session path
        finally
            Directory.Delete(root, true)

    let private parameter = """{"kind":"parameter","name":"value"}"""
    let private boolLiteral (value: bool) = $"""{{"kind":"boolLiteral","value":{(if value then "true" else "false")}}}"""
    let private text (value: string) = $"""{{"kind":"stringLiteral","value":{JsonSerializer.Serialize value}}}"""

    let private matchJson arms otherwise =
        let otherwisePart = otherwise |> Option.map (fun body -> $""","otherwise":{body}""") |> Option.defaultValue ""
        $"""{{"kind":"matchUnion","type":"Json","input":{parameter},"arms":[{String.concat "," arms}]{otherwisePart}}}"""

    let private one variant body = $"""{{"variant":"{variant}","body":{body}}}"""
    let private many (variants: string list) body =
        $"""{{"variants":[{variants |> List.map (fun name -> "\"" + name + "\"") |> String.concat ","}],"body":{body}}}"""

    let private create session name resultType body =
        publish
            session
            $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{{"displayName":"value","type":{json}}}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private simulateCode session body =
        rejectionCode
            session
            $"""{{"id":"bad","op":"simulateCreateFunctionByName","exported":true,"displayName":"bad","parameters":[{{"displayName":"value","type":{json}}}],"resultType":{primitive "bool"},"requiredEffects":[],"body":{body}}}"""

    /// A run value of the variant with fixed field values.
    let private value variant =
        let fields =
            fieldsOf variant
            |> List.map (fun (field, typeName) ->
                let item =
                    match typeName with
                    | "bool" -> """{"kind":"bool","value":true}"""
                    | "int64" -> """{"kind":"int64","value":"7"}"""
                    | _ -> $"""{{"kind":"string","value":"{variant}-text"}}"""
                $"""{{"field":"{field}","value":{item}}}""")
            |> String.concat ","
        $"""{{"kind":"union","type":"Json","variant":"{variant}","fields":[{fields}]}}"""

    let private expectRun session name variant expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{value variant}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name} {variant}: {run}")

    let private nodeCount (proposal: JsonElement) =
        proposal.GetProperty("review").GetProperty("createdFunction").GetProperty("body").GetProperty("nodeCount").GetInt32()

    let private readSnapshot path =
        match SnapshotCodec.deserialize (File.ReadAllText path) with
        | Ok snapshot -> snapshot
        | Error error -> failwithf "Workspace did not decode: %A" error

    let private matchArms (snapshot: Snapshot) name =
        let fn = snapshot.Functions |> List.find (fun fn -> fn.DisplayName = name)
        fn.Body.Nodes
        |> List.pick (fun node ->
            match node.Kind with
            | SemanticNodeKind.MatchUnion(_, arms) -> Some arms
            | _ -> None)

    let private variantIdOf (snapshot: Snapshot) name =
        snapshot.Types
        |> List.pick (fun typeEntity ->
            match typeEntity.Kind with
            | SemanticTypeKind.Union variants -> variants |> List.tryFind (fun variant -> variant.DisplayName = name) |> Option.map (fun variant -> variant.Id)
            | SemanticTypeKind.Product _ -> None)

    let private bool (value: bool) = $"""{{"kind":"bool","value":{(if value then "true" else "false")}}}"""
    let private str (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``Shared arms and otherwise store each body once, run like the full match and keep every variant named`` () =
        withSession (fun session path ->
            let shared = create session "isStrShared" (primitive "bool") (matchJson [ one "Str" (boolLiteral true) ] (Some(boolLiteral false)))
            let full =
                create session "isStrFull" (primitive "bool") (matchJson (variantNames |> List.map (fun name -> one name (boolLiteral (name = "Str")))) None)
            Assert.True(nodeCount shared < nodeCount full, $"shared {nodeCount shared} nodes, full {nodeCount full}")
            let field = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Json","variant":"Str","field":"s"}"""
            create
                session
                "describe"
                (primitive "string")
                (matchJson [ many [ "Obj"; "Null"; "Arr" ] (text "empty"); one "Str" field ] (Some(text "scalar")))
            |> ignore
            for variant in variantNames do
                expectRun session "isStrShared" variant (bool (variant = "Str"))
                expectRun session "isStrFull" variant (bool (variant = "Str"))
                let described =
                    match variant with
                    | "Null" | "Arr" | "Obj" -> "empty"
                    | "Str" -> "Str-text"
                    | _ -> "scalar"
                expectRun session "describe" variant (str described)

            let snapshot = readSnapshot path
            Assert.Empty(SnapshotValidator.validate snapshot)
            // The otherwise arm was expanded to the seven variants Str does not take, stored as one arm.
            let sharedArms = matchArms snapshot "isStrShared"
            Assert.Equal(2, sharedArms.Length)
            Assert.Equal(7, (sharedArms |> List.find (fun arm -> arm.VariantIds.Length > 1)).VariantIds.Length)
            Assert.All(sharedArms |> List.filter (fun arm -> arm.VariantIds.Length > 1), fun arm -> Assert.Empty(arm.FieldBindings))
            let describeArms = matchArms snapshot "describe"
            Assert.Equal<int list>([ 1; 3; 4 ], describeArms |> List.map (fun arm -> arm.VariantIds.Length) |> List.sort)
            // One-variant arms keep the single-ID persisted form; several variants use the list form.
            let persisted = File.ReadAllText path
            Assert.Contains("\"variantIds\"", persisted)
            Assert.Contains("\"variantId\"", persisted)
            let json = SnapshotCodec.serialize snapshot |> Result.defaultWith (failwithf "reserialize: %A")
            let restored = SnapshotCodec.deserialize json |> Result.defaultWith (failwithf "decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)

            // A ninth variant leaves every match over Json incomplete: an expanded otherwise does not absorb it.
            let withBytes =
                { snapshot with
                    Types =
                        snapshot.Types
                        |> List.map (fun typeEntity ->
                            match typeEntity.Kind with
                            | SemanticTypeKind.Union variants ->
                                { typeEntity with
                                    Kind =
                                        SemanticTypeKind.Union(
                                            variants
                                            @ [ { Id = StableId.createForTesting<VariantTag> "shared-arms-bytes"
                                                  DisplayName = "Bytes"
                                                  Fields = [] } ]
                                        ) }
                            | SemanticTypeKind.Product _ -> typeEntity) }
            let incomplete = SnapshotValidator.validate withBytes |> List.filter (fun issue -> issue.Code = "NonExhaustiveUnionMatch")
            Assert.Equal(3, incomplete.Length))

    [<Fact>]
    let ``A fieldless variant added like a variant of a shared arm joins that arm`` () =
        withSession (fun session path ->
            create
                session
                "describe"
                (primitive "string")
                (matchJson [ many [ "Null"; "Arr"; "Obj" ] (text "empty") ] (Some(text "other")))
            |> ignore
            let before = readSnapshot path
            let typeId = before.Types |> List.pick (fun typeEntity -> if typeEntity.DisplayName = "Json" then Some typeEntity.Id else None)
            publish
                session
                $"""{{"id":"variant","op":"simulateAddUnionVariantLike","typeId":"{StableId.value typeId}","sourceVariantId":"{StableId.value (variantIdOf before "Arr")}","displayName":"Set"}}"""
            |> ignore
            let after = readSnapshot path
            Assert.Empty(SnapshotValidator.validate after)
            let arms = matchArms after "describe"
            Assert.Equal(2, arms.Length)
            let setId = variantIdOf after "Set"
            Assert.Contains(arms, fun arm -> List.contains setId arm.VariantIds && List.contains (variantIdOf after "Arr") arm.VariantIds)
            expectRun session "describe" "Set" (str "empty"))

    [<Fact>]
    let ``Arm sets are checked before simulation`` () =
        withSession (fun session _ ->
            let otherwise = Some(boolLiteral false)
            Assert.Equal("PureExpressionMatchArmDuplicate", simulateCode session (matchJson [ many [ "Str"; "Str" ] (boolLiteral true) ] otherwise))
            Assert.Equal(
                "PureExpressionMatchArmDuplicate",
                simulateCode session (matchJson [ one "Str" (boolLiteral true); many [ "Str"; "Int" ] (boolLiteral true) ] otherwise)
            )
            Assert.Equal(
                "PureExpressionMatchOtherwiseUnused",
                simulateCode session (matchJson (variantNames |> List.map (fun name -> one name (boolLiteral true))) otherwise)
            )
            Assert.Equal("PureExpressionMatchArmMismatch", simulateCode session (matchJson [ many [ "Str"; "Int" ] (boolLiteral true) ] None))
            // The body of an otherwise arm sees no fields, even when it covers exactly one variant.
            let intField = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Json","variant":"Int","field":"i"}"""
            let allButInt = variantNames |> List.filter ((<>) "Int")
            let usesField =
                matchJson
                    [ many allButInt (boolLiteral true) ]
                    (Some $"""{{"kind":"int64Equal","left":{intField},"right":{{"kind":"int64Literal","value":"7"}}}}""")
            Assert.Equal("PureExpressionPatternSlotNotFound", simulateCode session usesField)
            Assert.Equal("ProtocolError", simulateCode session (matchJson [ $"""{{"variant":"Str","variants":["Int"],"body":{boolLiteral true}}}""" ] otherwise))
            Assert.Equal("ProtocolError", simulateCode session (matchJson [ many [] (boolLiteral true) ] otherwise)))
