namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
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
        { Exported = true; TypeParameters = []; Reference = reference name; DisplayName = name; Kind = AgentTypeDeclarationKindIntent.Product fields }
    let private union name variants: AgentTypeDeclarationGroupMember =
        { Exported = true; TypeParameters = []; Reference = reference name; DisplayName = name; Kind = AgentTypeDeclarationKindIntent.Union variants }
    let private named name = TypeRef.Named(reference name, [])

    let private workspace () =
        let snapshot =
            { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-type-group") with
                Types = [ { Exported = true; TypeParameters = []; Id = StableId.createForTesting<TypeTag> "type-group-existing"; DisplayName = "Existing"; Kind = SemanticTypeKind.Product [] } ] }
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
        Assert.Equal<TypeRef list>([ stringType; listOf (TypeRef.Named(jsonId, [])); listOf (TypeRef.Named(memberId, [])); stringType; TypeRef.Named(jsonId, []) ], fieldTypes)
        match AgentTypeDeclarationWorkflow.commitGroup ws proposal with
        | AgentTypeDeclarationGroupCommitResult.Committed(next, publication) ->
            Assert.Equal<TypeId list>([ jsonId; memberId ], publication.CreatedTypeIds)
            Assert.Empty(SnapshotValidator.validate (AgentWorkspace.snapshotForStructuralEditing next))
        | AgentTypeDeclarationGroupCommitResult.Rejected diagnostics -> failwithf "Unexpected commit rejection: %A" diagnostics

    [<Fact>]
    let ``a cycle broken by an Option or a List is inhabited`` () =
        let ws, baseId = workspace ()
        let declarations = [ product "Node" [ field "next" (optionOf (named "Node")); field "tree" (named "Tree") ]; product "Tree" [ field "children" (listOf (named "Node")) ] ]
        match AgentTypeDeclarationWorkflow.simulateGroup ws { ExpectedBaseSnapshotId = baseId; Declarations = declarations } with
        | Ok _ -> ()
        | Error error -> failwithf "Unexpected rejection: %A" error

    let private str = """{"kind":"primitive","primitive":"string"}"""
    let private jsonValue variant fields = $"""{{"kind":"union","type":"Json","variant":"{variant}","fields":[{fields}]}}"""
    let private text value = jsonValue "Str" $"""{{"field":"value","value":{{"kind":"string","value":"{value}"}}}}"""
