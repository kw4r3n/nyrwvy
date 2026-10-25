namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Products and Unions with type parameters: declaration, derived and explicit type arguments,
/// field access and matching at the arguments, regular recursion, equality, persistence, lowering
/// and runPure.
module GenericTypeTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private rejectionText session line =
        use reply = execute session line
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.GetRawText())
        reply.RootElement.GetRawText()

    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private int64Type = primitive "int64"
    let private str = primitive "string"
    let private boolType = primitive "bool"
    let private typeParameter name = $"""{{"kind":"typeParameter","name":"{name}"}}"""
    let private named name (arguments: string list) =
        $"""{{"kind":"named","name":"{name}","arguments":[{String.concat "," arguments}]}}"""
    let private listOf item = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{item}]}}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private field name typeRef = $"""{{"displayName":"{name}","type":{typeRef}}}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-generic-types-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            run session path
        finally
            Directory.Delete(root, true)

    let private signature (parameters: (string * string) list) =
        parameters |> List.map (fun (name, typeRef) -> field name typeRef) |> String.concat ","

    let private createLine name parameters resultType body =
        $"""{{"id":"{name}","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{signature parameters}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""

    let private define session name parameters resultType body = publish session (createLine name parameters resultType body)

    /// Box<T> { value: T }, Maybe<T> = Just(value: T) | Nothing, Tree<T> { value: T, children: List<Tree<T>> }.
    let private defineTypes session =
        publish session $"""{{"id":"box","op":"simulateCreateTypeByName","exported":true,"displayName":"Box","typeParameters":["T"],"typeKind":{{"kind":"product","fields":[{field "value" (typeParameter "T")}]}}}}"""
        publish session $"""{{"id":"maybe","op":"simulateCreateTypeByName","exported":true,"displayName":"Maybe","typeParameters":["T"],"typeKind":{{"kind":"union","variants":[{{"displayName":"Just","fields":[{field "value" (typeParameter "T")}]}},{{"displayName":"Nothing","fields":[]}}]}}}}"""
        publish session $"""{{"id":"tree","op":"simulateCreateTypeByName","exported":true,"displayName":"Tree","typeParameters":["T"],"typeKind":{{"kind":"product","fields":[{field "value" (typeParameter "T")},{field "children" (listOf (named "Tree" [ typeParameter "T" ]))}]}}}}"""

    let private boxOf item = named "Box" [ item ]
    let private maybeOf item = named "Maybe" [ item ]
    let private construct typeName fields = $"""{{"kind":"productConstruct","type":"{typeName}","fields":[{fields}]}}"""
    let private fieldValue name value = $"""{{"field":"{name}","value":{value}}}"""
    let private access typeName fieldName source = $"""{{"kind":"fieldAccess","type":"{typeName}","field":"{fieldName}","source":{source}}}"""

    let private defineFunctions session =
        // Derived type arguments: Box<Int64> from the field value.
        define session "boxInt" [ "n", int64Type ] (boxOf int64Type) (construct "Box" (fieldValue "value" (parameter "n")))
        define session "unbox" [ "b", boxOf int64Type ] int64Type (access "Box" "value" (parameter "b"))
        // A generic function returning a generic type, and a concrete caller.
        define session "wrap" [ "x", typeParameter "T" ] (boxOf (typeParameter "T")) (construct "Box" (fieldValue "value" (parameter "x")))
        define session "wrapped" [ "s", str ] str (access "Box" "value" $"""{{"kind":"directCall","function":"wrap","arguments":[{{"parameter":"x","value":{parameter "s"}}}]}}""")
        // A fieldless variant needs its type arguments.
        define session "nothing" [] (maybeOf int64Type) $"""{{"kind":"unionConstruct","type":"Maybe","variant":"Nothing","typeArguments":[{int64Type}],"fields":[]}}"""
        let justValue = """{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Maybe","variant":"Just","field":"value"}"""
        define
            session
            "orDefault"
            [ "m", maybeOf int64Type; "d", int64Type ]
            int64Type
            $"""{{"kind":"matchUnion","type":"Maybe","input":{parameter "m"},"arms":[{{"variant":"Just","body":{justValue}}},{{"variant":"Nothing","body":{parameter "d"}}}]}}"""
        define session "sameBox" [ "l", boxOf int64Type; "r", boxOf int64Type ] boolType $"""{{"kind":"valueEqual","left":{parameter "l"},"right":{parameter "r"}}}"""
        define session "leaf" [ "n", int64Type ] (named "Tree" [ int64Type ]) (construct "Tree" (fieldValue "value" (parameter "n") + "," + fieldValue "children" $"""{{"kind":"listEmpty","itemType":{named "Tree" [ int64Type ]}}}"""))

    [<Fact>]
    let ``Generic Products and Unions type check, persist and lower through every layer`` () =
        withSession (fun session path ->
            defineTypes session
            // A fieldless variant without type arguments cannot derive T.
            Assert.Contains(
                "PureExpressionTypeArgumentsRequired",
                rejectionText session (createLine "bad" [] (maybeOf int64Type) """{"kind":"unionConstruct","type":"Maybe","variant":"Nothing","fields":[]}""")
            )
            Assert.Contains(
                "PureExpressionTypeArgumentArityMismatch",
                rejectionText session (createLine "bad" [ "n", int64Type ] (boxOf int64Type) $"""{{"kind":"productConstruct","type":"Box","typeArguments":[{int64Type},{str}],"fields":[{fieldValue "value" (parameter "n")}]}}""")
            )
            // Explicit type arguments must agree with the field values.
            Assert.Contains(
                "PureExpressionFieldBindingTypeMismatch",
                rejectionText session (createLine "bad" [ "n", int64Type ] (boxOf str) $"""{{"kind":"productConstruct","type":"Box","typeArguments":[{str}],"fields":[{fieldValue "value" (parameter "n")}]}}""")
            )
            // A reference must pass one argument per type parameter.
            Assert.Contains("NamedTypeArityMismatch", rejectionText session (createLine "bad" [ "b", named "Box" [] ] int64Type (access "Box" "value" (parameter "b"))))
            // Equality follows the instantiated fields.
            Assert.Contains(
                "ValueEqualTypeNotEquatable",
                rejectionText session (createLine "bad" [ "l", boxOf (primitive "float64"); "r", boxOf (primitive "float64") ] boolType $"""{{"kind":"valueEqual","left":{parameter "l"},"right":{parameter "r"}}}""")
            )
            // Recursion into the type's own group passes plain type parameters only.
            Assert.Contains(
                "IrregularRecursiveType",
                rejectionText session $"""{{"id":"nest","op":"simulateCreateTypeByName","exported":true,"displayName":"Nest","typeParameters":["T"],"typeKind":{{"kind":"union","variants":[{{"displayName":"Leaf","fields":[{field "value" (typeParameter "T")}]}},{{"displayName":"Deeper","fields":[{field "inner" (named "Nest" [ listOf (typeParameter "T") ])}]}}]}}}}"""
            )
            // A field may use only its own type's type parameters.
            Assert.Contains(
                "TypeParameterNotAllowed",
                rejectionText session $"""{{"id":"loose","op":"simulateCreateTypeByName","exported":true,"displayName":"Loose","typeKind":{{"kind":"product","fields":[{field "value" (typeParameter "T")}]}}}}"""
            )
            defineFunctions session

            let snapshot =
                match SnapshotCodec.deserialize (File.ReadAllText path) with
                | Ok snapshot -> snapshot
                | Error error -> failwithf "Workspace did not decode: %A" error
            Assert.Empty(SnapshotValidator.validate snapshot)
            let box = snapshot.Types |> List.find (fun t -> t.DisplayName = "Box")
            Assert.Equal(1, box.TypeParameters.Length)
            let construct =
                snapshot.Functions
                |> List.find (fun fn -> fn.DisplayName = "nothing")
                |> fun fn -> fn.Body.Nodes |> List.pick (fun node -> match node.Kind with SemanticNodeKind.UnionConstruct(_, arguments, _) -> Some arguments | _ -> None)
            Assert.Equal<TypeRef list>([ TypeRef.Primitive PrimitiveType.Int64 ], construct)
            let json = SnapshotCodec.serializeCanonical snapshot |> Result.defaultWith (failwithf "serialize: %A")
            Assert.Contains("\"schemaVersion\":18", json.Replace(" ", ""))
            Assert.Contains("\"typeParameters\"", json)
            let restored = SnapshotCodec.deserialize json |> Result.defaultWith (failwithf "decode: %A")
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            let older = json.Replace("\"schemaVersion\": 18", "\"schemaVersion\": 17").Replace("\"schemaVersion\":18", "\"schemaVersion\":17")
            Assert.NotEqual<string>(json, older)
            match SnapshotCodec.deserialize older with
            | Ok _ -> failwith "A schema 17 snapshot with generic types decoded."
            | Error _ -> ()
            let execution =
                TypedSemanticIr.project restored
                |> Result.defaultWith (failwithf "typed: %A")
                |> ExecutionIr.lower
                |> Result.defaultWith (failwithf "lower: %A")
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> Result.defaultWith (failwithf "emit: %A")).Source
            Assert.Contains("public readonly record struct " + CSharpBackendNames.typeName box.Id + "<", source))

    let private boxValue (value: int64) =
        $"""{{"kind":"product","type":"Box","typeArguments":[{int64Type}],"fields":[{{"field":"value","value":{{"kind":"int64","value":"{value}"}}}}]}}"""
    let private just (value: int64) =
        $"""{{"kind":"union","type":"Maybe","variant":"Just","typeArguments":[{int64Type}],"fields":[{{"field":"value","value":{{"kind":"int64","value":"{value}"}}}}]}}"""
    let private nothingValue = $"""{{"kind":"union","type":"Maybe","variant":"Nothing","typeArguments":[{int64Type}],"fields":[]}}"""
    let private integer (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private text (value: string) = $"""{{"kind":"string","value":{JsonSerializer.Serialize value}}}"""
    let private boolean (value: bool) = $"""{{"kind":"bool","value":{(if value then "true" else "false")}}}"""

    let private expectRun session name (arguments: string list) expected =
        let run =
            result session $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{String.concat "," arguments}],"expected":{expected}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), $"{name}: {run}")

    [<Fact; Trait("Suite", "Extended")>]
    let ``Generic values run through the C# backend under runPure`` () =
        withSession (fun session _ ->
            defineTypes session
            defineFunctions session
            expectRun session "boxInt" [ integer 7L ] (boxValue 7L)
            expectRun session "unbox" [ boxValue 42L ] (integer 42L)
            expectRun session "wrapped" [ text "hello" ] (text "hello")
            expectRun session "nothing" [] nothingValue
            expectRun session "orDefault" [ just 5L; integer 9L ] (integer 5L)
            expectRun session "orDefault" [ nothingValue; integer 9L ] (integer 9L)
            expectRun session "sameBox" [ boxValue 1L; boxValue 1L ] (boolean true)
            expectRun session "sameBox" [ boxValue 1L; boxValue 2L ] (boolean false)
            expectRun session "leaf" [ integer 3L ] $"""{{"kind":"product","type":"Tree","typeArguments":[{int64Type}],"fields":[{{"field":"value","value":{integer 3L}}},{{"field":"children","value":{{"kind":"list","itemType":{named "Tree" [ int64Type ]},"items":[]}}}}]}}""")
