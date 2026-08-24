namespace Nayamnyam.Core

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type SnapshotCodecError =
    | InvalidJson of string
    | InvalidShape of string
    | UnsupportedSchemaVersion of int
    | InvalidSnapshot of ValidationIssue list

[<RequireQualifiedAccess>]
module SnapshotCodec =
    let private schemaVersion = 2

    let private invalidShape message = raise (InvalidDataException message)

    let private ensureObject (context: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            invalidShape $"{context} must be a JSON object."

    let private ensureProperties
        (context: string)
        (required: Set<string>)
        (allowed: Set<string>)
        (element: JsonElement)
        =
        ensureObject context element

        let propertyNames =
            element.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> List.ofSeq

        let actual = Set.ofList propertyNames

        if propertyNames.Length <> actual.Count then
            invalidShape $"{context} contains duplicate property names."

        let missing = Set.difference required actual
        let unexpected = Set.difference actual allowed

        if not missing.IsEmpty then
            let missingText = String.Join(", ", missing)
            invalidShape $"{context} is missing required properties: {missingText}."

        if not unexpected.IsEmpty then
            let unexpectedText = String.Join(", ", unexpected)
            invalidShape $"{context} contains unsupported properties: {unexpectedText}."

    let private requireProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) then
            value
        else
            invalidShape $"Missing required property '{name}'."

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private readString (name: string) (element: JsonElement) =
        let value = requireProperty name element

        if value.ValueKind <> JsonValueKind.String then
            invalidShape $"Property '{name}' must be a string."

        match value.GetString() with
        | null -> invalidShape $"Property '{name}' must not be null."
        | text -> text

    let private readInt32 (name: string) (element: JsonElement) =
        let value = requireProperty name element
        let mutable number = 0

        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt32(&number)) then
            invalidShape $"Property '{name}' must be a 32-bit integer."

        number

    let private readId<'Kind> (name: string) (element: JsonElement): StableId<'Kind> =
        let text = readString name element

        match StableId.tryCreate<'Kind> text with
        | Ok id -> id
        | Error error -> invalidShape $"Property '{name}' contains an invalid stable ID: {error}."

    let private readArray name reader (root: JsonElement) =
        let value = requireProperty name root

        if value.ValueKind <> JsonValueKind.Array then
            invalidShape $"Property '{name}' must be an array."

        value.EnumerateArray() |> Seq.map reader |> List.ofSeq

    let private writeType (writer: Utf8JsonWriter) (item: SemanticType) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value item.Id)
        writer.WriteString("displayName", item.DisplayName)
        writer.WriteEndObject()

    let private writeParameter (writer: Utf8JsonWriter) (parameter: SemanticParameter) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WriteString("displayName", parameter.DisplayName)
        writer.WriteString("typeId", StableId.value parameter.TypeId)
        writer.WriteEndObject()

    let private writeArgument (writer: Utf8JsonWriter) (argument: ArgumentBinding) =
        writer.WriteStartObject()
        writer.WriteString("targetParameterId", StableId.value argument.TargetParameterId)
        writer.WriteString("valueNodeId", StableId.value argument.ValueNodeId)
        writer.WriteEndObject()

    let private writeNode (writer: Utf8JsonWriter) (node: SemanticNode) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value node.Id)

        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId ->
            writer.WriteString("kind", "parameterReference")
            writer.WriteString("parameterId", StableId.value parameterId)
        | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
            writer.WriteString("kind", "directCall")
            writer.WriteString("targetFunctionId", StableId.value targetFunctionId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId, StableId.value argument.ValueNodeId)
            |> List.iter (writeArgument writer)
            writer.WriteEndArray()

        writer.WriteEndObject()

    let private writeFunction (writer: Utf8JsonWriter) (functionEntity: SemanticFunction) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value functionEntity.Id)
        writer.WriteString("displayName", functionEntity.DisplayName)
        writer.WritePropertyName("parameters")
        writer.WriteStartArray()
        functionEntity.Parameters |> List.iter (writeParameter writer)
        writer.WriteEndArray()
        writer.WriteString("resultTypeId", StableId.value functionEntity.ResultTypeId)
        writer.WritePropertyName("nodes")
        writer.WriteStartArray()
        functionEntity.Nodes
        |> List.sortBy (fun node -> StableId.value node.Id)
        |> List.iter (writeNode writer)
        writer.WriteEndArray()
        writer.WriteString("resultNodeId", StableId.value functionEntity.ResultNodeId)
        writer.WriteEndObject()

    let private writeObligation (writer: Utf8JsonWriter) (obligation: MissingArgumentObligation) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value obligation.Id)
        writer.WriteString("kind", "missingArgument")
        writer.WriteString("originTransactionId", StableId.value obligation.OriginTransactionId)
        writer.WriteString("callNodeId", StableId.value obligation.CallNodeId)
        writer.WriteString("parameterId", StableId.value obligation.ParameterId)

        match obligation.Status with
        | ObligationStatus.Open -> writer.WriteString("status", "open")
        | ObligationStatus.Resolved resolution ->
            writer.WriteString("status", "resolved")
            writer.WriteString("resolutionTransactionId", StableId.value resolution.TransactionId)
            writer.WriteString("resolutionValueNodeId", StableId.value resolution.ValueNodeId)

        writer.WriteEndObject()

    let private writeSnapshot (snapshot: Snapshot) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)

        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", schemaVersion)
        writer.WriteString("snapshotId", StableId.value snapshot.Id)

        match snapshot.ParentId with
        | Some parentId -> writer.WriteString("parentSnapshotId", StableId.value parentId)
        | None -> ()

        writer.WritePropertyName("types")
        writer.WriteStartArray()
        snapshot.Types
        |> List.sortBy (fun item -> StableId.value item.Id)
        |> List.iter (writeType writer)
        writer.WriteEndArray()

        writer.WritePropertyName("functions")
        writer.WriteStartArray()
        snapshot.Functions
        |> List.sortBy (fun item -> StableId.value item.Id)
        |> List.iter (writeFunction writer)
        writer.WriteEndArray()

        writer.WritePropertyName("obligations")
        writer.WriteStartArray()
        snapshot.Obligations
        |> List.sortBy (fun item -> StableId.value item.Id)
        |> List.iter (writeObligation writer)
        writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let serialize (snapshot: Snapshot): Result<string, SnapshotCodecError> =
        match SnapshotValidator.validate snapshot with
        | [] -> Ok(writeSnapshot snapshot)
        | issues -> Error(SnapshotCodecError.InvalidSnapshot issues)

    let private readType (element: JsonElement): SemanticType =
        ensureProperties "Type" (set [ "id"; "displayName" ]) (set [ "id"; "displayName" ]) element
        { Id = readId<TypeTag> "id" element
          DisplayName = readString "displayName" element }

    let private readParameter (element: JsonElement): SemanticParameter =
        ensureProperties
            "Parameter"
            (set [ "id"; "displayName"; "typeId" ])
            (set [ "id"; "displayName"; "typeId" ])
            element
        { Id = readId<ParameterTag> "id" element
          DisplayName = readString "displayName" element
          TypeId = readId<TypeTag> "typeId" element }

    let private readArgument (element: JsonElement): ArgumentBinding =
        ensureProperties
            "Argument"
            (set [ "targetParameterId"; "valueNodeId" ])
            (set [ "targetParameterId"; "valueNodeId" ])
            element
        { TargetParameterId = readId<ParameterTag> "targetParameterId" element
          ValueNodeId = readId<GraphNodeTag> "valueNodeId" element }

    let private readNode (element: JsonElement): SemanticNode =
        let kind = readString "kind" element

        if kind = "parameterReference" then
            ensureProperties
                "Parameter reference node"
                (set [ "id"; "kind"; "parameterId" ])
                (set [ "id"; "kind"; "parameterId" ])
                element
            { Id = readId<GraphNodeTag> "id" element
              Kind = SemanticNodeKind.ParameterReference(readId<ParameterTag> "parameterId" element) }
        elif kind = "directCall" then
            ensureProperties
                "Direct call node"
                (set [ "id"; "kind"; "targetFunctionId"; "arguments" ])
                (set [ "id"; "kind"; "targetFunctionId"; "arguments" ])
                element
            { Id = readId<GraphNodeTag> "id" element
              Kind =
                SemanticNodeKind.DirectCall(
                    readId<FunctionTag> "targetFunctionId" element,
                    readArray "arguments" readArgument element
                ) }
        else
            invalidShape $"Unsupported graph node kind '{kind}'."

    let private readFunction (element: JsonElement): SemanticFunction =
        ensureProperties
            "Function"
            (set [ "id"; "displayName"; "parameters"; "resultTypeId"; "nodes"; "resultNodeId" ])
            (set [ "id"; "displayName"; "parameters"; "resultTypeId"; "nodes"; "resultNodeId" ])
            element
        { Id = readId<FunctionTag> "id" element
          DisplayName = readString "displayName" element
          Parameters = readArray "parameters" readParameter element
          ResultTypeId = readId<TypeTag> "resultTypeId" element
          Nodes = readArray "nodes" readNode element
          ResultNodeId = readId<GraphNodeTag> "resultNodeId" element }

    let private readObligation (element: JsonElement): MissingArgumentObligation =
        let status = readString "status" element
        let kind = readString "kind" element

        if kind <> "missingArgument" then
            invalidShape $"Unsupported obligation kind '{kind}'."

        if status = "open" then
            ensureProperties
                "Open missing-argument obligation"
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status" ])
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status" ])
                element
            { Id = readId<ObligationTag> "id" element
              OriginTransactionId = readId<TransactionTag> "originTransactionId" element
              CallNodeId = readId<GraphNodeTag> "callNodeId" element
              ParameterId = readId<ParameterTag> "parameterId" element
              Status = ObligationStatus.Open }
        elif status = "resolved" then
            ensureProperties
                "Resolved missing-argument obligation"
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status"; "resolutionTransactionId"; "resolutionValueNodeId" ])
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status"; "resolutionTransactionId"; "resolutionValueNodeId" ])
                element
            { Id = readId<ObligationTag> "id" element
              OriginTransactionId = readId<TransactionTag> "originTransactionId" element
              CallNodeId = readId<GraphNodeTag> "callNodeId" element
              ParameterId = readId<ParameterTag> "parameterId" element
              Status =
                ObligationStatus.Resolved
                    { TransactionId = readId<TransactionTag> "resolutionTransactionId" element
                      ValueNodeId = readId<GraphNodeTag> "resolutionValueNodeId" element } }
        else
            invalidShape $"Unsupported obligation status '{status}'."

    let deserialize (json: string): Result<Snapshot, SnapshotCodecError> =
        if isNull json then
            Error(SnapshotCodecError.InvalidJson "Snapshot JSON must not be null.")
        else
            try
                use document = JsonDocument.Parse(json)
                let root = document.RootElement

                ensureProperties
                    "Snapshot"
                    (set [ "schemaVersion"; "snapshotId"; "types"; "functions"; "obligations" ])
                    (set [ "schemaVersion"; "snapshotId"; "parentSnapshotId"; "types"; "functions"; "obligations" ])
                    root

                let version = readInt32 "schemaVersion" root

                if version <> schemaVersion then
                    Error(SnapshotCodecError.UnsupportedSchemaVersion version)
                else
                    let parentId =
                        match tryProperty "parentSnapshotId" root with
                        | Some _ -> Some(readId<SnapshotTag> "parentSnapshotId" root)
                        | None -> None

                    let snapshot =
                        { Id = readId<SnapshotTag> "snapshotId" root
                          ParentId = parentId
                          Types = readArray "types" readType root
                          Functions = readArray "functions" readFunction root
                          Obligations = readArray "obligations" readObligation root }

                    match SnapshotValidator.validate snapshot with
                    | [] -> Ok snapshot
                    | issues -> Error(SnapshotCodecError.InvalidSnapshot issues)
            with
            | :? JsonException as error -> Error(SnapshotCodecError.InvalidJson error.Message)
            | :? InvalidDataException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? ArgumentException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? KeyNotFoundException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
