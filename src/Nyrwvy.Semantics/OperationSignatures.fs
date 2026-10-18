namespace Nyrwvy.Core

/// The single table of fixed-signature expression operations. Inference
/// (`AgentExpressionStructuralWorkflow`) checks these operations from the table, and the agent
/// recipe reference takes their one-line signatures from
/// `tools/nyrwvy-agent/recipe-signatures.generated.mjs`, which `referenceModule` renders. Operations
/// with callbacks, type arguments, branches, scopes, declared types or their own diagnostics stay
/// hand-coded in both places.
[<RequireQualifiedAccess>]
module OperationSignatures =
    /// A type in a fixed operation signature: primitives and the List, Option, Result, Map and Set
    /// generics, possibly mentioning variables (`T`, `E`, `K`, `V`) that stand for any type. The first
    /// operand that mentions a variable binds it; later operands and the result use the bound type.
    [<RequireQualifiedAccess>]
    type SignatureType =
        | Primitive of PrimitiveType
        | Variable of name: string
        | List of item: SignatureType
        | Option of item: SignatureType
        | Result of ok: SignatureType * error: SignatureType
        | Map of key: SignatureType * value: SignatureType
        | Set of item: SignatureType

    type Operand =
        { /// The recipe's wire field, e.g. `substring`.
          Field: string
          /// The operand word in `PureExpressionOperandTypeMismatch` messages. It equals `Field` except
          /// for a few string operations whose messages have always said `left` and `right`.
          Side: string
          Type: SignatureType
          /// Text the reference signature appends to the type, e.g. ` of bytes U+00-U+FF`.
          TypeDetail: string option }

    /// A variable that must be bound to an equatable type, checked as soon as an operand binds it.
    type EquatableConstraint =
        { Variable: string
          Code: string
          /// The message before the `EquatableTypes.explain` text.
          Message: string }

    /// An operation whose operand and result types are fixed, or fixed up to `SignatureType` variables,
    /// and whose only diagnostics are operand type mismatches and an optional equatable constraint.
    type Signature =
        { /// The recipe kind in wire spelling: the `ExpressionRecipe` case name with a lowercase initial.
          Kind: string
          /// In `ExpressionRecipe` case field order, which is also the inference order.
          Operands: Operand list
          Result: SignatureType
          Equatable: EquatableConstraint option
          /// The comment after the reference signature, e.g. `code points`.
          Note: string option }

    /// Why an operand does not fit its signature.
    [<RequireQualifiedAccess>]
    type OperandFailure =
        /// The operand's type differs from a type that the signature and earlier operands fix.
        | TypeMismatch of side: string * expected: TypeRef * actual: TypeRef
        /// The operand lacks the shape of a type that still has unbound variables, e.g. `List<T>`.
        | ShapeMismatch of side: string * expected: string * actual: TypeRef
        | NotEquatable of EquatableConstraint * TypeRef

    let private string = SignatureType.Primitive PrimitiveType.String
    let private bool = SignatureType.Primitive PrimitiveType.Bool
    let private int64 = SignatureType.Primitive PrimitiveType.Int64
    let private float64 = SignatureType.Primitive PrimitiveType.Float64
    let private bits32 = SignatureType.Primitive PrimitiveType.Bits32
    let private t = SignatureType.Variable "T"
    let private e = SignatureType.Variable "E"
    let private k = SignatureType.Variable "K"
    let private v = SignatureType.Variable "V"

    let private operand field operandType =
        { Field = field; Side = field; Type = operandType; TypeDetail = None }

    /// An operand whose mismatch message names it `side` instead of its wire field.
    let private operandAs field side operandType =
        { operand field operandType with Side = side }

    let private signature kind operands result note =
        { Kind = kind; Operands = operands; Result = result; Equatable = None; Note = note }

    let private unary kind operandType result note =
        signature kind [ operand "value" operandType ] result note

    let private binary kind operandType result note =
        signature kind [ operand "left" operandType; operand "right" operandType ] result note

    let private equatable variable code message (entry: Signature) =
        { entry with Equatable = Some { Variable = variable; Code = code; Message = message } }

    /// In recipe reference category order.
    let table: Signature list =
        [ // bool
          unary "boolNot" bool bool None
          // string
          unary "stringIsEmpty" string bool None
          signature "stringContains" [ operandAs "value" "left" string; operandAs "substring" "right" string ] bool None
          binary "stringEqual" string bool None
          binary "stringLessThan" string bool (Some "code-point order")
          binary "stringConcat" string string None
          unary "stringLength" string int64 (Some "code points")
          signature "stringSlice" [ operand "value" string; operand "start" int64; operand "end" int64 ] string (Some "clamped")
          unary "stringCodePoints" string (SignatureType.List string) None
          signature
              "stringIndexOf"
              [ operandAs "value" "left" string; operandAs "substring" "right" string; operand "from" int64 ]
              (SignatureType.Option int64)
              (Some "str.find")
          signature
              "stringLastIndexOf"
              [ operandAs "value" "left" string; operandAs "substring" "right" string ]
              (SignatureType.Option int64)
              (Some "str.rfind")
          signature "stringSpan" [ operand "value" string; operand "set" string; operand "from" int64 ] int64 (Some "lstrip as a position")
          signature "stringSpanBack" [ operand "value" string; operand "set" string; operand "end" int64 ] int64 (Some "rstrip as a position")
          signature "stringStartsWith" [ operandAs "value" "left" string; operandAs "prefix" "right" string ] bool None
          signature "stringEndsWith" [ operandAs "value" "left" string; operandAs "suffix" "right" string ] bool None
          signature
              "stringSplit"
              [ operandAs "value" "left" string; operandAs "separator" "right" string ]
              (SignatureType.List string)
              (Some "str.split(sep)")
          signature "stringSplitLines" [ operand "value" string; operand "breaks" string ] (SignatureType.List string) (Some "str.splitlines")
          signature
              "stringReplace"
              [ operand "value" string; operand "pattern" string; operand "replacement" string ]
              string
              (Some "str.replace(old, new)")
          unary "stringTrim" string string (Some "White_Space")
          unary "stringTrimStart" string string (Some "White_Space")
          unary "stringTrimEnd" string string (Some "White_Space")
          signature "stringJoin" [ operand "list" (SignatureType.List string); operand "separator" string ] string None
          unary "stringToLower" string string None
          unary "stringToUpper" string string None
          unary "stringUtf8ByteCount" string int64 None
          signature
              "stringDecodeUtf8Lossy"
              [ { operand "value" string with TypeDetail = Some " of bytes U+00-U+FF" } ]
              string
              (Some "decode('utf-8', 'replace')")
          unary "stringEncodeUtf8" string string (Some "encode('utf-8') as bytes U+00-U+FF")
          unary "stringFromCodePoint" int64 (SignatureType.Option string) (Some "chr")
          unary "stringToCodePoint" string (SignatureType.Option int64) (Some "ord")
          unary "int64ToString" int64 string None
          unary "stringParseInt64" string (SignatureType.Option int64) None
          unary "int64ToHexString" int64 string (Some "format(x, 'x')")
          unary "stringParseInt64Hex" string (SignatureType.Option int64) (Some "int(s, 16), strict")
          signature "stringRepeat" [ operand "text" string; operand "count" int64 ] string (Some "text * count")
          unary "float64ToString" float64 string (Some "repr")
          unary "stringParseFloat64" string (SignatureType.Option float64) None
          // int64
          binary "int64WrappingAdd" int64 int64 None
          binary "int64WrappingSubtract" int64 int64 None
          binary "int64WrappingMultiply" int64 int64 None
          binary "int64FloorDivide" int64 int64 (Some "Python //")
          binary "int64FloorModulo" int64 int64 (Some "Python %")
          binary "int64Min" int64 int64 None
          binary "int64Max" int64 int64 None
          binary "int64BitAnd" int64 int64 None
          binary "int64BitOr" int64 int64 None
          binary "int64BitXor" int64 int64 None
          signature "int64ShiftLeft" [ operand "value" int64; operand "count" int64 ] int64 (Some "wraps; count clamped to 0..64")
          signature "int64ShiftRight" [ operand "value" int64; operand "count" int64 ] int64 (Some "Python >>; count clamped to 0..64")
          binary "int64CheckedAdd" int64 (SignatureType.Option int64) (Some "left + right")
          binary "int64CheckedSubtract" int64 (SignatureType.Option int64) (Some "left - right")
          binary "int64CheckedMultiply" int64 (SignatureType.Option int64) (Some "left * right")
          unary "int64WrappingAbs" int64 int64 (Some "MinValue stays MinValue")
          unary "int64BitNot" int64 int64 (Some "Python ~x")
          binary "int64Equal" int64 bool None
          binary "int64LessThan" int64 bool None
          binary "int64LessThanOrEqual" int64 bool None
          binary "int64GreaterThan" int64 bool None
          binary "int64GreaterThanOrEqual" int64 bool None
          unary "int64ToFloat64" int64 float64 None
          // float64
          binary "float64Add" float64 float64 None
          binary "float64Min" float64 float64 (Some "NaN if either is NaN; -0.0 < 0.0")
          binary "float64Max" float64 float64 (Some "NaN if either is NaN; -0.0 < 0.0")
          binary "float64Subtract" float64 float64 None
          binary "float64Multiply" float64 float64 None
          binary "float64Divide" float64 float64 None
          binary "float64Equal" float64 bool None
          binary "float64LessThan" float64 bool None
          unary "float64IsNaN" float64 bool None
          unary "float64IsFinite" float64 bool None
          unary "float64IsInfinite" float64 bool None
          unary "float64Sqrt" float64 float64 None
          unary "float64Abs" float64 float64 None
          unary "float64Negate" float64 float64 None
          unary "float64Floor" float64 float64 None
          unary "float64Ceiling" float64 float64 None
          unary "float64Truncate" float64 float64 None
          unary "float64RoundToInt64" float64 (SignatureType.Option int64) (Some "nearest, half away from zero")
          signature "float64Round" [ operand "value" float64; operand "digits" int64 ] float64 (Some "decimal places, half away from zero")
          unary "float64FloorToInt64" float64 (SignatureType.Option int64) (Some "math.floor")
          // bits32
          unary "bits32FromInt64" int64 bits32 None
          unary "bits32ToInt64" bits32 int64 None
          binary "bits32Xor" bits32 bits32 None
          binary "bits32And" bits32 bits32 None
          binary "bits32Or" bits32 bits32 None
          unary "bits32Not" bits32 bits32 None
          binary "bits32Add" bits32 bits32 None
          signature "bits32RotateRight" [ operand "value" bits32; operand "amount" int64 ] bits32 None
          signature "bits32ShiftRight" [ operand "value" bits32; operand "amount" int64 ] bits32 None
          signature "bits32ShiftLeft" [ operand "value" bits32; operand "amount" int64 ] bits32 None
          // list
          unary "listLength" (SignatureType.List t) int64 None
          signature "listItemAt" [ operand "list" (SignatureType.List t); operand "index" int64 ] (SignatureType.Option t) None
          signature
              "listSlice"
              [ operand "list" (SignatureType.List t); operand "start" int64; operand "end" int64 ]
              (SignatureType.List t)
              (Some "clamped")
          signature
              "listSetAt"
              [ operand "list" (SignatureType.List t); operand "index" int64; operand "item" t ]
              (SignatureType.List t)
              (Some "unchanged when out of range")
          signature
              "listInsertAt"
              [ operand "list" (SignatureType.List t); operand "index" int64; operand "item" t ]
              (SignatureType.List t)
              (Some "index clamped to [0, length]")
          signature
              "listRemoveAt"
              [ operand "list" (SignatureType.List t); operand "index" int64 ]
              (SignatureType.List t)
              (Some "unchanged when out of range")
          signature "listRange" [ operand "start" int64; operand "end" int64 ] (SignatureType.List int64) (Some "list(range(start, end))")
          unary "listReverse" (SignatureType.List t) (SignatureType.List t) None
          unary "listLast" (SignatureType.List t) (SignatureType.Option t) (Some "xs[-1] if xs")
          unary "listDropLast" (SignatureType.List t) (SignatureType.List t) (Some "xs[:-1]")
          signature "listAppendItem" [ operand "list" (SignatureType.List t); operand "item" t ] (SignatureType.List t) None
          binary "listConcat" (SignatureType.List t) (SignatureType.List t) None
          signature "cancellationRequested" [] bool None
          signature "listIndexOf" [ operand "list" (SignatureType.List t); operand "item" t ] (SignatureType.Option int64) (Some "first equal item's index")
          |> equatable "T" "ListIndexOfItemTypeNotEquatable" "ListIndexOf compares items of a type that is not equatable: "
          signature "listLastIndexOf" [ operand "list" (SignatureType.List t); operand "item" t ] (SignatureType.Option int64) (Some "last equal item's index")
          |> equatable "T" "ListLastIndexOfItemTypeNotEquatable" "ListLastIndexOf compares items of a type that is not equatable: "
          signature "listContains" [ operand "list" (SignatureType.List t); operand "item" t ] bool (Some "item in list")
          |> equatable "T" "ListContainsItemTypeNotEquatable" "ListContains compares items of a type that is not equatable: "
          // map
          signature
              "mapInsert"
              [ operand "map" (SignatureType.Map(k, v)); operand "key" k; operand "value" v ]
              (SignatureType.Map(k, v))
              None
          signature "mapGet" [ operand "map" (SignatureType.Map(k, v)); operand "key" k ] (SignatureType.Option v) None
          signature "mapGetOrDefault" [ operand "map" (SignatureType.Map(k, v)); operand "key" k; operand "default" v ] v None
          signature "mapContainsKey" [ operand "map" (SignatureType.Map(k, v)); operand "key" k ] bool None
          signature "mapRemove" [ operand "map" (SignatureType.Map(k, v)); operand "key" k ] (SignatureType.Map(k, v)) None
          binary "mapUnion" (SignatureType.Map(k, v)) (SignatureType.Map(k, v)) (Some "left | right: right wins, left order then right's new keys")
          unary "mapSize" (SignatureType.Map(k, v)) int64 None
          unary "mapKeys" (SignatureType.Map(k, v)) (SignatureType.List k) (Some "insertion order")
          unary "mapValues" (SignatureType.Map(k, v)) (SignatureType.List v) (Some "insertion order")
          // set
          unary "setFromList" (SignatureType.List t) (SignatureType.Set t) (Some "first occurrence order")
          |> equatable "T" "SetItemTypeNotEquatable" "Set item type is not equatable: "
          signature "setInsert" [ operand "set" (SignatureType.Set t); operand "item" t ] (SignatureType.Set t) None
          signature "setContains" [ operand "set" (SignatureType.Set t); operand "item" t ] bool None
          signature "setRemove" [ operand "set" (SignatureType.Set t); operand "item" t ] (SignatureType.Set t) None
          binary "setUnion" (SignatureType.Set t) (SignatureType.Set t) (Some "left order, then right's new items")
          binary "setIntersection" (SignatureType.Set t) (SignatureType.Set t) (Some "left order")
          binary "setDifference" (SignatureType.Set t) (SignatureType.Set t) (Some "left order")
          binary "setIsSubset" (SignatureType.Set t) bool (Some "left <= right")
          unary "setSize" (SignatureType.Set t) int64 None
          unary "setToList" (SignatureType.Set t) (SignatureType.List t) (Some "insertion order")
          // optionResult
          unary "optionSome" t (SignatureType.Option t) None
          signature "optionGetOrDefault" [ operand "option" (SignatureType.Option t); operand "default" t ] t (Some "unwrap_or")
          signature "resultGetOrDefault" [ operand "result" (SignatureType.Result(t, e)); operand "default" t ] t (Some "unwrap_or")
          signature "optionOkOr" [ operand "option" (SignatureType.Option t); operand "error" e ] (SignatureType.Result(t, e)) (Some "ok_or")
          unary "resultOkValue" (SignatureType.Result(t, e)) (SignatureType.Option t) (Some "Result::ok")
          unary "resultErrValue" (SignatureType.Result(t, e)) (SignatureType.Option e) (Some "Result::err")
          unary "optionIsSome" (SignatureType.Option t) bool (Some "is_some")
          unary "resultIsOk" (SignatureType.Result(t, e)) bool (Some "is_ok") ]

    let private byKind = table |> List.map (fun entry -> entry.Kind, entry) |> Map.ofList

    /// Kinds kept for existing graphs and authoring scripts but left out of the agent reference,
    /// because another operation already expresses them directly ([decision](docs/decisions/retired-authoring-kinds.md)).
    /// Each maps to the recipe to use instead.
    let retired =
        Map.ofList
            [ "stringEqual", "valueEqual"
              "int64Equal", "valueEqual"
              "mapGetOrDefault", "optionGetOrDefault of mapGet"
              "int64GreaterThan", "int64LessThan with swapped operands"
              "int64LessThanOrEqual", "boolNot of int64LessThan with swapped operands"
              "int64GreaterThanOrEqual", "boolNot of int64LessThan" ]

    let tryFind kind = Map.tryFind kind byKind

    /// The type in wire `TypeRef` notation with variables by name: `Map<K, V>`.
    let rec render (signatureType: SignatureType) =
        match signatureType with
        | SignatureType.Primitive PrimitiveType.String -> "String"
        | SignatureType.Primitive PrimitiveType.Bool -> "Bool"
        | SignatureType.Primitive PrimitiveType.Unit -> "Unit"
        | SignatureType.Primitive PrimitiveType.Int64 -> "Int64"
        | SignatureType.Primitive PrimitiveType.Float64 -> "Float64"
        | SignatureType.Primitive PrimitiveType.Bits32 -> "Bits32"
        | SignatureType.Variable name -> name
        | SignatureType.List item -> $"List<{render item}>"
        | SignatureType.Option item -> $"Option<{render item}>"
        | SignatureType.Result(ok, error) -> $"Result<{render ok}, {render error}>"
        | SignatureType.Map(key, value) -> $"Map<{render key}, {render value}>"
        | SignatureType.Set item -> $"Set<{render item}>"

    /// The one-line recipe reference signature, e.g. `(value: String) -> Int64  // code points`.
    let referenceText (entry: Signature) =
        let operands =
            entry.Operands
            |> List.map (fun operand -> $"{operand.Field}: {render operand.Type}{Option.defaultValue System.String.Empty operand.TypeDetail}")
            |> String.concat ", "
        let note =
            match entry.Note with
            | Some note -> $"  // {note}"
            | None -> System.String.Empty
        $"({operands}) -> {render entry.Result}{note}"

    /// The checked-in `tools/nyrwvy-agent/recipe-signatures.generated.mjs`.
    let referenceModule () =
        let quote (text: string) =
            if text |> Seq.exists System.Char.IsControl then invalidArg (nameof text) $"Signature text contains a control character: {text}"
            "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let lines =
            [ "// Generated from src/Nyrwvy.Semantics/OperationSignatures.fs by scripts/generate-recipe-signatures.fsx."
              "// Do not edit: change the F# table and regenerate. RecipeKindConsistencyTests fails when this file is stale."
              ""
              "/** One-line signatures of the fixed-signature recipe kinds, in wire field names. */"
              "export const fixedSignatures = Object.freeze({"
              yield! table |> List.filter (fun entry -> not (retired.ContainsKey entry.Kind)) |> List.map (fun entry -> $"  {quote entry.Kind}: {quote (referenceText entry)},")
              "})"
              "" ]
        String.concat "\n" lines

    let rec private bind (bindings: Map<string, TypeRef>) (pattern: SignatureType) (actual: TypeRef) =
        let generic genericType (patterns: SignatureType list) =
            match actual with
            | TypeRef.BuiltinGeneric(actualType, arguments) when actualType = genericType && List.length arguments = List.length patterns ->
                List.zip patterns arguments
                |> List.fold (fun state (itemPattern, argument) -> state |> Option.bind (fun current -> bind current itemPattern argument)) (Some bindings)
            | _ -> None
        match pattern with
        | SignatureType.Primitive primitive -> if actual = TypeRef.Primitive primitive then Some bindings else None
        | SignatureType.Variable name ->
            match Map.tryFind name bindings with
            | Some bound -> if bound = actual then Some bindings else None
            | None -> Some(Map.add name actual bindings)
        | SignatureType.List item -> generic BuiltinGenericType.List [ item ]
        | SignatureType.Option item -> generic BuiltinGenericType.Option [ item ]
        | SignatureType.Result(ok, error) -> generic BuiltinGenericType.Result [ ok; error ]
        | SignatureType.Map(key, value) -> generic BuiltinGenericType.Map [ key; value ]
        | SignatureType.Set item -> generic BuiltinGenericType.Set [ item ]

    /// The type with every variable replaced by its binding, or None while one is unbound.
    let rec private instantiate (bindings: Map<string, TypeRef>) (pattern: SignatureType) =
        let generic genericType (patterns: SignatureType list) =
            let arguments = patterns |> List.map (instantiate bindings)
            if arguments |> List.forall Option.isSome then Some(TypeRef.BuiltinGeneric(genericType, List.choose id arguments)) else None
        match pattern with
        | SignatureType.Primitive primitive -> Some(TypeRef.Primitive primitive)
        | SignatureType.Variable name -> Map.tryFind name bindings
        | SignatureType.List item -> generic BuiltinGenericType.List [ item ]
        | SignatureType.Option item -> generic BuiltinGenericType.Option [ item ]
        | SignatureType.Result(ok, error) -> generic BuiltinGenericType.Result [ ok; error ]
        | SignatureType.Map(key, value) -> generic BuiltinGenericType.Map [ key; value ]
        | SignatureType.Set item -> generic BuiltinGenericType.Set [ item ]

    /// Infers `operands` in order against `entry` and stops at the first failure: an operand whose
    /// type the signature already fixes must equal it, another must have the signature's shape and
    /// binds its variables. Returns the result type.
    let check
        (infer: 'Recipe -> Result<TypeRef, 'Error>)
        (isEquatable: TypeRef -> bool)
        (fail: OperandFailure -> 'Error)
        (entry: Signature)
        (operands: 'Recipe list)
        : Result<TypeRef, 'Error> =
        if List.length operands <> List.length entry.Operands then
            invalidOp $"Recipe kind '{entry.Kind}' has {List.length operands} operands, but its signature lists {List.length entry.Operands}."
        let checkConstraint before (bindings: Map<string, TypeRef>) =
            match entry.Equatable with
            | Some constraintEntry when not (Map.containsKey constraintEntry.Variable before) ->
                match Map.tryFind constraintEntry.Variable bindings with
                | Some bound when not (isEquatable bound) -> Error(fail (OperandFailure.NotEquatable(constraintEntry, bound)))
                | Some _
                | None -> Ok bindings
            | Some _
            | None -> Ok bindings
        List.zip entry.Operands operands
        |> List.fold (fun state (operand, child) ->
            state
            |> Result.bind (fun bindings ->
                match infer child with
                | Error error -> Error error
                | Ok actual ->
                    match instantiate bindings operand.Type with
                    | Some expected when expected = actual -> Ok bindings
                    | Some expected -> Error(fail (OperandFailure.TypeMismatch(operand.Side, expected, actual)))
                    | None ->
                        match bind bindings operand.Type actual with
                        | Some extended -> checkConstraint bindings extended
                        | None -> Error(fail (OperandFailure.ShapeMismatch(operand.Side, render operand.Type, actual))))) (Ok Map.empty)
        |> Result.map (fun bindings ->
            match instantiate bindings entry.Result with
            | Some result -> result
            | None -> invalidOp $"The result type of recipe kind '{entry.Kind}' mentions a variable no operand binds.")
