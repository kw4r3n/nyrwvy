namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Recursive and mutually recursive type declarations created in one reviewed group.
module RecursiveTypeGroupTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private reference name = StableId.createForTesting<TypeTag> ("type-group-reference-" + name)
    let private field displayName typeRef: AgentTypeFieldIntent = { DisplayName = displayName; Type = typeRef }
    let private variant displayName fields: AgentTypeVariantIntent = { DisplayName = displayName; Fields = fields }
    let private product name fields: AgentTypeDeclarationGroupMember =
        { Reference = reference name; DisplayName = name; Kind = AgentTypeDeclarationKindIntent.Product fields }
    let private union name variants: AgentTypeDeclarationGroupMember =
        { Reference = reference name; DisplayName = name; Kind = AgentTypeDeclarationKindIntent.Union variants }
    let private named name = TypeRef.Named(reference name)

    let private workspace () =
        let snapshot =
            { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-type-group") with
                Types = [ { Id = StableId.createForTesting<TypeTag> "type-group-existing"; DisplayName = "Existing"; Kind = SemanticTypeKind.Product [] } ] }
        match AgentWorkspace.create snapshot with
        | Ok value -> value, snapshot.Id
        | Error issues -> failwithf "Invalid fixture: %A" issues

    let private json =
        union "Json" [ variant "Null" []; variant "Str" [ field "value" stringType ]; variant "Arr" [ field "items" (listOf (named "Json")) ]; variant "Obj" [ field "members" (listOf (named "Member")) ] ]
    let private memberType = product "Member" [ field "key" stringType; field "value" (named "Json") ]

    [<Fact>]
    let ``a mutually recursive group publishes with allocated identities and references rewritten to them`` () =
        let ws, baseId = workspace ()
        let proposal =
            match AgentTypeDeclarationWorkflow.simulateGroup ws { ExpectedBaseSnapshotId = baseId; Declarations = [ json; memberType ] } with
            | Ok value -> value
            | Error error -> failwithf "Unexpected rejection: %A" error
        let created = (AgentTypeDeclarationGroupProposal.review proposal).CreatedTypes
        Assert.Equal<string list>([ "Json"; "Member" ], created |> List.map (fun item -> item.DisplayName))
        let jsonId, memberId = created[0].Id, created[1].Id
        Assert.NotEqual(reference "Json", jsonId)
        let fieldTypes =
            created
            |> List.collect (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields -> fields
                | SemanticTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields))
            |> List.map (fun item -> item.Type)
        Assert.Equal<TypeRef list>([ stringType; listOf (TypeRef.Named jsonId); listOf (TypeRef.Named memberId); stringType; TypeRef.Named jsonId ], fieldTypes)
        match AgentTypeDeclarationWorkflow.commitGroup ws proposal with
        | AgentTypeDeclarationGroupCommitResult.Committed(next, publication) ->
            Assert.Equal<TypeId list>([ jsonId; memberId ], publication.CreatedTypeIds)
            Assert.Empty(SnapshotValidator.validate (AgentWorkspace.snapshotForStructuralEditing next))
        | AgentTypeDeclarationGroupCommitResult.Rejected diagnostics -> failwithf "Unexpected commit rejection: %A" diagnostics

    [<Theory>]
    [<InlineData("empty", "TypeDeclarationGroupEmpty")>]
    [<InlineData("duplicate", "TypeDeclarationDuplicateDisplayName")>]
    [<InlineData("existingName", "TypeDeclarationDuplicateDisplayName")>]
    [<InlineData("existingReference", "TypeDeclarationGroupReferenceInvalid")>]
    [<InlineData("selfProduct", "TypeDeclarationUninhabited")>]
    [<InlineData("mutualProducts", "TypeDeclarationUninhabited")>]
    let ``invalid groups are rejected before anything is created`` (case: string) (code: string) =
        let ws, baseId = workspace ()
        let declarations =
            match case with
            | "empty" -> []
            | "duplicate" -> [ product "Dup" []; { product "Dup" [] with Reference = reference "other" } ]
            | "existingName" -> [ product "Existing" [] ]
            | "existingReference" -> [ { product "Fresh" [] with Reference = StableId.createForTesting<TypeTag> "type-group-existing" } ]
            | "selfProduct" -> [ product "Loop" [ field "next" (named "Loop") ] ]
            | "mutualProducts" -> [ product "A" [ field "b" (named "B") ]; product "B" [ field "a" (named "A") ] ]
            | other -> failwith other
        match AgentTypeDeclarationWorkflow.simulateGroup ws { ExpectedBaseSnapshotId = baseId; Declarations = declarations } with
        | Error(AgentTypeDeclarationWorkflowError.SimulationRejected [ diagnostic ]) -> Assert.Equal(code, diagnostic.Code)
        | result -> failwithf "Expected %s, got %A" code result

    [<Fact>]
    let ``a cycle broken by an Option or a List is inhabited`` () =
        let ws, baseId = workspace ()
        let declarations = [ product "Node" [ field "next" (optionOf (named "Node")); field "tree" (named "Tree") ]; product "Tree" [ field "children" (listOf (named "Node")) ] ]
        match AgentTypeDeclarationWorkflow.simulateGroup ws { ExpectedBaseSnapshotId = baseId; Declarations = declarations } with
        | Ok _ -> ()
        | Error error -> failwithf "Unexpected rejection: %A" error

    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore
        proposal.GetProperty("review")

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-type-group-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            use session =
                match AuthoringSession.createNew (Path.Combine(root, "workspace.json")) with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            run session
        finally
            Directory.Delete(root, true)

    let private str = """{"kind":"primitive","primitive":"string"}"""
    let private jsonValue variant fields = $"""{{"kind":"union","type":"Json","variant":"{variant}","fields":[{fields}]}}"""
    let private text value = jsonValue "Str" $"""{{"field":"value","value":{{"kind":"string","value":"{value}"}}}}"""

    [<Fact>]
    let ``recursive types are declared by name, reviewed as a group and run like any recursive type`` () =
        withSession (fun session ->
            let tree =
                publish session $"""{{"id":"t","op":"simulateCreateTypeByName","displayName":"Tree","typeKind":{{"kind":"union","variants":[{{"displayName":"Leaf","fields":[{{"displayName":"label","type":{str}}}]}},{{"displayName":"Node","fields":[{{"displayName":"children","type":{{"kind":"builtinGeneric","generic":"list","arguments":[{{"kind":"named","name":"Tree"}}]}}}}]}}]}}}}"""
            Assert.Equal("createTypes", tree.GetProperty("kind").GetString())
            let plain = publish session $"""{{"id":"p","op":"simulateCreateTypeByName","displayName":"Plain","typeKind":{{"kind":"product","fields":[{{"displayName":"value","type":{str}}}]}}}}"""
            Assert.Equal("createType", plain.GetProperty("kind").GetString())
            let group =
                publish session $"""{{"id":"g","op":"simulateCreateTypesByName","types":[{{"displayName":"Json","typeKind":{{"kind":"union","variants":[{{"displayName":"Str","fields":[{{"displayName":"value","type":{str}}}]}},{{"displayName":"Obj","fields":[{{"displayName":"members","type":{{"kind":"builtinGeneric","generic":"list","arguments":[{{"kind":"named","name":"Member"}}]}}}}]}}]}}}},{{"displayName":"Member","typeKind":{{"kind":"product","fields":[{{"displayName":"key","type":{str}}},{{"displayName":"value","type":{{"kind":"named","name":"Json"}}}}]}}}}]}}"""
            Assert.Equal(2, group.GetProperty("createdTypes").GetArrayLength())
            publish session """{"id":"f","op":"simulateCreateFunctionByName","displayName":"single","parameters":[{"displayName":"key","type":{"kind":"primitive","primitive":"string"}},{"displayName":"value","type":{"kind":"named","name":"Json"}}],"resultType":{"kind":"named","name":"Json"},"requiredEffects":[],"body":{"kind":"unionConstruct","type":"Json","variant":"Obj","fields":[{"field":"members","value":{"kind":"listConstruct","itemType":{"kind":"named","name":"Member"},"items":[{"kind":"productConstruct","type":"Member","fields":[{"field":"key","value":{"kind":"parameter","name":"key"}},{"field":"value","value":{"kind":"parameter","name":"value"}}]}]}}]}}"""
            |> ignore
            let members items = $"""{{"field":"members","value":{{"kind":"list","itemType":{{"kind":"named","name":"Member"}},"items":[{items}]}}}}"""
            let pair key value = $"""{{"kind":"product","type":"Member","fields":[{{"field":"key","value":{{"kind":"string","value":"{key}"}}}},{{"field":"value","value":{value}}}]}}"""
            let inner = jsonValue "Obj" (members (pair "b" (text "c")))
            let run = result session $"""{{"id":"r","op":"runPureByName","function":"single","arguments":[{{"kind":"string","value":"a"}},{inner}],"expected":{jsonValue "Obj" (members (pair "a" inner))}}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.GetRawText())
            use unknown = execute session $"""{{"id":"u","op":"simulateCreateTypesByName","types":[{{"displayName":"Box","typeKind":{{"kind":"product","fields":[{{"displayName":"inner","type":{{"kind":"named","name":"Missing"}}}}]}}}}]}}"""
            Assert.Equal("ProtocolError", unknown.RootElement.GetProperty("error").GetProperty("code").GetString()))
