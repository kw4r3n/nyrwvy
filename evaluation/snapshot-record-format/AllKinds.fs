/// Structural-tier generator: one deterministic model holding every SemanticNodeKind case (twice:
/// once with every list filled and every option present, once with every list empty and every
/// option absent), both type kinds, every TypeRef form, a capability with and without a failure
/// type, open and resolved obligations, a shared arm and a dependency header. It does not validate;
/// it is written through the JSON codec's private writer by reflection, as the open-point-2 check did.
module Nyrwvy.SnapshotRecordFormat.AllKinds

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open Nyrwvy.Core

/// A symbolic fixture name becomes a stable lowercase hyphenated UUID (MD5 of the name).
let uuid (name: string) =
    Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes name)).ToString("D")

let private id<'T> (name: string) : StableId<'T> = StableId.createForTesting<'T> (uuid name)

// Copied from tests/Nyrwvy.Core.Tests/SemanticNodeShapeTests.fs NodeShapeFixtures (module internal),
// with `filled = false` making lists empty and options None.
let private isGeneric (definition: Type) (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = definition
let private isStableId = isGeneric (typedefof<StableId<GraphNodeTag>>)
let private isList = isGeneric (typedefof<list<obj>>)
let private isOption = isGeneric (typedefof<option<obj>>)
let private makeUnion (case: UnionCaseInfo) (fields: obj[]) = FSharpValue.MakeUnion(case, fields, true)

let rec private synthesize (filled: bool) (nextId: unit -> string) (t: Type) : obj =
    if t = typeof<string> then box "text"
    elif t = typeof<bool> then box true
    elif t = typeof<int64> then box 1L
    elif t = typeof<uint64> then box 0x3ff0000000000000UL
    elif t = typeof<int> then box 1
    elif isStableId t then
        let case = FSharpType.GetUnionCases(t, true) |> Array.exactlyOne
        makeUnion case [| box (nextId ()) |]
    elif isList t then
        let itemType = t.GetGenericArguments().[0]
        let cases = FSharpType.GetUnionCases t
        let empty = cases |> Array.find (fun case -> case.Name = "Empty")
        let cons = cases |> Array.find (fun case -> case.Name = "Cons")
        let count = if filled then 2 else 0
        [ 1 .. count ]
        |> List.fold (fun tail _ -> makeUnion cons [| synthesize filled nextId itemType; tail |]) (makeUnion empty [||])
    elif isOption t then
        let cases = FSharpType.GetUnionCases t
        if filled then
            let some = cases |> Array.find (fun case -> case.Name = "Some")
            makeUnion some [| synthesize filled nextId (t.GetGenericArguments().[0]) |]
        else
            makeUnion (cases |> Array.find (fun case -> case.Name = "None")) [||]
    elif FSharpType.IsTuple t then
        FSharpValue.MakeTuple(FSharpType.GetTupleElements t |> Array.map (synthesize filled nextId), t)
    elif FSharpType.IsRecord(t, true) then
        FSharpValue.MakeRecord(t, FSharpType.GetRecordFields(t, true) |> Array.map (fun f -> synthesize filled nextId f.PropertyType), true)
    elif FSharpType.IsUnion(t, true) then
        let case = FSharpType.GetUnionCases(t, true).[0]
        makeUnion case (case.GetFields() |> Array.map (fun field -> synthesize filled nextId field.PropertyType))
    else failwith $"No fixture value for {t.FullName}."

let private everyCase<'Union> (filled: bool) (prefix: string) : (string * 'Union) list =
    FSharpType.GetUnionCases(typeof<'Union>, true)
    |> Array.map (fun case ->
        let counter = ref 0
        let nextId () =
            counter.Value <- counter.Value + 1
            uuid $"{prefix}{case.Name}-{counter.Value}"
        case.Name, makeUnion case (case.GetFields() |> Array.map (fun field -> synthesize filled nextId field.PropertyType)) :?> 'Union)
    |> List.ofArray

/// Number of SemanticNodeKind cases (204 at the time of writing).
let nodeKindCount = FSharpType.GetUnionCases(typeof<SemanticNodeKind>, true).Length

let private lit name = ({ Id = id<GraphNodeTag> name; Kind = SemanticNodeKind.UnitLiteral }: SemanticNode)
let private g (ns: SemanticNode list) : ValueGraph = { Nodes = ns; ResultNodeId = (List.head ns).Id }

let snapshot () : Snapshot =
    let node prefix (name, kind) = ({ Id = id<GraphNodeTag> (prefix + name); Kind = kind }: SemanticNode)
    let nodes =
        (everyCase<SemanticNodeKind> true "full/" |> List.map (node "n-"))
        @ (everyCase<SemanticNodeKind> false "empty/" |> List.map (node "n-empty-"))
    let typeKinds = everyCase<SemanticTypeKind> true "type/"
    let typeRefs = everyCase<TypeRef> true "ref/" |> List.map (fun (name, t) -> ({ Id = id<FieldTag> $"f-{name}"; DisplayName = name; Type = t }: SemanticField))
    let tp = id<TypeParameterTag> "tp-1", "T"
    let tp2 = id<TypeParameterTag> "tp-2", "U"
    let types =
        (typeKinds |> List.map (fun (name, kind) -> ({ Id = id<TypeTag> ("t-" + name); DisplayName = name; Exported = true; TypeParameters = [ tp ]; Kind = kind }: SemanticType)))
        @ [ { Id = id<TypeTag> "t-refs"; DisplayName = "Refs"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product typeRefs }
            { Id = id<TypeTag> "t-empty-union"; DisplayName = "Never"; Exported = false; TypeParameters = []; Kind = SemanticTypeKind.Union [] } ]
    let effect: SemanticEffect = { Id = id<EffectTag> "e-1"; DisplayName = "io"; Exported = true }
    let op: SemanticCapabilityOperation =
        { Id = id<CapabilityOperationTag> "op-1"; DisplayName = "read"
          Parameters = [ { Id = id<CapabilityParameterTag> "cp-1"; DisplayName = "path"; Type = TypeRef.Primitive PrimitiveType.String }
                         { Id = id<CapabilityParameterTag> "cp-2"; DisplayName = "mode"; Type = TypeRef.Primitive PrimitiveType.Int64 } ]
          ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = Some(TypeRef.Primitive PrimitiveType.String); EffectId = effect.Id }
    let op2 = { op with Id = id<CapabilityOperationTag> "op-0"; DisplayName = "write"; Parameters = []; FailureType = None }
    let cap: SemanticCapability = { Id = id<CapabilityTag> "c-1"; DisplayName = "Fs"; Exported = true; Operations = [ op; op2 ] }
    // A shared arm; a one-variant arm whose two field bindings have binding IDs ordered opposite to
    // their source field IDs (the writer sorts field bindings by source field, the record file by
    // binding subject); a nameless binding; construct type arguments.
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
        { Id = id<FunctionTag> "fn-all"; DisplayName = "all"; Exported = true; TypeParameters = [ tp; tp2 ]
          Parameters = [ { Id = id<ParameterTag> "p-1"; DisplayName = "a"; Type = TypeRef.TypeParameter(fst tp) }
                         { Id = id<ParameterTag> "p-0"; DisplayName = "b"; Type = TypeRef.Function([ TypeRef.Primitive PrimitiveType.Int64 ], TypeRef.Function([], TypeRef.Primitive PrimitiveType.Unit)) } ]
          ResultType = TypeRef.Primitive PrimitiveType.Unit; RequiredEffects = [ effect.Id ]; Body = g (nodes @ extra) }
    let fn2 = { fn with Id = id<FunctionTag> "fn-plain"; DisplayName = "plain \"quoted\" <tag> & é \U0001F600"; Exported = false; TypeParameters = []; Parameters = []; RequiredEffects = []; Body = g [ lit "y-unit" ] }
    let obligations: MissingArgumentObligation list =
        [ { Id = id<ObligationTag> "o-open"; OriginTransactionId = id<TransactionTag> "tx-1"; CallNodeId = id<GraphNodeTag> "n-DirectCall"; ParameterId = id<ParameterTag> "p-1"; Status = ObligationStatus.Open }
          { Id = id<ObligationTag> "o-done"; OriginTransactionId = id<TransactionTag> "tx-1"; CallNodeId = id<GraphNodeTag> "n-DirectCall"; ParameterId = id<ParameterTag> "p-1"
            Status = ObligationStatus.Resolved { TransactionId = id<TransactionTag> "tx-2"; ValueNodeId = id<GraphNodeTag> "n-UnitLiteral" } } ]
    { Id = id<SnapshotTag> "s-1"; ParentId = Some(id<SnapshotTag> "s-0"); Types = types; Effects = [ effect ]; Capabilities = [ cap ]; Functions = [ fn; fn2 ]; Obligations = obligations }

/// Writes a snapshot through the codec's private writer (the public serializers validate), at the
/// current schema version, with an optional dependency marker.
let writeUnvalidated (dependency: (string * string) option) (snapshot: Snapshot) : string =
    let codec = typeof<Snapshot>.Assembly.GetType("Nyrwvy.Core.SnapshotCodec")
    let write = codec.GetMethod("writeSnapshotAt", BindingFlags.NonPublic ||| BindingFlags.Static)
    match write.Invoke(null, [| box (Some SnapshotSchema.current); box dependency; box snapshot |]) :?> Result<string, SnapshotCodecError> with
    | Ok json -> json
    | Error error -> failwith $"writer rejected the structural model: {error}"

/// The structural-tier fixture JSON.
let json () = writeUnvalidated (Some("dependency-set-00000000-0000-4000-8000-000000000000", String('0', 64))) (snapshot ())
