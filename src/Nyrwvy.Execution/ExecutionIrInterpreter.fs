namespace Nyrwvy.Core

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.Runtime.CompilerServices
open System.Text

/// A run value of the Execution IR interpreter. Equality is structural and matches the C# backend's
/// EqualityComparer<T>.Default: Float64 uses double.Equals, List compares items in order, and Map and
/// Set compare their entries regardless of order.
[<CustomEquality; NoComparison>]
type IrValue =
    | IrUnit
    | IrBool of bool
    | IrInt of int64
    | IrFloat of float
    | IrBits of uint32
    | IrString of string
    | IrList of ImmutableList<IrValue>
    | IrMap of IrMapData
    | IrSet of IrSetData
    | IrOption of IrValue option
    | IrResult of isOk: bool * payload: IrValue
    /// Fields sorted by field ID.
    | IrProduct of (FieldId * IrValue)[]
    | IrUnion of VariantId * (FieldId * IrValue)[]
    | IrFunction of FunctionId
    /// Arguments arrive in the operation's parameter declaration order.
    | IrCapability of CapabilityId * invoke: (CapabilityOperationId -> IrValue list -> IrValue)
    | IrException of exn

    override this.Equals(other) =
        match other with
        | :? IrValue as other -> IrValue.Equal(this, other)
        | _ -> false

    override this.GetHashCode() = IrValue.Hash this

    static member private FieldsEqual(left: (FieldId * IrValue)[], right: (FieldId * IrValue)[]) =
        left.Length = right.Length
        && Array.forall2 (fun (leftId, leftValue) (rightId, rightValue) -> leftId = rightId && IrValue.Equal(leftValue, rightValue)) left right

    static member Equal(left: IrValue, right: IrValue) : bool =
        match left, right with
        | IrUnit, IrUnit -> true
        | IrBool a, IrBool b -> a = b
        | IrInt a, IrInt b -> a = b
        | IrFloat a, IrFloat b -> a.Equals b
        | IrBits a, IrBits b -> a = b
        | IrString a, IrString b -> String.Equals(a, b, StringComparison.Ordinal)
        | IrList a, IrList b -> a.Count = b.Count && Seq.forall2 (fun x y -> IrValue.Equal(x, y)) a b
        | IrMap a, IrMap b -> a.StructurallyEquals b
        | IrSet a, IrSet b -> a.StructurallyEquals b
        | IrOption a, IrOption b ->
            match a, b with
            | None, None -> true
            | Some x, Some y -> IrValue.Equal(x, y)
            | _ -> false
        | IrResult(okA, a), IrResult(okB, b) -> okA = okB && IrValue.Equal(a, b)
        | IrProduct a, IrProduct b -> IrValue.FieldsEqual(a, b)
        | IrUnion(variantA, a), IrUnion(variantB, b) -> variantA = variantB && IrValue.FieldsEqual(a, b)
        | IrFunction a, IrFunction b -> a = b
        | IrCapability(_, a), IrCapability(_, b) -> obj.ReferenceEquals(a, b)
        | IrException a, IrException b -> obj.ReferenceEquals(a, b)
        | _ -> false

    static member Hash(value: IrValue) : int =
        let fields (items: (FieldId * IrValue)[]) = items |> Array.fold (fun hash (_, item) -> HashCode.Combine(hash, IrValue.Hash item)) 17
        match value with
        | IrUnit -> 0
        | IrBool value -> value.GetHashCode()
        | IrInt value -> value.GetHashCode()
        | IrFloat value -> value.GetHashCode()
        | IrBits value -> value.GetHashCode()
        | IrString value -> StringComparer.Ordinal.GetHashCode value
        | IrList items -> items |> Seq.fold (fun hash item -> HashCode.Combine(hash, IrValue.Hash item)) 19
        | IrMap map -> map.StructuralHash()
        | IrSet set -> set.StructuralHash()
        | IrOption None -> 1
        | IrOption(Some item) -> HashCode.Combine(2, IrValue.Hash item)
        | IrResult(isOk, payload) -> HashCode.Combine(isOk, IrValue.Hash payload)
        | IrProduct items -> fields items
        | IrUnion(variantId, items) -> HashCode.Combine(variantId, fields items)
        | IrFunction id -> id.GetHashCode()
        | IrCapability(id, _) -> id.GetHashCode()
        | IrException error -> RuntimeHelpers.GetHashCode error

/// NyMap's persistent insertion-ordered map: an existing key keeps its position on Insert.
and IrMapData(entries: ImmutableDictionary<IrValue, struct (int64 * IrValue)>, order: ImmutableSortedDictionary<int64, IrValue>, next: int64) =
    static let empty = IrMapData(ImmutableDictionary.Create<IrValue, struct (int64 * IrValue)>(HashIdentity.Structural), ImmutableSortedDictionary<int64, IrValue>.Empty, 0L)
    static member Empty = empty
    member _.Size = int64 entries.Count
    member _.ContainsKey key = entries.ContainsKey key
    member _.TryGet key =
        match entries.TryGetValue key with
        | true, struct (_, value) -> Some value
        | _ -> None
    member _.Insert(key, value) =
        match entries.TryGetValue key with
        | true, struct (number, _) -> IrMapData(entries.SetItem(key, struct (number, value)), order, next)
        | _ -> IrMapData(entries.SetItem(key, struct (next, value)), order.Add(next, key), next + 1L)
    member this.Remove key =
        match entries.TryGetValue key with
        | true, struct (number, _) -> IrMapData(entries.Remove key, order.Remove number, next)
        | _ -> this
    member _.Entries = order.Values |> Seq.map (fun key -> let struct (_, value) = entries[key] in key, value)
    member this.Union(other: IrMapData) =
        if other.Size = 0L then this
        elif this.Size = 0L then other
        else other.Entries |> Seq.fold (fun (map: IrMapData) (key, value) -> map.Insert(key, value)) this
    member _.MapValues(mapper: IrValue -> IrValue -> IrValue) =
        let builder = ImmutableDictionary.CreateBuilder<IrValue, struct (int64 * IrValue)>(HashIdentity.Structural)
        for key in order.Values do
            let struct (number, value) = entries[key]
            builder.Add(key, struct (number, mapper key value))
        IrMapData(builder.ToImmutable(), order, next)
    member _.StructurallyEquals(other: IrMapData) =
        entries.Count = int other.Size
        && entries |> Seq.forall (fun pair ->
            let struct (_, value) = pair.Value
            match other.TryGet pair.Key with
            | Some otherValue -> IrValue.Equal(value, otherValue)
            | None -> false)
    member _.StructuralHash() =
        entries |> Seq.sumBy (fun pair -> let struct (_, value) = pair.Value in HashCode.Combine(IrValue.Hash pair.Key, IrValue.Hash value))

/// NySet's persistent insertion-ordered set.
and IrSetData(items: ImmutableDictionary<IrValue, int64>, order: ImmutableSortedDictionary<int64, IrValue>, next: int64) =
    static let empty = IrSetData(ImmutableDictionary.Create<IrValue, int64>(HashIdentity.Structural), ImmutableSortedDictionary<int64, IrValue>.Empty, 0L)
    static member Empty = empty
    member _.Size = int64 items.Count
    member _.Contains item = items.ContainsKey item
    member this.Insert item = if items.ContainsKey item then this else IrSetData(items.Add(item, next), order.Add(next, item), next + 1L)
    member this.Remove item =
        match items.TryGetValue item with
        | true, number -> IrSetData(items.Remove item, order.Remove number, next)
        | _ -> this
    member _.Items = order.Values
    member _.IsSubsetOf(other: IrSetData) = int64 items.Count <= other.Size && items.Keys |> Seq.forall other.Contains
    member this.StructurallyEquals(other: IrSetData) = this.Size = other.Size && this.IsSubsetOf other
    member _.StructuralHash() = items.Keys |> Seq.sumBy IrValue.Hash

/// A semantic frame the interpreter was running when an exception left it.
[<RequireQualifiedAccess>]
type IrFrame =
    | Function of FunctionId
    | CapabilityOperation of CapabilityOperationId

exception IrTimeoutException
exception IrStackOverflowException

