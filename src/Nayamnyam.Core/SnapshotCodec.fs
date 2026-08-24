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
    let private schemaVersion = 1

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
        |> List.iter (fun item ->
            writer.WriteStartObject()
            writer.WriteString("id", StableId.value item.Id)
            writer.WriteString("displayName", item.DisplayName)
            writer.WriteEndObject())

        writer.WriteEndArray()
        writer.WritePropertyName("functions")
        writer.WriteStartArray()

        snapshot.Functions
        |> List.sortBy (fun item -> StableId.value item.Id)
        |> List.iter (fun item ->
            writer.WriteStartObject()
            writer.WriteString("id", StableId.value item.Id)
            writer.WriteString("displayName", item.DisplayName)
            writer.WriteEndObject())

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

    let private readFunction (element: JsonElement): SemanticFunction =
        ensureProperties "Function" (set [ "id"; "displayName" ]) (set [ "id"; "displayName" ]) element

        { Id = readId<FunctionTag> "id" element
          DisplayName = readString "displayName" element }

    let private readArray name reader (root: JsonElement) =
        let value = requireProperty name root

        if value.ValueKind <> JsonValueKind.Array then
            invalidShape $"Property '{name}' must be an array."

        value.EnumerateArray() |> Seq.map reader |> List.ofSeq

    let deserialize (json: string): Result<Snapshot, SnapshotCodecError> =
        if isNull json then
            Error(SnapshotCodecError.InvalidJson "Snapshot JSON must not be null.")
        else
            try
                use document = JsonDocument.Parse(json)
                let root = document.RootElement

                ensureProperties
                    "Snapshot"
                    (set [ "schemaVersion"; "snapshotId"; "types"; "functions" ])
                    (set [ "schemaVersion"; "snapshotId"; "parentSnapshotId"; "types"; "functions" ])
                    root

                let version = readInt32 "schemaVersion" root

                if version <> schemaVersion then
                    Error(SnapshotCodecError.UnsupportedSchemaVersion version)
                else
                    let parentId =
                        let mutable property = Unchecked.defaultof<JsonElement>

                        if root.TryGetProperty("parentSnapshotId", &property) then
                            Some(readId<SnapshotTag> "parentSnapshotId" root)
                        else
                            None

                    let snapshot =
                        { Id = readId<SnapshotTag> "snapshotId" root
                          ParentId = parentId
                          Types = readArray "types" readType root
                          Functions = readArray "functions" readFunction root }

                    match SnapshotValidator.validate snapshot with
                    | [] -> Ok snapshot
                    | issues -> Error(SnapshotCodecError.InvalidSnapshot issues)
            with
            | :? JsonException as error -> Error(SnapshotCodecError.InvalidJson error.Message)
            | :? InvalidDataException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? ArgumentException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? KeyNotFoundException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
