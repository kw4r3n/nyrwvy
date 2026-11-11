namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

/// Standard operations added after the owner's choices of 2026-10-01 (post-M2 orders 37 to 39):
/// ListRange and StringRepeat (an allocation too large is a host failure), Int64ToHexString and
/// StringParseInt64Hex (Python format(x, "x") with a '-' sign), and Int64Min, Int64Max and
/// Int64WrappingAbs; and the owner choice of 2026-10-03 (order 40): Int64CheckedAdd,
/// Int64CheckedSubtract and Int64CheckedMultiply, the exact result or None outside Int64; and
/// Float64Round (order 43), decimal places half away from zero on the shortest decimal text.
module StandardOperationChoiceTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("standard-operation-choices-" + name)
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    /// Parameter types, result type and body of a one-operation function for each mode.
    /// Parameters are numbered from 0 in declaration order.
    let private shapeOf mode =
        let unary kind = GraphBuilder.node (fun ids -> kind ids[0])
        let binary kind = GraphBuilder.node (fun ids -> kind (ids[0], ids[1]))
        let slot index = GraphBuilder.param (id<ParameterTag> $"p{index}")
        match mode with
        | "range" -> [ intType; intType ], listOf intType, binary SemanticNodeKind.ListRange [ slot 0; slot 1 ]
        | "repeat" -> [ textType; intType ], textType, binary SemanticNodeKind.StringRepeat [ slot 0; slot 1 ]
        | "hex" -> [ intType ], textType, unary SemanticNodeKind.Int64ToHexString [ slot 0 ]
        | "parseHex" -> [ textType ], optionOf intType, unary SemanticNodeKind.StringParseInt64Hex [ slot 0 ]
        | "min" -> [ intType; intType ], intType, binary SemanticNodeKind.Int64Min [ slot 0; slot 1 ]
        | "max" -> [ intType; intType ], intType, binary SemanticNodeKind.Int64Max [ slot 0; slot 1 ]
        | "abs" -> [ intType ], intType, unary SemanticNodeKind.Int64WrappingAbs [ slot 0 ]
        | "checkedAdd" -> [ intType; intType ], optionOf intType, binary SemanticNodeKind.Int64CheckedAdd [ slot 0; slot 1 ]
        | "checkedSubtract" -> [ intType; intType ], optionOf intType, binary SemanticNodeKind.Int64CheckedSubtract [ slot 0; slot 1 ]
        | "checkedMultiply" -> [ intType; intType ], optionOf intType, binary SemanticNodeKind.Int64CheckedMultiply [ slot 0; slot 1 ]
        | "round" -> [ floatType; intType ], floatType, binary SemanticNodeKind.Float64Round [ slot 0; slot 1 ]
        | other -> failwithf "Unknown mode %s" other

    [<Theory>]
    [<InlineData("range", "listRange", "Int64OperandTypeMismatch")>]
    [<InlineData("repeat", "stringRepeat", "StringIntrinsicTypeMismatch")>]
    [<InlineData("hex", "int64ToHexString", "Int64IntrinsicTypeMismatch")>]
    [<InlineData("parseHex", "stringParseInt64Hex", "StringIntrinsicTypeMismatch")>]
    [<InlineData("min", "int64Min", "Int64OperandTypeMismatch")>]
    [<InlineData("max", "int64Max", "Int64OperandTypeMismatch")>]
    [<InlineData("abs", "int64WrappingAbs", "Int64IntrinsicTypeMismatch")>]
    [<InlineData("checkedAdd", "int64CheckedAdd", "Int64OperandTypeMismatch")>]
    [<InlineData("checkedSubtract", "int64CheckedSubtract", "Int64OperandTypeMismatch")>]
    [<InlineData("checkedMultiply", "int64CheckedMultiply", "Int64OperandTypeMismatch")>]
    [<InlineData("round", "float64Round", "Float64OperandTypeMismatch")>]
    let ``Standard operations type check, persist, and lower through every layer``
        (mode: string, wireKind: string, issueCode: string) =
        let parameterTypes, resultType, body = shapeOf mode
        let wrongFor parameterType = if parameterType = intType then textType else intType

        let snapshotWith (types: TypeRef list) declaredResultType =
            let fn: SemanticFunction =
                { Id = id<FunctionTag> ("fn-" + mode)
                  DisplayName = mode
                  Exported = true
                  TypeParameters = []
                  Parameters = types |> List.mapi (fun index t -> { Id = id<ParameterTag> $"p{index}"; DisplayName = $"p{index}"; Type = t })
                  ResultType = declaredResultType
                  RequiredEffects = []
                  MustNotWait = false
                  Body = GraphBuilder.build (id<GraphNodeTag>) body }

            { Snapshot.empty (id<SnapshotTag> ("base-" + mode)) with Functions = [ fn ] }

        for index in 0 .. parameterTypes.Length - 1 do
            let mistyped = parameterTypes |> List.mapi (fun at parameterType -> if at = index then wrongFor parameterType else parameterType)
            Assert.Contains(SnapshotValidator.validate (snapshotWith mistyped resultType), fun issue -> issue.Code = issueCode)
        Assert.Contains(
            SnapshotValidator.validate (snapshotWith parameterTypes (wrongFor resultType)),
            fun issue -> issue.Code = "FunctionResultTypeMismatch"
        )
        let committed = snapshotWith parameterTypes resultType
        Assert.Empty(SnapshotValidator.validate committed)
        let json = SnapshotCodec.serialize committed |> unwrap "serialize"
        Assert.Contains("\"" + wireKind + "\"", json)
        let decoded = SnapshotCodec.deserialize json |> unwrap "deserialize"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute decoded)
        let persisted = SnapshotCodec.serialize decoded |> unwrap "reserialize"
        let restored = SnapshotCodec.deserialize persisted |> unwrap "decode"
        Assert.Equal(SemanticFingerprint.compute committed, SemanticFingerprint.compute restored)
        let typed = TypedSemanticIr.project restored |> unwrap "typed"
        Assert.Contains("\"" + wireKind + "\"", TypedSemanticIrSerializer.serialize typed)
        let execution = ExecutionIr.lower typed |> unwrap "lower"
        Assert.Contains("\"" + wireKind + "\"", ExecutionIrSerializer.serialize execution)
        // Every parameter reference replaced by a literal of the other primitive type.
        let parameters = committed.Functions.Head.Parameters
        let broken =
            { committed with
                Functions = committed.Functions |> List.map (fun fn ->
                    { fn with
                        Body =
                            { fn.Body with
                                Nodes = fn.Body.Nodes |> List.map (fun node ->
                                    match node.Kind with
                                    | SemanticNodeKind.ParameterReference reference ->
                                        let parameter = parameters |> List.find (fun parameter -> parameter.Id = reference)
                                        let literal =
                                            if parameter.Type = intType then SemanticNodeKind.StringLiteral "1"
                                            else SemanticNodeKind.Int64Literal 1L
                                        { node with Kind = literal }
                                    | _ -> node) } }) }
        Assert.Contains(SnapshotValidator.validate broken, fun issue -> issue.Code = issueCode)

    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private stringType = """{"kind":"primitive","primitive":"string"}"""
    let private int64 (value: string) = """{"kind":"int64","value":""" + "\"" + value + "\"}"
    let private text (value: string) = """{"kind":"string","value":""" + JsonSerializer.Serialize value + "}"
    let private parameter name = """{"kind":"parameter","name":""" + "\"" + name + "\"}"

    /// Defines a function from JSON fragments: (name, type) parameters, the result type and the body.
    let private define (call: string -> JsonElement) name (parameters: (string * string) list) resultType body =
        let parameterList =
            parameters
            |> List.map (fun (parameterName, parameterType) -> """{"displayName":""" + "\"" + parameterName + "\",\"type\":" + parameterType + "}")
            |> String.concat ","
        let proposal =
            call (
                """{"id":"define","op":"simulateCreateFunctionByName","exported":true,"displayName":""" + "\"" + name + "\""
                + ""","parameters":[""" + parameterList + """],"resultType":""" + resultType
                + ""","requiredEffects":[],"body":""" + body + "}")
        call ("""{"id":"commit","op":"commit","proposalId":""" + "\"" + proposal.GetProperty("proposalId").GetString() + "\"}") |> ignore

    let private expectRun (call: string -> JsonElement) name (arguments: string list) expected =
        let result =
            call (
                """{"id":"run","op":"runPureByName","function":""" + "\"" + name + "\""
                + ""","arguments":[""" + String.concat "," arguments + """],"expected":""" + expected + "}")
        Assert.True(result.GetProperty("matches").GetBoolean(), name + " " + String.concat ", " arguments + ": " + result.ToString())
