namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Text.Json
open Nyrwvy.Core

[<RequireQualifiedAccess>]
module AuthoringProtocol =
    open WireInput
    open WireJson

    let private success id result =
        objectValue
            [ "id", box id
              "ok", box true
              "result", result ]
        |> serialize

    let private failure id error =
        objectValue
            [ "id", if isNull id then null else box id
              "ok", box false
              "error", error ]
        |> serialize

    let private sessionFailure id error = failure id (WireEncode.error error)

    let private pureScalar (item: JsonElement): PureRunScalar =
        ensureFields "Scalar run value" [ "kind"; "value" ] [ "kind"; "value" ] item
        match requiredString "kind" item with
        | "string" -> PureRunScalar.String(requiredRunText "value" item)
        | "bool" -> PureRunScalar.Bool(requiredBool "value" item)
        | "int64" -> PureRunScalar.Int64(requiredInt64 "value" item)
        | _ -> fail "Scalar run values must be string, bool or int64."

    /// Reads a Float64 run value: a finite JSON number, rounded to the nearest binary64 as C# and
    /// JavaScript read decimal text, or one of the strings "NaN", "Infinity", "-Infinity" and "-0".
    /// A number beyond the finite range is rejected rather than read as an infinity.
    let private float64Value (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Number ->
            let number = Double.Parse(value.GetRawText(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture)
            if Double.IsFinite number then Float64Bits.ofFloat number
            else fail "A Float64 number must be within the finite binary64 range; write \"Infinity\" or \"-Infinity\" as a string."
        | JsonValueKind.String ->
            match value.GetString() with
            | "NaN" -> Float64Bits.ofFloat Double.NaN
            | "Infinity" -> Float64Bits.ofFloat Double.PositiveInfinity
            | "-Infinity" -> Float64Bits.ofFloat Double.NegativeInfinity
            | "-0" -> Float64Bits.ofFloat -0.0
            | _ -> fail "A Float64 value must be a JSON number or one of \"NaN\", \"Infinity\", \"-Infinity\" and \"-0\"."
        | _ -> fail "A Float64 value must be a JSON number or one of \"NaN\", \"Infinity\", \"-Infinity\" and \"-0\"."

    /// Reads a Bits32 run value like an Int64 one: a JSON integer or an exact decimal integer string,
    /// here the unsigned value 0..4294967295 of the 32 bits. Anything else is rejected, never wrapped.
    let private bits32Value (value: JsonElement) =
        let parsed =
            match value.ValueKind with
            | JsonValueKind.Number -> value.TryGetUInt32()
            | JsonValueKind.String ->
                UInt32.TryParse(value.GetString(), Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
            | _ -> false, 0u
        match parsed with
        | true, bits -> bits
        | false, _ -> fail "A Bits32 value must be an integer from 0 to 4294967295, as a JSON number or an exact decimal string."

    /// Wraps a fake response in its optional `delayMillis` of virtual time; absent or 0 means none.
    let private delayed (element: JsonElement) (response: PureRunFakeResponse) =
        match tryProperty "delayMillis" element with
        | None -> response
        | Some _ ->
            match requiredInt64 "delayMillis" element with
            | delay when delay < 0L -> fail "Fake delayMillis must not be negative."
            | 0L -> response
            | delay -> PureRunFakeResponse.Delayed(delay, response)

    // Keep wire diagnostics structural: never echo response payloads, unknown keys or response kinds.
    exception private RunValueShapeException of location: string * expected: string * constraintReason: string
    exception private RunValueSelectorException of message: string

    // Declaration selectors have their own actionable name-resolution diagnostics. They are
    // not response payloads, so do not replace them with a run-value shape explanation.
    let private resolveRunValueSelector resolve =
        try resolve ()
        with ProtocolInputException message -> raise (RunValueSelectorException message)

    let private atRunValuePath (location: string) expected parse =
        try parse ()
        with ProtocolInputException message when location.Contains(".operations[", StringComparison.Ordinal) ->
            // These complete messages are parser-owned constants, never caller-controlled text.
            let constraintReason =
                if List.contains message
                    [ $"Run values may have at most {PureRun.maxValueLayers} nested composite layers."
                      "Fake delayMillis must not be negative."
                      "Lists may contain at most 4096 items."
                      "List run values may contain at most 4096 items."
                      "Sets may contain at most 4096 items."
                      "Maps may contain at most 4096 entries."
                      "A Map run value names a key more than once."
                      "Named run values may contain at most 256 fields."
                      "A fake capability may script at most 256 operations."
                      "A fake operation may script at most 256 responses."
                      "A fake operation may script at most 256 cases."
                      "A fake case may contain at most 256 arguments."
                      "A fake operation may script at most 256 total responses." ] then message + " "
                else ""
            raise (RunValueShapeException(location, expected, constraintReason))

    let private runValueShape (item: JsonElement) =
        if item.ValueKind <> JsonValueKind.Object then "a non-null typed run-value object"
        else
            match item.TryGetProperty("kind") with
            | true, kind when kind.ValueKind = JsonValueKind.String ->
                match kind.GetString() with
                | "result" ->
                    "Result {kind, okType, errorType, isOk, value} with a typed run-value payload, or the legacy scalar {kind, okKind, errorKind, isOk, value} with a raw string/bool/int64 payload"
                | "string" -> "String {kind: string, value: JSON string}"
                | "bool" -> "Bool {kind: bool, value: JSON boolean}"
                | "int64" -> "Int64 {kind: int64, value: integer or exact decimal integer string}"
                | "unit" -> "Unit {kind: unit} without a value property"
                | _ -> "a typed run value with the required fields for its kind"
            | _ -> "a typed run-value object with a string kind"

    /// Decodes a run value. `names` selects the name-resolved wire form (`runPureByName`): semantic
    /// references are exact display names resolved against those declarations instead of stable IDs.
    let rec private pureValueAt (names: AuthoringDeclarations option) (remaining: int ref) depth location (item: JsonElement): PureRunValue =
        atRunValuePath location (runValueShape item) (fun () ->
            pureValueBody names remaining depth location item)

    and private pureValueBody (names: AuthoringDeclarations option) (remaining: int ref) depth location (item: JsonElement): PureRunValue =
        if item.ValueKind <> JsonValueKind.Object then fail "Run values must be non-null typed objects."
        if (tryProperty "kind" item).IsNone && (tryProperty "parameter" item).IsSome then
            fail "Run values are typed objects with a kind. runPure arguments are positional, one typed value per parameter in declaration order (for example [{\"kind\":\"string\",\"value\":\"a\"}]); {parameter, value} pairs are the form of fake capability case arguments."
        let kind = requiredString "kind" item
        if depth >= PureRun.maxValueLayers && kind <> "string" && kind <> "bool" && kind <> "int64" && kind <> "float64" && kind <> "bits32" && kind <> "unit" then
            fail $"Run values may have at most {PureRun.maxValueLayers} nested composite layers."
        // Tagged child values are charged by their recursive parse, exactly once. Only flat
        // scalar payloads and fake-case metadata have no recursive parse and need precharging.
        let children =
            match kind with
            | "list" when tryProperty "itemKind" item |> Option.isSome -> (array "items" item).Length
            | "result" when tryProperty "okType" item |> Option.isNone -> 1
            | "option" when tryProperty "itemKind" item |> Option.isSome -> if requiredBool "hasValue" item then 1 else 0
            | "fakeCapability" ->
                array "operations" item |> List.mapi (fun operationIndex operation ->
                    let operationPath = $"{location}.operations[{operationIndex}]"
                    atRunValuePath operationPath "a fake operation with a responses array or a cases array" (fun () ->
                        match tryProperty "cases" operation with
                        | Some _ ->
                            array "cases" operation |> List.mapi (fun caseIndex item ->
                                atRunValuePath $"{operationPath}.cases[{caseIndex}]" "a fake case with arguments and responses arrays" (fun () ->
                                    1 + (array "arguments" item).Length + (array "responses" item).Length)) |> List.sum
                        // Return/cancellation payloads are recursively counted; a throw's scalar
                        // message has no typed child parse and must still consume one value node.
                        | None ->
                            array "responses" operation |> List.mapi (fun responseIndex response ->
                                atRunValuePath $"{operationPath}.responses[{responseIndex}]" "a fake response object with a string kind" (fun () ->
                                    if requiredString "kind" response = "throw" then 1 else 0)) |> List.sum)) |> List.sum
            | _ -> 0
        remaining.Value <- remaining.Value - 1 - children
        if remaining.Value < 0 then raise (RunValueNodeBudgetExceeded(8192 - remaining.Value))
        let key idKey nameKey = if names.IsSome then nameKey else idKey
        let typeRef element =
            match names with
            | Some declarations -> WireSymbolic.typeRef declarations element
            | None -> typeRef element
        match kind with
        | "list" when tryProperty "itemType" item |> Option.isSome ->
            ensureFields "Nested List run value" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] item
            let items = array "items" item
            if items.Length > 4096 then fail "Lists may contain at most 4096 items."
            PureRunValue.ListValue(typeRef (property "itemType" item), items |> List.mapi (fun index value -> pureValueAt names remaining (depth + 1) $"{location}.items[{index}]" value))
        | "set" ->
            ensureFields "Set run value" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] item
            let items = array "items" item
            if items.Length > 4096 then fail "Sets may contain at most 4096 items."
            PureRunValue.SetValue(typeRef (property "itemType" item), items |> List.mapi (fun index value -> pureValueAt names remaining (depth + 1) $"{location}.items[{index}]" value))
        | "map" ->
            ensureFields "Map run value" [ "kind"; "keyType"; "valueType"; "entries" ] [ "kind"; "keyType"; "valueType"; "entries" ] item
            let entries = array "entries" item
            if entries.Length > 4096 then fail "Maps may contain at most 4096 entries."
            let parsed =
                entries |> List.mapi (fun index entry ->
                    atRunValuePath $"{location}.entries[{index}]" "a Map entry with typed key and value" (fun () ->
                        ensureFields "Map run value entry" [ "key"; "value" ] [ "key"; "value" ] entry
                        pureValueAt names remaining (depth + 1) $"{location}.entries[{index}].key" (property "key" entry), pureValueAt names remaining (depth + 1) $"{location}.entries[{index}].value" (property "value" entry)))
            if (parsed |> List.map fst |> List.distinct).Length <> parsed.Length then fail "A Map run value names a key more than once."
            PureRunValue.MapValue(typeRef (property "keyType" item), typeRef (property "valueType" item), parsed)
        | "option" when tryProperty "itemType" item |> Option.isSome ->
            let hasValue = requiredBool "hasValue" item
            let fields = [ "kind"; "itemType"; "hasValue" ] @ (if hasValue then [ "value" ] else [])
            ensureFields "Nested Option run value" fields fields item
            PureRunValue.OptionValue(typeRef (property "itemType" item),
                if hasValue then Some(pureValueAt names remaining (depth + 1) $"{location}.value" (property "value" item)) else None)
        | "unit" ->
            ensureFields "Unit run value" [ "kind" ] [ "kind" ] item
            PureRunValue.Unit
        | "float64" ->
            ensureFields "Float64 run value" [ "kind"; "value" ] [ "kind"; "value" ] item
            PureRunValue.Float64(float64Value (property "value" item))
        | "bits32" ->
            ensureFields "Bits32 run value" [ "kind"; "value" ] [ "kind"; "value" ] item
            PureRunValue.Bits32(bits32Value (property "value" item))
        | "hostCapability" ->
            let capabilityKey = key "capabilityId" "capability"
            ensureFields "Host capability" [ "kind"; capabilityKey; "implementation"; "supportsConcurrentCalls" ] [ "kind"; capabilityKey; "implementation" ] item
            let capabilityId =
                match names with
                | Some declarations -> (WireSymbolic.resolveCapabilityAt declarations capabilityKey item).Id
                | None -> id<CapabilityTag> capabilityKey item
            PureRunValue.HostCapability(capabilityId, requiredString "implementation" item, optionalBool "supportsConcurrentCalls" false item)
        | "fakeCapability" ->
            let capabilityKey, operationKey, parameterKey =
                key "capabilityId" "capability", key "operationId" "operation", key "capabilityParameterId" "parameter"
            ensureFields "Fake capability" [ "kind"; capabilityKey; "operations" ] [ "kind"; capabilityKey; "operations" ] item
            let operations = array "operations" item
            if operations.Length > 256 then fail "A fake capability may script at most 256 operations."
            let capability = names |> Option.map (fun declarations -> WireSymbolic.resolveCapabilityAt declarations capabilityKey item)
            PureRunValue.FakeCapability(
                (match capability with
                 | Some capability -> capability.Id
                 | None -> id<CapabilityTag> capabilityKey item),
                operations |> List.mapi (fun operationIndex operation ->
                    let operationPath = $"{location}.operations[{operationIndex}]"
                    atRunValuePath operationPath "a fake operation with exactly one of responses or cases" (fun () ->
                        ensureFields "Fake operation" [ operationKey; "responses"; "cases" ] [ operationKey ] operation
                        let parseResponses ownerPath owner =
                            let values = array "responses" owner
                            if values.Length > 256 then fail "A fake operation may script at most 256 responses."
                            values |> List.mapi (fun responseIndex response ->
                                let responsePath = $"{ownerPath}.responses[{responseIndex}]"
                                atRunValuePath responsePath "return/awaitCancellation with a typed value, or throw with a string message; only return/throw accept delayMillis" (fun () ->
                                    match requiredString "kind" response with
                                    | "return" ->
                                        ensureFields "Fake return" [ "kind"; "value"; "delayMillis" ] [ "kind"; "value" ] response
                                        delayed response (PureRunFakeResponse.Return(pureValueAt names remaining (depth + 1) $"{responsePath}.value" (property "value" response)))
                                    | "throw" ->
                                        ensureFields "Fake throw" [ "kind"; "message"; "delayMillis" ] [ "kind"; "message" ] response
                                        delayed response (PureRunFakeResponse.Throw(requiredRunText "message" response))
                                    | "awaitCancellation" ->
                                        ensureFields "Fake awaitCancellation" [ "kind"; "value" ] [ "kind"; "value" ] response
                                        PureRunFakeResponse.AwaitCancellation(pureValueAt names remaining (depth + 1) $"{responsePath}.value" (property "value" response))
                                    | other -> fail $"Unsupported fake response kind '{other}'."))
                        let resolvedOperation = capability |> Option.map (fun capability ->
                            let name = requiredString operationKey operation
                            resolveRunValueSelector (fun () -> WireSymbolic.capabilityOperation capability name))
                        let operationId =
                            match resolvedOperation with
                            | Some resolved -> resolved.Id
                            | None -> id<CapabilityOperationTag> operationKey operation
                        let parameterId argument =
                            match resolvedOperation with
                            | Some resolved ->
                                let name = requiredString parameterKey argument
                                (resolveRunValueSelector (fun () -> WireSymbolic.capabilityParameter resolved name)).Id
                            | None -> id<CapabilityParameterTag> parameterKey argument
                        match tryProperty "responses" operation, tryProperty "cases" operation with
                        | Some _, None -> { OperationId = operationId; Responses = parseResponses operationPath operation; Cases = None }
                        | None, Some _ ->
                            let cases = array "cases" operation
                            if cases.Length > 256 then fail "A fake operation may script at most 256 cases."
                            let cases = cases |> List.mapi (fun caseIndex item ->
                                let casePath = $"{operationPath}.cases[{caseIndex}]"
                                atRunValuePath casePath "a fake case with arguments and responses arrays" (fun () ->
                                    ensureFields "Fake case" [ "arguments"; "responses" ] [ "arguments"; "responses" ] item
                                    let arguments = array "arguments" item
                                    if arguments.Length > 256 then fail "A fake case may contain at most 256 arguments."
                                    let parsedArguments =
                                        arguments |> List.mapi (fun argumentIndex argument ->
                                            atRunValuePath $"{casePath}.arguments[{argumentIndex}]" "a fake argument with a declared parameter and typed value" (fun () ->
                                                ensureFields "Fake case argument" [ parameterKey; "value" ] [ parameterKey; "value" ] argument
                                                parameterId argument, pureValueAt names remaining (depth + 1) $"{casePath}.arguments[{argumentIndex}].value" (property "value" argument)))
                                    { Arguments = parsedArguments; Responses = parseResponses casePath item }))
                            if cases |> List.sumBy (fun item -> item.Responses.Length) > 256 then fail "A fake operation may script at most 256 total responses."
                            { OperationId = operationId; Responses = []; Cases = Some cases }
                        | _ -> fail "A fake operation requires exactly one of responses or cases.")))
        | "string" | "bool" | "int64" ->
            match pureScalar item with
            | PureRunScalar.String text -> PureRunValue.String text
            | PureRunScalar.Bool value -> PureRunValue.Bool value
            | PureRunScalar.Int64 value -> PureRunValue.Int64 value
        | "product" | "union" as kind ->
            let typeKey, variantKey, fieldKey = key "typeId" "type", key "variantId" "variant", key "fieldId" "field"
            let properties = [ "kind"; typeKey; "fields" ] @ (if kind = "union" then [ variantKey ] else [])
            // The exact-ID form also accepts the display names that run output reports next to IDs, so an
            // `actual` value can be passed back as `expected` or a fake response; IDs stay the identity.
            let echoedNames = if names.IsNone then [ "type" ] @ (if kind = "union" then [ "variant" ] else []) else []
            // A value of a generic type names its type arguments, as a construct recipe does.
            ensureFields "Named run value" (properties @ echoedNames @ [ "typeArguments" ]) properties item
            let typeArguments = optionalTypeArguments "typeArguments" typeRef item
            let fields = array "fields" item
            if fields.Length > 256 then fail "Named run values may contain at most 256 fields."
            // Name form: fields resolve within the named type (Product) or the named variant (Union).
            let typeId, variantId, fieldId =
                match names with
                | None ->
                    id<TypeTag> typeKey item,
                    (if kind = "union" then Some(id<VariantTag> variantKey item) else None),
                    fun field -> id<FieldTag> fieldKey field
                | Some declarations ->
                    let owner = WireSymbolic.resolveTypeAt declarations typeKey item
                    if kind = "union" then
                        let variant = WireSymbolic.unionVariant owner (requiredString variantKey item)
                        owner.Id, Some variant.Id, fun field -> (WireSymbolic.variantField variant (requiredString fieldKey field)).Id
                    else
                        owner.Id, None, fun field -> (WireSymbolic.productField owner (requiredString fieldKey field)).Id
            let fields = fields |> List.mapi (fun index field ->
                atRunValuePath $"{location}.fields[{index}]" "a declared field with a typed value" (fun () ->
                    ensureFields "Named run field" ([ fieldKey; "value" ] @ (if names.IsNone then [ "field" ] else [])) [ fieldKey; "value" ] field
                    fieldId field, pureValueAt names remaining (depth + 1) $"{location}.fields[{index}].value" (property "value" field)))
            match variantId with
            | Some variantId -> PureRunValue.Union(typeId, typeArguments, variantId, fields)
            | None -> PureRunValue.Product(typeId, typeArguments, fields)
        | "result" when tryProperty "case" item |> Option.isSome ->
            fail "Result run values select Ok or Err with isOk, not case: nested {kind:'result', okType, errorType, isOk, value:<typed value>} or flat {kind:'result', okKind, errorKind, isOk, value:<raw scalar>}."
        | "result" when tryProperty "okType" item |> Option.isSome ->
            let fields = [ "kind"; "okType"; "errorType"; "isOk"; "value" ]
            ensureFields "Nested Result run value" fields fields item
            PureRunValue.ResultValue(
                typeRef (property "okType" item),
                typeRef (property "errorType" item),
                requiredBool "isOk" item,
                pureValueAt names remaining (depth + 1) $"{location}.value" (property "value" item))
        | "result" ->
            let fields = [ "kind"; "okKind"; "errorKind"; "isOk"; "value" ]
            ensureFields "Result run value" fields fields item
            let kind name =
                match requiredString name item with
                | "string" -> PureRunScalarKind.String
                | "bool" -> PureRunScalarKind.Bool
                | "int64" -> PureRunScalarKind.Int64
                | _ -> fail "Result okKind and errorKind must be string, bool or int64."
            let okKind, errorKind = kind "okKind", kind "errorKind"
            let isOk = requiredBool "isOk" item
            let value =
                match (if isOk then okKind else errorKind) with
                | PureRunScalarKind.String -> atRunValuePath $"{location}.value" "a raw JSON string (flat Result uses okKind/errorKind; nested Result uses okType/errorType and a typed value)" (fun () -> PureRunScalar.String(requiredRunText "value" item))
                | PureRunScalarKind.Bool -> atRunValuePath $"{location}.value" "a raw JSON boolean" (fun () -> PureRunScalar.Bool(requiredBool "value" item))
                | PureRunScalarKind.Int64 -> atRunValuePath $"{location}.value" "a raw integer or exact decimal integer string" (fun () -> PureRunScalar.Int64(requiredInt64 "value" item))
            if isOk then PureRunValue.ResultOk(errorKind, value)
            else PureRunValue.ResultErr(okKind, value)
        | "option" ->
            let hasValue = requiredBool "hasValue" item
            let fields = [ "kind"; "itemKind"; "hasValue" ] @ (if hasValue then [ "value" ] else [])
            ensureFields "Option run value" fields fields item
            match requiredString "itemKind" item with
            | "string" -> PureRunValue.StringOption(if hasValue then Some(requiredRunText "value" item) else None)
            | "bool" -> PureRunValue.BoolOption(if hasValue then Some(requiredBool "value" item) else None)
            | "int64" -> PureRunValue.Int64Option(if hasValue then Some(requiredInt64 "value" item) else None)
            | _ -> fail "Option itemKind must be string, bool or int64."
        | "list" ->
            ensureFields "List run value" [ "kind"; "itemKind"; "items" ] [ "kind"; "itemKind"; "items" ] item
            let items = array "items" item
            if items.Length > 4096 then fail "List run values may contain at most 4096 items."
            match requiredString "itemKind" item with
            | "string" ->
                PureRunValue.StringList(items |> List.map (fun value ->
                    if value.ValueKind <> JsonValueKind.String then fail "String List items must be strings."
                    runText value))
            | "bool" ->
                PureRunValue.BoolList(items |> List.map (fun value ->
                    if value.ValueKind <> JsonValueKind.True && value.ValueKind <> JsonValueKind.False then fail "Bool List items must be booleans."
                    value.GetBoolean()))
            | "int64" ->
                PureRunValue.Int64List(items |> List.map (fun value ->
                    let parsed =
                        match value.ValueKind with
                        | JsonValueKind.Number -> value.TryGetInt64()
                        | JsonValueKind.String -> Int64.TryParse(value.GetString(), Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture)
                        | _ -> false, 0L
                    match parsed with
                    | true, number -> number
                    | _ -> fail "Int64 List items must be exact integers or decimal strings."))
            | _ -> fail "List itemKind must be string, bool or int64."
        | value -> fail $"Unsupported pure run value kind '{value}'."

    let private pureValue names location item =
        try pureValueAt names (ref 8192) 0 location item
        with
        | RunValueSelectorException message -> fail message
        | RunValueShapeException(path, expected, reason) -> fail $"Invalid run value at {path}. {reason}Expected {expected}."
        | RunValueNodeBudgetExceeded charged ->
            fail $"Run values may contain at most 8192 value nodes per argument or expected value. At {location}: at least {charged} nodes were charged, including containers and their values. Split the input into smaller batches or build it inside the graph and return an aggregate."

    /// Writes a Float64 run value: the shortest round-trip decimal as a JSON number for finite values
    /// other than -0.0, and the strings "NaN", "Infinity", "-Infinity" and "-0" otherwise.
    let private encodeFloat64 bits =
        let number = Float64Bits.toFloat bits
        let payload =
            if Double.IsNaN number then box "NaN"
            elif Double.IsPositiveInfinity number then box "Infinity"
            elif Double.IsNegativeInfinity number then box "-Infinity"
            elif number = 0.0 && Double.IsNegative number then box "-0"
            else box (JsonDocument.Parse(number.ToString("R", Globalization.CultureInfo.InvariantCulture)).RootElement.Clone())
        objectValue [ "kind", box "float64"; "value", payload ]

    let private encodePureScalar value =
        let kind, payload =
            match value with
            | PureRunScalar.String text -> "string", runString text
            | PureRunScalar.Bool value -> "bool", box value
            | PureRunScalar.Int64 number -> "int64", box (number.ToString(Globalization.CultureInfo.InvariantCulture))
        objectValue [ "kind", box kind; "value", payload ]

    /// Display names reported next to stable IDs in run output (`runPure` and `runPureByName`), keyed by
    /// ID value. Each name uses the property key of the name-resolved input form (`type`, `variant`, `field`,
    /// `function`, `capability`, `operation`, `parameter`); IDs remain the identity.
    type private RunNames =
        { Declarations: AuthoringDeclarations
          Names: Collections.Generic.Dictionary<string, string>
          OperationCapabilities: Collections.Generic.Dictionary<string, string> }

    let private runNames (declarations: AuthoringDeclarations) =
        let names = Collections.Generic.Dictionary<string, string>()
        let operationCapabilities = Collections.Generic.Dictionary<string, string>()
        let add id displayName = names.TryAdd(StableId.value id, displayName) |> ignore
        let addFields (fields: SemanticField list) = fields |> List.iter (fun field -> add field.Id field.DisplayName)
        for typeEntity in declarations.Types do
            add typeEntity.Id typeEntity.DisplayName
            match typeEntity.Kind with
            | SemanticTypeKind.Product fields -> addFields fields
            | SemanticTypeKind.Union variants ->
                for variant in variants do
                    add variant.Id variant.DisplayName
                    addFields variant.Fields
        for functionEntity in declarations.Functions do
            add functionEntity.Id functionEntity.DisplayName
            functionEntity.Parameters |> List.iter (fun parameter -> add parameter.Id parameter.DisplayName)
        for capability in declarations.Capabilities do
            add capability.Id capability.DisplayName
            for operation in capability.Operations do
                add operation.Id operation.DisplayName
                operationCapabilities.TryAdd(StableId.value operation.Id, capability.DisplayName) |> ignore
                operation.Parameters |> List.iter (fun parameter -> add parameter.Id parameter.DisplayName)
        { Declarations = declarations; Names = names; OperationCapabilities = operationCapabilities }

    /// The `key: displayName` property for a known ID; empty when the ID is not declared.
    let private named (names: RunNames) key id =
        match names.Names.TryGetValue(StableId.value id) with
        | true, displayName -> [ key, box displayName ]
        | _ -> []

    let private operationNamed (names: RunNames) (operationId: CapabilityOperationId) =
        (match names.OperationCapabilities.TryGetValue(StableId.value operationId) with
         | true, displayName -> [ "capability", box displayName ]
         | _ -> [])
        @ named names "operation" operationId

    /// Type arguments of a generic Product or Union run value; absent for a monomorphic one.
    let private encodeTypeArguments (typeArguments: TypeRef list) =
        if List.isEmpty typeArguments then [] else [ "typeArguments", typeArguments |> List.map WireEncodeCommon.typeRef |> arrayValue ]

    let rec private encodePureValue (names: RunNames) value =
        let encodePureValue = encodePureValue names
        let listValue kind items =
            objectValue [ "kind", box "list"; "itemKind", box kind; "items", arrayValue items ]
        let optionValue kind item =
            objectValue
                ([ "kind", box "option"; "itemKind", box kind; "hasValue", box (Option.isSome item) ]
                 @ (item |> Option.map (fun value -> [ "value", value ]) |> Option.defaultValue []))
        let resultValue isOk inactiveKind item =
            let kind = function
                | PureRunScalarKind.String -> "string"
                | PureRunScalarKind.Bool -> "bool"
                | PureRunScalarKind.Int64 -> "int64"
            let activeKind, payload =
                match item with
                | PureRunScalar.String text -> "string", runString text
                | PureRunScalar.Bool value -> "bool", box value
                | PureRunScalar.Int64 number -> "int64", box (number.ToString(Globalization.CultureInfo.InvariantCulture))
            objectValue
                [ "kind", box "result"
                  "okKind", box (if isOk then activeKind else kind inactiveKind)
                  "errorKind", box (if isOk then kind inactiveKind else activeKind)
                  "isOk", box isOk
                  "value", payload ]
        let encodeFields fields =
            fields
            |> List.map (fun (fieldId, value) ->
                objectValue ([ "fieldId", box (StableId.value fieldId) ] @ named names "field" fieldId @ [ "value", encodePureValue value ]))
            |> arrayValue
        match value with
        | PureRunValue.Unit -> objectValue [ "kind", box "unit" ]
        | PureRunValue.HostCapability(capabilityId, implementation, concurrent) ->
            objectValue
                ([ "kind", box "hostCapability"; "capabilityId", box (StableId.value capabilityId) ]
                 @ named names "capability" capabilityId
                 @ [ "implementation", box implementation; "supportsConcurrentCalls", box concurrent ])
        | PureRunValue.FakeCapability(capabilityId, operations) ->
            let rec response delay = function
                | PureRunFakeResponse.Return value -> objectValue ([ "kind", box "return"; "value", encodePureValue value ] @ delay)
                | PureRunFakeResponse.Throw message -> objectValue ([ "kind", box "throw"; "message", runString message ] @ delay)
                | PureRunFakeResponse.AwaitCancellation value -> objectValue [ "kind", box "awaitCancellation"; "value", encodePureValue value ]
                | PureRunFakeResponse.Delayed(millis, inner) ->
                    response [ "delayMillis", box (millis.ToString(Globalization.CultureInfo.InvariantCulture)) ] inner
            objectValue
                ([ "kind", box "fakeCapability"
                   "capabilityId", box (StableId.value capabilityId) ]
                 @ named names "capability" capabilityId
                 @ [ "operations", operations |> List.map (fun operation ->
                      let script =
                          match operation.Cases with
                          | None -> "responses", operation.Responses |> List.map (response []) |> arrayValue
                          | Some cases ->
                              "cases", cases |> List.map (fun item ->
                                  objectValue
                                      [ "arguments", item.Arguments |> List.map (fun (id, value) -> objectValue ([ "capabilityParameterId", box (StableId.value id) ] @ named names "parameter" id @ [ "value", encodePureValue value ])) |> arrayValue
                                        "responses", item.Responses |> List.map (response []) |> arrayValue ]) |> arrayValue
                      objectValue ([ "operationId", box (StableId.value operation.OperationId) ] @ named names "operation" operation.OperationId @ [ script ])) |> arrayValue ])
        | PureRunValue.ListValue(itemType, items) ->
            objectValue [ "kind", box "list"; "itemType", WireEncodeCommon.typeRef itemType
                          "items", items |> List.map encodePureValue |> arrayValue ]
        | PureRunValue.SetValue(itemType, items) ->
            objectValue [ "kind", box "set"; "itemType", WireEncodeCommon.typeRef itemType; "items", items |> List.map encodePureValue |> arrayValue ]
        | PureRunValue.MapValue(keyType, valueType, entries) ->
            objectValue [ "kind", box "map"; "keyType", WireEncodeCommon.typeRef keyType; "valueType", WireEncodeCommon.typeRef valueType
                          "entries", entries |> List.map (fun (key, value) -> objectValue [ "key", encodePureValue key; "value", encodePureValue value ]) |> arrayValue ]
        | PureRunValue.OptionValue(itemType, item) ->
            objectValue
                ([ "kind", box "option"; "itemType", WireEncodeCommon.typeRef itemType; "hasValue", box (Option.isSome item) ]
                 @ (item |> Option.map (fun value -> [ "value", encodePureValue value ]) |> Option.defaultValue []))
        | PureRunValue.Union(typeId, typeArguments, variantId, fields) ->
            objectValue
                ([ "kind", box "union"; "typeId", box (StableId.value typeId) ]
                 @ named names "type" typeId
                 @ encodeTypeArguments typeArguments
                 @ [ "variantId", box (StableId.value variantId) ]
                 @ named names "variant" variantId
                 @ [ "fields", encodeFields fields ])
        | PureRunValue.Product(typeId, typeArguments, fields) ->
            objectValue
                ([ "kind", box "product"; "typeId", box (StableId.value typeId) ]
                 @ named names "type" typeId
                 @ encodeTypeArguments typeArguments
                 @ [ "fields", encodeFields fields ])
        | PureRunValue.ResultValue(okType, errorType, isOk, payload) ->
            objectValue
                [ "kind", box "result"
                  "okType", WireEncodeCommon.typeRef okType
                  "errorType", WireEncodeCommon.typeRef errorType
                  "isOk", box isOk
                  "value", encodePureValue payload ]
        | PureRunValue.ResultOk(errorKind, item) -> resultValue true errorKind item
        | PureRunValue.ResultErr(okKind, item) -> resultValue false okKind item
        | PureRunValue.StringOption item -> optionValue "string" (item |> Option.map runString)
        | PureRunValue.BoolOption item -> optionValue "bool" (item |> Option.map box)
        | PureRunValue.Int64Option item -> optionValue "int64" (item |> Option.map (fun number -> box (number.ToString(Globalization.CultureInfo.InvariantCulture))))
        | PureRunValue.StringList items -> listValue "string" (items |> List.map runString)
        | PureRunValue.BoolList items -> listValue "bool" (items |> List.map box)
        | PureRunValue.Int64List items -> listValue "int64" (items |> List.map (fun item -> box (item.ToString(Globalization.CultureInfo.InvariantCulture))))
        | PureRunValue.String value -> encodePureScalar (PureRunScalar.String value)
        | PureRunValue.Bool value -> encodePureScalar (PureRunScalar.Bool value)
        | PureRunValue.Int64 value -> encodePureScalar (PureRunScalar.Int64 value)
        | PureRunValue.Float64 bits -> encodeFloat64 bits
        | PureRunValue.Bits32 bits -> objectValue [ "kind", box "bits32"; "value", box (bits.ToString(Globalization.CultureInfo.InvariantCulture)) ]

    let private encodePathStep names step =
        match step with
        | PureRunPathStep.Field(typeId, fieldId) ->
            objectValue
                ([ "kind", box "field"; "typeId", box (StableId.value typeId) ]
                 @ named names "type" typeId
                 @ [ "fieldId", box (StableId.value fieldId) ]
                 @ named names "field" fieldId)
        | PureRunPathStep.Item index -> objectValue [ "kind", box "item"; "index", box index ]
        | PureRunPathStep.SomeValue -> objectValue [ "kind", box "some" ]
        | PureRunPathStep.OkValue -> objectValue [ "kind", box "ok" ]
        | PureRunPathStep.ErrValue -> objectValue [ "kind", box "err" ]

    let private encodeMismatch names (mismatch: PureRunMismatch) =
        objectValue
            [ "path", mismatch.Path |> List.map (encodePathStep names) |> arrayValue
              "expected", encodePureValue names mismatch.Expected
              "actual", encodePureValue names mismatch.Actual ]

    let private encodeCall names (call: PureRunCall) =
        objectValue
            ([ "parameterId", box (StableId.value call.ParameterId) ]
             @ named names "parameter" call.ParameterId
             @ [ "operationId", box (StableId.value call.OperationId) ]
             @ operationNamed names call.OperationId
             @ [ "arguments", call.Arguments |> List.map (fun (parameterId, value) ->
                     objectValue
                         ([ "capabilityParameterId", box (StableId.value parameterId) ]
                          @ named names "parameter" parameterId
                          @ [ "value", encodePureValue names value ])) |> arrayValue ])

    let private encodeFrame names frame =
        match frame with
        | PureRunFrame.Function functionId ->
            objectValue ([ "kind", box "function"; "functionId", box (StableId.value functionId) ] @ named names "function" functionId)
        | PureRunFrame.CapabilityOperation operationId ->
            objectValue ([ "kind", box "capabilityOperation"; "operationId", box (StableId.value operationId) ] @ operationNamed names operationId)

    /// Shared wire diagnostics for committed and candidate execution.
    let private encodeRunError (names: RunNames) functionId inputIssue = function
        | PureRunError.SessionFailure error -> WireEncode.errorWithDeclarations names.Declarations error
        | PureRunError.RuntimeFailure runtime ->
            objectValue
                ([ "code", box (if runtime.BindingExhausted then "RunBindingExhausted" elif runtime.ExceptionType = "FakeArgumentsUnmatchedException" then "RunBindingNoMatch" else "RunRuntimeFailure")
                   "message", runString runtime.Message
                   "functionId", box (StableId.value runtime.FunctionId) ]
                 @ named names "function" runtime.FunctionId
                 @ [ "sourceSnapshotId", box (StableId.value runtime.SnapshotId)
                     "exceptionType", box runtime.ExceptionType
                     "frames", runtime.Frames |> List.map (encodeFrame names) |> arrayValue
                     "calls", runtime.Calls |> List.map (encodeCall names) |> arrayValue ])
        | PureRunError.Unsupported(code, message) ->
            objectValue
                ([ "code", box code; "message", box message; "functionId", box (StableId.value functionId) ]
                 @ named names "function" functionId
                 @ (inputIssue |> Option.map (fun (issue: PureRunInputIssue) ->
                     [ "details", objectValue [ "path", issue.Path |> List.map box |> arrayValue
                                                "expectedType", issue.ExpectedType |> Option.map WireEncodeCommon.typeRef |> Option.defaultValue null
                                                "actualType", issue.ActualType |> Option.map WireEncodeCommon.typeRef |> Option.defaultValue null
                                                "reason", box issue.Reason ] ]) |> Option.defaultValue []))

    let private currentSnapshotId (session: AuthoringSession) = session.SnapshotId

    let private typeFieldIntent (item: JsonElement): AgentTypeFieldIntent =
        ensureFields
            "type field"
            [ "displayName"; "type" ]
            [ "displayName"; "type" ]
            item
        { DisplayName = requiredString "displayName" item
          Type = typeRef (property "type" item) }

    let private typeKindIntent (element: JsonElement): AgentTypeDeclarationKindIntent =
        match requiredString "kind" element with
        | "product" ->
            ensureFields
                "product type kind"
                [ "kind"; "fields" ]
                [ "kind"; "fields" ]
                element
            AgentTypeDeclarationKindIntent.Product(
                array "fields" element |> List.map typeFieldIntent
            )
        | "union" ->
            ensureFields
                "union type kind"
                [ "kind"; "variants" ]
                [ "kind"; "variants" ]
                element
            let variants =
                array "variants" element
                |> List.map (fun item ->
                    ensureFields
                        "union variant"
                        [ "displayName"; "fields" ]
                        [ "displayName"; "fields" ]
                        item
                    { DisplayName = requiredString "displayName" item
                      Fields = array "fields" item |> List.map typeFieldIntent })
            AgentTypeDeclarationKindIntent.Union variants
        | value -> WireInput.fail $"Unsupported type declaration kind '{value}'."

    let private capabilityParameterIntent (item: JsonElement): AgentCapabilityParameterIntent =
        ensureFields
            "capability parameter"
            [ "displayName"; "type" ]
            [ "displayName"; "type" ]
            item
        { DisplayName = requiredString "displayName" item
          Type = typeRef (property "type" item) }

    let private capabilityOperationIntent (item: JsonElement): AgentCapabilityOperationIntent =
        ensureFields
            "capability operation"
            [ "displayName"; "parameters"; "resultType"; "failureType"; "effectId" ]
            [ "displayName"; "parameters"; "resultType"; "effectId" ]
            item
        let failureType = retiredFailureType item
        { DisplayName = requiredString "displayName" item
          Parameters = array "parameters" item |> List.map capabilityParameterIntent
          ResultType = typeRef (property "resultType" item)
          FailureType = failureType
          EffectId = id<EffectTag> "effectId" item }

    let private parameterTypeCallerRewrite (item: JsonElement): AgentParameterTypeCallerRewrite =
        ensureFields
            "parameter type caller rewrite"
            [ "callerFunctionId"; "replacement" ]
            [ "callerFunctionId"; "replacement" ]
            item
        { CallerFunctionId = id<FunctionTag> "callerFunctionId" item
          Replacement = recipe (property "replacement" item) }

    /// A caller rewrite of a `…MigrationByName` request: the caller by display name and a recipe
    /// resolved in that caller's scope.
    let private parameterTypeCallerRewriteByName declarations (item: JsonElement): AgentParameterTypeCallerRewrite =
        ensureFields "parameter type caller rewrite by name" [ "function"; "replacement" ] [ "function"; "replacement" ] item
        let caller = WireSymbolic.resolveFunctionAt declarations "function" item
        { CallerFunctionId = caller.Id
          Replacement = WireSymbolic.recipeForExistingFunction declarations caller (property "replacement" item) }

    /// The subject of a `references` request: `kind` selects the declaration kind, and the subject is
    /// named by its exact ID key or by display names (a member within its owner), never both.
    let private referenceSubject (declarations: AuthoringDeclarations) (root: JsonElement): ReferenceSubject =
        let has name = (tryProperty name root).IsSome
        let kind = requiredString "kind" root
        let exactly exactKeys nameKeys =
            ensureFields $"references request for a {kind}" ([ "id"; "op"; "kind" ] @ exactKeys @ nameKeys) [ "id"; "op"; "kind" ] root
            let exact = exactKeys |> List.forall has
            let named = nameKeys |> List.forall has
            if exact = named || (exactKeys @ nameKeys |> List.filter has).Length > (if exact then exactKeys.Length else nameKeys.Length) then
                let exactText = String.Join(", ", exactKeys)
                let nameText = String.Join(", ", nameKeys)
                WireInput.fail $"A references request for a {kind} names its subject by exactly one of {exactText} (exact) and {nameText} (display names)."
            exact
        match kind with
        | "function" ->
            if exactly [ "functionId" ] [ "function" ] then ReferenceSubject.Function(id<FunctionTag> "functionId" root)
            else ReferenceSubject.Function (WireSymbolic.resolveFunctionAt declarations "function" root).Id
        | "type" ->
            if exactly [ "typeId" ] [ "type" ] then ReferenceSubject.Type(id<TypeTag> "typeId" root)
            else ReferenceSubject.Type (WireSymbolic.resolveTypeAt declarations "type" root).Id
        | "effect" ->
            if exactly [ "effectId" ] [ "effect" ] then ReferenceSubject.Effect(id<EffectTag> "effectId" root)
            else ReferenceSubject.Effect (WireSymbolic.resolveEffectAt declarations "effect" root).Id
        | "capability" ->
            if exactly [ "capabilityId" ] [ "capability" ] then ReferenceSubject.Capability(id<CapabilityTag> "capabilityId" root)
            else ReferenceSubject.Capability (WireSymbolic.resolveCapabilityAt declarations "capability" root).Id
        | "capabilityOperation" ->
            if exactly [ "operationId" ] [ "capability"; "operation" ] then ReferenceSubject.CapabilityOperation(id<CapabilityOperationTag> "operationId" root)
            else
                let owner = WireSymbolic.resolveCapabilityAt declarations "capability" root
                ReferenceSubject.CapabilityOperation (WireSymbolic.capabilityOperation owner (requiredString "operation" root)).Id
        | "variant" ->
            if exactly [ "variantId" ] [ "type"; "variant" ] then ReferenceSubject.Variant(id<VariantTag> "variantId" root)
            else
                let owner = WireSymbolic.resolveTypeAt declarations "type" root
                ReferenceSubject.Variant (WireSymbolic.unionVariant owner (requiredString "variant" root)).Id
        | "field" ->
            // A Product field is `type` + `field`; a Union variant field adds `variant`.
            let nameKeys = if has "variant" then [ "type"; "variant"; "field" ] else [ "type"; "field" ]
            if exactly [ "fieldId" ] nameKeys then ReferenceSubject.Field(id<FieldTag> "fieldId" root)
            else
                let owner = WireSymbolic.resolveTypeAt declarations "type" root
                if has "variant" then
                    let variant = WireSymbolic.unionVariant owner (requiredString "variant" root)
                    ReferenceSubject.Field (WireSymbolic.variantField variant (requiredString "field" root)).Id
                else ReferenceSubject.Field (WireSymbolic.productField owner (requiredString "field" root)).Id
        | value -> WireInput.fail $"Unsupported references subject kind '{value}'; use function, type, variant, field, effect, capability or capabilityOperation."

    let private semanticQueryFailure requestId (error: SemanticQueryError) =
        let message =
            match error with
            | SemanticQueryError.FunctionNotFound functionId -> $"No function has the ID '{StableId.value functionId}'."
            | SemanticQueryError.TypeNotFound typeId -> $"No type has the ID '{StableId.value typeId}'."
            | SemanticQueryError.NodeNotFound(functionId, nodeId) ->
                $"Function '{StableId.value functionId}' has no node '{StableId.value nodeId}'."
            | SemanticQueryError.SubjectNotFound(kind, id) -> $"No {kind} has the ID '{id}'."
        failure requestId (objectValue [ "code", box "SemanticQueryFailure"; "message", box message ])

    /// The `InternalError` message: exception names and messages stay off the wire.
    let internalErrorMessage = "Unexpected internal failure; this request was not applied and the session is unchanged."

    let executeLine (session: AuthoringSession) line =
        use _scope = SnapshotComputationScope.Start()
        let mutable requestId: string = null
        try
            let sessionFailure id error = failure id (WireEncode.errorWithDeclarations (session.Declarations()) error)
            // A blank line is answered like any other malformed line, so a client that waits
            // for one reply per line never stalls.
            if String.IsNullOrWhiteSpace line then
                WireInput.fail "Empty request line; expected one JSON object."
            use document = parseDocument line
            let root = document.RootElement
            requestId <- requiredString "id" root
            let operationName = requiredString "op" root
            let encodeProposal =
                let full =
                    match tryProperty "detail" root with
                    | None -> false
                    | Some value when value.ValueKind = JsonValueKind.String && value.GetString() = "compact" -> false
                    | Some value when value.ValueKind = JsonValueKind.String && value.GetString() = "full" -> true
                    | Some _ -> WireInput.fail "detail must be \"compact\" or \"full\"."
                if not full then
                    WireEncode.compactProposal
                elif optionalBool "structuredReview" false root then
                    WireEncode.proposalWith "full" WireGraph.functionEntity WireGraph.focus
                else
                    WireEncode.proposal
            let proposalResponse =
                function
                | Ok proposal -> success requestId (encodeProposal proposal)
                | Error error -> sessionFailure requestId error
            // A simulate request may name the snapshot its author inspected; the workflow then rejects
            // it with its stale-base code instead of silently simulating against a newer snapshot.
            let expectedSnapshotId () =
                match tryProperty "expectedSnapshotId" root with
                | Some _ -> id<SnapshotTag> "expectedSnapshotId" root
                | None -> currentSnapshotId session

            match operationName with
            | "status" ->
                ensureFields "status request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.Status() with
                | Ok value -> success requestId (WireEncode.status value)
                | Error error -> sessionFailure requestId error
            | "dependency" ->
                ensureFields "dependency request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.DependencySelection with
                | None -> success requestId (objectValue [ "selection", null ])
                | Some manifest ->
                    let selected = JsonSerializer.Deserialize<JsonElement>(manifest)
                    success requestId (objectValue [ "selection", box selected ])
            | "previewDependencyUpgrade" ->
                ensureFields "previewDependencyUpgrade request" [ "id"; "op"; "source"; "replaces" ] [ "id"; "op"; "source" ] root
                let replaces = tryProperty "replaces" root |> Option.map (fun _ -> requiredString "replaces" root)
                match session.PreviewDependencyUpgrade(requiredString "source" root, replaces) with
                | Error error -> sessionFailure requestId error
                | Ok preview ->
                    let changes (items: AuthoringDependencyChange list) =
                        items |> List.map (fun item -> objectValue [ "kind", box item.Kind; "id", box item.Id; "exported", box item.Exported ]) |> arrayValue
                    success requestId
                        (objectValue
                            [ "replacedSnapshotId", box preview.ReplacedSnapshotId
                              "replacedFingerprint", box preview.ReplacedFingerprint
                              "newSnapshotId", box preview.NewSnapshotId
                              "newFingerprint", box preview.NewFingerprint
                              "compatible", box (List.isEmpty preview.Issues)
                              "added", changes preview.Added
                              "removed", changes preview.Removed
                              "changed", changes preview.Changed
                              "issues", preview.Issues |> List.map (fun issue -> objectValue [ "code", box issue.Code; "message", box issue.Message ]) |> arrayValue ])
            | "simulateDependencyUpgrade" | "simulateDependencyUpgradeByName" ->
                ensureFields "dependency upgrade repair" [ "id"; "op"; "upgrades"; "selection"; "repairs"; "expectedSnapshotId"; "structuredReview"; "detail" ] [ "id"; "op"; "repairs" ] root
                let change =
                    match tryProperty "upgrades" root, tryProperty "selection" root with
                    | Some _, None ->
                        AuthoringDependencyChangeRequest.Upgrades(array "upgrades" root |> List.map (fun item ->
                            ensureFields "dependency replacement" [ "source"; "replaces" ] [ "source"; "replaces" ] item
                            { Source = requiredString "source" item; Replaces = requiredString "replaces" item }))
                    | None, Some selected ->
                        ensureFields "dependency selection" [ "sources"; "replaces" ] [ "sources"; "replaces" ] selected
                        let strings name = array name selected |> List.map (fun item ->
                            if item.ValueKind <> JsonValueKind.String then fail "Selection entries must be strings."
                            item.GetString())
                        AuthoringDependencyChangeRequest.Selection { Sources = strings "sources"; Replaces = strings "replaces" }
                    | _ -> fail "Name exactly one of upgrades or selection."
                match session.DependencyUpgradeDeclarations change with
                | Error error -> sessionFailure requestId error
                | Ok declarations ->
                    let named = operationName.EndsWith("ByName", StringComparison.Ordinal)
                    let values = property "repairs" root
                    ensureFields "local repairs" [ "fields"; "functions"; "operations" ] [] values
                    let items name = match tryProperty name values with None -> [] | Some _ -> array name values
                    let resolveType item = if named then WireSymbolic.typeRef declarations item else typeRef item
                    let fields = items "fields" |> List.map (fun item ->
                        if named then
                            ensureFields "local field repair" [ "type"; "field"; "newType" ] [ "type"; "field"; "newType" ] item
                            let owner = WireSymbolic.resolveTypeAt declarations "type" item
                            { TypeId = owner.Id; FieldId = (WireSymbolic.productField owner (requiredString "field" item)).Id; NewType = resolveType (property "newType" item) }
                        else
                            ensureFields "local field repair" [ "typeId"; "fieldId"; "newType" ] [ "typeId"; "fieldId"; "newType" ] item
                            { TypeId = id<TypeTag> "typeId" item; FieldId = id<FieldTag> "fieldId" item; NewType = resolveType (property "newType" item) })
                    let functions = items "functions" |> List.map (fun item ->
                        let key = if named then "function" else "functionId"
                        ensureFields "local function repair" [ key; "parameterTypes"; "resultType"; "replacement" ] [ key; "parameterTypes"; "resultType"; "replacement" ] item
                        let fn = if named then WireSymbolic.resolveFunctionAt declarations key item else
                                     declarations.Functions |> List.tryFind (fun f -> f.Id = id<FunctionTag> key item) |> Option.defaultWith (fun () -> fail "Unknown repair function.")
                        let resolve = if named then WireSymbolic.typeRefForExistingFunction declarations fn else typeRef
                        { FunctionId = fn.Id
                          ParameterTypes = array "parameterTypes" item |> List.map (fun parameter ->
                              let key = if named then "parameter" else "parameterId"
                              ensureFields "function parameter type" [ key; "type" ] [ key; "type" ] parameter
                              { ParameterId = if named then (WireSymbolic.functionParameter fn (requiredString key parameter)).Id else id<ParameterTag> key parameter
                                Type = resolve (property "type" parameter) })
                          ResultType = resolve (property "resultType" item)
                          Replacement = if named then WireSymbolic.recipeForExistingFunction declarations fn (property "replacement" item) else recipe (property "replacement" item) })
                    let operations = items "operations" |> List.map (fun item ->
                        let keys = if named then [ "capability"; "operation" ] else [ "operationId" ]
                        ensureFields "local operation repair" (keys @ [ "parameterTypes"; "resultType" ]) (keys @ [ "parameterTypes"; "resultType" ]) item
                        let operation = if named then
                                            let cap = WireSymbolic.resolveCapabilityAt declarations "capability" item
                                            WireSymbolic.capabilityOperation cap (requiredString "operation" item)
                                        else declarations.Capabilities |> List.collect (fun c -> c.Operations) |> List.tryFind (fun o -> o.Id = id<CapabilityOperationTag> "operationId" item) |> Option.defaultWith (fun () -> fail "Unknown repair operation.")
                        { OperationId = operation.Id
                          ParameterTypes = array "parameterTypes" item |> List.map (fun parameter ->
                              let key = if named then "parameter" else "parameterId"
                              ensureFields "operation parameter type" [ key; "type" ] [ key; "type" ] parameter
                              { ParameterId = if named then (WireSymbolic.capabilityParameter operation (requiredString key parameter)).Id else id<CapabilityParameterTag> key parameter
                                Type = resolveType (property "type" parameter) })
                          ResultType = resolveType (property "resultType" item) })
                    proposalResponse(session.SimulateDependencyUpgrade(change, { Fields = fields; Functions = functions; Operations = operations }, expectedSnapshotId ()))
            | "previewDependencyUpgrades" ->
                ensureFields "previewDependencyUpgrades request" [ "id"; "op"; "upgrades"; "selection" ] [ "id"; "op" ] root
                let preview =
                    match tryProperty "upgrades" root, tryProperty "selection" root with
                    | Some _, None ->
                        let upgrades = array "upgrades" root |> List.map (fun item ->
                            ensureFields "dependency replacement" [ "source"; "replaces" ] [ "source"; "replaces" ] item
                            { Source = requiredString "source" item; Replaces = requiredString "replaces" item })
                        session.PreviewDependencyUpgrades upgrades
                    | None, Some selected ->
                        ensureFields "dependency selection upgrade" [ "sources"; "replaces" ] [ "sources"; "replaces" ] selected
                        let strings name = array name selected |> List.map (fun item ->
                            if item.ValueKind <> JsonValueKind.String then fail $"{name} entries must be strings."
                            item.GetString())
                        session.PreviewDependencySelectionUpgrade { Sources = strings "sources"; Replaces = strings "replaces" }
                    | _ -> fail "Name exactly one of upgrades or selection."
                match preview with
                | Error error -> sessionFailure requestId error
                | Ok preview ->
                    let changes (items: AuthoringDependencyChange list) =
                        items |> List.map (fun item -> objectValue [ "kind", box item.Kind; "id", box item.Id; "exported", box item.Exported ]) |> arrayValue
                    let members = preview.Upgrades |> List.map (fun item ->
                        objectValue [ "replacedSnapshotId", box item.ReplacedSnapshotId; "replacedFingerprint", box item.ReplacedFingerprint
                                      "newSnapshotId", box item.NewSnapshotId; "newFingerprint", box item.NewFingerprint
                                      "added", changes item.Added; "removed", changes item.Removed; "changed", changes item.Changed ]) |> arrayValue
                    success requestId (objectValue
                        [ "reviewFingerprint", box preview.ReviewFingerprint; "upgrades", members
                          "replaced", preview.Replaced |> List.map (fun item -> objectValue [ "snapshotId", box item.SnapshotId; "fingerprint", box item.Fingerprint ]) |> arrayValue
                          "selected", preview.Selected |> List.map (fun item -> objectValue [ "snapshotId", box item.SnapshotId; "fingerprint", box item.Fingerprint ]) |> arrayValue
                          "compatible", box (List.isEmpty preview.Issues)
                          "added", changes preview.Added; "removed", changes preview.Removed; "changed", changes preview.Changed
                          "issues", preview.Issues |> List.map (fun issue -> objectValue [ "code", box issue.Code; "message", box issue.Message ]) |> arrayValue ])
            | "declarations" ->
                ensureFields "declarations request" [ "id"; "op" ] [ "id"; "op" ] root
                success requestId (WireEncode.declarations (session.Declarations()))
            | "proposals" ->
                ensureFields "proposals request" [ "id"; "op" ] [ "id"; "op" ] root
                success requestId (WireEncode.pendingProposals (session.PendingProposals()))
            | "references" ->
                let declarations = session.Declarations()
                let subject = referenceSubject declarations root
                match session.References subject with
                | Ok items -> success requestId (WireEncode.references declarations subject items)
                | Error error -> semanticQueryFailure requestId error
            | "readFunction" ->
                ensureFields
                    "readFunction request"
                    [ "id"; "op"; "function"; "functionId" ]
                    [ "id"; "op" ]
                    root
                let functionId =
                    match tryProperty "function" root, tryProperty "functionId" root with
                    | Some _, None ->
                        (WireSymbolic.resolveFunctionAt (session.Declarations()) "function" root).Id
                    | None, Some _ -> id<FunctionTag> "functionId" root
                    | _ -> WireInput.fail "readFunction requires exactly one of function and functionId."
                match session.FocusFunction functionId with
                | Ok focus -> success requestId (WireGraph.focus focus)
                | Error error -> semanticQueryFailure requestId error
            | "readProposal" ->
                ensureFields "readProposal request" [ "id"; "op"; "proposalId"; "path"; "offset"; "limit"; "structuredReview" ] [ "id"; "op"; "proposalId" ] root
                let integer name fallback =
                    match tryProperty name root with
                    | None -> fallback
                    | Some value ->
                        if value.ValueKind <> JsonValueKind.Number then WireInput.fail $"{name} must be a JSON Int32."
                        match value.TryGetInt32() with
                        | true, number -> number
                        | _ -> WireInput.fail $"{name} must be a JSON Int32."
                let path = tryProperty "path" root |> Option.map (fun _ -> requiredString "path" root) |> Option.defaultValue ""
                match session.PendingProposalView(requiredString "proposalId" root) with
                | Error error -> sessionFailure requestId error
                | Ok pending ->
                    let structured = optionalBool "structuredReview" true root
                    let full = if structured then WireEncode.proposalWith "full" WireGraph.functionEntity WireGraph.focus pending.Proposal else WireEncode.proposal pending.Proposal
                    success requestId (WireProposalProjection.page pending structured full path (integer "offset" 0) (integer "limit" 20))
            | "viewFunction" ->
                ensureFields
                    "viewFunction request"
                    [ "id"; "op"; "function"; "functionId"; "output" ]
                    [ "id"; "op" ]
                    root
                let functionId =
                    match tryProperty "function" root, tryProperty "functionId" root with
                    | Some _, None ->
                        (WireSymbolic.resolveFunctionAt (session.Declarations()) "function" root).Id
                    | None, Some _ -> id<FunctionTag> "functionId" root
                    | _ -> WireInput.fail "viewFunction requires exactly one of function and functionId."
                let output = tryProperty "output" root |> Option.map (fun _ -> requiredString "output" root)
                match session.FocusFunction functionId with
                | Error error -> semanticQueryFailure requestId error
                | Ok focus ->
                    match ViewerFile.writeFocus session.Path output focus with
                    | Error message -> failure requestId (objectValue [ "code", box "ViewWriteFailed"; "message", box message ])
                    | Ok(path, artifact) ->
                        success
                            requestId
                            (objectValue
                                [ "functionId", box (StableId.value functionId)
                                  "displayName", box focus.Function.DisplayName
                                  "kind", box "functionFocus"
                                  "mediaType", box artifact.MediaType
                                  "path", box path ])
            | "viewProposal" ->
                ensureFields "viewProposal request" [ "id"; "op"; "proposalId"; "output" ] [ "id"; "op"; "proposalId" ] root
                let proposalId = requiredString "proposalId" root
                let output = tryProperty "output" root |> Option.map (fun _ -> requiredString "output" root)
                match session.PendingProposalView proposalId with
                | Error error -> sessionFailure requestId error
                | Ok view ->
                    let committedFocus functionId = session.FocusFunction functionId |> Result.toOption
                    match ViewerFile.writeProposalReview session.Path output view committedFocus with
                    | Error message -> failure requestId (objectValue [ "code", box "ViewWriteFailed"; "message", box message ])
                    | Ok(path, artifact) ->
                        success
                            requestId
                            (objectValue
                                [ "proposalId", box proposalId
                                  "kind", box "changeReview"
                                  "mediaType", box artifact.MediaType
                                  "path", box path ])
            | "simulateOperations" ->
                ensureFields
                    "simulateOperations request"
                    [ "id"; "op"; "operations"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "operations" ]
                    root
                let declarations = session.Declarations()
                let operations = array "operations" root |> List.map (WireOperation.operationRequest declarations)
                proposalResponse (session.SimulateOperationRequests(operations, expectedSnapshotId ()))
            | "simulateOperationsByName" ->
                ensureFields
                    "simulateOperationsByName request"
                    [ "id"; "op"; "operations"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "operations" ]
                    root
                let declarations = session.Declarations()
                let operations = array "operations" root |> List.map (WireOperation.operationRequestByName declarations)
                proposalResponse (session.SimulateOperationRequests(operations, expectedSnapshotId ()))
            | "simulateCreateFunction" ->
                ensureFields
                    "simulateCreateFunction request"
                    [ "id"; "op"; "displayName"; "exported"; "typeParameters"; "parameters"; "resultType"; "requiredEffects"; "body"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ]
                    root
                let parameters =
                    array "parameters" root
                    |> List.map (fun item ->
                        ensureFields
                            "function parameter"
                            [ "displayName"; "type" ]
                            [ "displayName"; "type" ]
                            item
                        let parameter: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
                            { DisplayName = requiredString "displayName" item
                              Type = typeRef (property "type" item) }
                        parameter)
                let requiredEffects =
                    array "requiredEffects" root
                    |> List.map (fun item ->
                        if item.ValueKind <> JsonValueKind.String then
                            WireInput.fail "requiredEffects entries must be stable-ID strings."
                        stableId<EffectTag> "requiredEffects" (item.GetString()))
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      DisplayName = requiredString "displayName" root
                      Exported = optionalBool "exported" false root
                      TypeParameters = WireInput.functionTypeParameters root
                      Parameters = parameters
                      ResultType = typeRef (property "resultType" root)
                      RequiredEffects = requiredEffects
                      Body = recipe (property "body" root) }
                proposalResponse (session.SimulateCreateFunction request)
            | "simulateCreateFunctionByName" ->
                ensureFields
                    "simulateCreateFunctionByName request"
                    [ "id"; "op"; "displayName"; "exported"; "typeParameters"; "parameters"; "resultType"; "requiredEffects"; "body"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ]
                    root
                let declarations = session.Declarations()
                let displayName = requiredString "displayName" root
                if declarations.Functions |> List.exists (fun item -> item.DisplayName = displayName) then
                    WireInput.fail $"Function display name '{displayName}' already exists. Choose a unique name or use the exact-ID authoring surface."
                let parameters = WireSymbolic.functionParameters declarations root
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      DisplayName = displayName
                      Exported = optionalBool "exported" false root
                      TypeParameters = WireInput.functionTypeParameters root
                      Parameters = parameters
                      ResultType = WireSymbolic.typeRef declarations (property "resultType" root)
                      RequiredEffects = WireSymbolic.requiredEffects declarations root
                      Body =
                        WireSymbolic.recipeForNewFunction
                            declarations
                            displayName
                            parameters
                            (property "body" root) }
                proposalResponse (session.SimulateCreateFunction request)
            | "simulateParameterTypeMigration" ->
                ensureFields
                    "simulateParameterTypeMigration request"
                    [ "id"; "op"; "targetFunctionId"; "targetParameterId"; "newParameterType"; "targetReplacement"; "callerRewrites"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "targetFunctionId"; "targetParameterId"; "newParameterType"; "targetReplacement"; "callerRewrites" ]
                    root
                let request: AgentParameterTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetFunctionId = id<FunctionTag> "targetFunctionId" root
                      TargetParameterId = id<ParameterTag> "targetParameterId" root
                      NewParameterType = typeRef (property "newParameterType" root)
                      TargetReplacement = recipe (property "targetReplacement" root)
                      CallerRewrites = array "callerRewrites" root |> List.map parameterTypeCallerRewrite }
                proposalResponse (session.SimulateParameterTypeMigration request)
            | "simulateParameterTypeMigrationByName" ->
                ensureFields
                    "simulateParameterTypeMigrationByName request"
                    [ "id"; "op"; "function"; "parameter"; "newParameterType"; "targetReplacement"; "callerRewrites"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "function"; "parameter"; "newParameterType"; "targetReplacement"; "callerRewrites" ]
                    root
                let declarations = session.Declarations()
                let fn = WireSymbolic.resolveFunctionAt declarations "function" root
                let parameter = WireSymbolic.functionParameter fn (requiredString "parameter" root)
                let request: AgentParameterTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetFunctionId = fn.Id
                      TargetParameterId = parameter.Id
                      NewParameterType = WireSymbolic.typeRefForExistingFunction declarations fn (property "newParameterType" root)
                      TargetReplacement = WireSymbolic.recipeForExistingFunction declarations fn (property "targetReplacement" root)
                      CallerRewrites = array "callerRewrites" root |> List.map (parameterTypeCallerRewriteByName declarations) }
                proposalResponse (session.SimulateParameterTypeMigration request)
            | "simulateResultTypeMigrationByName" ->
                ensureFields
                    "simulateResultTypeMigrationByName request"
                    [ "id"; "op"; "function"; "newResultType"; "targetReplacement"; "callerRewrites"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "function"; "newResultType"; "targetReplacement"; "callerRewrites" ]
                    root
                let declarations = session.Declarations()
                let fn = WireSymbolic.resolveFunctionAt declarations "function" root
                let request: AgentResultTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetFunctionId = fn.Id
                      NewResultType = WireSymbolic.typeRefForExistingFunction declarations fn (property "newResultType" root)
                      TargetReplacement = WireSymbolic.recipeForExistingFunction declarations fn (property "targetReplacement" root)
                      CallerRewrites = array "callerRewrites" root |> List.map (parameterTypeCallerRewriteByName declarations) }
                proposalResponse (session.SimulateResultTypeMigration request)
            | "simulateResultTypeMigration" ->
                ensureFields
                    "simulateResultTypeMigration request"
                    [ "id"; "op"; "targetFunctionId"; "newResultType"; "targetReplacement"; "callerRewrites"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "targetFunctionId"; "newResultType"; "targetReplacement"; "callerRewrites" ]
                    root
                let request: AgentResultTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetFunctionId = id<FunctionTag> "targetFunctionId" root
                      NewResultType = typeRef (property "newResultType" root)
                      TargetReplacement = recipe (property "targetReplacement" root)
                      CallerRewrites = array "callerRewrites" root |> List.map parameterTypeCallerRewrite }
                proposalResponse (session.SimulateResultTypeMigration request)
            | "simulateCapabilityOperationSignatureMigrationByName" ->
                ensureFields "operation signature migration by name" [ "id"; "op"; "capability"; "operation"; "parameterTypes"; "resultType"; "functionRepairs"; "structuredReview"; "detail"; "expectedSnapshotId" ] [ "id"; "op"; "capability"; "operation"; "parameterTypes"; "resultType"; "functionRepairs" ] root
                let declarations = session.Declarations()
                let cap = WireSymbolic.resolveCapabilityAt declarations "capability" root
                let operation = WireSymbolic.capabilityOperation cap (requiredString "operation" root)
                let request: AgentCapabilityOperationSignatureMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      OperationId = operation.Id
                      ParameterTypes = array "parameterTypes" root |> List.map (fun item ->
                          ensureFields "operation parameter type" [ "parameter"; "type" ] [ "parameter"; "type" ] item
                          let name = requiredString "parameter" item
                          let parameter = WireSymbolic.capabilityParameter operation name
                          { ParameterId = parameter.Id; Type = WireSymbolic.typeRef declarations (property "type" item) })
                      ResultType = WireSymbolic.typeRef declarations (property "resultType" root)
                      FunctionRepairs = array "functionRepairs" root |> List.map (fun item ->
                          ensureFields "function repair" [ "function"; "parameterTypes"; "resultType"; "replacement" ] [ "function"; "parameterTypes"; "resultType"; "replacement" ] item
                          let fn = WireSymbolic.resolveFunctionAt declarations "function" item
                          { FunctionId = fn.Id
                            ParameterTypes = array "parameterTypes" item |> List.map (fun parameter ->
                                ensureFields "function parameter type" [ "parameter"; "type" ] [ "parameter"; "type" ] parameter
                                let name = requiredString "parameter" parameter
                                let p = WireSymbolic.functionParameter fn name
                                { ParameterId = p.Id; Type = WireSymbolic.typeRefForExistingFunction declarations fn (property "type" parameter) })
                            ResultType = WireSymbolic.typeRefForExistingFunction declarations fn (property "resultType" item)
                            Replacement = WireSymbolic.recipeForExistingFunction declarations fn (property "replacement" item) }) }
                proposalResponse (session.SimulateCapabilityOperationSignatureMigration request)
            | "simulateCapabilityOperationSignatureMigration" ->
                ensureFields "operation signature migration" [ "id"; "op"; "operationId"; "parameterTypes"; "resultType"; "functionRepairs"; "structuredReview"; "detail"; "expectedSnapshotId" ] [ "id"; "op"; "operationId"; "parameterTypes"; "resultType"; "functionRepairs" ] root
                let request: AgentCapabilityOperationSignatureMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      OperationId = id<CapabilityOperationTag> "operationId" root
                      ParameterTypes = array "parameterTypes" root |> List.map (fun item ->
                          ensureFields "operation parameter type" [ "parameterId"; "type" ] [ "parameterId"; "type" ] item
                          { ParameterId = id<CapabilityParameterTag> "parameterId" item; Type = typeRef (property "type" item) })
                      ResultType = typeRef (property "resultType" root)
                      FunctionRepairs = array "functionRepairs" root |> List.map (fun item ->
                          ensureFields "function repair" [ "functionId"; "parameterTypes"; "resultType"; "replacement" ] [ "functionId"; "parameterTypes"; "resultType"; "replacement" ] item
                          { FunctionId = id<FunctionTag> "functionId" item
                            ParameterTypes = array "parameterTypes" item |> List.map (fun parameter ->
                                ensureFields "function parameter type" [ "parameterId"; "type" ] [ "parameterId"; "type" ] parameter
                                { ParameterId = id<ParameterTag> "parameterId" parameter; Type = typeRef (property "type" parameter) })
                            ResultType = typeRef (property "resultType" item)
                            Replacement = recipe (property "replacement" item) }) }
                proposalResponse (session.SimulateCapabilityOperationSignatureMigration request)
            | "simulateProductFieldTypeMigrationByName" ->
                ensureFields "field type migration by name"
                    [ "id"; "op"; "type"; "field"; "newFieldType"; "functionRewrites"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "type"; "field"; "newFieldType"; "functionRewrites" ] root
                let declarations = session.Declarations()
                let owner = WireSymbolic.resolveTypeAt declarations "type" root
                let field = WireSymbolic.productField owner (requiredString "field" root)
                let request: AgentProductFieldTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      FieldId = field.Id
                      NewFieldType = WireSymbolic.typeRef declarations (property "newFieldType" root)
                      FunctionRewrites = array "functionRewrites" root |> List.map (fun item ->
                          ensureFields "function rewrite by name" [ "function"; "replacement" ] [ "function"; "replacement" ] item
                          let fn = WireSymbolic.resolveFunctionAt declarations "function" item
                          { FunctionId = fn.Id
                            Replacement = WireSymbolic.recipeForExistingFunction declarations fn (property "replacement" item) }) }
                proposalResponse (session.SimulateProductFieldTypeMigration request)
            | "simulateProductFieldTypeMigration" ->
                ensureFields
                    "simulateProductFieldTypeMigration request"
                    [ "id"; "op"; "fieldId"; "newFieldType"; "functionRewrites"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "fieldId"; "newFieldType"; "functionRewrites" ]
                    root
                let request: AgentProductFieldTypeMigrationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      FieldId = id<FieldTag> "fieldId" root
                      NewFieldType = typeRef (property "newFieldType" root)
                      FunctionRewrites =
                        array "functionRewrites" root
                        |> List.map (fun item ->
                            ensureFields "function rewrite" [ "functionId"; "replacement" ] [ "functionId"; "replacement" ] item
                            { FunctionId = id<FunctionTag> "functionId" item
                              Replacement = recipe (property "replacement" item) }) }
                proposalResponse (session.SimulateProductFieldTypeMigration request)
            | "simulateCreateType" ->
                ensureFields
                    "simulateCreateType request"
                    [ "id"; "op"; "displayName"; "exported"; "typeParameters"; "typeKind"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName"; "typeKind" ]
                    root
                let request: AgentCreateTypeDeclarationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      DisplayName = requiredString "displayName" root
                      Exported = optionalBool "exported" false root
                      TypeParameters = typeParameterNames root
                      Kind = typeKindIntent (property "typeKind" root) }
                proposalResponse (session.SimulateCreateType request)
            | "simulateCreateTypeByName" ->
                ensureFields
                    "simulateCreateTypeByName request"
                    [ "id"; "op"; "displayName"; "exported"; "typeParameters"; "typeKind"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName"; "typeKind" ]
                    root
                let declarations = session.Declarations()
                let displayName = requiredString "displayName" root
                let exported = optionalBool "exported" false root
                let typeParameters = typeParameterNames root
                // A declaration that names itself is a recursive type: it is created as a group of one.
                let self = StableIdAllocator.newTypeId ()
                let kind = WireSymbolic.typeKindIntentIn (Map.ofList [ displayName, self ]) declarations (property "typeKind" root)
                let simulated =
                    if WireSymbolic.kindReferences self kind then
                        session.SimulateCreateTypeGroup
                            { ExpectedBaseSnapshotId = expectedSnapshotId ()
                              Declarations = [ { Reference = self; DisplayName = displayName; Exported = exported; TypeParameters = typeParameters; Kind = kind } ] }
                    else
                        session.SimulateCreateType
                            { ExpectedBaseSnapshotId = expectedSnapshotId ()
                              DisplayName = displayName
                              Exported = exported
                              TypeParameters = typeParameters
                              Kind = kind }
                proposalResponse simulated
            | "simulateCreateTypesByName" ->
                ensureFields
                    "simulateCreateTypesByName request"
                    [ "id"; "op"; "types"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "types" ]
                    root
                let declarations = session.Declarations()
                let items =
                    array "types" root
                    |> List.map (fun item ->
                        ensureFields "type declaration" [ "displayName"; "exported"; "typeParameters"; "typeKind" ] [ "displayName"; "typeKind" ] item
                        requiredString "displayName" item, (optionalBool "exported" false item, typeParameterNames item, property "typeKind" item))
                let references = items |> List.map (fun (name, _) -> name, StableIdAllocator.newTypeId ())
                // A name declared twice keeps its first reference; the workflow rejects the duplicate.
                let group = references |> List.rev |> Map.ofList
                let request: AgentCreateTypeDeclarationGroupRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      Declarations =
                        List.map2
                            (fun (name, (exported, typeParameters, kind)) (_, reference) ->
                                { Reference = reference
                                  DisplayName = name
                                  Exported = exported
                                  TypeParameters = typeParameters
                                  Kind = WireSymbolic.typeKindIntentIn group declarations kind })
                            items
                            references }
                proposalResponse (session.SimulateCreateTypeGroup request)
            | "simulateAddUnionVariantLike" ->
                ensureFields
                    "simulateAddUnionVariantLike request"
                    [ "id"; "op"; "typeId"; "sourceVariantId"; "displayName"; "additionalFields"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "typeId"; "sourceVariantId"; "displayName" ]
                    root
                let request: AgentAddUnionVariantLikeRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetTypeId = id<TypeTag> "typeId" root
                      SourceVariantId = id<VariantTag> "sourceVariantId" root
                      NewVariantDisplayName = requiredString "displayName" root }
                let additionalFields =
                    tryProperty "additionalFields" root
                    |> Option.map (fun _ -> array "additionalFields" root |> List.map typeFieldIntent)
                    |> Option.defaultValue []
                proposalResponse (session.SimulateAddUnionVariantLike(request, additionalFields))
            | "simulateAddUnionVariantLikeByName" ->
                ensureFields
                    "simulateAddUnionVariantLikeByName request"
                    [ "id"; "op"; "type"; "sourceVariant"; "displayName"; "additionalFields"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "type"; "sourceVariant"; "displayName" ]
                    root
                let declarations = session.Declarations()
                let owner = WireSymbolic.resolveTypeAt declarations "type" root
                let source = WireSymbolic.unionVariant owner (requiredString "sourceVariant" root)
                let request: AgentAddUnionVariantLikeRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetTypeId = owner.Id
                      SourceVariantId = source.Id
                      NewVariantDisplayName = requiredString "displayName" root }
                let additionalFields =
                    tryProperty "additionalFields" root
                    |> Option.map (fun _ -> array "additionalFields" root |> List.map (WireSymbolic.fieldIntent declarations))
                    |> Option.defaultValue []
                proposalResponse (session.SimulateAddUnionVariantLike(request, additionalFields))
            | "simulateCreateEffect" ->
                ensureFields
                    "simulateCreateEffect request"
                    [ "id"; "op"; "displayName"; "exported"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName" ]
                    root
                let request: AgentCreateEffectDeclarationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      DisplayName = requiredString "displayName" root
                      Exported = optionalBool "exported" false root }
                proposalResponse (session.SimulateCreateEffect request)
            | "simulateCreateCapability" ->
                ensureFields
                    "simulateCreateCapability request"
                    [ "id"; "op"; "displayName"; "exported"; "operations"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName"; "operations" ]
                    root
                let request: AgentCreateCapabilityDeclarationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      DisplayName = requiredString "displayName" root
                      Exported = optionalBool "exported" false root
                      Operations = array "operations" root |> List.map capabilityOperationIntent }
                proposalResponse (session.SimulateCreateCapability request)
            | "simulateCreateCapabilityByName" ->
                ensureFields
                    "simulateCreateCapabilityByName request"
                    [ "id"; "op"; "displayName"; "exported"; "operations"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "displayName"; "operations" ]
                    root
                let declarations = session.Declarations()
                let displayName = requiredString "displayName" root
                if declarations.Capabilities |> List.exists (fun item -> item.DisplayName = displayName) then
                    WireInput.fail $"Capability display name '{displayName}' already exists. Choose a unique name or use the exact-ID authoring surface."
                let request: AgentCreateCapabilityDeclarationRequest =
                    { ExpectedBaseSnapshotId = expectedSnapshotId ()
                      DisplayName = displayName
                      Exported = optionalBool "exported" false root
                      Operations = array "operations" root |> List.map (WireSymbolic.capabilityOperationIntent declarations) }
                proposalResponse (session.SimulateCreateCapability request)
            | "simulateReplaceBody" ->
                ensureFields
                    "simulateReplaceBody request"
                    [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement" ]
                    root
                let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = expectedSnapshotId ()
                      ExpectedCurrentResultNodeId = id<GraphNodeTag> "expectedCurrentResultNodeId" root
                      Replacement = recipe (property "replacement" root) }
                proposalResponse (session.SimulateReplaceBody request)
            | "simulateReplaceBodyByName" ->
                ensureFields
                    "simulateReplaceBodyByName request"
                    [ "id"; "op"; "function"; "replacement"; "structuredReview"; "detail"; "expectedSnapshotId"; "expectedResultNodeId" ]
                    [ "id"; "op"; "function"; "replacement" ]
                    root
                let declarations = session.Declarations()
                let functionEntity =
                    WireSymbolic.resolveFunctionAt declarations "function" root
                let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                    { TargetFunctionId = functionEntity.Id
                      ExpectedBaseSnapshotId = expectedSnapshotId ()
                      ExpectedCurrentResultNodeId =
                        match tryProperty "expectedResultNodeId" root with
                        | Some _ -> id<GraphNodeTag> "expectedResultNodeId" root
                        | None -> functionEntity.Body.ResultNodeId
                      Replacement =
                        WireSymbolic.recipeForExistingFunction
                            declarations
                            functionEntity
                            (property "replacement" root) }
                proposalResponse (session.SimulateReplaceBody request)
            | "simulateReplaceTarget" ->
                ensureFields
                    "simulateReplaceTarget request"
                    [ "id"; "op"; "functionId"; "targetNodeId"; "replacement"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "functionId"; "targetNodeId"; "replacement" ]
                    root
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetNodeId = id<GraphNodeTag> "targetNodeId" root
                      Replacement = recipe (property "replacement" root) }
                proposalResponse (session.SimulateReplaceTarget request)
            | "simulateReplaceTargetByName" ->
                ensureFields
                    "simulateReplaceTargetByName request"
                    [ "id"; "op"; "function"; "targetNodeId"; "replacement"; "structuredReview"; "detail"; "expectedSnapshotId" ]
                    [ "id"; "op"; "function"; "targetNodeId"; "replacement" ]
                    root
                let declarations = session.Declarations()
                let functionEntity = WireSymbolic.resolveFunctionAt declarations "function" root
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = functionEntity.Id
                      ExpectedBaseSnapshotId = expectedSnapshotId ()
                      TargetNodeId = id<GraphNodeTag> "targetNodeId" root
                      Replacement = WireSymbolic.recipeForExistingFunction declarations functionEntity (property "replacement" root) }
                proposalResponse (session.SimulateReplaceTarget request)
            | "commit" ->
                ensureFields
                    "commit request"
                    [ "id"; "op"; "proposalId"; "allowIncomplete" ]
                    [ "id"; "op"; "proposalId" ]
                    root
                let policy =
                    if optionalBool "allowIncomplete" false root then
                        CommitPolicy.AllowBlockingObligations
                    else
                        CommitPolicy.RejectBlockingObligations
                match session.Commit(requiredString "proposalId" root, policy) with
                | Ok receipt -> success requestId (WireEncode.commitReceipt receipt)
                | Error error -> sessionFailure requestId error
            | "reject" ->
                ensureFields
                    "reject request"
                    [ "id"; "op"; "proposalId" ]
                    [ "id"; "op"; "proposalId" ]
                    root
                match session.Reject(requiredString "proposalId" root) with
                | Ok receipt -> success requestId (WireEncode.rejectionReceipt receipt)
                | Error error -> sessionFailure requestId error
            | "executionReadiness" ->
                ensureFields
                    "executionReadiness request"
                    [ "id"; "op" ]
                    [ "id"; "op" ]
                    root
                match session.ProjectExecution() with
                | Ok execution ->
                    success
                        requestId
                        (objectValue
                            [ "kind", box "ready"
                              "sourceSnapshotId", box (StableId.value execution.SourceSnapshotId)
                              "functionCount", box execution.Functions.Length ])
                | Error error -> sessionFailure requestId error
            | "verifyChange" | "verifyChangeByName" as op ->
                ensureFields $"{op} request" [ "id"; "op"; "proposalId"; "cases"; "output" ] [ "id"; "op"; "proposalId"; "cases" ] root
                let proposalId = requiredString "proposalId" root
                // By name, the case function and its arguments resolve against the committed snapshot,
                // which both runs share; an expected candidate value resolves against the candidate,
                // whose declarations may differ (a created type, a renamed variant).
                let baseline = if op = "verifyChangeByName" then Some(session.Declarations()) else None
                let candidate =
                    baseline |> Option.map (fun declarations ->
                        match session.PendingProposalView proposalId with
                        | Ok { ProposedSnapshot = Some snapshot } ->
                            { Functions = snapshot.Functions; Types = snapshot.Types; Effects = snapshot.Effects
                              Capabilities = snapshot.Capabilities; Provenance = declarations.Provenance }
                        | _ -> declarations)
                let functionKey = if baseline.IsSome then "function" else "functionId"
                let cases = array "cases" root |> List.mapi (fun caseIndex item ->
                    ensureFields "verification case" [ "label"; functionKey; "arguments"; "expectedCandidate" ] [ "label"; functionKey; "arguments" ] item
                    { Label = requiredString "label" item
                      FunctionId =
                        match baseline with
                        | Some declarations -> (WireSymbolic.resolveFunctionAt declarations functionKey item).Id
                        | None -> id<FunctionTag> functionKey item
                      Arguments = array "arguments" item |> List.mapi (fun index value -> pureValue baseline $"cases[{caseIndex}].arguments[{index}]" value)
                      ExpectedCandidate = tryProperty "expectedCandidate" item |> Option.map (pureValue candidate $"cases[{caseIndex}].expectedCandidate") })
                match ChangeVerification.verify session proposalId cases with
                | Error error -> sessionFailure requestId error
                | Ok evidence ->
                    let names = runNames evidence.BaselineDeclarations
                    let candidateNames = runNames evidence.CandidateDeclarations
                    let observation names functionId arguments expected = function
                        | Ok result -> objectValue [ "actual", encodePureValue names result.Actual; "calls", result.Calls |> List.map (encodeCall names) |> arrayValue; "matches", result.Matches |> Option.map box |> Option.defaultValue null; "mismatch", result.Mismatch |> Option.map (encodeMismatch names) |> Option.defaultValue null ]
                        | Error error ->
                            let issue = PureRun.inputIssueForError names.Declarations functionId arguments expected error
                            objectValue [ "error", encodeRunError names functionId issue error ]
                    let encodeCase item =
                        objectValue
                            ([ "label", box item.Case.Label
                               "functionId", box (StableId.value item.Case.FunctionId) ]
                             @ named names "function" item.Case.FunctionId
                             @ [ "arguments", item.Case.Arguments |> List.map (encodePureValue names) |> arrayValue
                                 "expectedCandidate", item.Case.ExpectedCandidate |> Option.map (encodePureValue candidateNames) |> Option.defaultValue null
                                 "status", box item.Status
                                 "baseline", observation names item.Case.FunctionId item.Case.Arguments None item.Baseline
                                 "candidate", observation candidateNames item.Case.FunctionId item.Case.Arguments item.Case.ExpectedCandidate item.Candidate ])
                    let encoded =
                        objectValue
                            [ "proposalId", box evidence.ProposalId
                              "baselineSnapshotId", box (StableId.value evidence.BaselineSnapshotId)
                              "candidateSnapshotId", box (StableId.value evidence.CandidateSnapshotId)
                              "caseRequest", box ((property "cases" root).GetRawText())
                              "casesDigest", box (System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((property "cases" root).GetRawText())) |> System.Convert.ToHexString)
                              "verifierIdentity", objectValue
                                  [ "version", box "nyrwvy-change-verification-v5"
                                    "observationPolicy", box "canonical-result-and-ordered-fake-calls-v4; structured-run-diagnostics; canonical-explicit-expectations; sequential-only; mapped-recognized-candidate-only-failures-counterexample; other-failures-unknown; explicit-result-changes-only"
                                    "runtimeVersion", box (System.Environment.Version.ToString())
                                    "buildContextFingerprint", box (GeneratedBuild.fingerprint [])
                                    "authoringModule", box (typeof<AuthoringSession>.Module.ModuleVersionId.ToString())
                                    "semanticsModule", box (typeof<Snapshot>.Module.ModuleVersionId.ToString())
                                    "executionModule", box (typeof<ExecutionProgram>.Module.ModuleVersionId.ToString())
                                    "backendModule", box (typeof<CSharpBackendProfile>.Module.ModuleVersionId.ToString()) ]
                              "baselineFingerprint", box evidence.BaselineFingerprint
                              "candidateFingerprint", box evidence.CandidateFingerprint
                              "coverage", box "Explicit cases only; no proof of equivalence. Recognized mapped candidate-only program/fake failures are counterexamples after baseline success; other failures and concurrency are unknown. Effects must be preserved. Entry points are not dynamic coverage."
                              "caseEntryFunctionIds", evidence.Observations |> List.map (fun item -> item.Case.FunctionId) |> List.distinct |> List.map (StableId.value >> box) |> arrayValue
                              "affectedFunctionIds", evidence.AffectedFunctionIds |> List.map (StableId.value >> box) |> arrayValue
                              "cases", evidence.Observations |> List.map encodeCase |> arrayValue ]
                    let json = System.Text.Json.JsonSerializer.Serialize(encoded, System.Text.Json.JsonSerializerOptions(WriteIndented = true, MaxDepth = WireJson.responseMaxJsonDepth))
                    let output = tryProperty "output" root |> Option.map (fun _ -> requiredString "output" root)
                    match session.PendingProposalView evidence.ProposalId with
                    | Error error -> sessionFailure requestId error
                    | Ok view ->
                        match ViewerFile.writeVerificationReview session.Path output view (fun id -> session.FocusFunction id |> Result.toOption) evidence json with
                        | Error message -> failure requestId (objectValue [ "code", box "ViewWriteFailed"; "message", box message ])
                        | Ok(path, _) -> success requestId (objectValue [ "evidence", encoded; "reviewPath", box path ])
            | "runPure" | "runPureByName" as op ->
                let names = if op = "runPureByName" then Some(session.Declarations()) else None
                let functionKey = if names.IsSome then "function" else "functionId"
                ensureFields
                    $"{op} request"
                    [ "id"; "op"; functionKey; "arguments"; "expected"; "hostSources" ]
                    [ "id"; "op"; functionKey; "arguments" ]
                    root
                let functionId =
                    match names with
                    | Some declarations -> (WireSymbolic.resolveFunctionAt declarations functionKey root).Id
                    | None -> id<FunctionTag> functionKey root
                let arguments = array "arguments" root |> List.mapi (fun index value -> pureValue names $"arguments[{index}]" value)
                let expected = tryProperty "expected" root |> Option.map (pureValue names "expected")
                let hostSources =
                    tryProperty "hostSources" root
                    |> Option.map (fun _ ->
                        array "hostSources" root |> List.map (fun path ->
                            if path.ValueKind <> JsonValueKind.String then fail "hostSources must contain file paths."
                            path.GetString()))
                    |> Option.defaultValue []
                // Both run operations report the same output: exact IDs plus display names beside them.
                let runNames = runNames (names |> Option.defaultWith session.Declarations)
                match PureRun.runWithHost session functionId arguments expected hostSources with
                | Ok result ->
                    success requestId
                        (objectValue
                            ([ "sourceSnapshotId", box (StableId.value result.SnapshotId)
                               "functionId", box (StableId.value result.FunctionId) ]
                             @ named runNames "function" result.FunctionId
                             @ [ "actual", encodePureValue runNames result.Actual
                                 "expected", result.Expected |> Option.map (encodePureValue runNames) |> Option.defaultValue null
                                 "matches", result.Matches |> Option.map box |> Option.defaultValue null
                                 "mismatch", result.Mismatch |> Option.map (encodeMismatch runNames) |> Option.defaultValue null
                                 "calls", result.Calls |> List.map (encodeCall runNames) |> arrayValue ]))
                | Error error -> failure requestId (encodeRunError runNames functionId (PureRun.inputIssueForError runNames.Declarations functionId arguments expected error) error)
            | "emitCSharp" ->
                ensureFields
                    "emitCSharp request"
                    [ "id"; "op"; "profile" ]
                    [ "id"; "op"; "profile" ]
                    root
                let profileValue = property "profile" root
                let profile =
                    match requiredString "kind" profileValue with
                    | "empty" ->
                        ensureFields "empty C# profile" [ "kind" ] [ "kind" ] profileValue
                        CSharpBackendProfile.empty
                    | "systemConsoleWriteLine" ->
                        ensureFields
                            "System.Console.WriteLine C# profile"
                            [ "kind"; "operationId" ]
                            [ "kind"; "operationId" ]
                            profileValue
                        CSharpBackendProfile.systemConsoleWriteLine(
                            id<CapabilityOperationTag> "operationId" profileValue
                        )
                    | "externalImplementations" ->
                        ensureFields
                            "External C# implementations profile"
                            [ "kind"; "operationIds"; "concurrentCapabilityIds" ]
                            [ "kind"; "operationIds" ]
                            profileValue
                        let operationIds =
                            array "operationIds" profileValue
                            |> List.map (fun element ->
                                if element.ValueKind <> JsonValueKind.String then
                                    WireInput.fail "External operation ID must be a string."
                                stableId<CapabilityOperationTag> "operationId" (element.GetString()))
                        let concurrentIds =
                            tryProperty "concurrentCapabilityIds" profileValue
                            |> Option.map (fun _ ->
                                array "concurrentCapabilityIds" profileValue |> List.map (fun element ->
                                    if element.ValueKind <> JsonValueKind.String then fail "Concurrent capability ID must be a string."
                                    stableId<CapabilityTag> "capabilityId" (element.GetString())))
                            |> Option.defaultValue []
                        CSharpBackendProfile.externalImplementations operationIds
                        |> CSharpBackendProfile.withConcurrentCapabilities concurrentIds
                    | value -> WireInput.fail $"Unsupported C# profile '{value}'."
                match session.EmitCSharp profile with
                | Ok artifact -> success requestId (WireEncode.csharpArtifact artifact)
                | Error error -> sessionFailure requestId error
            | value ->
                failure
                    requestId
                    (objectValue
                        [ "code", box "UnsupportedOperation"
                          "message", box $"Unsupported operation '{value}'." ])
        with
        | WireInput.ProtocolInputException message ->
            failure
                requestId
                (objectValue
                    [ "code", box "ProtocolError"
                      "message", box message ])
        | :? InvalidOperationException as error ->
            failure
                requestId
                (objectValue
                    [ "code", box "SessionError"
                      "message", box error.Message ])
        // Last resort: an unexpected failure answers this request only. Session state is
        // mutated solely after a successful write, so the session remains usable. The wire gets a
        // stable code and message; the exception detail goes to the operator's stderr only.
        | error ->
            Console.Error.WriteLine($"InternalError for request '{requestId}': {error.GetType().FullName}: {error.Message}")
            failure
                requestId
                (objectValue
                    [ "code", box "InternalError"
                      "message", box internalErrorMessage ])

    /// Answers each input line with exactly one response line until the input ends.
    let serve (session: AuthoringSession) (input: IO.TextReader) (output: IO.TextWriter) =
        let mutable line = input.ReadLine()
        while not (isNull line) do
            output.WriteLine(executeLine session line)
            output.Flush()
            line <- input.ReadLine()

