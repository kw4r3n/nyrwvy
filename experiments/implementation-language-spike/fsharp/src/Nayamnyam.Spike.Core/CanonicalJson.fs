namespace Nayamnyam.Spike

open System
open System.IO
open System.Text
open System.Text.Json

module CanonicalJson =
    let private invalidJson message =
        raise (JsonException message)

    let private ensureObject (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            invalidJson "Expected a JSON object."

    let private ensureArray (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            invalidJson "Expected a JSON array."

    let private ensureProperties expected (element: JsonElement) =
        ensureObject element

        let actual =
            element.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Seq.toList

        let sameCount = List.length actual = List.length expected

        let containsOnlyExpected =
            actual
            |> List.forall (fun name -> List.contains name expected)

        let containsAllExpected =
            expected
            |> List.forall (fun name -> List.contains name actual)

        if not (sameCount && containsOnlyExpected && containsAllExpected) then
            let expectedText = String.Join(", ", expected)
            let actualText = String.Join(", ", actual)
            invalidJson $"Expected properties [{expectedText}], found [{actualText}]."

    let private property (name: string) (element: JsonElement) =
        element.GetProperty name

    let private stringValue (name: string) (element: JsonElement) =
        let value = property name element

        if value.ValueKind <> JsonValueKind.String then
            invalidJson $"Property '{name}' must be a string."

        let text = value.GetString()

        if isNull text then
            invalidJson $"Property '{name}' must not be null."

        text

    let private intValue (name: string) (element: JsonElement) =
        let value = property name element
        let mutable number = 0

        if
            value.ValueKind <> JsonValueKind.Number
            || not (value.TryGetInt32(&number))
        then
            invalidJson $"Property '{name}' must be a 32-bit integer."

        number

    let private arrayValues
        (name: string)
        (parser: JsonElement -> 'value)
        (element: JsonElement)
        =
        let array = property name element
        ensureArray array
        array.EnumerateArray() |> Seq.map parser |> Seq.toList

    let private readTypeEntity (element: JsonElement) =
        ensureProperties [ "id"; "name" ] element

        {
            Id = stringValue "id" element |> TypeId.create
            Name = stringValue "name" element
        }

    let private readParameter (element: JsonElement) =
        ensureProperties [ "id"; "name"; "type_id" ] element

        {
            Id = stringValue "id" element |> ParameterId.create
            Name = stringValue "name" element
            TypeId = stringValue "type_id" element |> TypeId.create
        }

    let private readFunctionEntity (element: JsonElement) =
        ensureProperties [ "id"; "name"; "parameters" ] element

        {
            Id = stringValue "id" element |> FunctionId.create
            Name = stringValue "name" element
            Parameters = arrayValues "parameters" readParameter element
        }

    let private readArgumentBinding (element: JsonElement) =
        ensureProperties [ "parameter_id"; "value_ref" ] element

        {
            ParameterId =
                stringValue "parameter_id" element
                |> ParameterId.create
            ValueRef = stringValue "value_ref" element
        }

    let private readCallEntity (element: JsonElement) =
        ensureProperties [ "id"; "callee_id"; "arguments" ] element

        {
            Id = stringValue "id" element |> CallId.create
            CalleeId =
                stringValue "callee_id" element
                |> FunctionId.create
            Arguments =
                arrayValues
                    "arguments"
                    readArgumentBinding
                    element
        }

    let private readObligationKind (element: JsonElement) =
        ensureProperties [ "kind"; "call_id"; "parameter_id" ] element

        let discriminator = stringValue "kind" element

        if discriminator <> "missing_argument" then
            invalidJson $"Unsupported obligation kind '{discriminator}'."

        MissingArgument(
            stringValue "call_id" element |> CallId.create,
            stringValue "parameter_id" element |> ParameterId.create
        )

    let private readObligation (element: JsonElement) =
        ensureProperties [ "id"; "kind"; "status" ] element
        let status = stringValue "status" element

        if status <> "open" then
            invalidJson $"Unsupported obligation status '{status}'."

        {
            Id = stringValue "id" element |> ObligationId.create
            Kind = property "kind" element |> readObligationKind
            Status = Open
        }

    let readSnapshot (json: string) =
        if isNull json then
            nullArg (nameof json)

        let options =
            JsonDocumentOptions(
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            )

        use document = JsonDocument.Parse(json, options)
        let root = document.RootElement

        ensureProperties
            [
                "schema_version"
                "id"
                "types"
                "functions"
                "calls"
                "obligations"
            ]
            root

        let schemaVersion = intValue "schema_version" root

        if schemaVersion <> 1 then
            invalidJson $"Unsupported schema version {schemaVersion}."

        {
            SchemaVersion = schemaVersion
            Id = stringValue "id" root |> SnapshotId.create
            Types = arrayValues "types" readTypeEntity root
            Functions =
                arrayValues
                    "functions"
                    readFunctionEntity
                    root
            Calls = arrayValues "calls" readCallEntity root
            Obligations =
                arrayValues "obligations" readObligation root
        }

    let private writeTypeEntity
        (writer: Utf8JsonWriter)
        (entity: TypeEntity)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value entity.Id)
        writer.WriteString("name", entity.Name)
        writer.WriteEndObject()

    let private writeParameter
        (writer: Utf8JsonWriter)
        (parameter: Parameter)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WriteString("name", parameter.Name)
        writer.WriteString("type_id", StableId.value parameter.TypeId)
        writer.WriteEndObject()

    let private writeFunctionEntity
        (writer: Utf8JsonWriter)
        (entity: FunctionEntity)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value entity.Id)
        writer.WriteString("name", entity.Name)
        writer.WriteStartArray("parameters")
        entity.Parameters |> List.iter (writeParameter writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeArgumentBinding
        (writer: Utf8JsonWriter)
        (binding: ArgumentBinding)
        =
        writer.WriteStartObject()

        writer.WriteString(
            "parameter_id",
            StableId.value binding.ParameterId
        )

        writer.WriteString("value_ref", binding.ValueRef)
        writer.WriteEndObject()

    let private writeCallEntity
        (writer: Utf8JsonWriter)
        (entity: CallEntity)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value entity.Id)
        writer.WriteString("callee_id", StableId.value entity.CalleeId)
        writer.WriteStartArray("arguments")
        entity.Arguments |> List.iter (writeArgumentBinding writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeObligationKind
        (writer: Utf8JsonWriter)
        (kind: ObligationKind)
        =
        writer.WriteStartObject()

        match kind with
        | MissingArgument (callId, parameterId) ->
            writer.WriteString("kind", "missing_argument")
            writer.WriteString("call_id", StableId.value callId)
            writer.WriteString("parameter_id", StableId.value parameterId)

        writer.WriteEndObject()

    let private writeObligation
        (writer: Utf8JsonWriter)
        (obligation: Obligation)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value obligation.Id)
        writer.WritePropertyName("kind")
        writeObligationKind writer obligation.Kind

        match obligation.Status with
        | Open -> writer.WriteString("status", "open")

        writer.WriteEndObject()

    let private writeText write =
        use stream = new MemoryStream()

        let options =
            JsonWriterOptions(
                Indented = true,
                SkipValidation = false
            )

        use writer = new Utf8JsonWriter(stream, options)
        write writer
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    let private compareId left right =
        StringComparer.Ordinal.Compare(
            StableId.value left,
            StableId.value right
        )

    let writeSnapshot (snapshot: Snapshot) =
        writeText (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("schema_version", snapshot.SchemaVersion)
            writer.WriteString("id", StableId.value snapshot.Id)
            writer.WriteStartArray("types")

            snapshot.Types
            |> List.sortWith (fun left right ->
                compareId left.Id right.Id)
            |> List.iter (writeTypeEntity writer)

            writer.WriteEndArray()
            writer.WriteStartArray("functions")

            snapshot.Functions
            |> List.sortWith (fun left right ->
                compareId left.Id right.Id)
            |> List.iter (writeFunctionEntity writer)

            writer.WriteEndArray()
            writer.WriteStartArray("calls")

            snapshot.Calls
            |> List.sortWith (fun left right ->
                compareId left.Id right.Id)
            |> List.iter (writeCallEntity writer)

            writer.WriteEndArray()
            writer.WriteStartArray("obligations")

            snapshot.Obligations
            |> List.sortWith (fun left right ->
                compareId left.Id right.Id)
            |> List.iter (writeObligation writer)

            writer.WriteEndArray()
            writer.WriteEndObject())

    let writeSummary (snapshot: Snapshot) =
        writeText (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("schema_version", snapshot.SchemaVersion)
            writer.WriteString("snapshot_id", StableId.value snapshot.Id)
            writer.WriteNumber("type_count", List.length snapshot.Types)

            writer.WriteNumber(
                "function_count",
                List.length snapshot.Functions
            )

            writer.WriteNumber("call_count", List.length snapshot.Calls)

            writer.WriteNumber(
                "obligation_count",
                List.length snapshot.Obligations
            )

            writer.WriteEndObject())