/// NyText ported to F#: code-point positions, boundaries and Python-compatible layouts.
module internal IrText =
    let isBoundary (text: string) offset =
        offset <= 0 || offset >= text.Length || not (Char.IsHighSurrogate text[offset - 1] && Char.IsLowSurrogate text[offset])

    let width (text: string) index =
        if Char.IsHighSurrogate text[index] && index + 1 < text.Length && Char.IsLowSurrogate text[index + 1] then 2 else 1

    let codePointAt (text: string) (index: byref<int>) =
        let unit = text[index]
        if Char.IsHighSurrogate unit && index + 1 < text.Length && Char.IsLowSurrogate text[index + 1] then
            let scalar = Char.ConvertToUtf32(unit, text[index + 1])
            index <- index + 2
            scalar
        else
            index <- index + 1
            int unit

    let lessThan (left: string) (right: string) =
        let mutable i = 0
        let mutable j = 0
        let mutable decided = None
        while decided.IsNone && i < left.Length && j < right.Length do
            let a = codePointAt left &i
            let b = codePointAt right &j
            if a <> b then decided <- Some(a < b)
        defaultArg decided (i >= left.Length && j < right.Length)

    let codePointLength (text: string) =
        let mutable count = 0L
        let mutable index = 0
        while index < text.Length do
            index <- index + width text index
            count <- count + 1L
        count

    /// The UTF-16 offset of a code-point position clamped to [0, length]; never inside a pair.
    let offsetOf (text: string) (position: int64) =
        if position <= 0L then 0
        elif position >= int64 text.Length then text.Length
        else
            let mutable offset = 0
            let mutable reached = 0L
            while reached < position && offset < text.Length do
                offset <- offset + width text offset
                reached <- reached + 1L
            offset

    let positionOf (text: string) (offset: int) =
        if offset <= 0 then 0L
        else
            let stop = min offset text.Length
            let mutable index = 0
            let mutable count = 0L
            while index < stop do
                index <- index + width text index
                count <- count + 1L
            count

    let find (text: string) (value: string) (offset: int) =
        let mutable offset = offset
        let mutable result = -1
        let mutable searching = true
        while searching && offset <= text.Length - value.Length do
            let found = text.IndexOf(value, offset, StringComparison.Ordinal)
            if found < 0 then searching <- false
            elif isBoundary text found && isBoundary text (found + value.Length) then
                result <- found
                searching <- false
            else offset <- found + 1
        result

    let codePointSet (set: string) =
        let members = HashSet<int>()
        let mutable index = 0
        while index < set.Length do
            members.Add(codePointAt set &index) |> ignore
        members

    let indexOf (text: string) (value: string) (from: int64) =
        let position = min (max 0L from) (codePointLength text)
        let offset = offsetOf text position
        if value.Length = 0 then Some position
        else
            let found = find text value offset
            if found < 0 then None else Some(positionOf text found)

    let span (text: string) (set: string) (from: int64) =
        let mutable position = min (max 0L from) (codePointLength text)
        let mutable offset = offsetOf text position
        if set.Length = 0 then position
        else
            let members = codePointSet set
            let mutable going = true
            while going && offset < text.Length do
                let mutable next = offset
                if not (members.Contains(codePointAt text &next)) then going <- false
                else
                    offset <- next
                    position <- position + 1L
            position

    let spanBack (text: string) (set: string) (stop: int64) =
        let mutable position = min (max 0L stop) (codePointLength text)
        let mutable offset = offsetOf text position
        if set.Length = 0 then position
        else
            let members = codePointSet set
            let mutable going = true
            while going && offset > 0 do
                let previous = if offset >= 2 && Char.IsLowSurrogate text[offset - 1] && Char.IsHighSurrogate text[offset - 2] then offset - 2 else offset - 1
                let mutable probe = previous
                if not (members.Contains(codePointAt text &probe)) then going <- false
                else
                    offset <- previous
                    position <- position - 1L
            position

    let lastMatchEndingBy (text: string) (value: string) (searchEnd: int) =
        let mutable searchEnd = searchEnd
        let mutable result = None
        let mutable searching = true
        while searching && searchEnd >= value.Length - 1 do
            let found = text.LastIndexOf(value, searchEnd, StringComparison.Ordinal)
            if found < 0 then searching <- false
            elif isBoundary text found && isBoundary text (found + value.Length) then
                result <- Some(positionOf text found)
                searching <- false
            else searchEnd <- found + value.Length - 2
        result

    let lastIndexOf (text: string) (value: string) =
        if value.Length = 0 then Some(codePointLength text) else lastMatchEndingBy text value (text.Length - 1)

    let lastIndexOfBefore (text: string) (value: string) (stop: int64) =
        let position = min (max 0L stop) (codePointLength text)
        let offset = offsetOf text position
        if value.Length = 0 then Some position else lastMatchEndingBy text value (offset - 1)

    let split (text: string) (separator: string) =
        if separator.Length = 0 then [ text ]
        else
            let pieces = ResizeArray()
            let mutable start = 0
            let mutable found = find text separator 0
            while found >= 0 do
                pieces.Add(text.Substring(start, found - start))
                start <- found + separator.Length
                found <- find text separator start
            pieces.Add(text.Substring start)
            List.ofSeq pieces

    let splitLines (text: string) (breaks: string) =
        if text.Length = 0 then []
        elif breaks.Length = 0 then [ text ]
        else
            let members = codePointSet breaks
            let crlf = members.Contains(int '\r') && members.Contains(int '\n')
            let lines = ResizeArray()
            let mutable start = 0
            let mutable offset = 0
            while offset < text.Length do
                let mutable next = offset
                let codePoint = codePointAt text &next
                if members.Contains codePoint then
                    lines.Add(text.Substring(start, offset - start))
                    if crlf && codePoint = int '\r' && next < text.Length && text[next] = '\n' then next <- next + 1
                    start <- next
                offset <- next
            if start < text.Length then lines.Add(text.Substring start)
            List.ofSeq lines

    let replace (text: string) (pattern: string) (replacement: string) =
        if pattern.Length = 0 then text
        else
            let mutable found = find text pattern 0
            if found < 0 then text
            else
                let builder = StringBuilder(text.Length)
                let mutable start = 0
                while found >= 0 do
                    builder.Append(text, start, found - start).Append(replacement) |> ignore
                    start <- found + pattern.Length
                    found <- find text pattern start
                builder.Append(text, start, text.Length - start).ToString()

    let trimStart (text: string) =
        let mutable start = 0
        while start < text.Length && Char.IsWhiteSpace text[start] do start <- start + 1
        if start = 0 then text else text.Substring start

    let trimEnd (text: string) =
        let mutable stop = text.Length
        while stop > 0 && Char.IsWhiteSpace text[stop - 1] do stop <- stop - 1
        if stop = text.Length then text else text.Substring(0, stop)

    let private mapCase (text: string) (rune: Rune -> Rune) (unit: char -> char) =
        let builder = StringBuilder(text.Length)
        let mutable index = 0
        while index < text.Length do
            let current = text[index]
            if width text index = 2 then
                builder.Append((rune (Rune(current, text[index + 1]))).ToString()) |> ignore
                index <- index + 1
            elif Char.IsSurrogate current then builder.Append current |> ignore
            else builder.Append(unit current) |> ignore
            index <- index + 1
        builder.ToString()

    let toLower (text: string) =
        mapCase text Rune.ToLowerInvariant (fun unit -> if unit = 'İ' then 'i' else Char.ToLowerInvariant unit)

    let toUpper (text: string) =
        mapCase text Rune.ToUpperInvariant (fun unit -> if unit = 'ı' then 'I' elif unit = 'ß' then 'ẞ' else Char.ToUpperInvariant unit)

    let decodeUtf8Lossy (text: string) =
        let bytes = Array.zeroCreate<byte> text.Length
        let mutable count = 0
        let mutable index = 0
        while index < text.Length do
            let unit = text[index]
            if width text index = 2 then index <- index + 1
            bytes[count] <- if unit <= 'ÿ' then byte unit else 0xFFuy
            count <- count + 1
            index <- index + 1
        Encoding.UTF8.GetString(bytes, 0, count)

    let encodeUtf8 (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        String(bytes |> Array.map char)

    let fromCodePoint (code: int64) =
        if code < 0L || code > 0x10FFFFL then None
        elif code < 0x10000L then Some(string (char code))
        else Some(Char.ConvertFromUtf32(int code))

    let toCodePoint (text: string) =
        if text.Length = 1 then Some(int64 text[0])
        elif text.Length = 2 && width text 0 = 2 then Some(int64 (Char.ConvertToUtf32(text[0], text[1])))
        else None

    let fromFloat64 (value: float) =
        if Double.IsNaN value then "nan"
        elif Double.IsInfinity value then (if value > 0.0 then "inf" else "-inf")
        elif value = 0.0 then (if Double.IsNegative value then "-0.0" else "0.0")
        else
            let shortest = value.ToString("R", CultureInfo.InvariantCulture)
            let negative = shortest[0] = '-'
            let mark = shortest.IndexOf 'E'
            let first = if negative then 1 else 0
            let mantissa = shortest.Substring(first, (if mark < 0 then shortest.Length else mark) - first)
            let exponent = if mark < 0 then 0 else Int32.Parse(shortest.Substring(mark + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            let point = mantissa.IndexOf '.'
            let digits = if point < 0 then mantissa else mantissa.Remove(point, 1)
            let decpt = (if point < 0 then mantissa.Length else point) + exponent
            let mutable lead = 0
            while digits[lead] = '0' do lead <- lead + 1
            let digits = digits.Substring(lead).TrimEnd('0')
            let decpt = decpt - lead
            let builder = StringBuilder(32)
            if negative then builder.Append '-' |> ignore
            if decpt <= -4 || decpt > 16 then
                builder.Append digits[0] |> ignore
                if digits.Length > 1 then builder.Append('.').Append(digits, 1, digits.Length - 1) |> ignore
                let power = decpt - 1
                builder.Append(if power < 0 then "e-" else "e+").Append((abs power).ToString("00", CultureInfo.InvariantCulture)) |> ignore
            elif decpt <= 0 then builder.Append("0.").Append('0', -decpt).Append(digits) |> ignore
            elif decpt >= digits.Length then builder.Append(digits).Append('0', decpt - digits.Length).Append(".0") |> ignore
            else builder.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt) |> ignore
            builder.ToString()

    let parseFloat64 (text: string) =
        let mutable index = if text.Length > 0 && text[0] = '-' then 1 else 0
        let skipDigits () =
            let start = index
            while index < text.Length && text[index] >= '0' && text[index] <= '9' do index <- index + 1
            index > start
        let valid =
            skipDigits ()
            && (if index < text.Length && text[index] = '.' then (index <- index + 1; skipDigits ()) else true)
            && (if index < text.Length && (text[index] = 'e' || text[index] = 'E') then
                    index <- index + 1
                    if index < text.Length && (text[index] = '+' || text[index] = '-') then index <- index + 1
                    skipDigits ()
                else true)
            && index = text.Length
        if not valid then None
        else Some(Double.Parse(text, NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowExponent, CultureInfo.InvariantCulture))

/// NyNumber ported to F#.
module internal IrNumber =
    let private toInt64 (integral: float) =
        if integral >= -9223372036854775808.0 && integral < 9223372036854775808.0 then Some(int64 integral) else None

    let roundToInt64 (value: float) = toInt64 (Math.Round(value, MidpointRounding.AwayFromZero))
    let floorToInt64 (value: float) = toInt64 (Math.Floor value)

    let floorModulo (dividend: float) (divisor: float) =
        let remainder = dividend % divisor
        if remainder <> 0.0 then (if (divisor < 0.0) <> (remainder < 0.0) then remainder + divisor else remainder)
        else Math.CopySign(0.0, divisor)

    let checkedAdd (left: int64) (right: int64) =
        let result = left + right
        if ((left ^^^ result) &&& (right ^^^ result)) < 0L then None else Some result

    let checkedSubtract (left: int64) (right: int64) =
        let result = left - right
        if ((left ^^^ right) &&& (left ^^^ result)) < 0L then None else Some result

    let checkedMultiply (left: int64) (right: int64) =
        let mutable low = 0L
        let high = Math.BigMul(left, right, &low)
        if high = (low >>> 63) then Some low else None

    let round (value: float) (digits: int64) =
        if Double.IsNaN value || Double.IsInfinity value || value = 0.0 then value
        else
            let culture = CultureInfo.InvariantCulture
            let text = (abs value).ToString("R", culture)
            let exponentAt = text.IndexOf 'E'
            let exponent = if exponentAt < 0 then 0 else Int32.Parse(text.Substring(exponentAt + 1), culture)
            let mantissa = if exponentAt < 0 then text else text.Substring(0, exponentAt)
            let pointAt = mantissa.IndexOf '.'
            let significand = if pointAt < 0 then mantissa else mantissa.Remove(pointAt, 1)
            let mutable point = int64 (if pointAt < 0 then mantissa.Length else pointAt) + int64 exponent
            let mutable leading = 0
            while leading < significand.Length - 1 && significand[leading] = '0' do leading <- leading + 1
            let significand = significand.Substring leading
            point <- point - int64 leading
            let keep = point + Math.Clamp(digits, -1000L, 1000L)
            if keep >= int64 significand.Length then value
            elif keep < 0L then (if value < 0.0 then -0.0 else 0.0)
            else
                let mutable kept = if keep = 0L then 0L else Int64.Parse(significand.Substring(0, int keep), culture)
                if significand[int keep] >= '5' then kept <- kept + 1L
                let rounded = Double.Parse(kept.ToString(culture) + "E" + (point - keep).ToString(culture), NumberStyles.AllowExponent, culture)
                if value < 0.0 then -rounded else rounded

/// Evaluates an Execution IR program directly, without generating C#. Only sequential programs are
/// supported: `unsupported` names the first instruction kind this interpreter cannot run.
type ExecutionIrInterpreter(program: ExecutionProgram, deadlineTimestamp: int64, maxDepth: int) =
    let functions = program.Functions |> List.map (fun fn -> fn.SemanticFunctionId, fn) |> dict
    let blocks = Dictionary<FunctionId, Dictionary<ExecutionBlockId, ExecutionBlock>>()
    let valueTypes = Dictionary<FunctionId, Dictionary<ExecutionValueId, TypeRef>>()
    let operations =
        program.Capabilities |> List.collect (fun capability -> capability.Operations) |> List.map (fun operation -> operation.Id, operation) |> dict
    let stack = Stack<IrFrame>()
    let frames = ConditionalWeakTable<exn, IrFrame list>()
    let mutable overflowFrames = []
    let compiledBodies = Dictionary<FunctionId, IrValue[] -> IrValue>()
    let mutable checks = 0

    let blocksOf (fn: ExecutionFunction) =
        match blocks.TryGetValue fn.SemanticFunctionId with
        | true, found -> found
        | _ ->
            let table = Dictionary()
            for block in fn.Blocks do table[block.Id] <- block
            blocks[fn.SemanticFunctionId] <- table
            table

    let typesOf (fn: ExecutionFunction) =
        match valueTypes.TryGetValue fn.SemanticFunctionId with
        | true, found -> found
        | _ ->
            let table = Dictionary()
            for parameter in fn.Parameters do table[parameter.Value.Id] <- parameter.Value.Type
            for block in fn.Blocks do
                for parameter in block.Parameters do table[parameter.Id] <- parameter.Type
                for instruction in block.Instructions do table[instruction.Result.Id] <- instruction.Result.Type
            valueTypes[fn.SemanticFunctionId] <- table
            table

    let record (error: exn) =
        match frames.TryGetValue error with
        | true, _ -> ()
        | _ -> frames.AddOrUpdate(error, List.ofSeq stack)

    let isInternal (error: exn) =
        match error with
        | IrTimeoutException
        | IrStackOverflowException -> true
        | _ -> false

    let bad (kind: string) = invalidOp $"Execution IR interpreter: {kind} operand has an unexpected value."
    let str = function IrString value -> value | _ -> bad "String"
    let int = function IrInt value -> value | _ -> bad "Int64"
    let bool = function IrBool value -> value | _ -> bad "Bool"
    let float = function IrFloat value -> value | _ -> bad "Float64"
    let bits = function IrBits value -> value | _ -> bad "Bits32"
    let list = function IrList value -> value | _ -> bad "List"
    let map = function IrMap value -> value | _ -> bad "Map"
    let set = function IrSet value -> value | _ -> bad "Set"
    let option = function IrOption value -> value | _ -> bad "Option"
    let result = function IrResult(isOk, payload) -> isOk, payload | _ -> bad "Result"
    // ImmutableList builds a balanced tree directly from an array, several times faster than from a lazy sequence.
    let ofList (items: IrValue seq) = IrList(ImmutableList.CreateRange(match items with :? (IrValue[]) as array -> array | _ -> Seq.toArray items))
    let someInt = Option.map IrInt >> IrOption
    let strings items = items |> List.map IrString |> ofList
    let fieldsOf (fields: ExecutionFieldValue list) (read: ExecutionValueId -> IrValue) =
        fields |> List.map (fun field -> field.TargetFieldId, read field.ValueId) |> List.sortBy (fst >> StableId.value) |> Array.ofList
    let field (items: (FieldId * IrValue)[]) fieldId = items |> Array.find (fun (id, _) -> id = fieldId) |> snd

    /// Supported instruction kinds; everything else falls back to the compiled backend.
    static member Unsupported(kind: ExecutionInstructionKind) =
        match kind with
        | ExecutionInstructionKind.ParallelAll _ -> Some "ParallelAll"
        | ExecutionInstructionKind.CancellationRequested -> Some "CancellationRequested"
        | ExecutionInstructionKind.CancellationShield _ -> Some "CancellationShield"
        | ExecutionInstructionKind.TaskScope _ -> Some "TaskScope"
        | ExecutionInstructionKind.TaskStart _ -> Some "TaskStart"
        | ExecutionInstructionKind.TaskWait _ -> Some "TaskWait"
        | ExecutionInstructionKind.TaskCancel _ -> Some "TaskCancel"
        | ExecutionInstructionKind.OneShotCreate _ -> Some "OneShotCreate"
        | ExecutionInstructionKind.OneShotComplete _ -> Some "OneShotComplete"
        | ExecutionInstructionKind.OneShotWait _ -> Some "OneShotWait"
        | _ -> None

    /// The frames that were running when the error left the program, innermost first.
    member _.FramesOf(error: exn) =
        match frames.TryGetValue error with
        | true, found -> found
        | _ -> []

    /// The frames running when the depth limit was reached, innermost first.
    member _.OverflowFrames = overflowFrames

    /// Uses a compiled body for the function instead of interpreting it.
    member _.Register(functionId: FunctionId, body: IrValue[] -> IrValue) = compiledBodies[functionId] <- body

    /// Runs one function frame: the depth limit, the deadline and exception frame recording.
    member _.Enter(functionId: FunctionId, run: unit -> IrValue) : IrValue =
        if stack.Count >= maxDepth then
            overflowFrames <- List.ofSeq stack
            raise IrStackOverflowException
        if Diagnostics.Stopwatch.GetTimestamp() > deadlineTimestamp then raise IrTimeoutException
        stack.Push(IrFrame.Function functionId)
        try
            try run ()
            with error when not (isInternal error) ->
                record error
                reraise ()
        finally
            stack.Pop() |> ignore

    /// Raises the timeout once the deadline has passed, reading the clock every 256th time; compiled
    /// code calls it on back edges and calls.
    member _.CheckDeadline() =
        checks <- checks + 1
        if checks &&& 255 = 0 && Diagnostics.Stopwatch.GetTimestamp() > deadlineTimestamp then raise IrTimeoutException

    /// Runs a compiled function frame without allocating: the frame object is made once per function.
    member this.EnterCompiled(frame: IrFrame, body: Func<IrValue[], IrValue>, arguments: IrValue[]) : IrValue =
        if stack.Count >= maxDepth then
            overflowFrames <- List.ofSeq stack
            raise IrStackOverflowException
        this.CheckDeadline()
        stack.Push frame
        try
            try body.Invoke arguments
            with error when not (isInternal error) ->
                record error
                reraise ()
        finally
            stack.Pop() |> ignore

    /// Calls a function with its arguments in parameter declaration order.
    member this.CallArray(functionId: FunctionId, arguments: IrValue[]) : IrValue =
        match compiledBodies.TryGetValue functionId with
        | true, body -> this.Enter(functionId, fun () -> body arguments)
        | _ -> this.Call(functionId, List.ofArray arguments)

    /// Calls a function with its arguments in parameter declaration order.
    member this.Call(functionId: FunctionId, arguments: IrValue list) : IrValue =
        match compiledBodies.TryGetValue functionId with
        | true, body -> this.Enter(functionId, fun () -> body (Array.ofList arguments))
        | _ -> this.Enter(functionId, fun () -> this.Interpret(functions[functionId], arguments))

    member private this.Interpret(fn: ExecutionFunction, arguments: IrValue list) : IrValue =
                let env = Dictionary<ExecutionValueId, IrValue>()
                List.iter2 (fun (parameter: ExecutionParameter) value -> env[parameter.Value.Id] <- value) fn.Parameters arguments
                let read (id: ExecutionValueId) = env[id]
                let table = blocksOf fn
                let mutable block = table[fn.EntryBlockId]
                let mutable returned = None
                while returned.IsNone do
                    if Diagnostics.Stopwatch.GetTimestamp() > deadlineTimestamp then raise IrTimeoutException
                    for instruction in block.Instructions do
                        env[instruction.Result.Id] <- this.Evaluate(fn, read, instruction)
                    match block.Terminator with
                    | ExecutionTerminator.Return valueId -> returned <- Some env[valueId]
                    | ExecutionTerminator.Abort(_, errorValueId) -> returned <- Some(IrResult(false, env[errorValueId]))
                    | ExecutionTerminator.Jump(target, arguments) ->
                        let values = arguments |> List.map (fun argument -> env[argument.ValueId])
                        List.iter2 (fun (argument: ExecutionJumpArgument) value -> env[argument.TargetValueId] <- value) arguments values
                        block <- table[target]
                    | ExecutionTerminator.TailCall(_, arguments) ->
                        let values = arguments |> List.map (fun argument -> argument.TargetParameterId, env[argument.ValueId])
                        for parameterId, value in values do
                            let parameter = fn.Parameters |> List.find (fun parameter -> parameter.SemanticParameterId = parameterId)
                            env[parameter.Value.Id] <- value
                        block <- table[fn.EntryBlockId]
                    | ExecutionTerminator.Branch(condition, whenTrue, whenFalse) ->
                        block <- table[if bool env[condition] then whenTrue else whenFalse]
                    | ExecutionTerminator.MatchOption(input, someBlock, noneBlock) ->
                        block <- table[if (option env[input]).IsSome then someBlock else noneBlock]
                    | ExecutionTerminator.MatchResult(input, okBlock, errBlock) ->
                        block <- table[if fst (result env[input]) then okBlock else errBlock]
                    | ExecutionTerminator.MatchUnion(input, targets) ->
                        match env[input] with
                        | IrUnion(variantId, _) ->
                            match targets |> List.tryFind (fun target -> target.VariantId = variantId) with
                            | Some target -> block <- table[target.BlockId]
                            | None -> raise (InvalidOperationException "Execution IR union value did not match a declared variant.")
                        | _ -> bad "Union"
                returned.Value

    /// Calls a callback: `context` binds some parameters and the iteration values fill the others in order.
    member private this.Invoke(functionId: FunctionId, context: ExecutionCallArgument list, read: ExecutionValueId -> IrValue, iteration: IrValue list) =
        let fn = functions[functionId]
        let mutable remaining = iteration
        let arguments =
            fn.Parameters |> List.map (fun parameter ->
                match context |> List.tryFind (fun argument -> argument.TargetParameterId = parameter.SemanticParameterId) with
                | Some argument -> read argument.ValueId
                | None ->
                    let value = List.head remaining
                    remaining <- List.tail remaining
                    value)
        this.Call(functionId, arguments)

    member private this.InvokeCapability(capability: IrValue, operationId: CapabilityOperationId, arguments: IrValue list) =
        match capability with
        | IrCapability(_, invoke) ->
            stack.Push(IrFrame.CapabilityOperation operationId)
            try
                try invoke operationId arguments
                with error when not (isInternal error) ->
                    record error
                    reraise ()
            finally
                stack.Pop() |> ignore
        | _ -> bad "Capability"

    /// Evaluates one instruction, reading its operands through `read`.
    member this.Evaluate(fn: ExecutionFunction, read: ExecutionValueId -> IrValue, instruction: ExecutionInstruction) : IrValue =
        let v (id: ExecutionValueId) = read id
        let call functionId context iteration = this.Invoke(functionId, context, read, iteration)
        let predicate functionId context = fun item -> bool (call functionId context [ item ])
        match instruction.Kind with
        | ExecutionInstructionKind.ReadParameter id
        | ExecutionInstructionKind.ReadPatternBinding id -> v id
        | ExecutionInstructionKind.StringLiteral value -> IrString value
        | ExecutionInstructionKind.BoolLiteral value -> IrBool value
        | ExecutionInstructionKind.UnitLiteral -> IrUnit
        | ExecutionInstructionKind.Int64Literal value -> IrInt value
        | ExecutionInstructionKind.Float64Literal value -> IrFloat(BitConverter.UInt64BitsToDouble(Float64Bits.value value))
        | ExecutionInstructionKind.StringIsEmpty id -> IrBool((str (v id)).Length = 0)
        | ExecutionInstructionKind.Int64ToString id -> IrString((int (v id)).ToString(CultureInfo.InvariantCulture))
        | ExecutionInstructionKind.Int64ToHexString id ->
            let number = int (v id)
            IrString(if number < 0L then "-" + (uint64 (-number)).ToString("x", CultureInfo.InvariantCulture) else number.ToString("x", CultureInfo.InvariantCulture))
        | ExecutionInstructionKind.StringParseInt64 id ->
            let text = str (v id)
            let start = if text.Length > 0 && text[0] = '-' then 1 else 0
            let digits = text.Length > start && Seq.forall Char.IsAsciiDigit (text.Substring start)
            match digits, Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, (true, parsed) -> IrOption(Some(IrInt parsed))
            | _ -> IrOption None
        | ExecutionInstructionKind.StringParseInt64Hex id ->
            let text = str (v id)
            let negative = text.Length > 0 && text[0] = '-'
            let start = if negative then 1 else 0
            let mutable digits = text.Length > start
            let mutable magnitude = 0UL
            let mutable index = start
            while digits && index < text.Length do
                let c = text[index]
                let digit =
                    if c >= '0' && c <= '9' then Operators.int c - Operators.int '0'
                    elif c >= 'a' && c <= 'f' then Operators.int c - Operators.int 'a' + 10
                    elif c >= 'A' && c <= 'F' then Operators.int c - Operators.int 'A' + 10
                    else -1
                digits <- digit >= 0 && magnitude <= (UInt64.MaxValue >>> 4)
                if digits then magnitude <- (magnitude <<< 4) + uint64 digit
                index <- index + 1
            if digits && magnitude <= (if negative then 9223372036854775808UL else 9223372036854775807UL) then
                IrOption(Some(IrInt(if negative then -(int64 magnitude) else int64 magnitude)))
            else IrOption None
        | ExecutionInstructionKind.Float64ToString id -> IrString(IrText.fromFloat64 (float (v id)))
        | ExecutionInstructionKind.StringParseFloat64 id -> IrOption(IrText.parseFloat64 (str (v id)) |> Option.map IrFloat)
        | ExecutionInstructionKind.StringCodePoints id ->
            let text = str (v id)
            let items = ResizeArray()
            let mutable index = 0
            while index < text.Length do
                let width = IrText.width text index
                items.Add(IrString(text.Substring(index, width)))
                index <- index + width
            ofList items
        | ExecutionInstructionKind.StringToLower id -> IrString(IrText.toLower (str (v id)))
        | ExecutionInstructionKind.StringToUpper id -> IrString(IrText.toUpper (str (v id)))
        | ExecutionInstructionKind.StringDecodeUtf8Lossy id -> IrString(IrText.decodeUtf8Lossy (str (v id)))
        | ExecutionInstructionKind.StringEncodeUtf8 id -> IrString(IrText.encodeUtf8 (str (v id)))
        | ExecutionInstructionKind.Float64RoundToInt64 id -> someInt (IrNumber.roundToInt64 (float (v id)))
        | ExecutionInstructionKind.Float64FloorToInt64 id -> someInt (IrNumber.floorToInt64 (float (v id)))
        | ExecutionInstructionKind.Float64Sqrt id -> IrFloat(Math.Sqrt(float (v id)))
        | ExecutionInstructionKind.Float64Abs id -> IrFloat(Math.Abs(float (v id)))
        | ExecutionInstructionKind.Float64Negate id -> IrFloat(-(float (v id)))
        | ExecutionInstructionKind.Float64Floor id -> IrFloat(Math.Floor(float (v id)))
        | ExecutionInstructionKind.Float64Ceiling id -> IrFloat(Math.Ceiling(float (v id)))
        | ExecutionInstructionKind.Float64Truncate id -> IrFloat(Math.Truncate(float (v id)))
        | ExecutionInstructionKind.StringUtf8ByteCount id -> IrInt(int64 (Encoding.UTF8.GetByteCount(str (v id))))
        | ExecutionInstructionKind.StringFromCodePoint id -> IrOption(IrText.fromCodePoint (int (v id)) |> Option.map IrString)
        | ExecutionInstructionKind.StringToCodePoint id -> someInt (IrText.toCodePoint (str (v id)))
        | ExecutionInstructionKind.StringLength id -> IrInt(IrText.codePointLength (str (v id)))
        | ExecutionInstructionKind.BoolNot id -> IrBool(not (bool (v id)))
        | ExecutionInstructionKind.StringSlice(id, startId, endId) ->
            let text = str (v id)
            let start = max 0L (int (v startId))
            let stop = max 0L (int (v endId))
            let startOffset = IrText.offsetOf text start
            let endOffset = if stop > start then IrText.offsetOf text stop else startOffset
            IrString(if endOffset > startOffset then text.Substring(startOffset, endOffset - startOffset) else "")
        | ExecutionInstructionKind.StringContains(id, otherId) -> IrBool(IrText.find (str (v id)) (str (v otherId)) 0 >= 0)
        | ExecutionInstructionKind.StringIndexOf(id, otherId, fromId) -> someInt (IrText.indexOf (str (v id)) (str (v otherId)) (int (v fromId)))
        | ExecutionInstructionKind.StringSpan(id, setId, fromId) -> IrInt(IrText.span (str (v id)) (str (v setId)) (int (v fromId)))
        | ExecutionInstructionKind.StringSpanBack(id, setId, endId) -> IrInt(IrText.spanBack (str (v id)) (str (v setId)) (int (v endId)))
        | ExecutionInstructionKind.StringLastIndexOfBefore(id, otherId, endId) -> someInt (IrText.lastIndexOfBefore (str (v id)) (str (v otherId)) (int (v endId)))
        | ExecutionInstructionKind.StringLastIndexOf(id, otherId) -> someInt (IrText.lastIndexOf (str (v id)) (str (v otherId)))
        | ExecutionInstructionKind.StringStartsWith(id, otherId) ->
            let text, prefix = str (v id), str (v otherId)
            IrBool(text.StartsWith(prefix, StringComparison.Ordinal) && IrText.isBoundary text prefix.Length)
        | ExecutionInstructionKind.StringEndsWith(id, otherId) ->
            let text, suffix = str (v id), str (v otherId)
            IrBool(text.EndsWith(suffix, StringComparison.Ordinal) && IrText.isBoundary text (text.Length - suffix.Length))
        | ExecutionInstructionKind.StringSplit(id, otherId) -> strings (IrText.split (str (v id)) (str (v otherId)))
        | ExecutionInstructionKind.StringSplitLines(id, breaksId) -> strings (IrText.splitLines (str (v id)) (str (v breaksId)))
        | ExecutionInstructionKind.StringReplace(id, patternId, replacementId) -> IrString(IrText.replace (str (v id)) (str (v patternId)) (str (v replacementId)))
        | ExecutionInstructionKind.StringTrim id -> IrString(IrText.trimEnd (IrText.trimStart (str (v id))))
        | ExecutionInstructionKind.StringTrimStart id -> IrString(IrText.trimStart (str (v id)))
        | ExecutionInstructionKind.StringTrimEnd id -> IrString(IrText.trimEnd (str (v id)))
        | ExecutionInstructionKind.StringJoin(listId, separatorId) -> IrString(String.Join(str (v separatorId), list (v listId) |> Seq.map str))
        | ExecutionInstructionKind.StringEqual(leftId, rightId) -> IrBool(String.Equals(str (v leftId), str (v rightId), StringComparison.Ordinal))
        | ExecutionInstructionKind.StringLessThan(leftId, rightId) -> IrBool(IrText.lessThan (str (v leftId)) (str (v rightId)))
        | ExecutionInstructionKind.ValueEqual(leftId, rightId) -> IrBool(IrValue.Equal(v leftId, v rightId))
        | ExecutionInstructionKind.StringConcat(leftId, rightId) -> IrString(String.Concat(str (v leftId), str (v rightId)))
        | ExecutionInstructionKind.Int64WrappingAdd(leftId, rightId) -> IrInt(int (v leftId) + int (v rightId))
        | ExecutionInstructionKind.Int64WrappingSubtract(leftId, rightId) -> IrInt(int (v leftId) - int (v rightId))
        | ExecutionInstructionKind.Int64WrappingMultiply(leftId, rightId) -> IrInt(int (v leftId) * int (v rightId))
        | ExecutionInstructionKind.Int64FloorDivide(leftId, rightId) ->
            let dividend, divisor = int (v leftId), int (v rightId)
            if divisor = 0L then IrInt 0L
            elif divisor = -1L then IrInt(-dividend)
            else
                let quotient = dividend / divisor
                IrInt(if dividend % divisor <> 0L && (dividend < 0L) <> (divisor < 0L) then quotient - 1L else quotient)
        | ExecutionInstructionKind.Int64FloorModulo(leftId, rightId) ->
            let dividend, divisor = int (v leftId), int (v rightId)
            if divisor = 0L then IrInt dividend
            elif divisor = -1L then IrInt 0L
            else
                let remainder = dividend % divisor
                IrInt(if remainder <> 0L && (remainder < 0L) <> (divisor < 0L) then remainder + divisor else remainder)
        | ExecutionInstructionKind.Int64Min(leftId, rightId) -> IrInt(min (int (v leftId)) (int (v rightId)))
        | ExecutionInstructionKind.Int64Max(leftId, rightId) -> IrInt(max (int (v leftId)) (int (v rightId)))
        | ExecutionInstructionKind.Int64BitAnd(leftId, rightId) -> IrInt(int (v leftId) &&& int (v rightId))
        | ExecutionInstructionKind.Int64BitOr(leftId, rightId) -> IrInt(int (v leftId) ||| int (v rightId))
        | ExecutionInstructionKind.Int64BitXor(leftId, rightId) -> IrInt(int (v leftId) ^^^ int (v rightId))
        | ExecutionInstructionKind.Int64ShiftLeft(id, countId) ->
            let value, count = int (v id), int (v countId)
            IrInt(if count <= 0L then value elif count >= 64L then 0L else value <<< Operators.int count)
        | ExecutionInstructionKind.Int64ShiftRight(id, countId) ->
            let value, count = int (v id), int (v countId)
            IrInt(if count <= 0L then value elif count >= 64L then (if value < 0L then -1L else 0L) else value >>> Operators.int count)
        | ExecutionInstructionKind.Int64CheckedAdd(leftId, rightId) -> someInt (IrNumber.checkedAdd (int (v leftId)) (int (v rightId)))
        | ExecutionInstructionKind.Int64CheckedSubtract(leftId, rightId) -> someInt (IrNumber.checkedSubtract (int (v leftId)) (int (v rightId)))
        | ExecutionInstructionKind.Int64CheckedMultiply(leftId, rightId) -> someInt (IrNumber.checkedMultiply (int (v leftId)) (int (v rightId)))
        | ExecutionInstructionKind.ListRange(startId, endId) ->
            let first, last = int (v startId), int (v endId)
            if last <= first then IrList ImmutableList<IrValue>.Empty
            else
                let count = uint64 (last - first)
                if count > uint64 Array.MaxLength then raise (OutOfMemoryException())
                ofList (Array.init (Operators.int count) (fun index -> IrInt(first + int64 index)))
        | ExecutionInstructionKind.StringRepeat(textId, countId) ->
            let text, count = str (v textId), int (v countId)
            if count <= 0L || text.Length = 0 then IrString ""
            elif count > int64 (Int32.MaxValue / text.Length) then raise (OutOfMemoryException())
            else IrString(StringBuilder(text.Length * Operators.int count).Insert(0, text, Operators.int count).ToString())
        | ExecutionInstructionKind.Float64Round(id, digitsId) -> IrFloat(IrNumber.round (float (v id)) (int (v digitsId)))
        | ExecutionInstructionKind.Int64Equal(leftId, rightId) -> IrBool(int (v leftId) = int (v rightId))
        | ExecutionInstructionKind.Int64LessThan(leftId, rightId) -> IrBool(int (v leftId) < int (v rightId))
        | ExecutionInstructionKind.Int64LessThanOrEqual(leftId, rightId) -> IrBool(int (v leftId) <= int (v rightId))
        | ExecutionInstructionKind.Int64GreaterThan(leftId, rightId) -> IrBool(int (v leftId) > int (v rightId))
        | ExecutionInstructionKind.Int64GreaterThanOrEqual(leftId, rightId) -> IrBool(int (v leftId) >= int (v rightId))
        | ExecutionInstructionKind.Float64Add(leftId, rightId) -> IrFloat(float (v leftId) + float (v rightId))
        | ExecutionInstructionKind.Float64Min(leftId, rightId) -> IrFloat(Math.Min(float (v leftId), float (v rightId)))
        | ExecutionInstructionKind.Float64Max(leftId, rightId) -> IrFloat(Math.Max(float (v leftId), float (v rightId)))
        | ExecutionInstructionKind.Float64Subtract(leftId, rightId) -> IrFloat(float (v leftId) - float (v rightId))
        | ExecutionInstructionKind.Float64Multiply(leftId, rightId) -> IrFloat(float (v leftId) * float (v rightId))
        | ExecutionInstructionKind.Float64Divide(leftId, rightId) -> IrFloat(float (v leftId) / float (v rightId))
        | ExecutionInstructionKind.Float64FloorModulo(leftId, rightId) -> IrFloat(IrNumber.floorModulo (float (v leftId)) (float (v rightId)))
        | ExecutionInstructionKind.Float64Equal(leftId, rightId) -> IrBool((float (v leftId): float) = float (v rightId))
        | ExecutionInstructionKind.Float64LessThan(leftId, rightId) -> IrBool((float (v leftId): float) < float (v rightId))
        | ExecutionInstructionKind.Float64IsNaN id -> IrBool(Double.IsNaN(float (v id)))
        | ExecutionInstructionKind.Float64IsFinite id -> IrBool(Double.IsFinite(float (v id)))
        | ExecutionInstructionKind.Float64IsInfinite id -> IrBool(Double.IsInfinity(float (v id)))
        | ExecutionInstructionKind.Int64ToFloat64 id -> IrFloat(Operators.float (int (v id)))
        | ExecutionInstructionKind.Int64WrappingAbs id -> let value = int (v id) in IrInt(if value < 0L then -value else value)
        | ExecutionInstructionKind.Int64BitNot id -> IrInt(~~~(int (v id)))
        | ExecutionInstructionKind.Bits32FromInt64 id -> IrBits(uint32 (int (v id)))
        | ExecutionInstructionKind.Bits32ToInt64 id -> IrInt(int64 (bits (v id)))
        | ExecutionInstructionKind.Bits32Not id -> IrBits(~~~(bits (v id)))
        | ExecutionInstructionKind.Bits32Xor(leftId, rightId) -> IrBits(bits (v leftId) ^^^ bits (v rightId))
        | ExecutionInstructionKind.Bits32And(leftId, rightId) -> IrBits(bits (v leftId) &&& bits (v rightId))
        | ExecutionInstructionKind.Bits32Or(leftId, rightId) -> IrBits(bits (v leftId) ||| bits (v rightId))
        | ExecutionInstructionKind.Bits32Add(leftId, rightId) -> IrBits(bits (v leftId) + bits (v rightId))
        | ExecutionInstructionKind.Bits32RotateRight(id, amountId) ->
            IrBits(Numerics.BitOperations.RotateRight(bits (v id), Operators.int (int (v amountId) &&& 31L)))
        | ExecutionInstructionKind.Bits32ShiftRight(id, amountId) ->
            let value, amount = bits (v id), int (v amountId)
            IrBits(if amount <= 0L then value elif amount >= 32L then 0u else value >>> Operators.int amount)
        | ExecutionInstructionKind.Bits32ShiftLeft(id, amountId) ->
            let value, amount = bits (v id), int (v amountId)
            IrBits(if amount <= 0L then value elif amount >= 32L then 0u else value <<< Operators.int amount)
        | ExecutionInstructionKind.ListEmpty -> IrList ImmutableList<IrValue>.Empty
        | ExecutionInstructionKind.SetEmpty -> IrSet IrSetData.Empty
        | ExecutionInstructionKind.MapEmpty -> IrMap IrMapData.Empty
        | ExecutionInstructionKind.ListConstruct ids -> ofList (ids |> List.map v)
        | ExecutionInstructionKind.ListLength id -> IrInt(int64 (list (v id)).Count)
        | ExecutionInstructionKind.MapValues id -> ofList ((map (v id)).Entries |> Seq.map snd)
        | ExecutionInstructionKind.MapKeys id -> ofList ((map (v id)).Entries |> Seq.map fst)
        | ExecutionInstructionKind.SetToList id -> ofList (set (v id)).Items
        | ExecutionInstructionKind.MapSize id -> IrInt (map (v id)).Size
        | ExecutionInstructionKind.SetFromList id -> IrSet(list (v id) |> Seq.fold (fun (set: IrSetData) item -> set.Insert item) IrSetData.Empty)
        | ExecutionInstructionKind.SetSize id -> IrInt (set (v id)).Size
        | ExecutionInstructionKind.ListReverse id -> IrList((list (v id)).Reverse())
        | ExecutionInstructionKind.ListLast id ->
            let items = list (v id)
            IrOption(if items.Count > 0 then Some items[items.Count - 1] else None)
        | ExecutionInstructionKind.ListDropLast id ->
            let items = list (v id)
            IrList(if items.Count > 1 then items.RemoveAt(items.Count - 1) else ImmutableList<IrValue>.Empty)
        | ExecutionInstructionKind.ListAppendItem(listId, itemId) -> IrList((list (v listId)).Add(v itemId))
        | ExecutionInstructionKind.ListItemAt(listId, indexId) ->
            let items, index = list (v listId), int (v indexId)
            IrOption(if index < 0L || index >= int64 items.Count then None else Some items[Operators.int index])
        | ExecutionInstructionKind.MapRemove(mapId, keyId) -> IrMap((map (v mapId)).Remove(v keyId))
        | ExecutionInstructionKind.SetInsert(setId, itemId) -> IrSet((set (v setId)).Insert(v itemId))
        | ExecutionInstructionKind.SetRemove(setId, itemId) -> IrSet((set (v setId)).Remove(v itemId))
        | ExecutionInstructionKind.SetUnion(leftId, rightId) ->
            IrSet((set (v rightId)).Items |> Seq.fold (fun (set: IrSetData) item -> set.Insert item) (set (v leftId)))
        | ExecutionInstructionKind.SetIntersection(leftId, rightId) ->
            let left, right = set (v leftId), set (v rightId)
            IrSet(left.Items |> Seq.fold (fun (result: IrSetData) item -> if right.Contains item then result else result.Remove item) left)
        | ExecutionInstructionKind.SetDifference(leftId, rightId) ->
            let left, right = set (v leftId), set (v rightId)
            IrSet(right.Items |> Seq.fold (fun (result: IrSetData) item -> result.Remove item) left)
        | ExecutionInstructionKind.SetIsSubset(leftId, rightId) -> IrBool((set (v leftId)).IsSubsetOf(set (v rightId)))
        | ExecutionInstructionKind.MapUnion(leftId, rightId) -> IrMap((map (v leftId)).Union(map (v rightId)))
        | ExecutionInstructionKind.MapContainsKey(mapId, keyId) -> IrBool((map (v mapId)).ContainsKey(v keyId))
        | ExecutionInstructionKind.SetContains(setId, itemId) -> IrBool((set (v setId)).Contains(v itemId))
        | ExecutionInstructionKind.ListIndexOf(listId, itemId) ->
            let item = v itemId
            someInt (list (v listId) |> Seq.tryFindIndex (fun candidate -> IrValue.Equal(candidate, item)) |> Option.map int64)
        | ExecutionInstructionKind.ListLastIndexOf(listId, itemId) ->
            let item = v itemId
            someInt (list (v listId) |> Seq.toArray |> Array.tryFindIndexBack (fun candidate -> IrValue.Equal(candidate, item)) |> Option.map int64)
        | ExecutionInstructionKind.ListContains(listId, itemId) ->
            let item = v itemId
            IrBool(list (v listId) |> Seq.exists (fun candidate -> IrValue.Equal(candidate, item)))
        | ExecutionInstructionKind.MapGet(mapId, keyId) -> IrOption((map (v mapId)).TryGet(v keyId))
        | ExecutionInstructionKind.MapGetOrDefault(mapId, keyId, defaultId) -> (map (v mapId)).TryGet(v keyId) |> Option.defaultValue (v defaultId)
        | ExecutionInstructionKind.OptionGetOrDefault(optionId, defaultId) -> option (v optionId) |> Option.defaultValue (v defaultId)
        | ExecutionInstructionKind.ResultGetOrDefault(resultId, defaultId) ->
            match result (v resultId) with
            | true, payload -> payload
            | _ -> v defaultId
        | ExecutionInstructionKind.OptionOkOr(optionId, errorId) ->
            match option (v optionId) with
            | Some item -> IrResult(true, item)
            | None -> IrResult(false, v errorId)
        | ExecutionInstructionKind.ResultOkValue id -> let isOk, payload = result (v id) in IrOption(if isOk then Some payload else None)
        | ExecutionInstructionKind.ResultErrValue id -> let isOk, payload = result (v id) in IrOption(if isOk then None else Some payload)
        | ExecutionInstructionKind.OptionIsSome id -> IrBool((option (v id)).IsSome)
        | ExecutionInstructionKind.ResultIsOk id -> IrBool(fst (result (v id)))
        | ExecutionInstructionKind.ListSlice(listId, startId, endId) ->
            let items = list (v listId)
            let from = Operators.int (Math.Clamp(int (v startId), 0L, int64 items.Count))
            let until = Operators.int (Math.Clamp(int (v endId), 0L, int64 items.Count))
            IrList(if until <= from then ImmutableList<IrValue>.Empty else items.GetRange(from, until - from))
        | ExecutionInstructionKind.ListSetAt(listId, indexId, itemId) ->
            let items, index = list (v listId), int (v indexId)
            IrList(if index < 0L || index >= int64 items.Count then items else items.SetItem(Operators.int index, v itemId))
        | ExecutionInstructionKind.ListInsertAt(listId, indexId, itemId) ->
            let items = list (v listId)
            IrList(items.Insert(Operators.int (Math.Clamp(int (v indexId), 0L, int64 items.Count)), v itemId))
        | ExecutionInstructionKind.ListRemoveAt(listId, indexId) ->
            let items, index = list (v listId), int (v indexId)
            IrList(if index < 0L || index >= int64 items.Count then items else items.RemoveAt(Operators.int index))
        | ExecutionInstructionKind.MapInsert(mapId, keyId, valueId) -> IrMap((map (v mapId)).Insert(v keyId, v valueId))
        | ExecutionInstructionKind.ListConcat(leftId, rightId) -> IrList((list (v leftId)).AddRange(list (v rightId)))
        | ExecutionInstructionKind.ListFold(sourceId, seedId, folder, context) ->
            list (v sourceId) |> Seq.fold (fun state item -> call folder context [ state; item ]) (v seedId)
        | ExecutionInstructionKind.MapFold(sourceId, seedId, folder, context) ->
            (map (v sourceId)).Entries |> Seq.fold (fun state (key, value) -> call folder context [ state; key; value ]) (v seedId)
        | ExecutionInstructionKind.SetFold(sourceId, seedId, folder, context) ->
            (set (v sourceId)).Items |> Seq.fold (fun state item -> call folder context [ state; item ]) (v seedId)
        | ExecutionInstructionKind.ListEffectFold(sourceId, seedId, folder, context) ->
            let types = typesOf fn
            if instruction.Result.Type = types[seedId] then
                list (v sourceId) |> Seq.fold (fun state item -> call folder context [ state; item ]) (v seedId)
            else
                // Result accumulator: the folder is not called again after the first Err.
                let mutable state = IrResult(true, v seedId)
                let mutable items = (list (v sourceId)).GetEnumerator()
                while fst (result state) && items.MoveNext() do
                    state <- call folder context [ snd (result state); items.Current ]
                state
        | ExecutionInstructionKind.UseResource(acquireId, releaseId, releaseOperation, body, context) ->
            match result (v acquireId) with
            | false, error -> IrResult(false, error)
            | true, resource ->
                let release () = this.InvokeCapability(v releaseId, releaseOperation, [ resource ])
                let outcome =
                    try call body context [ resource ]
                    with error when not (isInternal error) ->
                        (try release () |> ignore with released when not (isInternal released) -> ())
                        reraise ()
                match operations[releaseOperation].ResultType with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, _) ->
                    match release (), outcome with
                    | IrResult(false, error), IrResult(true, _) -> IrResult(false, error)
                    | _ -> outcome
                | _ ->
                    release () |> ignore
                    outcome
        | ExecutionInstructionKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            try call body bodyContext [ IrUnit ]
            with error when not (isInternal error) -> call handler handlerContext [ IrException error ]
        | ExecutionInstructionKind.ExceptionIsCancellation id ->
            match v id with
            | IrException error -> IrBool(error :? OperationCanceledException)
            | _ -> bad "Exception"
        | ExecutionInstructionKind.ExceptionMessage id ->
            match v id with
            | IrException error -> IrString error.Message
            | _ -> bad "Exception"
        | ExecutionInstructionKind.ExceptionRethrow(id, _) ->
            match v id with
            | IrException error -> Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); IrUnit
            | _ -> bad "Exception"
        | ExecutionInstructionKind.ListMap(sourceId, mapper, context) ->
            ofList (list (v sourceId) |> Seq.map (fun item -> call mapper context [ item ]) |> Seq.toArray)
        | ExecutionInstructionKind.ListZipWith(leftId, rightId, combiner, context) ->
            ofList (Seq.zip (list (v leftId)) (list (v rightId)) |> Seq.map (fun (a, b) -> call combiner context [ a; b ]) |> Seq.toArray)
        | ExecutionInstructionKind.ListFlatMap(sourceId, mapper, context) ->
            ofList (list (v sourceId) |> Seq.toArray |> Array.collect (fun item -> list (call mapper context [ item ]) |> Seq.toArray))
        | ExecutionInstructionKind.MapFromList(sourceId, keyOf, context) ->
            IrMap(list (v sourceId) |> Seq.fold (fun (map: IrMapData) item -> map.Insert(call keyOf context [ item ], item)) IrMapData.Empty)
        | ExecutionInstructionKind.ListFilter(sourceId, test, context) ->
            ofList (list (v sourceId) |> Seq.filter (predicate test context) |> Seq.toArray)
        | ExecutionInstructionKind.MapFilter(sourceId, keep, context) ->
            let source = map (v sourceId)
            IrMap(source.Entries |> Seq.toArray |> Array.fold (fun (result: IrMapData) (key, value) -> if bool (call keep context [ key; value ]) then result else result.Remove key) source)
        | ExecutionInstructionKind.SetFilter(sourceId, test, context) ->
            let source = set (v sourceId)
            IrSet(source.Items |> Seq.toArray |> Array.fold (fun (result: IrSetData) item -> if predicate test context item then result else result.Remove item) source)
        | ExecutionInstructionKind.MapMapValues(sourceId, mapper, context) ->
            IrMap((map (v sourceId)).MapValues(fun key value -> call mapper context [ key; value ]))
        | ExecutionInstructionKind.SetMap(sourceId, mapper, context) ->
            IrSet((set (v sourceId)).Items |> Seq.toArray |> Array.fold (fun (result: IrSetData) item -> result.Insert(call mapper context [ item ])) IrSetData.Empty)
        | ExecutionInstructionKind.OptionMap(sourceId, mapper, context) ->
            IrOption(option (v sourceId) |> Option.map (fun item -> call mapper context [ item ]))
        | ExecutionInstructionKind.OptionAndThen(sourceId, binder, context) ->
            match option (v sourceId) with
            | Some item -> call binder context [ item ]
            | None -> IrOption None
        | ExecutionInstructionKind.ResultMap(sourceId, mapper, context) ->
            match result (v sourceId) with
            | true, payload -> IrResult(true, call mapper context [ payload ])
            | _ -> v sourceId
        | ExecutionInstructionKind.ResultMapErr(sourceId, mapper, context) ->
            match result (v sourceId) with
            | false, payload -> IrResult(false, call mapper context [ payload ])
            | _ -> v sourceId
        | ExecutionInstructionKind.ResultAndThen(sourceId, binder, context) ->
            match result (v sourceId) with
            | true, payload -> call binder context [ payload ]
            | _ -> v sourceId
        | ExecutionInstructionKind.ListSortBy(sourceId, lessThan, context) ->
            // NyList.SortBy's top-down merge sort, taking the right head only when lessThan(right, left).
            let items = Seq.toArray (list (v sourceId))
            let scratch = Array.zeroCreate items.Length
            let less a b = bool (call lessThan context [ a; b ])
            let rec sort start stop =
                let length = stop - start
                if length >= 2 then
                    let middle = start + length / 2
                    sort start middle
                    sort middle stop
                    let mutable left, right, next = start, middle, start
                    while left < middle && right < stop do
                        if less items[right] items[left] then
                            scratch[next] <- items[right]
                            right <- right + 1
                        else
                            scratch[next] <- items[left]
                            left <- left + 1
                        next <- next + 1
                    while left < middle do
                        scratch[next] <- items[left]
                        left <- left + 1
                        next <- next + 1
                    while right < stop do
                        scratch[next] <- items[right]
                        right <- right + 1
                        next <- next + 1
                    Array.blit scratch start items start length
            sort 0 items.Length
            ofList items
        | ExecutionInstructionKind.ListMinBy(sourceId, lessThan, context) ->
            let items = list (v sourceId)
            if items.Count = 0 then IrOption None
            else IrOption(Some(items |> Seq.skip 1 |> Seq.fold (fun best item -> if bool (call lessThan context [ item; best ]) then item else best) items[0]))
        | ExecutionInstructionKind.ListMaxBy(sourceId, lessThan, context) ->
            let items = list (v sourceId)
            if items.Count = 0 then IrOption None
            else IrOption(Some(items |> Seq.skip 1 |> Seq.fold (fun best item -> if bool (call lessThan context [ best; item ]) then item else best) items[0]))
        | ExecutionInstructionKind.ListAny(sourceId, test, context) -> IrBool(list (v sourceId) |> Seq.exists (predicate test context))
        | ExecutionInstructionKind.ListFindIndex(sourceId, test, context) ->
            someInt (list (v sourceId) |> Seq.tryFindIndex (predicate test context) |> Option.map int64)
        | ExecutionInstructionKind.ListAll(sourceId, test, context) -> IrBool(list (v sourceId) |> Seq.forall (predicate test context))
        | ExecutionInstructionKind.CapabilityConstruct(capabilityId, implementations) ->
            let implementations = dict implementations
            IrCapability(capabilityId, fun operationId arguments -> this.Call(implementations[operationId], arguments))
        | ExecutionInstructionKind.FunctionReference functionId -> IrFunction functionId
        | ExecutionInstructionKind.FunctionInvoke(id, arguments) ->
            match v id with
            | IrFunction functionId -> this.Call(functionId, arguments |> List.map v)
            | _ -> bad "Function"
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            let target = functions[functionId]
            this.Call(functionId, target.Parameters |> List.map (fun parameter ->
                v (arguments |> List.find (fun argument -> argument.TargetParameterId = parameter.SemanticParameterId)).ValueId))
        | ExecutionInstructionKind.CapabilityInvoke(capabilityId, operationId, arguments) ->
            let values =
                operations[operationId].Parameters |> List.map (fun parameter ->
                    v (arguments |> List.find (fun argument -> argument.TargetCapabilityParameterId = parameter.Id)).ValueId)
            this.InvokeCapability(v capabilityId, operationId, values)
        | ExecutionInstructionKind.ProductConstruct fields -> IrProduct(fieldsOf fields read)
        | ExecutionInstructionKind.FieldAccess(sourceId, fieldId) ->
            match v sourceId with
            | IrProduct fields -> field fields fieldId
            | _ -> bad "Product"
        | ExecutionInstructionKind.ProductUpdate(sourceId, updates) ->
            match v sourceId with
            | IrProduct fields ->
                let replaced = fieldsOf updates read
                IrProduct(fields |> Array.map (fun (id, value) ->
                    match replaced |> Array.tryFind (fun (replacedId, _) -> replacedId = id) with
                    | Some update -> update
                    | None -> id, value))
            | _ -> bad "Product"
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) -> IrUnion(variantId, fieldsOf fields read)
        | ExecutionInstructionKind.OptionNone -> IrOption None
        | ExecutionInstructionKind.OptionSome id -> IrOption(Some(v id))
        | ExecutionInstructionKind.ResultOk id -> IrResult(true, v id)
        | ExecutionInstructionKind.ResultErr id -> IrResult(false, v id)
        | ExecutionInstructionKind.ExtractUnionField(inputId, variantId, fieldId) ->
            match v inputId with
            | IrUnion(actual, fields) when actual = variantId -> field fields fieldId
            | _ -> raise (InvalidCastException "The union value is not the extracted variant.")
        | ExecutionInstructionKind.ExtractOptionSome id ->
            match option (v id) with
            | Some item -> item
            | None -> bad "Some"
        | ExecutionInstructionKind.ExtractResultOk id -> snd (result (v id))
        | ExecutionInstructionKind.ExtractResultErr id -> snd (result (v id))
        | unsupported -> invalidOp $"Execution IR interpreter does not support {ExecutionIrInterpreter.Unsupported unsupported}."
