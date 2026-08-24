namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module ValueGraphTests =
    let private requireIssue code issues =
        if not (issues |> List.exists (fun issue -> issue.Code = code)) then
            failwithf "Expected validation issue %s, got %A" code issues

    let private productSnapshot () =
        let typeId = StableId.createForTesting<TypeTag> "type-raw-input"
        let fieldId = StableId.createForTesting<FieldTag> "field-name"
        let functionId = StableId.createForTesting<FunctionTag> "function-product"
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-name"
        let parameterNodeId = StableId.createForTesting<GraphNodeTag> "node-name"
        let constructNodeId = StableId.createForTesting<GraphNodeTag> "node-construct"
        let accessNodeId = StableId.createForTesting<GraphNodeTag> "node-access"
        let stringType = TypeRef.Primitive PrimitiveType.String

        let field: SemanticField =
            { Id = fieldId
              DisplayName = "name"
              Type = stringType }

        let typeEntity: SemanticType =
            { Id = typeId
              DisplayName = "RawUserInput"
              Kind = SemanticTypeKind.Product [ field ] }

        let parameter: SemanticParameter =
            { Id = parameterId
              DisplayName = "name"
              Type = stringType }

        let parameterNode: SemanticNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameterId }

        let constructNode: SemanticNode =
            { Id = constructNodeId
              Kind =
                SemanticNodeKind.ProductConstruct(
                    typeId,
                    [ { TargetFieldId = fieldId
                        ValueNodeId = parameterNodeId } ]
                ) }

        let accessNode: SemanticNode =
            { Id = accessNodeId
              Kind = SemanticNodeKind.FieldAccess(constructNodeId, fieldId) }

        let functionEntity: SemanticFunction =
            { Id = functionId
              DisplayName = "round_trip_name"
              Parameters = [ parameter ]
              ResultType = stringType
              Body =
                { Nodes = [ parameterNode; constructNode; accessNode ]
                  ResultNodeId = accessNodeId } }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-product"
              ParentId = None
              Types = [ typeEntity ]
              Functions = [ functionEntity ]
              Obligations = [] }

        snapshot, functionId, constructNodeId

    let private unionMatchSnapshot includeInvalidEmailArm =
        let unionTypeId = StableId.createForTesting<TypeTag> "type-validation-error"
        let emptyNameVariantId = StableId.createForTesting<VariantTag> "variant-empty-name"
        let invalidEmailVariantId = StableId.createForTesting<VariantTag> "variant-invalid-email"
        let functionId = StableId.createForTesting<FunctionTag> "function-render-error"
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-error"
        let parameterNodeId = StableId.createForTesting<GraphNodeTag> "node-error"
        let matchNodeId = StableId.createForTesting<GraphNodeTag> "node-match-error"
        let emptyLiteralId = StableId.createForTesting<GraphNodeTag> "node-empty-name-text"
        let invalidLiteralId = StableId.createForTesting<GraphNodeTag> "node-invalid-email-text"
        let stringType = TypeRef.Primitive PrimitiveType.String

        let errorType: SemanticType =
            { Id = unionTypeId
              DisplayName = "ValidationError"
              Kind =
                SemanticTypeKind.Union
                    [ { Id = emptyNameVariantId
                        DisplayName = "EmptyName"
                        Fields = [] }
                      { Id = invalidEmailVariantId
                        DisplayName = "InvalidEmail"
                        Fields = [] } ] }

        let parameter: SemanticParameter =
            { Id = parameterId
              DisplayName = "error"
              Type = TypeRef.Named unionTypeId }

        let parameterNode: SemanticNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameterId }

        let emptyLiteral: SemanticNode =
            { Id = emptyLiteralId
              Kind = SemanticNodeKind.StringLiteral "empty name" }

        let invalidLiteral: SemanticNode =
            { Id = invalidLiteralId
              Kind = SemanticNodeKind.StringLiteral "invalid email" }

        let emptyArm: UnionMatchArm =
            { VariantId = emptyNameVariantId
              FieldBindings = []
              Body =
                { Nodes = [ emptyLiteral ]
                  ResultNodeId = emptyLiteralId } }

        let invalidArm: UnionMatchArm =
            { VariantId = invalidEmailVariantId
              FieldBindings = []
              Body =
                { Nodes = [ invalidLiteral ]
                  ResultNodeId = invalidLiteralId } }

        let arms = if includeInvalidEmailArm then [ emptyArm; invalidArm ] else [ emptyArm ]

        let matchNode: SemanticNode =
            { Id = matchNodeId
              Kind = SemanticNodeKind.MatchUnion(parameterNodeId, arms) }

        let functionEntity: SemanticFunction =
            { Id = functionId
              DisplayName = "render_error"
              Parameters = [ parameter ]
              ResultType = stringType
              Body =
                { Nodes = [ parameterNode; matchNode ]
                  ResultNodeId = matchNodeId } }

        { Id = StableId.createForTesting<SnapshotTag> "snapshot-union-match"
          ParentId = None
          Types = [ errorType ]
          Functions = [ functionEntity ]
          Obligations = [] }

    [<Fact>]
    let ``product construction and field access use stable field identity`` () =
        let snapshot, _, _ = productSnapshot ()
        Assert.Empty(SnapshotValidator.validate snapshot)

    [<Fact>]
    let ``product construction must bind every declared field exactly once`` () =
        let snapshot, functionId, constructNodeId = productSnapshot ()
        let functions =
            snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id <> functionId then
                    functionEntity
                else
                    let updateNode (node: SemanticNode) =
                        if node.Id = constructNodeId then
                            match node.Kind with
                            | SemanticNodeKind.ProductConstruct(typeId, _) ->
                                { node with Kind = SemanticNodeKind.ProductConstruct(typeId, []) }
                            | _ -> node
                        else
                            node
                    { functionEntity with Body = ValueGraph.mapNodes updateNode functionEntity.Body })

        SnapshotValidator.validate { snapshot with Functions = functions }
        |> requireIssue "FieldBindingMismatch"

    [<Fact>]
    let ``union matching is exhaustive by stable variant identity`` () =
        let valid = unionMatchSnapshot true
        Assert.Empty(SnapshotValidator.validate valid)

        let incomplete = unionMatchSnapshot false
        SnapshotValidator.validate incomplete |> requireIssue "NonExhaustiveUnionMatch"

    [<Fact>]
    let ``pattern bindings cannot escape their match arm`` () =
        let optionType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.String ])
        let functionId = StableId.createForTesting<FunctionTag> "function-option-scope"
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-option"
        let parameterNodeId = StableId.createForTesting<GraphNodeTag> "node-option"
        let matchNodeId = StableId.createForTesting<GraphNodeTag> "node-option-match"
        let someReferenceId = StableId.createForTesting<GraphNodeTag> "node-some-reference"
        let noneLiteralId = StableId.createForTesting<GraphNodeTag> "node-none-text"
        let escapedReferenceId = StableId.createForTesting<GraphNodeTag> "node-escaped-reference"
        let bindingId = StableId.createForTesting<PatternBindingTag> "binding-option-value"
        let binding: PatternBinding =
            { Id = bindingId
              DisplayName = Some "value"
              Type = TypeRef.Primitive PrimitiveType.String }

        let someReference: SemanticNode =
            { Id = someReferenceId
              Kind = SemanticNodeKind.PatternReference bindingId }

        let noneLiteral: SemanticNode =
            { Id = noneLiteralId
              Kind = SemanticNodeKind.StringLiteral "none" }

        let matchNode: SemanticNode =
            { Id = matchNodeId
              Kind =
                SemanticNodeKind.MatchOption(
                    parameterNodeId,
                    { ValueBinding = binding
                      Body =
                        { Nodes = [ someReference ]
                          ResultNodeId = someReferenceId } },
                    { Nodes = [ noneLiteral ]
                      ResultNodeId = noneLiteralId }
                ) }

        let parameterNode: SemanticNode =
            { Id = parameterNodeId
              Kind = SemanticNodeKind.ParameterReference parameterId }

        let escapedReference: SemanticNode =
            { Id = escapedReferenceId
              Kind = SemanticNodeKind.PatternReference bindingId }

        let functionEntity: SemanticFunction =
            { Id = functionId
              DisplayName = "option_scope"
              Parameters =
                [ { Id = parameterId
                    DisplayName = "input"
                    Type = optionType } ]
              ResultType = TypeRef.Primitive PrimitiveType.String
              Body =
                { Nodes = [ parameterNode; matchNode; escapedReference ]
                  ResultNodeId = escapedReferenceId } }

        let snapshot: Snapshot =
            { Id = StableId.createForTesting<SnapshotTag> "snapshot-option-scope"
              ParentId = None
              Types = []
              Functions = [ functionEntity ]
              Obligations = [] }

        SnapshotValidator.validate snapshot |> requireIssue "UnknownPatternReference"

    [<Fact>]
    let ``reference JSON codec round-trips nested value graphs`` () =
        let snapshot = unionMatchSnapshot true
        let encoded =
            match SnapshotCodec.serialize snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected serialization failure: %A" error
        let decoded =
            match SnapshotCodec.deserialize encoded with
            | Ok value -> value
            | Error error -> failwithf "Unexpected deserialization failure: %A" error
        let reencoded =
            match SnapshotCodec.serialize decoded with
            | Ok value -> value
            | Error error -> failwithf "Unexpected reserialization failure: %A" error

        Assert.Equal(encoded, reencoded)
