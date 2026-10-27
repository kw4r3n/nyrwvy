namespace Nyrwvy.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

/// `src/Nyrwvy.Semantics/RecordKinds.fs` is generated from the strict JSON writer's output on a
/// structural model holding every node kind; this derives it again and compares. Ported from the
/// retired evaluation harness (`eval/snapshot-record-format-20261023`: AllKinds.fs, RecordText.fs).
module RecordKindsTests =
    open NodeShapeFixtures

    // ---------------------------------------------------------------------------------------------
    // The structural all-kinds model: every SemanticNodeKind case twice (lists filled and options
    // present, then empty and absent), both type kinds, every TypeRef form, a capability with and
    // without a failure type (and the wait and task-binding markers), open and resolved obligations, a shared arm and a dependency header.
    // It does not validate, so it is written through the codec's private writer by reflection.
    // ---------------------------------------------------------------------------------------------

    /// A symbolic fixture name becomes a stable lowercase hyphenated UUID (MD5 of the name).
    let private uuid (name: string) =
        Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes name)).ToString("D")

    let private id<'T> (name: string) : StableId<'T> = StableId.createForTesting<'T> (uuid name)

    let private everyCase<'Union> (filled: bool) (prefix: string) : (string * 'Union) list =
        FSharpType.GetUnionCases(typeof<'Union>, true)
        |> Array.map (fun case ->
            let counter = ref 0
            let nextId () =
                counter.Value <- counter.Value + 1
                uuid $"{prefix}{case.Name}-{counter.Value}"
            case.Name, makeUnion case (case.GetFields() |> Array.map (fun field -> synthesizeWith filled nextId field.PropertyType)) :?> 'Union)
        |> List.ofArray

    let private lit name = ({ Id = id<GraphNodeTag> name; Kind = SemanticNodeKind.UnitLiteral }: SemanticNode)
    let private g (ns: SemanticNode list) : ValueGraph = { Nodes = ns; ResultNodeId = (List.head ns).Id }

    let private allKindsSnapshot () : Snapshot =
        let node prefix (name, kind) = ({ Id = id<GraphNodeTag> (prefix + name); Kind = kind }: SemanticNode)
        let nodes =
            (everyCase<SemanticNodeKind> true "full/" |> List.map (node "n-"))
            @ (everyCase<SemanticNodeKind> false "empty/" |> List.map (node "n-empty-"))
        let typeKinds = everyCase<SemanticTypeKind> true "type/"
        let typeRefs = everyCase<TypeRef> true "ref/" |> List.map (fun (name, t) -> ({ Id = id<FieldTag> $"f-{name}"; DisplayName = name; Type = t }: SemanticField))
        let tp = id<TypeParameterTag> "tp-1", "T"
        let tp2 = id<TypeParameterTag> "tp-2", "U"
        let types =
            (typeKinds |> List.map (fun (name, kind) -> ({ Id = id<TypeTag> ("t-" + name); DisplayName = name; Exported = true; TypeParameters = [ id<TypeParameterTag> ("tp-" + name), "T" ]; Kind = kind }: SemanticType)))
            @ [ { Id = id<TypeTag> "t-refs"; DisplayName = "Refs"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product typeRefs }
                { Id = id<TypeTag> "t-empty-union"; DisplayName = "Never"; Exported = false; TypeParameters = []; Kind = SemanticTypeKind.Union [] } ]
        let effect: SemanticEffect = { Id = id<EffectTag> "e-1"; DisplayName = "io"; Exported = true }
        let op: SemanticCapabilityOperation =
            { MayWait = true; Id = id<CapabilityOperationTag> "op-1"; DisplayName = "read"
              Parameters = [ { Id = id<CapabilityParameterTag> "cp-1"; DisplayName = "path"; Type = TypeRef.Primitive PrimitiveType.String }
                             { Id = id<CapabilityParameterTag> "cp-2"; DisplayName = "mode"; Type = TypeRef.Primitive PrimitiveType.Int64 } ]
              ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = Some(TypeRef.Primitive PrimitiveType.String); EffectId = effect.Id }
        let op2 = { op with Id = id<CapabilityOperationTag> "op-0"; DisplayName = "write"; Parameters = []; FailureType = None; MayWait = false }
        let cap: SemanticCapability = { TaskBound = true; Id = id<CapabilityTag> "c-1"; DisplayName = "Fs"; Exported = true; Operations = [ op; op2 ] }
        // A shared arm; a one-variant arm whose two field bindings have binding IDs ordered opposite to
        // their source field IDs; a nameless binding; construct type arguments.
        let sortedIds (names: string list) = names |> List.map uuid |> List.sort
        let fieldIds = sortedIds [ "fa"; "fb" ] |> List.map StableId.createForTesting<FieldTag>
        let bindingIds = sortedIds [ "ba"; "bb" ] |> List.map StableId.createForTesting<PatternBindingTag>
        let binding (bid: PatternBindingId) name : PatternBinding = { Id = bid; DisplayName = name; Type = TypeRef.Primitive PrimitiveType.Bool }
        let extra =
            [ { Id = id<GraphNodeTag> "x-match"
                Kind = SemanticNodeKind.MatchUnion(id<GraphNodeTag> "full/UnitLiteral-0",
                         [ { VariantIds = [ id<VariantTag> "v-b"; id<VariantTag> "v-c" ]; FieldBindings = []; Body = g [ lit "x-shared-body" ] }
                           { VariantIds = [ id<VariantTag> "v-a" ]
                             FieldBindings = [ { SourceFieldId = fieldIds[0]; Binding = binding bindingIds[1] None }
                                               { SourceFieldId = fieldIds[1]; Binding = binding bindingIds[0] (Some "named") } ]
                             Body = g [ lit "x-single-body" ] } ]) }
              { Id = id<GraphNodeTag> "x-construct"
                Kind = SemanticNodeKind.UnionConstruct(id<VariantTag> "v-a", [ TypeRef.Primitive PrimitiveType.Int64 ], []) } ]
        let fn: SemanticFunction =
            { MustNotWait = true; Id = id<FunctionTag> "fn-all"; DisplayName = "all"; Exported = true; TypeParameters = [ tp; tp2 ]
              Parameters = [ { Id = id<ParameterTag> "p-1"; DisplayName = "a"; Type = TypeRef.TypeParameter(fst tp) }
                             { Id = id<ParameterTag> "p-0"; DisplayName = "b"; Type = TypeRef.Function([ TypeRef.Primitive PrimitiveType.Int64 ], TypeRef.Function([], TypeRef.Primitive PrimitiveType.Unit)) } ]
              ResultType = TypeRef.Primitive PrimitiveType.Unit; RequiredEffects = [ effect.Id ]; Body = g (nodes @ extra) }
        let fn2 = { fn with Id = id<FunctionTag> "fn-plain"; DisplayName = "plain \"quoted\" <tag> & é \U0001F600"; Exported = false; MustNotWait = false; TypeParameters = []; Parameters = []; RequiredEffects = []; Body = g [ lit "y-unit" ] }
        let obligations: MissingArgumentObligation list =
            [ { Id = id<ObligationTag> "o-open"; OriginTransactionId = id<TransactionTag> "tx-1"; CallNodeId = id<GraphNodeTag> "n-DirectCall"; ParameterId = id<ParameterTag> "p-1"; Status = ObligationStatus.Open }
              { Id = id<ObligationTag> "o-done"; OriginTransactionId = id<TransactionTag> "tx-1"; CallNodeId = id<GraphNodeTag> "n-DirectCall"; ParameterId = id<ParameterTag> "p-1"
                Status = ObligationStatus.Resolved { TransactionId = id<TransactionTag> "tx-2"; ValueNodeId = id<GraphNodeTag> "n-UnitLiteral" } } ]
        { Id = id<SnapshotTag> "s-1"; ParentId = Some(id<SnapshotTag> "s-0"); Types = types; Effects = [ effect ]; Capabilities = [ cap; { cap with Id = id<CapabilityTag> "c-2"; DisplayName = "Env"; TaskBound = false; Operations = [] } ]; Functions = [ fn; fn2 ]; Obligations = obligations }

    /// The all-kinds model as the writer's JSON, at the current schema version with a dependency marker
    /// (the public serializers validate, so this goes through the private `writeSnapshotAt`).
    let private allKindsJson () : string =
        let codec = typeof<Snapshot>.Assembly.GetType("Nyrwvy.Core.SnapshotCodec")
        let write = codec.GetMethod("writeSnapshotAt", BindingFlags.NonPublic ||| BindingFlags.Static)
        let dependency = Some("dependency-set-00000000-0000-4000-8000-000000000000", String('0', 64))
        match write.Invoke(null, [| box (Some SnapshotSchema.current); box dependency; box (allKindsSnapshot ()) |]) :?> Result<string, SnapshotCodecError> with
        | Ok json -> json
        | Error error -> failwith $"writer rejected the structural model: {error}"

    // ---------------------------------------------------------------------------------------------
    // Naming rules and key shapes, as the writer's output shows them
    // ---------------------------------------------------------------------------------------------

    /// Longest ID noun first; the tags of StableId.fs.
    let private domains =
        [ "CapabilityParameterId", "capability-parameter"; "TypeParameterId", "type-parameter"; "PatternBindingId", "binding"
          "OperationId", "operation"; "TransactionId", "transaction"; "NodeId", "node"; "FunctionId", "function"
          "FieldId", "field"; "ParameterId", "parameter"; "VariantId", "variant"; "EffectId", "effect"
          "CapabilityId", "capability"; "TypeId", "type"; "SnapshotId", "snapshot" ]

    /// The ID domain an ID-valued JSON key names, from its suffix; None for a free value.
    let private domainOf (key: string) =
        domains |> List.tryPick (fun (suffix, domain) -> if key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) then Some domain else None)

    /// Bare-string ID arrays: the element domain comes from the model's element type, not the key.
    let private arrayDomain = dict [ "requiredEffects", "effect"; "variantIds", "variant"; "itemNodeIds", "node"; "arguments", "node" ]

    /// Collections the writer omits when empty (SnapshotCodec.fs writeTypeParameters, writeTypeArguments,
    /// writeNamedCallbackContext); every other collection is written even when empty.
    let private optionalWhenEmpty = set [ "typeParameters"; "typeArguments"; "context"; "bodyContext"; "handlerContext" ]

    let private memberKindOf = dict [ "parameters", "parameter"; "fields", "field"; "variants", "variant"; "operations", "operation"; "typeParameters", "type-parameter" ]

    let private kebab (s: string) = Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant()
    let private camel (s: string) = Regex.Replace(s, "-([a-z])", fun m -> m.Groups[1].Value.ToUpperInvariant())

    /// JSON key -> record key.
    let private keyName (key: string) =
        if key = "displayName" then "name"
        elif key.EndsWith("NodeIds", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 7) + "s")
        elif key.EndsWith("Ids", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 3) + "s")
        elif key.EndsWith("NodeId", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 6))
        elif key.EndsWith("Id", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 2))
        else kebab key

    type private Shape =
        | Id of domain: string
        | Bool
        | Int
        | Str
        | Graph
        | ValueArm of bindingKey: string
        | Arms
        | TypeExpr
        | TypeList
        | IdList of domain: string
        | Pairs of string * string
        | Members of memberKind: string
        | Unknown

    let private has (v: JsonElement) (name: string) = v.TryGetProperty name |> fst
    let private isGraph (v: JsonElement) = v.ValueKind = JsonValueKind.Object && has v "nodes" && has v "resultNodeId"
    let private isValueArm (v: JsonElement) = v.ValueKind = JsonValueKind.Object && has v "body" && not (has v "id") && not (has v "variantId") && not (has v "variantIds")
    let private isTypeRef (v: JsonElement) = v.ValueKind = JsonValueKind.Object && has v "kind" && not (has v "id")

    /// Observations of one JSON object: its record kind and (JSON key, shape) in writer order.
    type private Observe = string -> (string * Shape) list -> unit

    let rec private observeObject (observe: Observe) (kind: string) (obj: JsonElement) =
        let seen = List<string * Shape>()
        for p in obj.EnumerateObject() do
            let jk, v = p.Name, p.Value
            let add shape = seen.Add((jk, shape))
            match jk with
            | "id" | "kind" | "typeParameterId" -> add Str
            | _ when isGraph v ->
                add Graph
                observeGraph observe v
            | _ when isValueArm v ->
                let bindingKey = if has v "errorBinding" then "errorBinding" else "valueBinding"
                observeBinding observe (v.GetProperty bindingKey)
                add (ValueArm bindingKey)
                observeGraph observe (v.GetProperty "body")
            | "arms" ->
                for arm in v.EnumerateArray() do
                    for fb in arm.GetProperty("fieldBindings").EnumerateArray() do observeBinding observe (fb.GetProperty "binding")
                    observeGraph observe (arm.GetProperty "body")
                add Arms
            | _ when isTypeRef v -> add TypeExpr
            | _ when v.ValueKind = JsonValueKind.Array ->
                let items = v.EnumerateArray() |> List.ofSeq
                match items with
                | [] -> add Unknown
                | first :: _ when isTypeRef first -> add TypeList
                | first :: _ when first.ValueKind = JsonValueKind.String -> add (IdList arrayDomain[jk])
                | first :: _ when has first "id" || has first "typeParameterId" ->
                    let memberKind = memberKindOf[jk]
                    add (Members memberKind)
                    for m in items do
                        observeObject observe (if has m "kind" then kebab (m.GetProperty("kind").GetString()) else memberKind) m
                | first :: _ ->
                    match first.EnumerateObject() |> Seq.map (fun p -> p.Name) |> List.ofSeq with
                    | [ a; b ] -> add (Pairs(a, b))
                    | _ -> failwith $"unmapped keyed list {jk}"
            | _ when v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False -> add Bool
            | _ when v.ValueKind = JsonValueKind.Number -> add Int
            | _ when v.ValueKind = JsonValueKind.String -> add (match domainOf jk with Some domain -> Id domain | None -> Str)
            | _ -> failwith $"unmapped value under {kind}.{jk}"
        observe kind (List.ofSeq seen)

    and private observeBinding (observe: Observe) (b: JsonElement) =
        observe "binding" [ for p in b.EnumerateObject() ->
                                match p.Name with
                                | "id" | "displayName" -> p.Name, Str
                                | "type" -> p.Name, TypeExpr
                                | other -> failwith $"unmapped binding key {other}" ]

    and private observeGraph (observe: Observe) (g: JsonElement) =
        for n in g.GetProperty("nodes").EnumerateArray() do
            observeObject observe (kebab (n.GetProperty("kind").GetString())) n

    let private observeSnapshot (observe: Observe) (json: string) =
        use doc = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
        for group, domain in [ "types", "type"; "effects", "effect"; "capabilities", "capability"; "functions", "function"; "obligations", "obligation" ] do
            for d in doc.RootElement.GetProperty(group).EnumerateArray() do
                observeObject observe (if has d "kind" then kebab (d.GetProperty("kind").GetString()) else domain) d

    // ---------------------------------------------------------------------------------------------
    // The per-kind table, derived from the writer's output on the all-kinds model
    // ---------------------------------------------------------------------------------------------

    /// The table as text, one kind per line, in the form `RecordKinds.text` embeds:
    /// `record-kind[=jsonKind]: id|kind|typeParameterId|jsonKey/record-key:shape[?][~]`, where `?` marks
    /// a key absent from at least one observed record of the kind (the writer omits an absent option)
    /// or `exported` (omitted when false), and `~` a collection the writer omits when empty.
    let private describeTable (json: string) =
        let keys = Dictionary<string, List<string>>()
        let shapes = Dictionary<string, Dictionary<string, Shape>>()
        let records = Dictionary<string, int>()
        let occurrences = Dictionary<string, Dictionary<string, int>>()
        let observe kind (seen: (string * Shape) list) =
            if not (keys.ContainsKey kind) then
                keys[kind] <- List<string>()
                shapes[kind] <- Dictionary<string, Shape>()
                records[kind] <- 0
                occurrences[kind] <- Dictionary<string, int>()
            records[kind] <- records[kind] + 1
            let order, known = keys[kind], shapes[kind]
            let mutable previous = -1
            for jk, shape in seen do
                if not (order.Contains jk) then order.Insert(previous + 1, jk)
                previous <- order.IndexOf jk
                occurrences[kind][jk] <- (match occurrences[kind].TryGetValue jk with | true, n -> n + 1 | _ -> 1)
                match known.TryGetValue jk with
                | true, existing when existing <> Unknown -> ()
                | _ -> known[jk] <- shape
        observeSnapshot observe json
        let shapeText kind jk =
            match shapes[kind][jk] with
            | Id d -> $"id({d})"
            | Bool -> "bool"
            | Int -> "int"
            | Str -> "str"
            | Graph -> "graph"
            | ValueArm b -> $"arm({b})"
            | Arms -> "arms"
            | TypeExpr -> "type"
            | TypeList -> "types"
            | IdList d -> $"ids({d})"
            | Pairs(a, b) -> $"pairs({a}:{(domainOf a).Value},{b}:{(domainOf b).Value})"
            | Members m -> $"members({m})"
            | Unknown -> failwith $"table: shape of {kind}.{jk} never observed non-empty"
        keys
        |> Seq.sortBy (fun (KeyValue(kind, _)) -> kind)
        |> Seq.map (fun (KeyValue(kind, order)) ->
            let cell jk =
                match jk with
                | "id" | "kind" | "typeParameterId" -> jk
                | _ ->
                    let optional = jk = "exported" || occurrences[kind][jk] < records[kind]
                    $"{jk}/{keyName jk}:{shapeText kind jk}" + (if optional then "?" else "") + (if optionalWhenEmpty.Contains jk then "~" else "")
            kind + (if order.Contains "kind" then "=" + camel kind else "") + ": " + (order |> Seq.map cell |> String.concat " "))
        |> String.concat "\n"
        |> fun text -> text + "\n"

    /// The first 1-based line where two texts differ (comparing lines, then the line count).
    let private firstDifferingLine (a: string) (b: string) =
        let la, lb = a.Split('\n'), b.Split('\n')
        Seq.zip la lb |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min la.Length lb.Length) |> (+) 1

    [<Fact>]
    let ``RecordKinds.text is the table derived from the writer's all-kinds output`` () =
        let derived = describeTable (allKindsJson ())
        if derived <> RecordCodec.kindTable then
            let path = Path.Combine(AppContext.BaseDirectory, "RecordKinds.regenerated.txt")
            File.WriteAllText(path, derived)
            Assert.Fail(
                $"tools/zig/snapshot-reader/record_kinds.txt no longer matches the writer (first difference at table line {firstDifferingLine RecordCodec.kindTable derived}). "
                + $"Paste the regenerated text below (also written to {path}) into tools/zig/snapshot-reader/record_kinds.txt:\n\n{derived}")

    /// The model does not validate and its synthesized references dangle, so the production decoder
    /// (which checks references before building the model) cannot read it back; the validated
    /// fixtures in RecordCodecTests cover the decode side.
    [<Fact>]
    let ``the all-kinds JSON renders a record of every table kind`` () =
        let text = RecordCodec.ofJson (allKindsJson ())
        for line in RecordCodec.kindTable.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
            let kind = line.Substring(0, line.IndexOfAny [| '='; ':' |])
            Assert.Contains("\n" + kind + " ", text)
