namespace Nyrwvy.Core

/// Semantic facts are intentionally explicit and locally inspectable. A node kind
/// should carry enough information that an agent can understand its role from the
/// node plus directly referenced declarations, without relying on display names,
/// serialized order, or a second representation of the same fact.
[<RequireQualifiedAccess>]
type PrimitiveType =
    | String
    | Bool
    | Unit
    | Int64
    | Float64
    /// Exactly 32 bits with no numeric meaning; see `Bits32FromInt64` and `Bits32ToInt64`.
    | Bits32

/// Authoritative IEEE 754 binary64 literal representation. Construction always
/// canonicalizes every NaN payload/sign variant to one quiet NaN bit pattern while
/// preserving every non-NaN bit exactly, including signed zero and infinities.
[<Struct>]
type Float64Bits = private Float64Bits of uint64

[<RequireQualifiedAccess>]
module Float64Bits =
    let canonicalNaNBits = 0x7ff8000000000000UL
    let private exponentMask = 0x7ff0000000000000UL
    let private fractionMask = 0x000fffffffffffffUL

    let private isRawNaN bits =
        bits &&& exponentMask = exponentMask && bits &&& fractionMask <> 0UL

    let create bits =
        Float64Bits(if isRawNaN bits then canonicalNaNBits else bits)

    let value (Float64Bits bits) = bits

    let ofFloat (number: float) =
        System.BitConverter.DoubleToUInt64Bits number |> create

    let toFloat bits =
        bits |> value |> System.BitConverter.UInt64BitsToDouble

    let isNaN bits =
        bits |> value |> isRawNaN

    let toHex bits =
        (value bits).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)

    let tryParseHex (text: string): Result<Float64Bits, string> =
        if isNull text || text.Length <> 16 then
            Error "Float64 bits must contain exactly 16 hexadecimal digits."
        else
            match
                System.UInt64.TryParse(
                    text,
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, bits -> Ok(create bits)
            | false, _ -> Error "Float64 bits contain invalid hexadecimal digits."

[<RequireQualifiedAccess>]
type BuiltinGenericType =
    /// Opaque captured host exception. No construction, equality or external serialization.
    | Exception
    | Option
    | Result
    | List
    /// `Task<T>`: a handle to a task started in a task scope. Its only operations are TaskWait and TaskCancel.
    | Task
    /// A scope-owned, copyable one-shot result handle; live state is never persisted.
    | OneShot
    /// `TaskScope`: the opaque scope value a TaskScope body receives; takes no type arguments.
    | TaskScope
    /// `Set<T>`: an immutable set of equatable items (compared with ValueEqual), visited in the order
    /// items were first inserted.
    | Set
    /// `Map<K, V>`: an immutable map from equatable keys (compared with ValueEqual) to values, visited
    /// in the order keys were first inserted.
    | Map

[<RequireQualifiedAccess>]
type TaskWaitMode =
    /// Returns once at least one listed task has finished (at once for an empty list).
    | Any
    /// Returns once every listed task has finished.
    | All

[<RequireQualifiedAccess>]
type TypeRef =
    | Primitive of PrimitiveType
    | Named of TypeId
    | Capability of CapabilityId
    /// Pure, capture-free callable; parameter order is semantic, parameter identities are not.
    | Function of parameters: TypeRef list * resultType: TypeRef
    | BuiltinGeneric of BuiltinGenericType * TypeRef list
    /// A type parameter of the function whose parameter types mention it. Its identity is
    /// system-owned; the display name is fixed with it and carried for presentation only.
    | TypeParameter of id: TypeParameterId * displayName: string

/// Completion is a derived control-flow fact, never a language value type.
[<RequireQualifiedAccess>]
type GraphCompletion =
    /// The value type on normal paths; other paths may still abort.
    | Normal of TypeRef
    /// There is no normally completing path through this expression.
    | Aborts

[<RequireQualifiedAccess>]
module GraphCompletion =
    let normalType = function
        | GraphCompletion.Normal value -> Some value
        | GraphCompletion.Aborts -> None

    /// Unknown or inconsistent arms remain unknown; only abrupt arms are ignored.
    let merge (arms: GraphCompletion option list) =
        if List.isEmpty arms || List.exists Option.isNone arms then None
        else
            let normal = arms |> List.choose (Option.bind normalType)
            match normal with
            | [] -> Some GraphCompletion.Aborts
            | first :: rest when List.forall ((=) first) rest -> Some(GraphCompletion.Normal first)
            | _ -> None

type SemanticField =
    { Id: FieldId
      DisplayName: string
      Type: TypeRef }

type SemanticVariant =
    { Id: VariantId
      DisplayName: string
      Fields: SemanticField list }

[<RequireQualifiedAccess>]
type SemanticTypeKind =
    | Product of SemanticField list
    | Union of SemanticVariant list

type SemanticType =
    { Id: TypeId
      DisplayName: string
      Kind: SemanticTypeKind }

type SemanticEffect =
    { Id: EffectId
      DisplayName: string }

type SemanticCapabilityParameter =
    { Id: CapabilityParameterId
      DisplayName: string
      Type: TypeRef }

type SemanticCapabilityOperation =
    { Id: CapabilityOperationId
      DisplayName: string
      Parameters: SemanticCapabilityParameter list
      ResultType: TypeRef
      FailureType: TypeRef option
      EffectId: EffectId }

type SemanticCapability =
    { Id: CapabilityId
      DisplayName: string
      Operations: SemanticCapabilityOperation list }

type SemanticParameter =
    { Id: ParameterId
      DisplayName: string
      Type: TypeRef }

type ArgumentBinding =
    { TargetParameterId: ParameterId
      ValueNodeId: GraphNodeId }

type CapabilityArgumentBinding =
    { TargetCapabilityParameterId: CapabilityParameterId
      ValueNodeId: GraphNodeId }

type FieldValueBinding =
    { TargetFieldId: FieldId
      ValueNodeId: GraphNodeId }

type PatternBinding =
    { Id: PatternBindingId
      DisplayName: string option
      Type: TypeRef }

type PatternFieldBinding =
    { SourceFieldId: FieldId
      Binding: PatternBinding }

/// One arm of a union match. An arm names one or more variants and has one body. An arm with one
/// variant binds each of its fields exactly once; an arm with several variants binds no fields.
/// Every declared variant is named by exactly one arm, so adding a variant leaves every match over
/// the union incomplete until an arm names it.
type UnionMatchArm =
    { VariantIds: VariantId list
      FieldBindings: PatternFieldBinding list
      Body: ValueGraph }

/// Bind, Option.Some and Result.Ok intentionally share one semantic shape instead of
/// duplicating near-identical record schemas. Their parent node kind supplies the
/// role; the arm itself represents only "bind one produced value, then evaluate".
and ValueMatchArm =
    { ValueBinding: PatternBinding
      Body: ValueGraph }

/// Result.Err is structurally distinct because the binding has an error role.
and ErrorMatchArm =
    { ErrorBinding: PatternBinding
      Body: ValueGraph }

and [<RequireQualifiedAccess>] SemanticNodeKind =
    | ParameterReference of ParameterId
    | PatternReference of PatternBindingId
    | StringLiteral of string
    | BoolLiteral of bool
    | UnitLiteral
    | Int64Literal of int64
    | Float64Literal of Float64Bits
    | StringIsEmpty of valueNodeId: GraphNodeId
    /// Canonical invariant decimal text of an Int64, with a leading '-' only for negative values.
    | Int64ToString of valueNodeId: GraphNodeId
    /// Lowercase hexadecimal text of an Int64 with the fewest digits, as Python format(value, "x"): a leading
    /// '-' only for negative values ('-ff'), no '0x' prefix, no padding. Never fails.
    | Int64ToHexString of valueNodeId: GraphNodeId
    /// Some(value) only for an optional '-' followed by one or more ASCII digits within the Int64
    /// range; any other text, including '+', whitespace or an empty String, is None.
    | StringParseInt64 of valueNodeId: GraphNodeId
    /// Some(value) only for an optional '-' followed by one or more ASCII hexadecimal digits (either case) within
    /// the Int64 range; '0x', '+', whitespace, '_' or an empty String give None.
    | StringParseInt64Hex of valueNodeId: GraphNodeId
    /// Shortest round-trip decimal text of a Float64, exactly like Python repr(float): the fewest
    /// significant digits that read back as the same binary64, in fixed notation with at least one
    /// fractional digit ('1.0', '0.0001', '1234567890123456.0') when the decimal exponent is in
    /// [-4, 16), otherwise in exponent notation with a signed exponent of at least two digits ('1e+16',
    /// '1.5e-05'). NaN is 'nan', the infinities 'inf' and '-inf', and -0.0 is '-0.0'. Never fails.
    | Float64ToString of valueNodeId: GraphNodeId
    /// Some(value) only when the whole String is a JSON number, '-'? ASCII digits with an optional
    /// fraction '.' digits and an optional exponent [eE][+-]? digits (leading zeros allowed), like
    /// Python float(text) on that grammar: the correctly rounded (ties to even) nearest Float64 for
    /// any number of digits, an overflow as a signed infinity and an underflow as a signed zero. Any
    /// other text, including '+', whitespace, '.5', '5.', 'inf', 'nan' or an empty String, is None.
    /// Never fails.
    | StringParseFloat64 of valueNodeId: GraphNodeId
    /// Each code point of the String as a one-code-point String, in order, as a List<String>. A
    /// surrogate pair is one element; an unpaired surrogate code unit is its own one-unit element,
    /// as StringLength counts it, so the list length always equals StringLength and it never fails.
    | StringCodePoints of valueNodeId: GraphNodeId
    /// Each code point replaced by its Unicode simple (one-to-one) lowercase mapping, culture
    /// invariant; code points without one, including unpaired surrogates, are kept. Never fails.
    | StringToLower of valueNodeId: GraphNodeId
    /// Each code point replaced by its Unicode simple (one-to-one) uppercase mapping, culture
    /// invariant, the mirror of StringToLower (which undoes it); code points without one, including
    /// unpaired surrogates, are kept, so the code-point length never changes. Never fails.
    | StringToUpper of valueNodeId: GraphNodeId
    /// The text of a String holding one byte per code point (U+0000..U+00FF), decoded as UTF-8 with
    /// one U+FFFD per maximal ill-formed subpart, as Python bytes.decode("utf-8", "replace"). A code
    /// point above U+00FF reads as the invalid byte 0xFF. Never fails.
    | StringDecodeUtf8Lossy of valueNodeId: GraphNodeId
    /// The nearest integer as Option<Int64>, rounding half away from zero (Python's round rounds ties to
    /// even). None for NaN, an infinity, or a result outside Int64. Never fails.
    | Float64RoundToInt64 of valueNodeId: GraphNodeId
    /// Python math.floor(value) as Option<Int64>, with None in the same cases as Float64RoundToInt64.
    | Float64FloorToInt64 of valueNodeId: GraphNodeId
    /// IEEE 754 square root, correctly rounded. A negative value (but not -0.0) gives NaN, where Python
    /// math.sqrt raises; -0.0 gives -0.0. Never fails.
    | Float64Sqrt of valueNodeId: GraphNodeId
    /// The value with its sign bit cleared, as Python abs(value) or math.fabs: -0.0 gives 0.0, NaN stays NaN.
    /// Never fails.
    | Float64Abs of valueNodeId: GraphNodeId
    /// The value with its sign bit flipped, as Python -value: 0.0 gives -0.0. Never fails.
    | Float64Negate of valueNodeId: GraphNodeId
    /// Python math.floor(value) as a Float64 (the largest integral value not above it): -0.5 gives -0.0;
    /// NaN, the infinities and the zeros are returned unchanged. Never fails.
    | Float64Floor of valueNodeId: GraphNodeId
    /// Python math.ceil(value) as a Float64 (the smallest integral value not below it): -0.5 gives -0.0;
    /// NaN, the infinities and the zeros are returned unchanged. Never fails.
    | Float64Ceiling of valueNodeId: GraphNodeId
    /// Python math.trunc(value) as a Float64 (rounded toward zero): -1.5 gives -1.0, -0.5 gives -0.0; NaN,
    /// the infinities and the zeros are returned unchanged. Never fails.
    | Float64Truncate of valueNodeId: GraphNodeId
    /// Number of bytes in the UTF-8 encoding of the String, as an Int64; an unpaired surrogate
    /// counts as 3 bytes (its U+FFFD replacement, as WTF-8 also does). Never fails.
    | StringUtf8ByteCount of valueNodeId: GraphNodeId
    /// Some one-code-point String for an Int64 code in [0, 0x10FFFF], None otherwise, like Python
    /// chr. A code in the surrogate range 0xD800..0xDFFF gives one unpaired surrogate code unit, so
    /// concatenating a high and a low surrogate forms the pair. Never fails.
    | StringFromCodePoint of valueNodeId: GraphNodeId
    /// Some code point of a String that is exactly one code point as StringLength counts it (a
    /// surrogate pair gives its scalar value, an unpaired surrogate its code unit), None for the
    /// empty String or a longer one, like Python ord. Never fails.
    | StringToCodePoint of valueNodeId: GraphNodeId
    /// Number of code points in the String, as an Int64. A surrogate pair counts once; an unpaired
    /// surrogate code unit counts as one code point.
    | StringLength of valueNodeId: GraphNodeId
    /// The code points at positions [start, end) of the String. Both bounds are clamped to
    /// [0, StringLength], and end <= start yields the empty String, so slicing never fails.
    | StringSlice of valueNodeId: GraphNodeId * startNodeId: GraphNodeId * endNodeId: GraphNodeId
    | StringContains of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    /// Code-point position of the first ordinal occurrence of the substring starting at or after
    /// `from`, as Option<Int64> (Python `str.find(sub, start)`). `from` is clamped to
    /// [0, StringLength] like StringSlice; an empty substring is found at the clamped `from`. A match
    /// never starts or ends inside a surrogate pair. Never fails.
    | StringIndexOf of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId * fromNodeId: GraphNodeId
    /// The code-point position of the first code point at or after `from` that is not a member of
    /// `set` (any code point of the set String), or StringLength when there is none, as Int64 (Python
    /// `start + len(v[start:]) - len(v[start:].lstrip(set))`). `from` is clamped to [0, StringLength];
    /// an empty set yields the clamped `from`. Never fails.
    | StringSpan of valueNodeId: GraphNodeId * setNodeId: GraphNodeId * fromNodeId: GraphNodeId
    /// The least code-point position p <= `end` such that every code point in [p, end) is a member of
    /// `set`, as Int64 (Python `len(v[:end].rstrip(set))`). `end` is clamped to [0, StringLength]; an
    /// empty set yields the clamped `end`. Never fails.
    | StringSpanBack of valueNodeId: GraphNodeId * setNodeId: GraphNodeId * endNodeId: GraphNodeId
    /// Code-point position of the last ordinal occurrence of the substring, as Option<Int64>
    /// (Python `str.rfind(sub)`); an empty substring is found at StringLength. Never fails.
    | StringLastIndexOf of valueNodeId: GraphNodeId * substringNodeId: GraphNodeId
    /// True when the String begins with the prefix, compared ordinally; the empty prefix always matches.
    | StringStartsWith of valueNodeId: GraphNodeId * prefixNodeId: GraphNodeId
    /// True when the String ends with the suffix, compared ordinally; the empty suffix always matches.
    | StringEndsWith of valueNodeId: GraphNodeId * suffixNodeId: GraphNodeId
    /// The pieces between non-overlapping ordinal occurrences of the separator, left to right, as
    /// List<String>, keeping empty pieces (Python `str.split(sep)`). An empty separator never fails:
    /// it yields the one-item list [value]. Never splits a surrogate pair.
    | StringSplit of valueNodeId: GraphNodeId * separatorNodeId: GraphNodeId
    /// The lines of the String as List<String>: each code point that is a member of `breaks` (any code
    /// point of the breaks String, as StringSpan counts them) ends the current line and is dropped; when
    /// both "\r" and "\n" are members, "\r\n" is one break. Like Python `str.splitlines()`, an empty
    /// value gives [] and a final break adds no trailing empty line; an empty `breaks` gives [value]
    /// (or [] for ""). Linear time; never fails.
    | StringSplitLines of valueNodeId: GraphNodeId * breaksNodeId: GraphNodeId
    /// The String with every non-overlapping ordinal occurrence of the pattern, scanning left to
    /// right, replaced by the replacement (Python `str.replace(old, new)`). A match starts and ends on
    /// code-point boundaries, so it never splits a surrogate pair. An empty pattern gives the value
    /// unchanged. Never fails.
    | StringReplace of valueNodeId: GraphNodeId * patternNodeId: GraphNodeId * replacementNodeId: GraphNodeId
    /// The String without its leading and trailing code points that have the Unicode White_Space
    /// property (.NET `char.IsWhiteSpace`). Never fails.
    | StringTrim of valueNodeId: GraphNodeId
    /// The String without its leading White_Space code points, as StringTrim. Never fails.
    | StringTrimStart of valueNodeId: GraphNodeId
    /// The String without its trailing White_Space code points, as StringTrim. Never fails.
    | StringTrimEnd of valueNodeId: GraphNodeId
    /// The List<String> items concatenated in order with the separator between adjacent items
    /// (Python `sep.join(list)`): an empty list gives "", one item gives that item. Never fails.
    | StringJoin of listNodeId: GraphNodeId * separatorNodeId: GraphNodeId
    | StringEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Ordinal String ordering: true when the left String sorts strictly before the right one,
    /// comparing Unicode code points lexicographically (a proper prefix sorts first). Scalar values
    /// above U+FFFF sort after every BMP code point, unlike UTF-16 code-unit order. Never fails.
    | StringLessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Structural equality of two values of one equatable type (see `EquatableTypes`): the same
    /// variant with pairwise equal fields, lists by length and item by item in order. Never fails.
    | ValueEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | StringConcat of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64WrappingMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64FloorDivide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64FloorModulo of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// The smaller of two Int64 values, as Python min(left, right). Never fails.
    | Int64Min of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// The larger of two Int64 values, as Python max(left, right). Never fails.
    | Int64Max of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Two's-complement bitwise AND, as Python left & right. Never fails.
    | Int64BitAnd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Two's-complement bitwise OR, as Python left | right. Never fails.
    | Int64BitOr of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Two's-complement bitwise XOR, as Python left ^ right. Never fails.
    | Int64BitXor of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// The value shifted left by count bits, keeping the low 64 bits. A negative count shifts by 0 and a
    /// count of 64 or more gives 0. Never fails.
    | Int64ShiftLeft of valueNodeId: GraphNodeId * countNodeId: GraphNodeId
    /// The value shifted right by count bits with the sign propagated, as Python value >> count. A negative
    /// count shifts by 0 and a count of 64 or more gives 0 or -1 by the value's sign. Never fails.
    | Int64ShiftRight of valueNodeId: GraphNodeId * countNodeId: GraphNodeId
    /// Exact left + right as Option<Int64>: None when the exact result is outside Int64. Never fails.
    | Int64CheckedAdd of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Exact left - right as Option<Int64>: None when the exact result is outside Int64. Never fails.
    | Int64CheckedSubtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Exact left * right as Option<Int64>: None when the exact result is outside Int64. Never fails.
    | Int64CheckedMultiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// The List<Int64> start, start + 1, ..., end - 1, as Python list(range(start, end)); empty when
    /// end <= start. A list too large to allocate is a host failure, as for ListConcat.
    | ListRange of startNodeId: GraphNodeId * endNodeId: GraphNodeId
    /// The String repeated count times, as Python text * count; "" when count <= 0. A String too large to
    /// allocate is a host failure, as for StringConcat.
    | StringRepeat of textNodeId: GraphNodeId * countNodeId: GraphNodeId
    /// The Float64 value rounded to `digits` decimal places (tens, hundreds, ... when negative), half away
    /// from zero, on the shortest decimal text that reads back to the value (2.675 to 2 places is 2.68).
    /// NaN, the infinities and zeros are returned unchanged. Never fails.
    | Float64Round of valueNodeId: GraphNodeId * digitsNodeId: GraphNodeId
    | Int64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64LessThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Int64GreaterThanOrEqual of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Add of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// The smaller operand as IEEE 754-2019 minimum (.NET Math.Min): NaN when either operand is NaN, and
    /// -0.0 is less than 0.0. Never fails.
    | Float64Min of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// The larger operand as IEEE 754-2019 maximum (.NET Math.Max): NaN when either operand is NaN, and
    /// 0.0 is greater than -0.0. Never fails.
    | Float64Max of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Subtract of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Multiply of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Divide of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64Equal of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64LessThan of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Float64IsNaN of valueNodeId: GraphNodeId
    | Float64IsFinite of valueNodeId: GraphNodeId
    | Float64IsInfinite of valueNodeId: GraphNodeId
    /// The Float64 nearest to the Int64: exact for |value| <= 2^53, otherwise IEEE 754 round to nearest,
    /// ties to even (C# `(double)long`, Python `float(int)`). Every Int64 is finite as a Float64. Never fails.
    | Int64ToFloat64 of valueNodeId: GraphNodeId
    /// The absolute value of an Int64 with two's-complement wrapping, so Int64.MinValue gives itself, as
    /// Int64FloorDivide wraps Int64.MinValue / -1. Never fails.
    | Int64WrappingAbs of valueNodeId: GraphNodeId
    /// The two's-complement bitwise complement, as Python ~value (-value - 1). Never fails.
    | Int64BitNot of valueNodeId: GraphNodeId
    /// The low 32 bits of the Int64's two's complement form (value mod 2^32). Never fails.
    | Bits32FromInt64 of valueNodeId: GraphNodeId
    /// The Bits32 read as an unsigned integer, 0..4294967295. Never fails.
    | Bits32ToInt64 of valueNodeId: GraphNodeId
    | Bits32Xor of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32And of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32Or of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    | Bits32Not of valueNodeId: GraphNodeId
    /// Addition modulo 2^32. Never fails.
    | Bits32Add of leftNodeId: GraphNodeId * rightNodeId: GraphNodeId
    /// Rotation right by the Int64 amount taken floor-modulo 32 (a negative amount rotates left). Never fails.
    | Bits32RotateRight of valueNodeId: GraphNodeId * amountNodeId: GraphNodeId
    /// Logical shift by the Int64 amount: amount <= 0 gives the value unchanged, amount >= 32 gives zero. Never fails.
    | Bits32ShiftRight of valueNodeId: GraphNodeId * amountNodeId: GraphNodeId
    /// Logical shift by the Int64 amount: amount <= 0 gives the value unchanged, amount >= 32 gives zero. Never fails.
    | Bits32ShiftLeft of valueNodeId: GraphNodeId * amountNodeId: GraphNodeId
    /// The empty List<T>.
    | ListEmpty of itemType: TypeRef
    /// The empty Set<T>. Sets hold each item once, compare items with ValueEqual (so T must be
    /// equatable) and visit them in insertion order; they are immutable values.
    | SetEmpty of itemType: TypeRef
    /// The empty Map<K, V>. Maps are immutable and visit entries in the order keys were first inserted.
    | MapEmpty of keyType: TypeRef * valueType: TypeRef
    /// A List<T> of the item nodes' values, in order.
    | ListConstruct of itemType: TypeRef * itemNodeIds: GraphNodeId list
    /// The number of items as Int64, as Python len(list). Never fails.
    | ListLength of listNodeId: GraphNodeId
    /// The values as a List<V>, in iteration order. Never fails.
    | MapValues of mapNodeId: GraphNodeId
    /// The keys as a List<K>, in iteration order. Never fails.
    | MapKeys of mapNodeId: GraphNodeId
    /// The items as a List<T>, in iteration order. Never fails.
    | SetToList of setNodeId: GraphNodeId
    /// The number of entries as Int64. Never fails.
    | MapSize of mapNodeId: GraphNodeId
    /// The items of a List<T> without repeats, in first-occurrence order, as a Set<T>. Never fails.
    | SetFromList of sourceListNodeId: GraphNodeId
    /// The number of items as Int64. Never fails.
    | SetSize of setNodeId: GraphNodeId
    /// The items of the List<T> in reverse order, as Python list(reversed(list)). Never fails.
    | ListReverse of listNodeId: GraphNodeId
    /// `Some` last item of the List<T>, or `None` for the empty list; never fails.
    | ListLast of listNodeId: GraphNodeId
    /// The List<T> without its last item; the empty list stays empty. Never fails.
    | ListDropLast of listNodeId: GraphNodeId
    /// The List<T> with the item added at the end, as Python list + [item]. Never fails.
    | ListAppendItem of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// `Some item` at a zero-based Int64 index in range, else `None`; never fails.
    | ListItemAt of listNodeId: GraphNodeId * indexNodeId: GraphNodeId
    /// The Map<K, V> without the key; unchanged when the key is absent. Never fails.
    | MapRemove of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId
    /// The Set<T> with the item, added last when it was absent; unchanged when present. Never fails.
    | SetInsert of setNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// The Set<T> without the item; unchanged when it is absent. Never fails.
    | SetRemove of setNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// The left items in their order, then the right items not in left in theirs. Never fails.
    | SetUnion of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    /// The left items that the right set contains, in left order. Never fails.
    | SetIntersection of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    /// The left items that the right set does not contain, in left order. Never fails.
    | SetDifference of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    /// True when every left item is in the right set (ValueEqual), as Python left <= right; an empty
    /// left set is a subset of any set. Never fails.
    | SetIsSubset of leftSetNodeId: GraphNodeId * rightSetNodeId: GraphNodeId
    /// The Map<K, V> with every entry of both maps; on a shared key the right value wins (Python
    /// left | right). Left keys keep their positions, then right's new keys follow in right's order,
    /// as inserting right's entries into left with MapInsert. Never fails.
    | MapUnion of leftMapNodeId: GraphNodeId * rightMapNodeId: GraphNodeId
    /// True when the map has the key, compared with ValueEqual, as Python key in map. Never fails.
    | MapContainsKey of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId
    /// True when the set has the item, compared with ValueEqual, as Python item in set. Never fails.
    | SetContains of setNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// `Some` zero-based position of the first item equal to the item (ValueEqual), else `None`; never fails.
    | ListIndexOf of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// `Some` zero-based position of the last item equal to the item (ValueEqual), scanning from the
    /// end, else `None`; never fails.
    | ListLastIndexOf of listNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// `Some` value for the key, else `None`, as Python map.get(key); never fails.
    | MapGet of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId
    /// The value for key, or the default (of the map's value type) when the key is absent. All
    /// three operands are evaluated. Never fails.
    | MapGetOrDefault of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId * defaultNodeId: GraphNodeId
    /// The items at zero-based Int64 positions [start, end), both bounds clamped to [0, length]
    /// like StringSlice (negative bounds are not counted from the end; end <= start gives the
    /// empty list). Same List<T> type as the input; never fails.
    | ListSlice of listNodeId: GraphNodeId * startNodeId: GraphNodeId * endNodeId: GraphNodeId
    /// The List<T> with the item at zero-based Int64 `index` replaced; an index outside
    /// [0, length) returns the list unchanged. O(log n) path copy. Never fails.
    | ListSetAt of listNodeId: GraphNodeId * indexNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// The List<T> with the item inserted before position `index`, clamped to [0, length]
    /// (negative inserts first, length or more appends; not counted from the end). Never fails.
    | ListInsertAt of listNodeId: GraphNodeId * indexNodeId: GraphNodeId * itemNodeId: GraphNodeId
    /// The List<T> without the item at zero-based `index`; an index outside [0, length) returns the
    /// list unchanged. Never fails.
    | ListRemoveAt of listNodeId: GraphNodeId * indexNodeId: GraphNodeId
    /// The Map<K, V> with the key set to the value: a present key keeps its position, an absent key
    /// goes last, as Python map[key] = value on a copy. Never fails.
    | MapInsert of mapNodeId: GraphNodeId * keyNodeId: GraphNodeId * valueNodeId: GraphNodeId
    | ListConcat of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId
    /// List callbacks reference an existing pure function. `context` binds some of its parameters,
    /// by identity, to values evaluated once before iteration; the remaining parameters, in
    /// declaration order, receive the iteration values (accumulator and item for ListFold, the
    /// item otherwise). An empty context is the plain callback.
    | ListFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    /// ListFold over the entries in iteration order; the folder's unbound parameters receive
    /// (accumulator, key, value).
    | MapFold of sourceMapNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    /// Acquires a resource with `acquire : Result<R, E>`; on Ok runs the body function with R (other
    /// parameters bound by `context`) and then always releases R through a capability operation.
    /// Runs the worker for every source item concurrently and waits for all of them. The first Err
    /// (or host exception) requests cancellation of the others; the result is the Err of the lowest
    /// failing index, or Ok of every worker's value in source order.
    | ParallelAll of sourceListNodeId: GraphNodeId * workerFunctionId: FunctionId * context: ArgumentBinding list
    /// Whether cancellation was requested for the ParallelAll worker, task or TaskScope body evaluating it; false elsewhere.
    | CancellationRequested
    | UseResource of acquireNodeId: GraphNodeId * releaseCapabilityNodeId: GraphNodeId * releaseOperationId: CapabilityOperationId * bodyFunctionId: FunctionId * context: ArgumentBinding list
    /// Explicit host fault recovery. Contexts evaluate before the protected Unit body.
    | ExceptionCatch of bodyFunctionId: FunctionId * bodyContext: ArgumentBinding list * handlerFunctionId: FunctionId * handlerContext: ArgumentBinding list
    | ExceptionIsCancellation of exceptionValue: GraphNodeId
    | ExceptionMessage of exceptionValue: GraphNodeId
    | ExceptionRethrow of exceptionValue: GraphNodeId * resultType: TypeRef
    /// Runs a Unit -> Result<T, E> body with ambient cancellation masked. Context evaluates first.
    /// Restores the caller token on every exit; cancellation stays cooperative after the body.
    | CancellationShield of bodyFunctionId: FunctionId * context: ArgumentBinding list
    /// Runs the body function with a new `TaskScope` value (other parameters bound by `context`).
    /// When the body exits by Ok, Err or host exception, every unfinished task of the scope is
    /// cancelled and awaited before the node completes. The result is the body's `Result<T, E>`.
    | TaskScope of bodyFunctionId: FunctionId * context: ArgumentBinding list
    /// Starts the worker with the item as a new task of the scope and returns its `Task<Result<U, E>>`
    /// handle at once. A task's Err does not cancel its siblings.
    | TaskStart of scopeNodeId: GraphNodeId * workerFunctionId: FunctionId * context: ArgumentBinding list * itemNodeId: GraphNodeId
    /// Waits until the mode is satisfied or the optional `Option<Int64>` millisecond limit elapses, and
    /// returns `Some result` for every finished task and `None` for every running one, in input order.
    /// Tasks keep running after it returns. An absent limit operand means no limit, as `None` does.
    | TaskWait of tasksNodeId: GraphNodeId * mode: TaskWaitMode * limitNodeId: GraphNodeId option
    /// Requests cooperative cancellation of one task without waiting for it. Idempotent.
    | TaskCancel of taskNodeId: GraphNodeId
    | OneShotCreate of scope: GraphNodeId * valueType: TypeRef
    | OneShotComplete of cell: GraphNodeId * value: GraphNodeId
    | OneShotWait of cell: GraphNodeId * limit: GraphNodeId option
    | ListEffectFold of sourceListNodeId: GraphNodeId * seedNodeId: GraphNodeId * folderFunctionId: FunctionId * context: ArgumentBinding list
    | ListMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    /// The List<R> whose item i is combiner(left[i], right[i]) for every index below the shorter
    /// length; the extra items of the longer list are ignored, as Python zip. Never fails.
    | ListZipWith of leftListNodeId: GraphNodeId * rightListNodeId: GraphNodeId * combinerFunctionId: FunctionId * context: ArgumentBinding list
    /// The concatenation, in source order, of the List<R> the mapper returns for each item, built in
    /// time linear in the total output length. Never fails.
    | ListFlatMap of sourceListNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    /// The items of a List<T> indexed by a pure `T -> K` callback, as Map<K, T>: a repeated key
    /// keeps its first position and takes the last item, as repeated MapInsert does. K must be
    /// equatable. Never fails.
    | MapFromList of sourceListNodeId: GraphNodeId * keyOfFunctionId: FunctionId * context: ArgumentBinding list
    | ListFilter of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    /// The entries for which a pure `(key, value) -> Bool` callback returns true, keeping their
    /// positions. Same Map<K, V> type as the input.
    | MapFilter of sourceMapNodeId: GraphNodeId * keepFunctionId: FunctionId * context: ArgumentBinding list
    /// The same keys in the same insertion order, each value replaced by a pure `(key, value) -> U`
    /// callback's result. Map<K, V> to Map<K, U>; never fails.
    | MapMapValues of sourceMapNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    /// The source items in a stable order under a pure `(T, T) -> Bool` "less than" callback whose
    /// two unbound parameters receive (left, right). The result is fixed by a top-down merge sort:
    /// split at length / 2, sort both halves, and merge taking the right head only when
    /// lessThan(rightHead, leftHead) holds. A consistent strict order gives the stable sort; any
    /// other callback still yields a deterministic permutation of the source. Never fails.
    | ListSortBy of sourceListNodeId: GraphNodeId * lessThanFunctionId: FunctionId * context: ArgumentBinding list
    /// The smallest item under the same kind of pure "less than" callback as ListSortBy, as
    /// Option<T>: None for the empty list; otherwise a left-to-right scan that starts from the first
    /// item and replaces the current best with an item only when lessThan(item, best) holds, so
    /// the first of equal items wins and any callback gives a deterministic result. Never fails.
    | ListMinBy of sourceListNodeId: GraphNodeId * lessThanFunctionId: FunctionId * context: ArgumentBinding list
    | ListAny of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    /// `Some` zero-based position of the first item for which a pure `T -> Bool` callback is true,
    /// scanning left to right and stopping there, else `None`; never fails.
    | ListFindIndex of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    | ListAll of sourceListNodeId: GraphNodeId * predicateFunctionId: FunctionId * context: ArgumentBinding list
    | DirectCall of FunctionId * ArgumentBinding list
    /// Construct a stateless capability using named pure functions, one per operation.
    | CapabilityConstruct of CapabilityId * implementations: (CapabilityOperationId * FunctionId) list
    | FunctionReference of FunctionId
    | FunctionInvoke of functionValueNodeId: GraphNodeId * arguments: GraphNodeId list
    | CapabilityInvoke of capabilityValueNodeId: GraphNodeId * operationId: CapabilityOperationId * arguments: CapabilityArgumentBinding list
    | ProductConstruct of TypeId * FieldValueBinding list
    | FieldAccess of GraphNodeId * FieldId
    /// A copy of the source product with the bound fields replaced and every other field kept.
    /// The source produces exactly the named product type; the bindings name distinct fields of it,
    /// at least one, each with an exact-compatible value. Never fails.
    | ProductUpdate of TypeId * sourceNodeId: GraphNodeId * FieldValueBinding list
    | UnionConstruct of VariantId * FieldValueBinding list
    | OptionNone of itemType: TypeRef
    | OptionSome of valueNodeId: GraphNodeId
    | ResultOk of errorType: TypeRef * valueNodeId: GraphNodeId
    | Abort of errorNodeId: GraphNodeId
    | ResultErr of okType: TypeRef * errorNodeId: GraphNodeId
    /// The Some value, or the default (of the option's item type) for None, as Rust's
    /// Option::unwrap_or. Both operands are evaluated. Never fails.
    | OptionGetOrDefault of optionNodeId: GraphNodeId * defaultNodeId: GraphNodeId
    /// Option<T> to Option<R>: None stays None and Some x becomes Some of a pure `T -> R` callback's
    /// result, as Rust's Option::map. The callback runs only for Some. Never fails.
    | OptionMap of sourceOptionNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    /// Option<T> to Option<R>: None stays None and Some x becomes a pure `T -> Option<R>` callback's
    /// result, as Rust's Option::and_then. The callback runs only for Some. Never fails.
    | OptionAndThen of sourceOptionNodeId: GraphNodeId * binderFunctionId: FunctionId * context: ArgumentBinding list
    /// Result<T, E> to Result<R, E>: Ok x becomes Ok of a pure `T -> R` callback's result and Err
    /// stays unchanged, as Rust's Result::map. The callback runs only for Ok. Never fails.
    | ResultMap of sourceResultNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    /// Result<T, E> to Result<T, F>: Err e becomes Err of a pure `E -> F` callback's result and Ok
    /// stays unchanged, as Rust's Result::map_err. The callback runs only for Err. Never fails.
    | ResultMapErr of sourceResultNodeId: GraphNodeId * mapperFunctionId: FunctionId * context: ArgumentBinding list
    /// Result<T, E> to Result<R, E>: Ok x becomes a pure `T -> Result<R, E>` callback's result and
    /// Err stays unchanged, as Rust's Result::and_then. The callback runs only for Ok. Never fails.
    | ResultAndThen of sourceResultNodeId: GraphNodeId * binderFunctionId: FunctionId * context: ArgumentBinding list
    /// Logical negation of a Bool operand; the dedicated form of Conditional(operand, false, true).
    | BoolNot of operandNodeId: GraphNodeId
    /// True when both operands are true. The nested right graph follows Conditional branch rules and
    /// is evaluated only when left is true; the dedicated form of Conditional(left, right, false).
    | BoolAnd of leftNodeId: GraphNodeId * right: ValueGraph
    /// True when either operand is true. The nested right graph follows Conditional branch rules and
    /// is evaluated only when left is false; the dedicated form of Conditional(left, true, right).
    | BoolOr of leftNodeId: GraphNodeId * right: ValueGraph
    | Conditional of conditionNodeId: GraphNodeId * whenTrue: ValueGraph * whenFalse: ValueGraph
    | MatchUnion of inputNodeId: GraphNodeId * arms: UnionMatchArm list
    | Bind of inputNodeId: GraphNodeId * body: ValueMatchArm
    | MatchOption of inputNodeId: GraphNodeId * someArm: ValueMatchArm * noneBody: ValueGraph
    | MatchResult of inputNodeId: GraphNodeId * okArm: ValueMatchArm * errArm: ErrorMatchArm

and SemanticNode =
    { Id: GraphNodeId
      Kind: SemanticNodeKind }

and ValueGraph =
    { Nodes: SemanticNode list
      ResultNodeId: GraphNodeId }

type SemanticFunction =
    { Id: FunctionId
      DisplayName: string
      Parameters: SemanticParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list
      Body: ValueGraph }

type ObligationResolution =
    { TransactionId: TransactionId
      ValueNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
type ObligationStatus =
    | Open
    | Resolved of ObligationResolution

type MissingArgumentObligation =
    { Id: ObligationId
      OriginTransactionId: TransactionId
      CallNodeId: GraphNodeId
      ParameterId: ParameterId
      Status: ObligationStatus }

type Snapshot =
    { Id: SnapshotId
      ParentId: SnapshotId option
      Types: SemanticType list
      Effects: SemanticEffect list
      Capabilities: SemanticCapability list
      Functions: SemanticFunction list
      Obligations: MissingArgumentObligation list }

/// How a node names a function by identity.
[<RequireQualifiedAccess>]
type FunctionReferenceRole =
    /// DirectCall: called with the node's argument bindings.
    | DirectCall
    /// ListFold, ListMap, ListFilter, ListSortBy, ListMinBy, ListFindIndex, ListAny, ListAll, MapFold,
    /// MapFromList, MapFilter or MapMapValues: a callback validation requires to be pure.
    /// ListZipWith and ListFlatMap callbacks are pure in the same way.
    /// MapFromList, MapFilter, MapMapValues, OptionMap, OptionAndThen, ResultMap, ResultMapErr or
    /// ResultAndThen: a callback validation requires to be pure.
    | PureListCallback
    /// ListEffectFold: a folder that may require effects.
    | EffectListCallback
    /// ParallelAll or TaskStart: a worker run as its own concurrent task.
    | Worker
    /// UseResource or TaskScope: a body run once by the node.
    | Body
    /// FunctionReference: the function as a value; it is not called here.
    | FunctionValue
    /// CapabilityConstruct: the implementation of one operation; it is called only through the capability.
    | CapabilityImplementation of CapabilityOperationId

/// One function a node names, its role, and the parameters the node binds by identity (DirectCall
/// arguments or a callback context; empty for a function value or a capability implementation).
type NodeFunctionReference =
    { FunctionId: FunctionId
      Role: FunctionReferenceRole
      Arguments: ArgumentBinding list }

[<RequireQualifiedAccess>]
module FunctionReferenceRole =
    /// Whether the node itself runs the function (so the node needs whatever the function requires),
    /// rather than only handing it on as a value or as a capability implementation.
    let isInvoked (role: FunctionReferenceRole) =
        match role with
        | FunctionReferenceRole.DirectCall
        | FunctionReferenceRole.PureListCallback
        | FunctionReferenceRole.EffectListCallback
        | FunctionReferenceRole.Worker
        | FunctionReferenceRole.Body -> true
        | FunctionReferenceRole.FunctionValue
        | FunctionReferenceRole.CapabilityImplementation _ -> false

[<RequireQualifiedAccess>]
module UnionMatchArm =
    /// Variant IDs in canonical (ordinal StableId) order; persisted forms and lowering use it.
    let canonicalVariantIds (arm: UnionMatchArm) = arm.VariantIds |> List.sortBy StableId.value

    /// The text of the arm's first variant in canonical order, which orders arms; "" for an arm with
    /// no variant, which validation reports, so serialization never fails before validation runs.
    let key (arm: UnionMatchArm) =
        canonicalVariantIds arm |> List.tryHead |> Option.map StableId.value |> Option.defaultValue ""


/// The role one nested graph plays in the node that owns it.
[<RequireQualifiedAccess>]
type NestedGraphRole =
    | WhenTrue
    | WhenFalse
    | BoolAndRight
    | BoolOrRight
    | UnionArm of VariantId list
    | BindBody
    | OptionSome
    | OptionNone
    | ResultOk
    | ResultErr

/// One pattern binding a nested graph's scope introduces, in the role its arm gives it.
[<RequireQualifiedAccess>]
type NestedBinding =
    | Field of FieldId * PatternBinding
    | Value of PatternBinding
    | Error of PatternBinding

/// A nested graph owned by a node: its role and the bindings visible only inside it.
type NestedScope =
    { Role: NestedGraphRole
      Bindings: NestedBinding list
      Graph: ValueGraph }

[<RequireQualifiedAccess>]
module NestedBinding =
    let pattern (binding: NestedBinding): PatternBinding =
        match binding with
        | NestedBinding.Field(_, pattern)
        | NestedBinding.Value pattern
        | NestedBinding.Error pattern -> pattern

/// The one definition of how a node refers to other nodes, which graphs it owns, which types it
/// states and which functions and capability operations it names. Every structural traversal in
/// the Semantics layer (dependency order, cycle detection, pruning, cloning, migration, review and
/// viewer projection), fingerprint normalization, call-site and effect queries derive from these
/// functions, so a new node kind is described here once. Typing, validation, lowering and
/// serialization keep their own kind-specific rules. `SemanticNodeShapeTests` checks each case against the union's shape.
[<RequireQualifiedAccess>]
module SemanticNodeShape =
    let private contextValues (context: ArgumentBinding list) =
        context |> List.map (fun binding -> binding.ValueNodeId)

    let private mapContextValues mapNodeId (context: ArgumentBinding list) =
        context |> List.map (fun binding -> { binding with ValueNodeId = mapNodeId binding.ValueNodeId })

    let private mapFieldValues mapNodeId (fields: FieldValueBinding list) =
        fields |> List.map (fun field -> { field with ValueNodeId = mapNodeId field.ValueNodeId })

    /// Direct value-node references required to interpret one node locally, in operand order.
    /// Nodes inside nested graphs are intentionally not included: `nestedScopes` exposes the
    /// graphs and `dependencies` adds the references they make from outside themselves.
    let directValueReferences (node: SemanticNode): GraphNodeId list =
        match node.Kind with
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Int64ToString valueNodeId
        | SemanticNodeKind.Int64ToHexString valueNodeId
        | SemanticNodeKind.StringParseInt64 valueNodeId
        | SemanticNodeKind.StringParseInt64Hex valueNodeId
        | SemanticNodeKind.Float64ToString valueNodeId
        | SemanticNodeKind.StringParseFloat64 valueNodeId
        | SemanticNodeKind.StringCodePoints valueNodeId
        | SemanticNodeKind.StringToLower valueNodeId
        | SemanticNodeKind.StringToUpper valueNodeId
        | SemanticNodeKind.StringDecodeUtf8Lossy valueNodeId
        | SemanticNodeKind.Float64RoundToInt64 valueNodeId
        | SemanticNodeKind.Float64FloorToInt64 valueNodeId
        | SemanticNodeKind.Float64Sqrt valueNodeId
        | SemanticNodeKind.Float64Abs valueNodeId
        | SemanticNodeKind.Float64Negate valueNodeId
        | SemanticNodeKind.Float64Floor valueNodeId
        | SemanticNodeKind.Float64Ceiling valueNodeId
        | SemanticNodeKind.Float64Truncate valueNodeId
        | SemanticNodeKind.StringUtf8ByteCount valueNodeId
        | SemanticNodeKind.StringFromCodePoint valueNodeId
        | SemanticNodeKind.StringToCodePoint valueNodeId
        | SemanticNodeKind.StringLength valueNodeId
        | SemanticNodeKind.StringTrim valueNodeId
        | SemanticNodeKind.StringTrimStart valueNodeId
        | SemanticNodeKind.StringTrimEnd valueNodeId
        | SemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.StringReplace(valueNodeId, patternNodeId, replacementNodeId) ->
            [ valueNodeId; patternNodeId; replacementNodeId ]
        | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) -> [ valueNodeId; startNodeId; endNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            [ valueNodeId; substringNodeId ]
        | SemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) -> [ valueNodeId; substringNodeId; fromNodeId ]
        | SemanticNodeKind.StringSpan(valueNodeId, setNodeId, boundNodeId)
        | SemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, boundNodeId) -> [ valueNodeId; setNodeId; boundNodeId ]
        | SemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId)
        | SemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId)
        | SemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId)
        | SemanticNodeKind.StringSplit(valueNodeId, otherNodeId)
        | SemanticNodeKind.StringSplitLines(valueNodeId, otherNodeId) -> [ valueNodeId; otherNodeId ]
        | SemanticNodeKind.StringJoin(listNodeId, separatorNodeId) -> [ listNodeId; separatorNodeId ]
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64ShiftLeft(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64ShiftRight(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListRange(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringRepeat(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Round(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Min(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Max(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32RotateRight(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32ShiftRight(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32ShiftLeft(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
            [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Int64ToFloat64 valueNodeId
        | SemanticNodeKind.Int64WrappingAbs valueNodeId
        | SemanticNodeKind.Int64BitNot valueNodeId
        | SemanticNodeKind.Bits32FromInt64 valueNodeId
        | SemanticNodeKind.Bits32ToInt64 valueNodeId
        | SemanticNodeKind.Bits32Not valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) -> itemNodeIds
        | SemanticNodeKind.ListLength listNodeId -> [ listNodeId ]
        | SemanticNodeKind.MapValues sourceMapNodeId -> [ sourceMapNodeId ]
        | SemanticNodeKind.MapKeys sourceMapNodeId -> [ sourceMapNodeId ]
        | SemanticNodeKind.SetToList sourceSetNodeId -> [ sourceSetNodeId ]
        | SemanticNodeKind.MapSize sourceMapNodeId -> [ sourceMapNodeId ]
        | SemanticNodeKind.SetFromList sourceListNodeId -> [ sourceListNodeId ]
        | SemanticNodeKind.SetSize sourceSetNodeId -> [ sourceSetNodeId ]
        | SemanticNodeKind.ListReverse listNodeId -> [ listNodeId ]
        | SemanticNodeKind.ListLast listNodeId -> [ listNodeId ]
        | SemanticNodeKind.ListDropLast listNodeId -> [ listNodeId ]
        | SemanticNodeKind.ListZipWith(leftNodeId, rightNodeId, _, context) -> leftNodeId :: rightNodeId :: contextValues context
        | SemanticNodeKind.ListFlatMap(sourceNodeId, _, context)
        | SemanticNodeKind.ListMap(sourceNodeId, _, context)
        | SemanticNodeKind.MapFromList(sourceNodeId, _, context)
        | SemanticNodeKind.ListFilter(sourceNodeId, _, context)
        | SemanticNodeKind.MapFilter(sourceNodeId, _, context)
        | SemanticNodeKind.ListSortBy(sourceNodeId, _, context)
        | SemanticNodeKind.OptionMap(sourceNodeId, _, context)
        | SemanticNodeKind.OptionAndThen(sourceNodeId, _, context)
        | SemanticNodeKind.ResultMap(sourceNodeId, _, context)
        | SemanticNodeKind.ResultMapErr(sourceNodeId, _, context)
        | SemanticNodeKind.ResultAndThen(sourceNodeId, _, context)
        | SemanticNodeKind.MapMapValues(sourceNodeId, _, context)
        | SemanticNodeKind.ListMinBy(sourceNodeId, _, context)
        | SemanticNodeKind.ListAny(sourceNodeId, _, context)
        | SemanticNodeKind.ListFindIndex(sourceNodeId, _, context)
        | SemanticNodeKind.ListAll(sourceNodeId, _, context) -> sourceNodeId :: contextValues context
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | SemanticNodeKind.ListItemAt(listNodeId, indexNodeId) -> [ listNodeId; indexNodeId ]
        | SemanticNodeKind.MapRemove(sourceMapNodeId, keyNodeId) -> [ sourceMapNodeId; keyNodeId ]
        | SemanticNodeKind.SetInsert(sourceSetNodeId, itemNodeId) -> [ sourceSetNodeId; itemNodeId ]
        | SemanticNodeKind.SetRemove(sourceSetNodeId, itemNodeId) -> [ sourceSetNodeId; itemNodeId ]
        | SemanticNodeKind.SetUnion(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetIntersection(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetDifference(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetIsSubset(leftNodeId, rightNodeId)
        | SemanticNodeKind.MapUnion(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.MapContainsKey(sourceMapNodeId, keyNodeId) -> [ sourceMapNodeId; keyNodeId ]
        | SemanticNodeKind.SetContains(sourceSetNodeId, itemNodeId) -> [ sourceSetNodeId; itemNodeId ]
        | SemanticNodeKind.ListIndexOf(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | SemanticNodeKind.ListLastIndexOf(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | SemanticNodeKind.MapGet(sourceMapNodeId, keyNodeId) -> [ sourceMapNodeId; keyNodeId ]
        | SemanticNodeKind.OptionGetOrDefault(optionNodeId, defaultNodeId) -> [ optionNodeId; defaultNodeId ]
        | SemanticNodeKind.MapGetOrDefault(sourceMapNodeId, keyNodeId, defaultNodeId) -> [ sourceMapNodeId; keyNodeId; defaultNodeId ]
        | SemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) -> [ listNodeId; startNodeId; endNodeId ]
        | SemanticNodeKind.ListSetAt(listNodeId, indexNodeId, itemNodeId)
        | SemanticNodeKind.ListInsertAt(listNodeId, indexNodeId, itemNodeId) -> [ listNodeId; indexNodeId; itemNodeId ]
        | SemanticNodeKind.ListRemoveAt(listNodeId, indexNodeId) -> [ listNodeId; indexNodeId ]
        | SemanticNodeKind.MapInsert(sourceMapNodeId, keyNodeId, valueNodeId) -> [ sourceMapNodeId; keyNodeId; valueNodeId ]
        | SemanticNodeKind.ListFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.MapFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.ListEffectFold(sourceNodeId, seedNodeId, _, context) ->
            sourceNodeId :: seedNodeId :: contextValues context
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            acquireNodeId :: releaseCapabilityNodeId :: contextValues context
        | SemanticNodeKind.ParallelAll(sourceListNodeId, _, context) ->
            sourceListNodeId :: contextValues context
        | SemanticNodeKind.CancellationRequested -> []
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _ -> []
        | SemanticNodeKind.FunctionInvoke(value, arguments) -> value :: arguments
        | SemanticNodeKind.ExceptionCatch(_, bodyContext, _, handlerContext) -> contextValues bodyContext @ contextValues handlerContext
        | SemanticNodeKind.ExceptionIsCancellation value
        | SemanticNodeKind.ExceptionMessage value
        | SemanticNodeKind.ExceptionRethrow(value, _) -> [ value ]
        | SemanticNodeKind.CancellationShield(_, context) -> contextValues context
        | SemanticNodeKind.TaskScope(_, context) -> contextValues context
        | SemanticNodeKind.TaskStart(scopeNodeId, _, context, itemNodeId) ->
            scopeNodeId :: itemNodeId :: contextValues context
        | SemanticNodeKind.TaskWait(tasksNodeId, _, limitNodeId) -> tasksNodeId :: Option.toList limitNodeId
        | SemanticNodeKind.OneShotCreate(scope, _) -> [ scope ]
        | SemanticNodeKind.OneShotComplete(cell, value) -> [ cell; value ]
        | SemanticNodeKind.OneShotWait(cell, limit) -> cell :: Option.toList limit
        | SemanticNodeKind.TaskCancel taskNodeId -> [ taskNodeId ]
        | SemanticNodeKind.DirectCall(_, arguments) -> contextValues arguments
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, fields)
        | SemanticNodeKind.UnionConstruct(_, fields) ->
            fields |> List.map (fun field -> field.ValueNodeId)
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | SemanticNodeKind.ProductUpdate(_, sourceNodeId, fields) ->
            sourceNodeId :: (fields |> List.map (fun field -> field.ValueNodeId))
        | SemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ResultOk(_, valueNodeId) -> [ valueNodeId ]
        | SemanticNodeKind.Abort errorNodeId
        | SemanticNodeKind.ResultErr(_, errorNodeId) -> [ errorNodeId ]
        | SemanticNodeKind.BoolNot operandNodeId
        | SemanticNodeKind.BoolAnd(operandNodeId, _)
        | SemanticNodeKind.BoolOr(operandNodeId, _) -> [ operandNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.Bind(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.OptionNone _ -> []

    /// The same node with every direct value reference replaced. Nested graphs, the node's own
    /// ID and every other payload are untouched; `mapNested` handles the graphs.
    let mapValueReferences (mapNodeId: GraphNodeId -> GraphNodeId) (node: SemanticNode): SemanticNode =
        let kind =
            match node.Kind with
            | SemanticNodeKind.ParameterReference _
            | SemanticNodeKind.PatternReference _
            | SemanticNodeKind.StringLiteral _
            | SemanticNodeKind.BoolLiteral _
            | SemanticNodeKind.UnitLiteral
            | SemanticNodeKind.Int64Literal _
            | SemanticNodeKind.Float64Literal _
            | SemanticNodeKind.ListEmpty _
            | SemanticNodeKind.SetEmpty _
            | SemanticNodeKind.MapEmpty _
            | SemanticNodeKind.OptionNone _
            | SemanticNodeKind.CancellationRequested
            | SemanticNodeKind.CapabilityConstruct _
            | SemanticNodeKind.FunctionReference _ -> node.Kind
            | SemanticNodeKind.StringIsEmpty valueNodeId -> SemanticNodeKind.StringIsEmpty(mapNodeId valueNodeId)
            | SemanticNodeKind.Int64ToString valueNodeId -> SemanticNodeKind.Int64ToString(mapNodeId valueNodeId)
            | SemanticNodeKind.Int64ToHexString valueNodeId -> SemanticNodeKind.Int64ToHexString(mapNodeId valueNodeId)
            | SemanticNodeKind.StringParseInt64 valueNodeId -> SemanticNodeKind.StringParseInt64(mapNodeId valueNodeId)
            | SemanticNodeKind.StringParseInt64Hex valueNodeId -> SemanticNodeKind.StringParseInt64Hex(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64ToString valueNodeId -> SemanticNodeKind.Float64ToString(mapNodeId valueNodeId)
            | SemanticNodeKind.StringParseFloat64 valueNodeId -> SemanticNodeKind.StringParseFloat64(mapNodeId valueNodeId)
            | SemanticNodeKind.StringCodePoints valueNodeId -> SemanticNodeKind.StringCodePoints(mapNodeId valueNodeId)
            | SemanticNodeKind.StringToLower valueNodeId -> SemanticNodeKind.StringToLower(mapNodeId valueNodeId)
            | SemanticNodeKind.StringToUpper valueNodeId -> SemanticNodeKind.StringToUpper(mapNodeId valueNodeId)
            | SemanticNodeKind.StringDecodeUtf8Lossy valueNodeId -> SemanticNodeKind.StringDecodeUtf8Lossy(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64RoundToInt64 valueNodeId -> SemanticNodeKind.Float64RoundToInt64(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64FloorToInt64 valueNodeId -> SemanticNodeKind.Float64FloorToInt64(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64Sqrt valueNodeId -> SemanticNodeKind.Float64Sqrt(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64Abs valueNodeId -> SemanticNodeKind.Float64Abs(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64Negate valueNodeId -> SemanticNodeKind.Float64Negate(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64Floor valueNodeId -> SemanticNodeKind.Float64Floor(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64Ceiling valueNodeId -> SemanticNodeKind.Float64Ceiling(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64Truncate valueNodeId -> SemanticNodeKind.Float64Truncate(mapNodeId valueNodeId)
            | SemanticNodeKind.StringUtf8ByteCount valueNodeId -> SemanticNodeKind.StringUtf8ByteCount(mapNodeId valueNodeId)
            | SemanticNodeKind.StringFromCodePoint valueNodeId -> SemanticNodeKind.StringFromCodePoint(mapNodeId valueNodeId)
            | SemanticNodeKind.StringToCodePoint valueNodeId -> SemanticNodeKind.StringToCodePoint(mapNodeId valueNodeId)
            | SemanticNodeKind.StringLength valueNodeId -> SemanticNodeKind.StringLength(mapNodeId valueNodeId)
            | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
                SemanticNodeKind.StringSlice(mapNodeId valueNodeId, mapNodeId startNodeId, mapNodeId endNodeId)
            | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
                SemanticNodeKind.StringContains(mapNodeId valueNodeId, mapNodeId substringNodeId)
            | SemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) ->
                SemanticNodeKind.StringIndexOf(mapNodeId valueNodeId, mapNodeId substringNodeId, mapNodeId fromNodeId)
            | SemanticNodeKind.StringSpan(valueNodeId, setNodeId, fromNodeId) ->
                SemanticNodeKind.StringSpan(mapNodeId valueNodeId, mapNodeId setNodeId, mapNodeId fromNodeId)
            | SemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, endNodeId) ->
                SemanticNodeKind.StringSpanBack(mapNodeId valueNodeId, mapNodeId setNodeId, mapNodeId endNodeId)
            | SemanticNodeKind.StringLastIndexOf(valueNodeId, substringNodeId) ->
                SemanticNodeKind.StringLastIndexOf(mapNodeId valueNodeId, mapNodeId substringNodeId)
            | SemanticNodeKind.StringStartsWith(valueNodeId, prefixNodeId) ->
                SemanticNodeKind.StringStartsWith(mapNodeId valueNodeId, mapNodeId prefixNodeId)
            | SemanticNodeKind.StringEndsWith(valueNodeId, suffixNodeId) ->
                SemanticNodeKind.StringEndsWith(mapNodeId valueNodeId, mapNodeId suffixNodeId)
            | SemanticNodeKind.StringSplit(valueNodeId, separatorNodeId) ->
                SemanticNodeKind.StringSplit(mapNodeId valueNodeId, mapNodeId separatorNodeId)
            | SemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId) ->
                SemanticNodeKind.StringSplitLines(mapNodeId valueNodeId, mapNodeId breaksNodeId)
            | SemanticNodeKind.StringReplace(valueNodeId, patternNodeId, replacementNodeId) ->
                SemanticNodeKind.StringReplace(mapNodeId valueNodeId, mapNodeId patternNodeId, mapNodeId replacementNodeId)
            | SemanticNodeKind.StringTrim valueNodeId -> SemanticNodeKind.StringTrim(mapNodeId valueNodeId)
            | SemanticNodeKind.StringTrimStart valueNodeId -> SemanticNodeKind.StringTrimStart(mapNodeId valueNodeId)
            | SemanticNodeKind.StringTrimEnd valueNodeId -> SemanticNodeKind.StringTrimEnd(mapNodeId valueNodeId)
            | SemanticNodeKind.StringJoin(listNodeId, separatorNodeId) ->
                SemanticNodeKind.StringJoin(mapNodeId listNodeId, mapNodeId separatorNodeId)
            | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
                SemanticNodeKind.StringEqual(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.StringLessThan(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId) ->
                SemanticNodeKind.ValueEqual(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
                SemanticNodeKind.StringConcat(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64WrappingAdd(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64WrappingSubtract(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64WrappingMultiply(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64FloorDivide(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64FloorModulo(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64Min(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64Max(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64BitAnd(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64BitOr(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64BitXor(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64ShiftLeft(valueNodeId, countNodeId) ->
                SemanticNodeKind.Int64ShiftLeft(mapNodeId valueNodeId, mapNodeId countNodeId)
            | SemanticNodeKind.Int64ShiftRight(valueNodeId, countNodeId) ->
                SemanticNodeKind.Int64ShiftRight(mapNodeId valueNodeId, mapNodeId countNodeId)
            | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64CheckedAdd(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64CheckedSubtract(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64CheckedMultiply(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.ListRange(leftNodeId, rightNodeId) ->
                SemanticNodeKind.ListRange(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.StringRepeat(leftNodeId, rightNodeId) ->
                SemanticNodeKind.StringRepeat(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Round(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Round(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64Equal(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64LessThan(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64LessThanOrEqual(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64GreaterThan(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Int64GreaterThanOrEqual(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Add(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Min(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Min(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Max(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Max(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Subtract(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Multiply(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Divide(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64Equal(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
                SemanticNodeKind.Float64LessThan(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Float64IsNaN valueNodeId -> SemanticNodeKind.Float64IsNaN(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64IsFinite valueNodeId -> SemanticNodeKind.Float64IsFinite(mapNodeId valueNodeId)
            | SemanticNodeKind.Float64IsInfinite valueNodeId -> SemanticNodeKind.Float64IsInfinite(mapNodeId valueNodeId)
            | SemanticNodeKind.Int64ToFloat64 valueNodeId -> SemanticNodeKind.Int64ToFloat64(mapNodeId valueNodeId)
            | SemanticNodeKind.Int64WrappingAbs valueNodeId -> SemanticNodeKind.Int64WrappingAbs(mapNodeId valueNodeId)
            | SemanticNodeKind.Int64BitNot valueNodeId -> SemanticNodeKind.Int64BitNot(mapNodeId valueNodeId)
            | SemanticNodeKind.Bits32FromInt64 valueNodeId -> SemanticNodeKind.Bits32FromInt64(mapNodeId valueNodeId)
            | SemanticNodeKind.Bits32ToInt64 valueNodeId -> SemanticNodeKind.Bits32ToInt64(mapNodeId valueNodeId)
            | SemanticNodeKind.Bits32Not valueNodeId -> SemanticNodeKind.Bits32Not(mapNodeId valueNodeId)
            | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId) -> SemanticNodeKind.Bits32Xor(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId) -> SemanticNodeKind.Bits32And(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId) -> SemanticNodeKind.Bits32Or(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId) -> SemanticNodeKind.Bits32Add(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.Bits32RotateRight(valueNodeId, amountNodeId) ->
                SemanticNodeKind.Bits32RotateRight(mapNodeId valueNodeId, mapNodeId amountNodeId)
            | SemanticNodeKind.Bits32ShiftRight(valueNodeId, amountNodeId) ->
                SemanticNodeKind.Bits32ShiftRight(mapNodeId valueNodeId, mapNodeId amountNodeId)
            | SemanticNodeKind.Bits32ShiftLeft(valueNodeId, amountNodeId) ->
                SemanticNodeKind.Bits32ShiftLeft(mapNodeId valueNodeId, mapNodeId amountNodeId)
            | SemanticNodeKind.ListConstruct(itemType, itemNodeIds) ->
                SemanticNodeKind.ListConstruct(itemType, List.map mapNodeId itemNodeIds)
            | SemanticNodeKind.ListLength listNodeId -> SemanticNodeKind.ListLength(mapNodeId listNodeId)
            | SemanticNodeKind.MapValues sourceMapNodeId -> SemanticNodeKind.MapValues(mapNodeId sourceMapNodeId)
            | SemanticNodeKind.MapKeys sourceMapNodeId -> SemanticNodeKind.MapKeys(mapNodeId sourceMapNodeId)
            | SemanticNodeKind.SetToList sourceSetNodeId -> SemanticNodeKind.SetToList(mapNodeId sourceSetNodeId)
            | SemanticNodeKind.MapSize sourceMapNodeId -> SemanticNodeKind.MapSize(mapNodeId sourceMapNodeId)
            | SemanticNodeKind.SetFromList sourceListNodeId -> SemanticNodeKind.SetFromList(mapNodeId sourceListNodeId)
            | SemanticNodeKind.SetSize sourceSetNodeId -> SemanticNodeKind.SetSize(mapNodeId sourceSetNodeId)
            | SemanticNodeKind.ListReverse listNodeId -> SemanticNodeKind.ListReverse(mapNodeId listNodeId)
            | SemanticNodeKind.ListLast listNodeId -> SemanticNodeKind.ListLast(mapNodeId listNodeId)
            | SemanticNodeKind.ListDropLast listNodeId -> SemanticNodeKind.ListDropLast(mapNodeId listNodeId)
            | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
                SemanticNodeKind.ListAppendItem(mapNodeId listNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
                SemanticNodeKind.ListItemAt(mapNodeId listNodeId, mapNodeId indexNodeId)
            | SemanticNodeKind.MapRemove(sourceMapNodeId, keyNodeId) ->
                SemanticNodeKind.MapRemove(mapNodeId sourceMapNodeId, mapNodeId keyNodeId)
            | SemanticNodeKind.SetInsert(sourceSetNodeId, itemNodeId) ->
                SemanticNodeKind.SetInsert(mapNodeId sourceSetNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.SetRemove(sourceSetNodeId, itemNodeId) ->
                SemanticNodeKind.SetRemove(mapNodeId sourceSetNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.SetUnion(leftNodeId, rightNodeId) ->
                SemanticNodeKind.SetUnion(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.SetIntersection(leftNodeId, rightNodeId) ->
                SemanticNodeKind.SetIntersection(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.SetDifference(leftNodeId, rightNodeId) ->
                SemanticNodeKind.SetDifference(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.SetIsSubset(leftNodeId, rightNodeId) ->
                SemanticNodeKind.SetIsSubset(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.MapUnion(leftNodeId, rightNodeId) ->
                SemanticNodeKind.MapUnion(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.MapContainsKey(sourceMapNodeId, keyNodeId) ->
                SemanticNodeKind.MapContainsKey(mapNodeId sourceMapNodeId, mapNodeId keyNodeId)
            | SemanticNodeKind.SetContains(sourceSetNodeId, itemNodeId) ->
                SemanticNodeKind.SetContains(mapNodeId sourceSetNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.ListIndexOf(listNodeId, itemNodeId) ->
                SemanticNodeKind.ListIndexOf(mapNodeId listNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.ListLastIndexOf(listNodeId, itemNodeId) ->
                SemanticNodeKind.ListLastIndexOf(mapNodeId listNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.MapGet(sourceMapNodeId, keyNodeId) ->
                SemanticNodeKind.MapGet(mapNodeId sourceMapNodeId, mapNodeId keyNodeId)
            | SemanticNodeKind.MapGetOrDefault(sourceMapNodeId, keyNodeId, defaultNodeId) ->
                SemanticNodeKind.MapGetOrDefault(mapNodeId sourceMapNodeId, mapNodeId keyNodeId, mapNodeId defaultNodeId)
            | SemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) ->
                SemanticNodeKind.ListSlice(mapNodeId listNodeId, mapNodeId startNodeId, mapNodeId endNodeId)
            | SemanticNodeKind.ListSetAt(listNodeId, indexNodeId, itemNodeId) ->
                SemanticNodeKind.ListSetAt(mapNodeId listNodeId, mapNodeId indexNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.ListInsertAt(listNodeId, indexNodeId, itemNodeId) ->
                SemanticNodeKind.ListInsertAt(mapNodeId listNodeId, mapNodeId indexNodeId, mapNodeId itemNodeId)
            | SemanticNodeKind.ListRemoveAt(listNodeId, indexNodeId) ->
                SemanticNodeKind.ListRemoveAt(mapNodeId listNodeId, mapNodeId indexNodeId)
            | SemanticNodeKind.MapInsert(sourceMapNodeId, keyNodeId, valueNodeId) ->
                SemanticNodeKind.MapInsert(mapNodeId sourceMapNodeId, mapNodeId keyNodeId, mapNodeId valueNodeId)
            | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
                SemanticNodeKind.ListConcat(mapNodeId leftNodeId, mapNodeId rightNodeId)
            | SemanticNodeKind.ListFold(listNodeId, seedNodeId, functionId, context) ->
                SemanticNodeKind.ListFold(mapNodeId listNodeId, mapNodeId seedNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.MapFold(listNodeId, seedNodeId, functionId, context) ->
                SemanticNodeKind.MapFold(mapNodeId listNodeId, mapNodeId seedNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListEffectFold(listNodeId, seedNodeId, functionId, context) ->
                SemanticNodeKind.ListEffectFold(mapNodeId listNodeId, mapNodeId seedNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ParallelAll(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ParallelAll(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, operationId, functionId, context) ->
                SemanticNodeKind.UseResource(
                    mapNodeId acquireNodeId,
                    mapNodeId releaseCapabilityNodeId,
                    operationId,
                    functionId,
                    mapContextValues mapNodeId context
                )
            | SemanticNodeKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
                SemanticNodeKind.ExceptionCatch(body, mapContextValues mapNodeId bodyContext, handler, mapContextValues mapNodeId handlerContext)
            | SemanticNodeKind.ExceptionIsCancellation value -> SemanticNodeKind.ExceptionIsCancellation(mapNodeId value)
            | SemanticNodeKind.ExceptionMessage value -> SemanticNodeKind.ExceptionMessage(mapNodeId value)
            | SemanticNodeKind.ExceptionRethrow(value, resultType) -> SemanticNodeKind.ExceptionRethrow(mapNodeId value, resultType)
            | SemanticNodeKind.CancellationShield(functionId, context) ->
                SemanticNodeKind.CancellationShield(functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.TaskScope(functionId, context) ->
                SemanticNodeKind.TaskScope(functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.TaskStart(scopeNodeId, functionId, context, itemNodeId) ->
                SemanticNodeKind.TaskStart(mapNodeId scopeNodeId, functionId, mapContextValues mapNodeId context, mapNodeId itemNodeId)
            | SemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) ->
                SemanticNodeKind.TaskWait(mapNodeId tasksNodeId, mode, Option.map mapNodeId limitNodeId)
            | SemanticNodeKind.OneShotCreate(scope, valueType) -> SemanticNodeKind.OneShotCreate(mapNodeId scope, valueType)
            | SemanticNodeKind.OneShotComplete(cell, value) -> SemanticNodeKind.OneShotComplete(mapNodeId cell, mapNodeId value)
            | SemanticNodeKind.OneShotWait(cell, limit) -> SemanticNodeKind.OneShotWait(mapNodeId cell, Option.map mapNodeId limit)
            | SemanticNodeKind.TaskCancel taskNodeId -> SemanticNodeKind.TaskCancel(mapNodeId taskNodeId)
            | SemanticNodeKind.ListZipWith(leftNodeId, rightNodeId, functionId, context) ->
                SemanticNodeKind.ListZipWith(mapNodeId leftNodeId, mapNodeId rightNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListFlatMap(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListFlatMap(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListMap(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListMap(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.MapFromList(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.MapFromList(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListFilter(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListFilter(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.MapFilter(sourceMapNodeId, functionId, context) ->
                SemanticNodeKind.MapFilter(mapNodeId sourceMapNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListSortBy(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListSortBy(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.OptionGetOrDefault(optionNodeId, defaultNodeId) ->
                SemanticNodeKind.OptionGetOrDefault(mapNodeId optionNodeId, mapNodeId defaultNodeId)
            | SemanticNodeKind.OptionMap(sourceNodeId, functionId, context) ->
                SemanticNodeKind.OptionMap(mapNodeId sourceNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.OptionAndThen(sourceNodeId, functionId, context) ->
                SemanticNodeKind.OptionAndThen(mapNodeId sourceNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ResultMap(sourceNodeId, functionId, context) ->
                SemanticNodeKind.ResultMap(mapNodeId sourceNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ResultMapErr(sourceNodeId, functionId, context) ->
                SemanticNodeKind.ResultMapErr(mapNodeId sourceNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ResultAndThen(sourceNodeId, functionId, context) ->
                SemanticNodeKind.ResultAndThen(mapNodeId sourceNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.MapMapValues(sourceMapNodeId, functionId, context) ->
                SemanticNodeKind.MapMapValues(mapNodeId sourceMapNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListMinBy(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListMinBy(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListAny(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListAny(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListFindIndex(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListFindIndex(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.ListAll(sourceListNodeId, functionId, context) ->
                SemanticNodeKind.ListAll(mapNodeId sourceListNodeId, functionId, mapContextValues mapNodeId context)
            | SemanticNodeKind.FunctionInvoke(value, arguments) ->
                SemanticNodeKind.FunctionInvoke(mapNodeId value, List.map mapNodeId arguments)
            | SemanticNodeKind.DirectCall(functionId, arguments) ->
                SemanticNodeKind.DirectCall(functionId, mapContextValues mapNodeId arguments)
            | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, operationId, arguments) ->
                SemanticNodeKind.CapabilityInvoke(
                    mapNodeId capabilityValueNodeId,
                    operationId,
                    arguments |> List.map (fun argument -> { argument with ValueNodeId = mapNodeId argument.ValueNodeId })
                )
            | SemanticNodeKind.ProductConstruct(typeId, fields) ->
                SemanticNodeKind.ProductConstruct(typeId, mapFieldValues mapNodeId fields)
            | SemanticNodeKind.UnionConstruct(variantId, fields) ->
                SemanticNodeKind.UnionConstruct(variantId, mapFieldValues mapNodeId fields)
            | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) -> SemanticNodeKind.FieldAccess(mapNodeId sourceNodeId, fieldId)
            | SemanticNodeKind.ProductUpdate(typeId, sourceNodeId, fields) ->
                SemanticNodeKind.ProductUpdate(typeId, mapNodeId sourceNodeId, mapFieldValues mapNodeId fields)
            | SemanticNodeKind.OptionSome valueNodeId -> SemanticNodeKind.OptionSome(mapNodeId valueNodeId)
            | SemanticNodeKind.ResultOk(errorType, valueNodeId) -> SemanticNodeKind.ResultOk(errorType, mapNodeId valueNodeId)
            | SemanticNodeKind.Abort errorNodeId -> SemanticNodeKind.Abort(mapNodeId errorNodeId)
            | SemanticNodeKind.ResultErr(okType, errorNodeId) -> SemanticNodeKind.ResultErr(okType, mapNodeId errorNodeId)
            | SemanticNodeKind.BoolNot operandNodeId -> SemanticNodeKind.BoolNot(mapNodeId operandNodeId)
            | SemanticNodeKind.BoolAnd(leftNodeId, right) -> SemanticNodeKind.BoolAnd(mapNodeId leftNodeId, right)
            | SemanticNodeKind.BoolOr(leftNodeId, right) -> SemanticNodeKind.BoolOr(mapNodeId leftNodeId, right)
            | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                SemanticNodeKind.Conditional(mapNodeId conditionNodeId, whenTrue, whenFalse)
            | SemanticNodeKind.MatchUnion(inputNodeId, arms) -> SemanticNodeKind.MatchUnion(mapNodeId inputNodeId, arms)
            | SemanticNodeKind.Bind(inputNodeId, body) -> SemanticNodeKind.Bind(mapNodeId inputNodeId, body)
            | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                SemanticNodeKind.MatchOption(mapNodeId inputNodeId, someArm, noneBody)
            | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                SemanticNodeKind.MatchResult(mapNodeId inputNodeId, okArm, errArm)

        { node with Kind = kind }

    /// The nested graphs a node owns, in evaluation order, each with its role and the pattern
    /// bindings its scope introduces. This is the exhaustive list of nested-graph node kinds.
    let nestedScopes (node: SemanticNode): NestedScope list =
        let scope role bindings graph = { Role = role; Bindings = bindings; Graph = graph }
        match node.Kind with
        | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
            [ scope NestedGraphRole.WhenTrue [] whenTrue
              scope NestedGraphRole.WhenFalse [] whenFalse ]
        | SemanticNodeKind.BoolAnd(_, right) -> [ scope NestedGraphRole.BoolAndRight [] right ]
        | SemanticNodeKind.BoolOr(_, right) -> [ scope NestedGraphRole.BoolOrRight [] right ]
        | SemanticNodeKind.MatchUnion(_, arms) ->
            arms
            |> List.map (fun arm ->
                scope
                    (NestedGraphRole.UnionArm arm.VariantIds)
                    (arm.FieldBindings |> List.map (fun field -> NestedBinding.Field(field.SourceFieldId, field.Binding)))
                    arm.Body)
        | SemanticNodeKind.Bind(_, body) ->
            [ scope NestedGraphRole.BindBody [ NestedBinding.Value body.ValueBinding ] body.Body ]
        | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
            [ scope NestedGraphRole.OptionSome [ NestedBinding.Value someArm.ValueBinding ] someArm.Body
              scope NestedGraphRole.OptionNone [] noneBody ]
        | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
            [ scope NestedGraphRole.ResultOk [ NestedBinding.Value okArm.ValueBinding ] okArm.Body
              scope NestedGraphRole.ResultErr [ NestedBinding.Error errArm.ErrorBinding ] errArm.Body ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.StringIsEmpty _
        | SemanticNodeKind.Int64ToString _
        | SemanticNodeKind.Int64ToHexString _
        | SemanticNodeKind.StringParseInt64 _
        | SemanticNodeKind.StringParseInt64Hex _
        | SemanticNodeKind.Float64ToString _
        | SemanticNodeKind.StringParseFloat64 _
        | SemanticNodeKind.StringCodePoints _
        | SemanticNodeKind.StringToLower _
        | SemanticNodeKind.StringToUpper _
        | SemanticNodeKind.StringDecodeUtf8Lossy _
        | SemanticNodeKind.Float64RoundToInt64 _
        | SemanticNodeKind.Float64FloorToInt64 _
        | SemanticNodeKind.Float64Sqrt _
        | SemanticNodeKind.Float64Abs _
        | SemanticNodeKind.Float64Negate _
        | SemanticNodeKind.Float64Floor _
        | SemanticNodeKind.Float64Ceiling _
        | SemanticNodeKind.Float64Truncate _
        | SemanticNodeKind.StringUtf8ByteCount _
        | SemanticNodeKind.StringFromCodePoint _
        | SemanticNodeKind.StringToCodePoint _
        | SemanticNodeKind.StringLength _
        | SemanticNodeKind.StringSlice _
        | SemanticNodeKind.StringContains _
        | SemanticNodeKind.StringIndexOf _
        | SemanticNodeKind.StringSpan _
        | SemanticNodeKind.StringSpanBack _
        | SemanticNodeKind.StringLastIndexOf _
        | SemanticNodeKind.StringStartsWith _
        | SemanticNodeKind.StringEndsWith _
        | SemanticNodeKind.StringJoin _
        | SemanticNodeKind.StringSplit _
        | SemanticNodeKind.StringSplitLines _
        | SemanticNodeKind.StringReplace _
        | SemanticNodeKind.StringTrim _
        | SemanticNodeKind.StringTrimStart _
        | SemanticNodeKind.StringTrimEnd _
        | SemanticNodeKind.StringEqual _
        | SemanticNodeKind.StringLessThan _
        | SemanticNodeKind.ValueEqual _
        | SemanticNodeKind.StringConcat _
        | SemanticNodeKind.Int64WrappingAdd _
        | SemanticNodeKind.Int64WrappingSubtract _
        | SemanticNodeKind.Int64WrappingMultiply _
        | SemanticNodeKind.Int64FloorDivide _
        | SemanticNodeKind.Int64FloorModulo _
        | SemanticNodeKind.Int64Min _
        | SemanticNodeKind.Int64Max _
        | SemanticNodeKind.Int64BitAnd _
        | SemanticNodeKind.Int64BitOr _
        | SemanticNodeKind.Int64BitXor _
        | SemanticNodeKind.Int64ShiftLeft _
        | SemanticNodeKind.Int64ShiftRight _
        | SemanticNodeKind.Int64CheckedAdd _
        | SemanticNodeKind.Int64CheckedSubtract _
        | SemanticNodeKind.Int64CheckedMultiply _
        | SemanticNodeKind.ListRange _
        | SemanticNodeKind.StringRepeat _
        | SemanticNodeKind.Float64Round _
        | SemanticNodeKind.Int64Equal _
        | SemanticNodeKind.Int64LessThan _
        | SemanticNodeKind.Int64LessThanOrEqual _
        | SemanticNodeKind.Int64GreaterThan _
        | SemanticNodeKind.Int64GreaterThanOrEqual _
        | SemanticNodeKind.Float64Add _
        | SemanticNodeKind.Float64Min _
        | SemanticNodeKind.Float64Max _
        | SemanticNodeKind.Float64Subtract _
        | SemanticNodeKind.Float64Multiply _
        | SemanticNodeKind.Float64Divide _
        | SemanticNodeKind.Float64Equal _
        | SemanticNodeKind.Float64LessThan _
        | SemanticNodeKind.Float64IsNaN _
        | SemanticNodeKind.Float64IsFinite _
        | SemanticNodeKind.Float64IsInfinite _
        | SemanticNodeKind.Int64ToFloat64 _
        | SemanticNodeKind.Int64WrappingAbs _
        | SemanticNodeKind.Int64BitNot _
        | SemanticNodeKind.Bits32FromInt64 _
        | SemanticNodeKind.Bits32ToInt64 _
        | SemanticNodeKind.Bits32Xor _
        | SemanticNodeKind.Bits32And _
        | SemanticNodeKind.Bits32Or _
        | SemanticNodeKind.Bits32Not _
        | SemanticNodeKind.Bits32Add _
        | SemanticNodeKind.Bits32RotateRight _
        | SemanticNodeKind.Bits32ShiftRight _
        | SemanticNodeKind.Bits32ShiftLeft _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.ListConstruct _
        | SemanticNodeKind.ListLength _
        | SemanticNodeKind.MapValues _
        | SemanticNodeKind.MapKeys _
        | SemanticNodeKind.SetToList _
        | SemanticNodeKind.MapSize _
        | SemanticNodeKind.SetFromList _
        | SemanticNodeKind.SetSize _
        | SemanticNodeKind.ListReverse _
        | SemanticNodeKind.ListLast _
        | SemanticNodeKind.ListDropLast _
        | SemanticNodeKind.ListAppendItem _
        | SemanticNodeKind.ListSlice _
        | SemanticNodeKind.ListSetAt _
        | SemanticNodeKind.ListInsertAt _
        | SemanticNodeKind.ListRemoveAt _
        | SemanticNodeKind.MapInsert _
        | SemanticNodeKind.ListItemAt _
        | SemanticNodeKind.MapRemove _
        | SemanticNodeKind.SetInsert _
        | SemanticNodeKind.SetRemove _
        | SemanticNodeKind.SetUnion _
        | SemanticNodeKind.SetIntersection _
        | SemanticNodeKind.SetDifference _
        | SemanticNodeKind.SetIsSubset _
        | SemanticNodeKind.MapUnion _
        | SemanticNodeKind.MapContainsKey _
        | SemanticNodeKind.SetContains _
        | SemanticNodeKind.ListIndexOf _
        | SemanticNodeKind.ListLastIndexOf _
        | SemanticNodeKind.MapGet _
        | SemanticNodeKind.OptionGetOrDefault _
        | SemanticNodeKind.MapGetOrDefault _
        | SemanticNodeKind.ListConcat _
        | SemanticNodeKind.ListFold _
        | SemanticNodeKind.MapFold _
        | SemanticNodeKind.ParallelAll _
        | SemanticNodeKind.CancellationRequested
        | SemanticNodeKind.UseResource _
        | SemanticNodeKind.ExceptionCatch _
        | SemanticNodeKind.ExceptionIsCancellation _
        | SemanticNodeKind.ExceptionMessage _
        | SemanticNodeKind.ExceptionRethrow _
        | SemanticNodeKind.CancellationShield _
        | SemanticNodeKind.TaskScope _
        | SemanticNodeKind.TaskStart _
        | SemanticNodeKind.TaskWait _
        | SemanticNodeKind.OneShotCreate _
        | SemanticNodeKind.OneShotComplete _
        | SemanticNodeKind.OneShotWait _
        | SemanticNodeKind.TaskCancel _
        | SemanticNodeKind.ListEffectFold _
        | SemanticNodeKind.ListZipWith _
        | SemanticNodeKind.ListFlatMap _
        | SemanticNodeKind.ListMap _
        | SemanticNodeKind.MapFromList _
        | SemanticNodeKind.ListFilter _
        | SemanticNodeKind.MapFilter _
        | SemanticNodeKind.ListSortBy _
        | SemanticNodeKind.OptionMap _
        | SemanticNodeKind.OptionAndThen _
        | SemanticNodeKind.ResultMap _
        | SemanticNodeKind.ResultMapErr _
        | SemanticNodeKind.ResultAndThen _
        | SemanticNodeKind.MapMapValues _
        | SemanticNodeKind.ListMinBy _
        | SemanticNodeKind.ListAny _
        | SemanticNodeKind.ListFindIndex _
        | SemanticNodeKind.ListAll _
        | SemanticNodeKind.DirectCall _
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _
        | SemanticNodeKind.FunctionInvoke _
        | SemanticNodeKind.CapabilityInvoke _
        | SemanticNodeKind.ProductConstruct _
        | SemanticNodeKind.FieldAccess _
        | SemanticNodeKind.ProductUpdate _
        | SemanticNodeKind.UnionConstruct _
        | SemanticNodeKind.OptionNone _
        | SemanticNodeKind.OptionSome _
        | SemanticNodeKind.ResultOk _
        | SemanticNodeKind.Abort _
        | SemanticNodeKind.ResultErr _
        | SemanticNodeKind.BoolNot _ -> []

    /// The nested graphs a node owns, in evaluation order.
    let nestedGraphs (node: SemanticNode): ValueGraph list =
        nestedScopes node |> List.map (fun scope -> scope.Graph)

    /// The same node with every nested graph and every scope binding replaced. Direct value
    /// references and the rest of the payload are untouched; `mapValueReferences` handles those.
    let mapNested
        (mapBinding: PatternBinding -> PatternBinding)
        (mapGraph: ValueGraph -> ValueGraph)
        (node: SemanticNode)
        : SemanticNode =
        let mapValueArm (arm: ValueMatchArm): ValueMatchArm =
            { ValueBinding = mapBinding arm.ValueBinding
              Body = mapGraph arm.Body }

        let kind =
            match node.Kind with
            | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
                SemanticNodeKind.Conditional(conditionNodeId, mapGraph whenTrue, mapGraph whenFalse)
            | SemanticNodeKind.BoolAnd(leftNodeId, right) -> SemanticNodeKind.BoolAnd(leftNodeId, mapGraph right)
            | SemanticNodeKind.BoolOr(leftNodeId, right) -> SemanticNodeKind.BoolOr(leftNodeId, mapGraph right)
            | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
                SemanticNodeKind.MatchUnion(
                    inputNodeId,
                    arms
                    |> List.map (fun arm ->
                        { arm with
                            FieldBindings =
                                arm.FieldBindings
                                |> List.map (fun field -> { field with Binding = mapBinding field.Binding })
                            Body = mapGraph arm.Body })
                )
            | SemanticNodeKind.Bind(inputNodeId, body) -> SemanticNodeKind.Bind(inputNodeId, mapValueArm body)
            | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
                SemanticNodeKind.MatchOption(inputNodeId, mapValueArm someArm, mapGraph noneBody)
            | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
                SemanticNodeKind.MatchResult(
                    inputNodeId,
                    mapValueArm okArm,
                    { ErrorBinding = mapBinding errArm.ErrorBinding
                      Body = mapGraph errArm.Body }
                )
            // Every other kind owns no nested graph: `nestedScopes` is the exhaustive list.
            | other -> other

        { node with Kind = kind }

    /// The same node with every nested graph replaced; scope bindings stay as they are.
    let mapNestedGraphs (mapGraph: ValueGraph -> ValueGraph) (node: SemanticNode): SemanticNode =
        mapNested id mapGraph node

    /// The one list of the types a node states in its own payload (pattern bindings and nested
    /// graphs excluded), paired with the node's kind after `mapTypeRef` rewrote each of them.
    let private typeRefPayload (mapTypeRef: TypeRef -> TypeRef) (node: SemanticNode): TypeRef list * SemanticNodeKind =
        match node.Kind with
        | SemanticNodeKind.ExceptionRethrow(value, resultType) -> [ resultType ], SemanticNodeKind.ExceptionRethrow(value, mapTypeRef resultType)
        | SemanticNodeKind.OneShotCreate(scope, valueType) -> [ valueType ], SemanticNodeKind.OneShotCreate(scope, mapTypeRef valueType)
        | SemanticNodeKind.ListEmpty itemType -> [ itemType ], SemanticNodeKind.ListEmpty(mapTypeRef itemType)
        | SemanticNodeKind.SetEmpty itemType -> [ itemType ], SemanticNodeKind.SetEmpty(mapTypeRef itemType)
        | SemanticNodeKind.MapEmpty(keyType, valueType) -> [ keyType; valueType ], SemanticNodeKind.MapEmpty(mapTypeRef keyType, mapTypeRef valueType)
        | SemanticNodeKind.ListConstruct(itemType, itemNodeIds) ->
            [ itemType ], SemanticNodeKind.ListConstruct(mapTypeRef itemType, itemNodeIds)
        | SemanticNodeKind.OptionNone itemType -> [ itemType ], SemanticNodeKind.OptionNone(mapTypeRef itemType)
        | SemanticNodeKind.ResultOk(errorType, valueNodeId) ->
            [ errorType ], SemanticNodeKind.ResultOk(mapTypeRef errorType, valueNodeId)
        | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
            [ okType ], SemanticNodeKind.ResultErr(mapTypeRef okType, errorNodeId)
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.StringIsEmpty _
        | SemanticNodeKind.Int64ToString _
        | SemanticNodeKind.Int64ToHexString _
        | SemanticNodeKind.StringParseInt64 _
        | SemanticNodeKind.StringParseInt64Hex _
        | SemanticNodeKind.Float64ToString _
        | SemanticNodeKind.StringParseFloat64 _
        | SemanticNodeKind.StringCodePoints _
        | SemanticNodeKind.StringToLower _
        | SemanticNodeKind.StringToUpper _
        | SemanticNodeKind.StringDecodeUtf8Lossy _
        | SemanticNodeKind.Float64RoundToInt64 _
        | SemanticNodeKind.Float64FloorToInt64 _
        | SemanticNodeKind.Float64Sqrt _
        | SemanticNodeKind.Float64Abs _
        | SemanticNodeKind.Float64Negate _
        | SemanticNodeKind.Float64Floor _
        | SemanticNodeKind.Float64Ceiling _
        | SemanticNodeKind.Float64Truncate _
        | SemanticNodeKind.StringUtf8ByteCount _
        | SemanticNodeKind.StringFromCodePoint _
        | SemanticNodeKind.StringToCodePoint _
        | SemanticNodeKind.StringLength _
        | SemanticNodeKind.StringSlice _
        | SemanticNodeKind.StringContains _
        | SemanticNodeKind.StringIndexOf _
        | SemanticNodeKind.StringSpan _
        | SemanticNodeKind.StringSpanBack _
        | SemanticNodeKind.StringLastIndexOf _
        | SemanticNodeKind.StringStartsWith _
        | SemanticNodeKind.StringEndsWith _
        | SemanticNodeKind.StringSplit _
        | SemanticNodeKind.StringSplitLines _
        | SemanticNodeKind.StringReplace _
        | SemanticNodeKind.StringTrim _
        | SemanticNodeKind.StringTrimStart _
        | SemanticNodeKind.StringTrimEnd _
        | SemanticNodeKind.StringJoin _
        | SemanticNodeKind.StringEqual _
        | SemanticNodeKind.StringLessThan _
        | SemanticNodeKind.ValueEqual _
        | SemanticNodeKind.StringConcat _
        | SemanticNodeKind.Int64WrappingAdd _
        | SemanticNodeKind.Int64WrappingSubtract _
        | SemanticNodeKind.Int64WrappingMultiply _
        | SemanticNodeKind.Int64FloorDivide _
        | SemanticNodeKind.Int64FloorModulo _
        | SemanticNodeKind.Int64Min _
        | SemanticNodeKind.Int64Max _
        | SemanticNodeKind.Int64BitAnd _
        | SemanticNodeKind.Int64BitOr _
        | SemanticNodeKind.Int64BitXor _
        | SemanticNodeKind.Int64ShiftLeft _
        | SemanticNodeKind.Int64ShiftRight _
        | SemanticNodeKind.Int64CheckedAdd _
        | SemanticNodeKind.Int64CheckedSubtract _
        | SemanticNodeKind.Int64CheckedMultiply _
        | SemanticNodeKind.ListRange _
        | SemanticNodeKind.StringRepeat _
        | SemanticNodeKind.Float64Round _
        | SemanticNodeKind.Int64Equal _
        | SemanticNodeKind.Int64LessThan _
        | SemanticNodeKind.Int64LessThanOrEqual _
        | SemanticNodeKind.Int64GreaterThan _
        | SemanticNodeKind.Int64GreaterThanOrEqual _
        | SemanticNodeKind.Float64Add _
        | SemanticNodeKind.Float64Min _
        | SemanticNodeKind.Float64Max _
        | SemanticNodeKind.Float64Subtract _
        | SemanticNodeKind.Float64Multiply _
        | SemanticNodeKind.Float64Divide _
        | SemanticNodeKind.Float64Equal _
        | SemanticNodeKind.Float64LessThan _
        | SemanticNodeKind.Float64IsNaN _
        | SemanticNodeKind.Float64IsFinite _
        | SemanticNodeKind.Float64IsInfinite _
        | SemanticNodeKind.Int64ToFloat64 _
        | SemanticNodeKind.Int64WrappingAbs _
        | SemanticNodeKind.Int64BitNot _
        | SemanticNodeKind.Bits32FromInt64 _
        | SemanticNodeKind.Bits32ToInt64 _
        | SemanticNodeKind.Bits32Xor _
        | SemanticNodeKind.Bits32And _
        | SemanticNodeKind.Bits32Or _
        | SemanticNodeKind.Bits32Not _
        | SemanticNodeKind.Bits32Add _
        | SemanticNodeKind.Bits32RotateRight _
        | SemanticNodeKind.Bits32ShiftRight _
        | SemanticNodeKind.Bits32ShiftLeft _
        | SemanticNodeKind.ListLength _
        | SemanticNodeKind.MapValues _
        | SemanticNodeKind.MapKeys _
        | SemanticNodeKind.SetToList _
        | SemanticNodeKind.MapSize _
        | SemanticNodeKind.SetFromList _
        | SemanticNodeKind.SetSize _
        | SemanticNodeKind.ListReverse _
        | SemanticNodeKind.ListLast _
        | SemanticNodeKind.ListDropLast _
        | SemanticNodeKind.ListAppendItem _
        | SemanticNodeKind.ListItemAt _
        | SemanticNodeKind.MapRemove _
        | SemanticNodeKind.SetInsert _
        | SemanticNodeKind.SetRemove _
        | SemanticNodeKind.SetUnion _
        | SemanticNodeKind.SetIntersection _
        | SemanticNodeKind.SetDifference _
        | SemanticNodeKind.SetIsSubset _
        | SemanticNodeKind.MapUnion _
        | SemanticNodeKind.MapContainsKey _
        | SemanticNodeKind.SetContains _
        | SemanticNodeKind.ListIndexOf _
        | SemanticNodeKind.ListLastIndexOf _
        | SemanticNodeKind.MapGet _
        | SemanticNodeKind.OptionGetOrDefault _
        | SemanticNodeKind.MapGetOrDefault _
        | SemanticNodeKind.ListSlice _
        | SemanticNodeKind.ListSetAt _
        | SemanticNodeKind.ListInsertAt _
        | SemanticNodeKind.ListRemoveAt _
        | SemanticNodeKind.MapInsert _
        | SemanticNodeKind.ListConcat _
        | SemanticNodeKind.ListFold _
        | SemanticNodeKind.MapFold _
        | SemanticNodeKind.ParallelAll _
        | SemanticNodeKind.CancellationRequested
        | SemanticNodeKind.UseResource _
        | SemanticNodeKind.ExceptionCatch _
        | SemanticNodeKind.ExceptionIsCancellation _
        | SemanticNodeKind.ExceptionMessage _
        | SemanticNodeKind.CancellationShield _
        | SemanticNodeKind.TaskScope _
        | SemanticNodeKind.TaskStart _
        | SemanticNodeKind.TaskWait _
        | SemanticNodeKind.OneShotComplete _
        | SemanticNodeKind.OneShotWait _
        | SemanticNodeKind.TaskCancel _
        | SemanticNodeKind.ListEffectFold _
        | SemanticNodeKind.ListZipWith _
        | SemanticNodeKind.ListFlatMap _
        | SemanticNodeKind.ListMap _
        | SemanticNodeKind.MapFromList _
        | SemanticNodeKind.ListFilter _
        | SemanticNodeKind.MapFilter _
        | SemanticNodeKind.ListSortBy _
        | SemanticNodeKind.OptionMap _
        | SemanticNodeKind.OptionAndThen _
        | SemanticNodeKind.ResultMap _
        | SemanticNodeKind.ResultMapErr _
        | SemanticNodeKind.ResultAndThen _
        | SemanticNodeKind.MapMapValues _
        | SemanticNodeKind.ListMinBy _
        | SemanticNodeKind.ListAny _
        | SemanticNodeKind.ListFindIndex _
        | SemanticNodeKind.ListAll _
        | SemanticNodeKind.DirectCall _
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _
        | SemanticNodeKind.FunctionInvoke _
        | SemanticNodeKind.CapabilityInvoke _
        | SemanticNodeKind.ProductConstruct _
        | SemanticNodeKind.FieldAccess _
        | SemanticNodeKind.ProductUpdate _
        | SemanticNodeKind.UnionConstruct _
        | SemanticNodeKind.OptionSome _
        | SemanticNodeKind.Abort _
        | SemanticNodeKind.BoolNot _
        | SemanticNodeKind.BoolAnd _
        | SemanticNodeKind.BoolOr _
        | SemanticNodeKind.Conditional _
        | SemanticNodeKind.MatchUnion _
        | SemanticNodeKind.Bind _
        | SemanticNodeKind.MatchOption _
        | SemanticNodeKind.MatchResult _ -> [], node.Kind

    /// The types a node states in its own payload, outside pattern bindings (see `nestedScopes`)
    /// and nested graphs. Type-parameter checks and fingerprint normalization cover exactly these.
    let payloadTypeRefs (node: SemanticNode): TypeRef list = typeRefPayload id node |> fst

    /// The same node with every payload type replaced; pattern bindings, nested graphs and value
    /// references are untouched (`mapNested` rewrites bindings).
    let mapPayloadTypeRefs (mapTypeRef: TypeRef -> TypeRef) (node: SemanticNode): SemanticNode =
        { node with Kind = typeRefPayload mapTypeRef node |> snd }

    /// The one list of the functions a node names and the capability operations it invokes.
    let private symbolReferences (node: SemanticNode): NodeFunctionReference list * CapabilityOperationId list =
        let reference role arguments functionId =
            { FunctionId = functionId; Role = role; Arguments = arguments }
        match node.Kind with
        | SemanticNodeKind.DirectCall(functionId, arguments) ->
            [ reference FunctionReferenceRole.DirectCall arguments functionId ], []
        | SemanticNodeKind.ListFold(_, _, functionId, context)
        | SemanticNodeKind.MapFold(_, _, functionId, context)
        | SemanticNodeKind.ListZipWith(_, _, functionId, context)
        | SemanticNodeKind.ListFlatMap(_, functionId, context)
        | SemanticNodeKind.ListMap(_, functionId, context)
        | SemanticNodeKind.MapFromList(_, functionId, context)
        | SemanticNodeKind.ListFilter(_, functionId, context)
        | SemanticNodeKind.MapFilter(_, functionId, context)
        | SemanticNodeKind.ListSortBy(_, functionId, context)
        | SemanticNodeKind.OptionMap(_, functionId, context)
        | SemanticNodeKind.OptionAndThen(_, functionId, context)
        | SemanticNodeKind.ResultMap(_, functionId, context)
        | SemanticNodeKind.ResultMapErr(_, functionId, context)
        | SemanticNodeKind.ResultAndThen(_, functionId, context)
        | SemanticNodeKind.MapMapValues(_, functionId, context)
        | SemanticNodeKind.ListMinBy(_, functionId, context)
        | SemanticNodeKind.ListAny(_, functionId, context)
        | SemanticNodeKind.ListFindIndex(_, functionId, context)
        | SemanticNodeKind.ListAll(_, functionId, context) ->
            [ reference FunctionReferenceRole.PureListCallback context functionId ], []
        | SemanticNodeKind.ListEffectFold(_, _, functionId, context) ->
            [ reference FunctionReferenceRole.EffectListCallback context functionId ], []
        | SemanticNodeKind.ParallelAll(_, functionId, context)
        | SemanticNodeKind.TaskStart(_, functionId, context, _) -> [ reference FunctionReferenceRole.Worker context functionId ], []
        | SemanticNodeKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            [ reference FunctionReferenceRole.Body bodyContext body; reference FunctionReferenceRole.Body handlerContext handler ], []
        | SemanticNodeKind.CancellationShield(functionId, context) -> [ reference FunctionReferenceRole.Body context functionId ], []
        | SemanticNodeKind.TaskScope(functionId, context) -> [ reference FunctionReferenceRole.Body context functionId ], []
        | SemanticNodeKind.UseResource(_, _, releaseOperationId, functionId, context) ->
            [ reference FunctionReferenceRole.Body context functionId ], [ releaseOperationId ]
        | SemanticNodeKind.FunctionReference functionId -> [ reference FunctionReferenceRole.FunctionValue [] functionId ], []
        | SemanticNodeKind.CapabilityConstruct(_, implementations) ->
            implementations
            |> List.map (fun (operationId, functionId) ->
                reference (FunctionReferenceRole.CapabilityImplementation operationId) [] functionId),
            []
        | SemanticNodeKind.CapabilityInvoke(_, operationId, _) -> [], [ operationId ]
        | SemanticNodeKind.ExceptionIsCancellation _
        | SemanticNodeKind.ExceptionMessage _
        | SemanticNodeKind.ExceptionRethrow _
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.StringIsEmpty _
        | SemanticNodeKind.Int64ToString _
        | SemanticNodeKind.Int64ToHexString _
        | SemanticNodeKind.StringParseInt64 _
        | SemanticNodeKind.StringParseInt64Hex _
        | SemanticNodeKind.Float64ToString _
        | SemanticNodeKind.StringParseFloat64 _
        | SemanticNodeKind.StringCodePoints _
        | SemanticNodeKind.StringToLower _
        | SemanticNodeKind.StringToUpper _
        | SemanticNodeKind.StringDecodeUtf8Lossy _
        | SemanticNodeKind.Float64RoundToInt64 _
        | SemanticNodeKind.Float64FloorToInt64 _
        | SemanticNodeKind.Float64Sqrt _
        | SemanticNodeKind.Float64Abs _
        | SemanticNodeKind.Float64Negate _
        | SemanticNodeKind.Float64Floor _
        | SemanticNodeKind.Float64Ceiling _
        | SemanticNodeKind.Float64Truncate _
        | SemanticNodeKind.StringUtf8ByteCount _
        | SemanticNodeKind.StringFromCodePoint _
        | SemanticNodeKind.StringToCodePoint _
        | SemanticNodeKind.StringLength _
        | SemanticNodeKind.StringSlice _
        | SemanticNodeKind.StringContains _
        | SemanticNodeKind.StringIndexOf _
        | SemanticNodeKind.StringSpan _
        | SemanticNodeKind.StringSpanBack _
        | SemanticNodeKind.StringLastIndexOf _
        | SemanticNodeKind.StringStartsWith _
        | SemanticNodeKind.StringEndsWith _
        | SemanticNodeKind.StringSplit _
        | SemanticNodeKind.StringSplitLines _
        | SemanticNodeKind.StringReplace _
        | SemanticNodeKind.StringTrim _
        | SemanticNodeKind.StringTrimStart _
        | SemanticNodeKind.StringTrimEnd _
        | SemanticNodeKind.StringJoin _
        | SemanticNodeKind.StringEqual _
        | SemanticNodeKind.StringLessThan _
        | SemanticNodeKind.ValueEqual _
        | SemanticNodeKind.StringConcat _
        | SemanticNodeKind.Int64WrappingAdd _
        | SemanticNodeKind.Int64WrappingSubtract _
        | SemanticNodeKind.Int64WrappingMultiply _
        | SemanticNodeKind.Int64FloorDivide _
        | SemanticNodeKind.Int64FloorModulo _
        | SemanticNodeKind.Int64Min _
        | SemanticNodeKind.Int64Max _
        | SemanticNodeKind.Int64BitAnd _
        | SemanticNodeKind.Int64BitOr _
        | SemanticNodeKind.Int64BitXor _
        | SemanticNodeKind.Int64ShiftLeft _
        | SemanticNodeKind.Int64ShiftRight _
        | SemanticNodeKind.Int64CheckedAdd _
        | SemanticNodeKind.Int64CheckedSubtract _
        | SemanticNodeKind.Int64CheckedMultiply _
        | SemanticNodeKind.ListRange _
        | SemanticNodeKind.StringRepeat _
        | SemanticNodeKind.Float64Round _
        | SemanticNodeKind.Int64Equal _
        | SemanticNodeKind.Int64LessThan _
        | SemanticNodeKind.Int64LessThanOrEqual _
        | SemanticNodeKind.Int64GreaterThan _
        | SemanticNodeKind.Int64GreaterThanOrEqual _
        | SemanticNodeKind.Float64Add _
        | SemanticNodeKind.Float64Min _
        | SemanticNodeKind.Float64Max _
        | SemanticNodeKind.Float64Subtract _
        | SemanticNodeKind.Float64Multiply _
        | SemanticNodeKind.Float64Divide _
        | SemanticNodeKind.Float64Equal _
        | SemanticNodeKind.Float64LessThan _
        | SemanticNodeKind.Float64IsNaN _
        | SemanticNodeKind.Float64IsFinite _
        | SemanticNodeKind.Float64IsInfinite _
        | SemanticNodeKind.Int64ToFloat64 _
        | SemanticNodeKind.Int64WrappingAbs _
        | SemanticNodeKind.Int64BitNot _
        | SemanticNodeKind.Bits32FromInt64 _
        | SemanticNodeKind.Bits32ToInt64 _
        | SemanticNodeKind.Bits32Xor _
        | SemanticNodeKind.Bits32And _
        | SemanticNodeKind.Bits32Or _
        | SemanticNodeKind.Bits32Not _
        | SemanticNodeKind.Bits32Add _
        | SemanticNodeKind.Bits32RotateRight _
        | SemanticNodeKind.Bits32ShiftRight _
        | SemanticNodeKind.Bits32ShiftLeft _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.ListConstruct _
        | SemanticNodeKind.ListLength _
        | SemanticNodeKind.MapValues _
        | SemanticNodeKind.MapKeys _
        | SemanticNodeKind.SetToList _
        | SemanticNodeKind.MapSize _
        | SemanticNodeKind.SetFromList _
        | SemanticNodeKind.SetSize _
        | SemanticNodeKind.ListReverse _
        | SemanticNodeKind.ListLast _
        | SemanticNodeKind.ListDropLast _
        | SemanticNodeKind.ListAppendItem _
        | SemanticNodeKind.ListItemAt _
        | SemanticNodeKind.MapRemove _
        | SemanticNodeKind.SetInsert _
        | SemanticNodeKind.SetRemove _
        | SemanticNodeKind.SetUnion _
        | SemanticNodeKind.SetIntersection _
        | SemanticNodeKind.SetDifference _
        | SemanticNodeKind.SetIsSubset _
        | SemanticNodeKind.MapUnion _
        | SemanticNodeKind.MapContainsKey _
        | SemanticNodeKind.SetContains _
        | SemanticNodeKind.ListIndexOf _
        | SemanticNodeKind.ListLastIndexOf _
        | SemanticNodeKind.MapGet _
        | SemanticNodeKind.OptionGetOrDefault _
        | SemanticNodeKind.MapGetOrDefault _
        | SemanticNodeKind.ListSlice _
        | SemanticNodeKind.ListSetAt _
        | SemanticNodeKind.ListInsertAt _
        | SemanticNodeKind.ListRemoveAt _
        | SemanticNodeKind.MapInsert _
        | SemanticNodeKind.ListConcat _
        | SemanticNodeKind.CancellationRequested
        | SemanticNodeKind.TaskWait _
        | SemanticNodeKind.OneShotCreate _
        | SemanticNodeKind.OneShotComplete _
        | SemanticNodeKind.OneShotWait _
        | SemanticNodeKind.TaskCancel _
        | SemanticNodeKind.FunctionInvoke _
        | SemanticNodeKind.ProductConstruct _
        | SemanticNodeKind.FieldAccess _
        | SemanticNodeKind.ProductUpdate _
        | SemanticNodeKind.UnionConstruct _
        | SemanticNodeKind.OptionNone _
        | SemanticNodeKind.OptionSome _
        | SemanticNodeKind.ResultOk _
        | SemanticNodeKind.Abort _
        | SemanticNodeKind.ResultErr _
        | SemanticNodeKind.BoolNot _
        | SemanticNodeKind.BoolAnd _
        | SemanticNodeKind.BoolOr _
        | SemanticNodeKind.Conditional _
        | SemanticNodeKind.MatchUnion _
        | SemanticNodeKind.Bind _
        | SemanticNodeKind.MatchOption _
        | SemanticNodeKind.MatchResult _ -> [], []

    /// Every function a node names, in payload order, with its role. Nodes inside nested graphs are
    /// not included; callers walk them with `ValueGraph.collectNodes` or `nestedGraphs`.
    let functionReferences (node: SemanticNode): NodeFunctionReference list = symbolReferences node |> fst

    /// The capability operations a node itself invokes (CapabilityInvoke and the UseResource
    /// release). The operations a CapabilityConstruct implements are roles in `functionReferences`.
    let invokedOperations (node: SemanticNode): CapabilityOperationId list = symbolReferences node |> snd

    /// Whether the node itself performs an effect or ambient behavior (a capability call, a resource
    /// scope, concurrency or cancellation), apart from any function it names. Calls and callbacks
    /// are effectful through their target, which callers follow with `functionReferences`. This is the
    /// purity rule for function references (`PureFunctions`); `GraphOrder` and the typed IR's
    /// effect-order analysis treat ParallelAll through its worker instead.
    let isIntrinsicallyEffectful (node: SemanticNode): bool =
        match node.Kind with
        | SemanticNodeKind.ParallelAll _
        | SemanticNodeKind.CancellationRequested
        | SemanticNodeKind.UseResource _
        | SemanticNodeKind.ExceptionCatch _
        | SemanticNodeKind.ExceptionRethrow _
        | SemanticNodeKind.CancellationShield _
        | SemanticNodeKind.TaskScope _
        | SemanticNodeKind.TaskStart _
        | SemanticNodeKind.TaskWait _
        | SemanticNodeKind.OneShotCreate _
        | SemanticNodeKind.OneShotComplete _
        | SemanticNodeKind.OneShotWait _
        | SemanticNodeKind.TaskCancel _
        | SemanticNodeKind.CapabilityInvoke _ -> true
        | SemanticNodeKind.ExceptionIsCancellation _
        | SemanticNodeKind.ExceptionMessage _
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.StringIsEmpty _
        | SemanticNodeKind.Int64ToString _
        | SemanticNodeKind.Int64ToHexString _
        | SemanticNodeKind.StringParseInt64 _
        | SemanticNodeKind.StringParseInt64Hex _
        | SemanticNodeKind.Float64ToString _
        | SemanticNodeKind.StringParseFloat64 _
        | SemanticNodeKind.StringCodePoints _
        | SemanticNodeKind.StringToLower _
        | SemanticNodeKind.StringToUpper _
        | SemanticNodeKind.StringDecodeUtf8Lossy _
        | SemanticNodeKind.Float64RoundToInt64 _
        | SemanticNodeKind.Float64FloorToInt64 _
        | SemanticNodeKind.Float64Sqrt _
        | SemanticNodeKind.Float64Abs _
        | SemanticNodeKind.Float64Negate _
        | SemanticNodeKind.Float64Floor _
        | SemanticNodeKind.Float64Ceiling _
        | SemanticNodeKind.Float64Truncate _
        | SemanticNodeKind.StringUtf8ByteCount _
        | SemanticNodeKind.StringFromCodePoint _
        | SemanticNodeKind.StringToCodePoint _
        | SemanticNodeKind.StringLength _
        | SemanticNodeKind.StringSlice _
        | SemanticNodeKind.StringContains _
        | SemanticNodeKind.StringIndexOf _
        | SemanticNodeKind.StringSpan _
        | SemanticNodeKind.StringSpanBack _
        | SemanticNodeKind.StringLastIndexOf _
        | SemanticNodeKind.StringStartsWith _
        | SemanticNodeKind.StringEndsWith _
        | SemanticNodeKind.StringSplit _
        | SemanticNodeKind.StringSplitLines _
        | SemanticNodeKind.StringReplace _
        | SemanticNodeKind.StringTrim _
        | SemanticNodeKind.StringTrimStart _
        | SemanticNodeKind.StringTrimEnd _
        | SemanticNodeKind.StringJoin _
        | SemanticNodeKind.StringEqual _
        | SemanticNodeKind.StringLessThan _
        | SemanticNodeKind.ValueEqual _
        | SemanticNodeKind.StringConcat _
        | SemanticNodeKind.Int64WrappingAdd _
        | SemanticNodeKind.Int64WrappingSubtract _
        | SemanticNodeKind.Int64WrappingMultiply _
        | SemanticNodeKind.Int64FloorDivide _
        | SemanticNodeKind.Int64FloorModulo _
        | SemanticNodeKind.Int64Min _
        | SemanticNodeKind.Int64Max _
        | SemanticNodeKind.Int64BitAnd _
        | SemanticNodeKind.Int64BitOr _
        | SemanticNodeKind.Int64BitXor _
        | SemanticNodeKind.Int64ShiftLeft _
        | SemanticNodeKind.Int64ShiftRight _
        | SemanticNodeKind.Int64CheckedAdd _
        | SemanticNodeKind.Int64CheckedSubtract _
        | SemanticNodeKind.Int64CheckedMultiply _
        | SemanticNodeKind.ListRange _
        | SemanticNodeKind.StringRepeat _
        | SemanticNodeKind.Float64Round _
        | SemanticNodeKind.Int64Equal _
        | SemanticNodeKind.Int64LessThan _
        | SemanticNodeKind.Int64LessThanOrEqual _
        | SemanticNodeKind.Int64GreaterThan _
        | SemanticNodeKind.Int64GreaterThanOrEqual _
        | SemanticNodeKind.Float64Add _
        | SemanticNodeKind.Float64Min _
        | SemanticNodeKind.Float64Max _
        | SemanticNodeKind.Float64Subtract _
        | SemanticNodeKind.Float64Multiply _
        | SemanticNodeKind.Float64Divide _
        | SemanticNodeKind.Float64Equal _
        | SemanticNodeKind.Float64LessThan _
        | SemanticNodeKind.Float64IsNaN _
        | SemanticNodeKind.Float64IsFinite _
        | SemanticNodeKind.Float64IsInfinite _
        | SemanticNodeKind.Int64ToFloat64 _
        | SemanticNodeKind.Int64WrappingAbs _
        | SemanticNodeKind.Int64BitNot _
        | SemanticNodeKind.Bits32FromInt64 _
        | SemanticNodeKind.Bits32ToInt64 _
        | SemanticNodeKind.Bits32Xor _
        | SemanticNodeKind.Bits32And _
        | SemanticNodeKind.Bits32Or _
        | SemanticNodeKind.Bits32Not _
        | SemanticNodeKind.Bits32Add _
        | SemanticNodeKind.Bits32RotateRight _
        | SemanticNodeKind.Bits32ShiftRight _
        | SemanticNodeKind.Bits32ShiftLeft _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.ListConstruct _
        | SemanticNodeKind.ListLength _
        | SemanticNodeKind.MapValues _
        | SemanticNodeKind.MapKeys _
        | SemanticNodeKind.SetToList _
        | SemanticNodeKind.MapSize _
        | SemanticNodeKind.SetFromList _
        | SemanticNodeKind.SetSize _
        | SemanticNodeKind.ListReverse _
        | SemanticNodeKind.ListLast _
        | SemanticNodeKind.ListDropLast _
        | SemanticNodeKind.ListAppendItem _
        | SemanticNodeKind.ListItemAt _
        | SemanticNodeKind.MapRemove _
        | SemanticNodeKind.SetInsert _
        | SemanticNodeKind.SetRemove _
        | SemanticNodeKind.SetUnion _
        | SemanticNodeKind.SetIntersection _
        | SemanticNodeKind.SetDifference _
        | SemanticNodeKind.SetIsSubset _
        | SemanticNodeKind.MapUnion _
        | SemanticNodeKind.MapContainsKey _
        | SemanticNodeKind.SetContains _
        | SemanticNodeKind.ListIndexOf _
        | SemanticNodeKind.ListLastIndexOf _
        | SemanticNodeKind.MapGet _
        | SemanticNodeKind.OptionGetOrDefault _
        | SemanticNodeKind.MapGetOrDefault _
        | SemanticNodeKind.ListSlice _
        | SemanticNodeKind.ListSetAt _
        | SemanticNodeKind.ListInsertAt _
        | SemanticNodeKind.ListRemoveAt _
        | SemanticNodeKind.MapInsert _
        | SemanticNodeKind.ListConcat _
        | SemanticNodeKind.ListFold _
        | SemanticNodeKind.MapFold _
        | SemanticNodeKind.ListEffectFold _
        | SemanticNodeKind.ListZipWith _
        | SemanticNodeKind.ListFlatMap _
        | SemanticNodeKind.ListMap _
        | SemanticNodeKind.MapFromList _
        | SemanticNodeKind.ListFilter _
        | SemanticNodeKind.MapFilter _
        | SemanticNodeKind.ListSortBy _
        | SemanticNodeKind.OptionMap _
        | SemanticNodeKind.OptionAndThen _
        | SemanticNodeKind.ResultMap _
        | SemanticNodeKind.ResultMapErr _
        | SemanticNodeKind.ResultAndThen _
        | SemanticNodeKind.MapMapValues _
        | SemanticNodeKind.ListMinBy _
        | SemanticNodeKind.ListAny _
        | SemanticNodeKind.ListFindIndex _
        | SemanticNodeKind.ListAll _
        | SemanticNodeKind.DirectCall _
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _
        | SemanticNodeKind.FunctionInvoke _
        | SemanticNodeKind.ProductConstruct _
        | SemanticNodeKind.FieldAccess _
        | SemanticNodeKind.ProductUpdate _
        | SemanticNodeKind.UnionConstruct _
        | SemanticNodeKind.OptionNone _
        | SemanticNodeKind.OptionSome _
        | SemanticNodeKind.ResultOk _
        | SemanticNodeKind.Abort _
        | SemanticNodeKind.ResultErr _
        | SemanticNodeKind.BoolNot _
        | SemanticNodeKind.BoolAnd _
        | SemanticNodeKind.BoolOr _
        | SemanticNodeKind.Conditional _
        | SemanticNodeKind.MatchUnion _
        | SemanticNodeKind.Bind _
        | SemanticNodeKind.MatchOption _
        | SemanticNodeKind.MatchResult _ -> false

    /// The target and argument bindings of a DirectCall node; None for every other kind.
    let tryDirectCall (node: SemanticNode): (FunctionId * ArgumentBinding list) option =
        match node.Kind with
        | SemanticNodeKind.DirectCall(functionId, arguments) -> Some(functionId, arguments)
        | _ -> None

    /// A node depends on its direct value references and on every node its nested graphs
    /// reference from outside themselves, the same edges IR lowering orders by. A nested
    /// graph's local nodes are not dependencies of the enclosing node.
    let rec dependencies (node: SemanticNode): GraphNodeId list =
        directValueReferences node
        @ (nestedGraphs node |> List.collect freeReferences)
        |> List.distinct

    /// The nodes a graph references that it does not own, through any nesting depth.
    and freeReferences (graph: ValueGraph): GraphNodeId list =
        let localIds = graph.Nodes |> List.map (fun node -> node.Id) |> Set.ofList
        graph.Nodes |> List.collect dependencies |> List.filter (fun id -> not (Set.contains id localIds))

[<RequireQualifiedAccess>]
module ValueGraph =
    /// Every node of the graph and of its nested graphs, each node before the nodes it owns.
    let rec collectNodes (graph: ValueGraph): SemanticNode list =
        graph.Nodes
        |> List.collect (fun node -> node :: (SemanticNodeShape.nestedGraphs node |> List.collect collectNodes))

    /// Transforms every node of the graph and of its nested graphs, innermost graphs first.
    let rec mapNodes (transform: SemanticNode -> SemanticNode) (graph: ValueGraph): ValueGraph =
        { graph with
            Nodes =
                graph.Nodes
                |> List.map (fun node -> transform (SemanticNodeShape.mapNestedGraphs (mapNodes transform) node)) }

[<RequireQualifiedAccess>]
module Snapshot =
    let empty id =
        { Id = id
          ParentId = None
          Types = []
          Effects = []
          Capabilities = []
          Functions = []
          Obligations = [] }

/// Task and OneShot handles and captured Exceptions exist only inside one run. Declared fields may hold these handles
/// but not TaskScope, which is lexical. Runtime-bearing types, including through declared types,
/// never cross a capability or runPure boundary.
[<RequireQualifiedAccess>]
module TaskTypes =
    let rec private occurs (matches: BuiltinGenericType -> bool) (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.BuiltinGeneric(kind, _) when matches kind -> true
        | TypeRef.BuiltinGeneric(_, arguments) -> List.exists (occurs matches) arguments
        | TypeRef.Function(parameters, result) -> List.exists (occurs matches) parameters || occurs matches result
        | TypeRef.Primitive _
        | TypeRef.Named _
        | TypeRef.Capability _
        | TypeRef.TypeParameter _ -> false

    /// Whether the type itself mentions a runtime handle, captured exception or TaskScope, without inspecting declared types.
    let mentions (typeRef: TypeRef) =
        occurs (function BuiltinGenericType.Exception | BuiltinGenericType.OneShot | BuiltinGenericType.Task | BuiltinGenericType.TaskScope -> true | _ -> false) typeRef

    /// Whether the type itself mentions a captured exception.
    let mentionsException (typeRef: TypeRef) = occurs ((=) BuiltinGenericType.Exception) typeRef

    /// Whether the type itself mentions OneShot, without looking into declared types.
    let mentionsOneShot (typeRef: TypeRef) =
        occurs ((=) BuiltinGenericType.OneShot) typeRef

    /// Whether the type itself mentions TaskScope, without looking into declared types.
    let mentionsScope (typeRef: TypeRef) =
        occurs (function BuiltinGenericType.TaskScope -> true | _ -> false) typeRef

    /// Whether a value of the type can hold a task handle or scope: the type mentions one, or reaches
    /// a declared Product or Union whose fields do, through any nesting. Recursive and unknown named
    /// types are handled; each named type is inspected once.
    let bearing (types: SemanticType list) (typeRef: TypeRef) =
        let byId = types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let visited = System.Collections.Generic.HashSet<TypeId>()
        let rec named (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Named typeId -> [ typeId ]
            | TypeRef.BuiltinGeneric(_, arguments) -> List.collect named arguments
            | TypeRef.Function(parameters, result) -> List.collect named (result :: parameters)
            | TypeRef.Primitive _
            | TypeRef.Capability _
            | TypeRef.TypeParameter _ -> []
        let rec visit (typeRef: TypeRef) =
            mentions typeRef
            || named typeRef
               |> List.exists (fun typeId ->
                   visited.Add typeId
                   && (match Map.tryFind typeId byId with
                       | None -> false
                       | Some typeEntity ->
                           let fields =
                               match typeEntity.Kind with
                               | SemanticTypeKind.Product fields -> fields
                               | SemanticTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields)
                           fields |> List.exists (fun field -> visit field.Type)))
        visit typeRef

/// `ValueEqual` compares two values of one *equatable* type: String, Int64, Bool, Unit, Bits32,
/// `List`, `Option` and `Result` of equatable types, and declared Products and Unions whose fields
/// are all equatable, through any nesting and including recursive types. Float64 (NaN; use
/// Float64Equal), function types, Task, TaskScope, Map, Set, capabilities, type parameters and
/// anything reaching them are not equatable. A type parameter is never equatable, because nothing
/// constrains its type arguments (there is no `T : equality`), so a Set item or Map key type is
/// always concrete and a generic function cannot take or return `Set<T>` or `Map<T, _>`.
[<RequireQualifiedAccess>]
module EquatableTypes =
    /// Presentation text of a type using display names; unknown named types show their StableId.
    let rec describe (types: SemanticType list) (typeRef: TypeRef) =
        let go = describe types
        match typeRef with
        | TypeRef.Primitive primitive -> string primitive
        | TypeRef.Named typeId ->
            types
            |> List.tryFind (fun item -> item.Id = typeId)
            |> Option.map (fun item -> item.DisplayName)
            |> Option.defaultValue (StableId.value typeId)
        | TypeRef.Capability capabilityId -> $"capability '{StableId.value capabilityId}'"
        | TypeRef.Function(parameters, result) ->
            "(" + (parameters |> List.map go |> String.concat ", ") + ") -> " + go result
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, arguments) -> "OneShot<" + (arguments |> List.map go |> String.concat ", ") + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, _) -> "TaskScope"
        | TypeRef.BuiltinGeneric(kind, arguments) -> $"{kind}<" + (arguments |> List.map go |> String.concat ", ") + ">"
        | TypeRef.TypeParameter(_, displayName) -> displayName

    /// The first part that makes the type not equatable, as a path of display-name steps from the
    /// type to that part (empty when the type itself is the part) and the offending type; `None`
    /// when the type is equatable. A declared type under inspection counts as equatable where it
    /// recurs, so recursive types are equatable exactly when their fields are; unknown named types
    /// are left to the declaration checks.
    let firstNotEquatable (types: SemanticType list) (typeRef: TypeRef) : (string list * TypeRef) option =
        let byId = types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let visited = System.Collections.Generic.HashSet<TypeId>()
        let rec visit (path: string list) (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Primitive(PrimitiveType.String | PrimitiveType.Int64 | PrimitiveType.Bool | PrimitiveType.Unit | PrimitiveType.Bits32) -> None
            | TypeRef.Primitive PrimitiveType.Float64
            | TypeRef.Function _
            | TypeRef.Capability _
            | TypeRef.TypeParameter _
            | TypeRef.BuiltinGeneric((BuiltinGenericType.Exception | BuiltinGenericType.OneShot | BuiltinGenericType.Task | BuiltinGenericType.TaskScope | BuiltinGenericType.Map | BuiltinGenericType.Set), _) -> Some(List.rev path, typeRef)
            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
                arguments |> List.tryPick (visit ("List item" :: path))
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
                arguments |> List.tryPick (visit ("Option value" :: path))
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
                arguments
                |> List.mapi (fun index argument -> (if index = 0 then "Result ok" else "Result err"), argument)
                |> List.tryPick (fun (step, argument) -> visit (step :: path) argument)
            | TypeRef.Named typeId ->
                if not (visited.Add typeId) then
                    None
                else
                    match Map.tryFind typeId byId with
                    | None -> None
                    | Some typeEntity ->
                        match typeEntity.Kind with
                        | SemanticTypeKind.Product fields ->
                            fields
                            |> List.tryPick (fun field -> visit ($"{typeEntity.DisplayName}.{field.DisplayName}" :: path) field.Type)
                        | SemanticTypeKind.Union variants ->
                            variants
                            |> List.tryPick (fun variant ->
                                variant.Fields
                                |> List.tryPick (fun field ->
                                    visit ($"{typeEntity.DisplayName}.{variant.DisplayName}.{field.DisplayName}" :: path) field.Type))
        visit [] typeRef

    let isEquatable (types: SemanticType list) (typeRef: TypeRef) =
        (firstNotEquatable types typeRef).IsNone

    /// Diagnostic text: why the type is not equatable, naming the path to the offending part.
    let explain (types: SemanticType list) (typeRef: TypeRef) =
        match firstNotEquatable types typeRef with
        | None -> $"{describe types typeRef} is equatable."
        | Some(path, offending) ->
            let reason =
                match offending with
                | TypeRef.Primitive PrimitiveType.Float64 -> "Float64 is not equatable because of NaN; compare with Float64Equal"
                | TypeRef.Function _ -> "function values are not equatable"
                | TypeRef.BuiltinGeneric((BuiltinGenericType.Exception | BuiltinGenericType.OneShot | BuiltinGenericType.Task | BuiltinGenericType.TaskScope), _) -> "runtime handles and scopes are not equatable"
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, _) -> "maps are not equatable"
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, _) -> "sets are not equatable"
                | TypeRef.Capability _ -> "capability values are not equatable"
                | TypeRef.TypeParameter _ -> "type parameters are never equatable, because nothing constrains their type arguments, so a Set item or Map key type must be concrete"
                | _ -> "it is not equatable"
            let location =
                match path with
                | [] -> ""
                | steps -> " at " + String.concat " -> " steps
            $"{describe types typeRef} is not equatable: {describe types offending}{location}; {reason}."

/// Function type parameters. A function's type parameters are those occurring in its parameter
/// types; at a call or callback, type arguments are derived by matching each parameter type
/// against the concrete argument type, so no inference beyond structural matching is involved.
[<RequireQualifiedAccess>]
module TypeParameters =
    /// Type parameters occurring in a type, in first-occurrence order.
    let occurring (typeRef: TypeRef): (TypeParameterId * string) list =
        let rec collect (collected: (TypeParameterId * string) list) current =
            match current with
            | TypeRef.TypeParameter(id, name) when collected |> List.exists (fun (seen, _) -> seen = id) -> collected
            | TypeRef.TypeParameter(id, name) -> collected @ [ id, name ]
            | TypeRef.Function(arguments, result) -> (arguments @ [ result ]) |> List.fold collect collected
            | TypeRef.BuiltinGeneric(_, arguments) -> arguments |> List.fold collect collected
            | TypeRef.Primitive _
            | TypeRef.Named _
            | TypeRef.Capability _ -> collected
        collect [] typeRef

    /// Type parameters declared by a function's parameter types, in first-occurrence order.
    let ofFunction (functionEntity: SemanticFunction): (TypeParameterId * string) list =
        functionEntity.Parameters
        |> List.collect (fun parameter -> occurring parameter.Type)
        |> List.distinctBy fst

    let rec substitute (substitution: Map<TypeParameterId, TypeRef>) (typeRef: TypeRef): TypeRef =
        match typeRef with
        | TypeRef.TypeParameter(id, _) -> Map.tryFind id substitution |> Option.defaultValue typeRef
        | TypeRef.Function(arguments, result) -> TypeRef.Function(arguments |> List.map (substitute substitution), substitute substitution result)
        | TypeRef.BuiltinGeneric(kind, arguments) -> TypeRef.BuiltinGeneric(kind, arguments |> List.map (substitute substitution))
        | TypeRef.Primitive _
        | TypeRef.Named _
        | TypeRef.Capability _ -> typeRef

    /// Extends `substitution` so that `declared` becomes `actual`, or returns None on a conflict.
    let rec matchType (substitution: Map<TypeParameterId, TypeRef>) (declared: TypeRef) (actual: TypeRef) =
        match declared, actual with
        | TypeRef.TypeParameter(id, _), _ ->
            match Map.tryFind id substitution with
            | Some bound when bound = actual -> Some substitution
            | Some _ -> None
            | None -> Some(Map.add id actual substitution)
        | TypeRef.Function(declaredArguments, declaredResult), TypeRef.Function(actualArguments, actualResult) when declaredArguments.Length = actualArguments.Length ->
            List.zip (declaredArguments @ [ declaredResult ]) (actualArguments @ [ actualResult ])
            |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some substitution)
        | TypeRef.BuiltinGeneric(declaredKind, declaredArguments), TypeRef.BuiltinGeneric(actualKind, actualArguments)
            when declaredKind = actualKind && declaredArguments.Length = actualArguments.Length ->
            List.zip declaredArguments actualArguments
            |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some substitution)
        | _ when declared = actual -> Some substitution
        | _ -> None

    /// Extends `substitution` by matching declared types against actual types in order; None when any pair conflicts.
    let matchFrom (substitution: Map<TypeParameterId, TypeRef>) (pairs: (TypeRef * TypeRef) list) =
        pairs |> List.fold (fun state (declared, actual) -> state |> Option.bind (fun current -> matchType current declared actual)) (Some substitution)

    /// Matches declared parameter types against actual types in order; None when any pair conflicts.
    let matchAll (pairs: (TypeRef * TypeRef) list) = matchFrom Map.empty pairs
