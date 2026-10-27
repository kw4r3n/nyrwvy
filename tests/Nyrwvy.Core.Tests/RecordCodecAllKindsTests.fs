namespace Nyrwvy.Core.Tests

open System
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

/// The record codec against a valid snapshot per `SemanticNodeKind` case: the fixed-signature kinds
/// come from `OperationSignatures.table` (one function whose parameters are the operands), the rest
/// are hand-built over a small set of shared declarations. A kind without a builder fails its row.
module RecordCodecAllKindsTests =
    let private uuid (name: string) =
        Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes name)).ToString("D")

    let private id<'T> (name: string) : StableId<'T> = StableId.createForTesting<'T> (uuid name)

    let private int64T = TypeRef.Primitive PrimitiveType.Int64
    let private stringT = TypeRef.Primitive PrimitiveType.String
    let private boolT = TypeRef.Primitive PrimitiveType.Bool
    let private unitT = TypeRef.Primitive PrimitiveType.Unit
    let private generic g args = TypeRef.BuiltinGeneric(g, args)
    let private listOf t = generic BuiltinGenericType.List [ t ]
    let private optionOf t = generic BuiltinGenericType.Option [ t ]
    let private resultOf ok err = generic BuiltinGenericType.Result [ ok; err ]
    let private outcomeT = resultOf int64T stringT

    /// Nodes are added to the graph being built; node IDs are unique across every graph built here.
    type private Graph() =
        static let mutable counter = 0
        let nodes = ResizeArray<SemanticNode>()
        member _.Add(kind: SemanticNodeKind) =
            counter <- counter + 1
            let nodeId = id<GraphNodeTag> $"node-{counter}"
            nodes.Add { Id = nodeId; Kind = kind }
            nodeId
        member this.Lit(value: int64) = this.Add(SemanticNodeKind.Int64Literal value)
        member _.Graph(result: GraphNodeId) : ValueGraph = { Nodes = List.ofSeq nodes; ResultNodeId = result }

    let private graph (build: Graph -> GraphNodeId) =
        let g = Graph()
        g.Graph(build g)

    let private param (fnName: string) name t : SemanticParameter =
        { Id = id<ParameterTag> $"param-{fnName}-{name}"; DisplayName = name; Type = t }

    let private fn name (parameters: SemanticParameter list) effects resultType build : SemanticFunction =
        { MustNotWait = false; Id = id<FunctionTag> ("fn-" + name); DisplayName = name; Exported = true; TypeParameters = []
          Parameters = parameters; ResultType = resultType; RequiredEffects = effects; Body = graph build }

    /// A function of one parameter whose body is built from that parameter's reference.
    let private fn1 name (pName, pType) effects resultType (build: Graph -> GraphNodeId -> GraphNodeId) =
        let p = param name pName pType
        fn name [ p ] effects resultType (fun g -> build g (g.Add(SemanticNodeKind.ParameterReference p.Id)))

    let private binding name t : PatternBinding = { Id = id<PatternBindingTag> ("binding-" + name); DisplayName = Some name; Type = t }
    let private patternRef (g: Graph) (b: PatternBinding) = g.Add(SemanticNodeKind.PatternReference b.Id)

    // ---------------------------------------------------------------------------------------------
    // Shared declarations: one effect, a capability, a product, a union and pure callbacks.
    // ---------------------------------------------------------------------------------------------

    let private io: SemanticEffect = { Id = id<EffectTag> "effect-io"; DisplayName = "io"; Exported = true }
    let private resourceParam: SemanticCapabilityParameter = { Id = id<CapabilityParameterTag> "cp-resource"; DisplayName = "resource"; Type = int64T }
    let private release: SemanticCapabilityOperation =
        { MayWait = false; Id = id<CapabilityOperationTag> "op-release"; DisplayName = "release"; Parameters = [ resourceParam ]
          ResultType = unitT; FailureType = None; EffectId = io.Id }
    let private fs: SemanticCapability = { TaskBound = false; Id = id<CapabilityTag> "cap-fs"; DisplayName = "Fs"; Exported = true; Operations = [ release ] }
    let private fieldA: SemanticField = { Id = id<FieldTag> "field-a"; DisplayName = "a"; Type = int64T }
    let private fieldB: SemanticField = { Id = id<FieldTag> "field-b"; DisplayName = "b"; Type = stringT }
    let private pair: SemanticType = { Id = id<TypeTag> "type-pair"; DisplayName = "Pair"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product [ fieldA; fieldB ] }
    let private radius: SemanticField = { Id = id<FieldTag> "field-radius"; DisplayName = "radius"; Type = int64T }
    let private circle: SemanticVariant = { Id = id<VariantTag> "variant-circle"; DisplayName = "Circle"; Fields = [ radius ] }
    let private dot: SemanticVariant = { Id = id<VariantTag> "variant-dot"; DisplayName = "Dot"; Fields = [] }
    let private shape: SemanticType = { Id = id<TypeTag> "type-shape"; DisplayName = "Shape"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Union [ circle; dot ] }
    let private pairT = TypeRef.Named(pair.Id, [])
    let private shapeT = TypeRef.Named(shape.Id, [])

    let private fn2 name (aName, aType) (bName, bType) resultType build =
        let a, b = param name aName aType, param name bName bType
        fn name [ a; b ] [] resultType (fun g -> build g (g.Add(SemanticNodeKind.ParameterReference a.Id)) (g.Add(SemanticNodeKind.ParameterReference b.Id)))

    let private fold2 = fn2 "fold2" ("acc", int64T) ("item", int64T) int64T (fun g a b -> g.Add(SemanticNodeKind.Int64WrappingAdd(a, b)))
    let private fold3 =
        let acc, key, value = param "fold3" "acc" int64T, param "fold3" "key" stringT, param "fold3" "value" int64T
        fn "fold3" [ acc; key; value ] [] int64T (fun g -> g.Add(SemanticNodeKind.ParameterReference acc.Id))
    let private lessThan = fn2 "lessThan" ("left", int64T) ("right", int64T) boolT (fun g a b -> g.Add(SemanticNodeKind.Int64LessThan(a, b)))
    let private keep = fn2 "keep" ("key", stringT) ("value", int64T) boolT (fun g _ _ -> g.Add(SemanticNodeKind.BoolLiteral true))
    let private entryValue = fn2 "entryValue" ("key", stringT) ("value", int64T) int64T (fun _ _ b -> b)
    let private pred1 = fn1 "pred1" ("x", int64T) [] boolT (fun g _ -> g.Add(SemanticNodeKind.BoolLiteral true))
    let private map1 = fn1 "map1" ("x", int64T) [] int64T (fun _ x -> x)
    let private some1 = fn1 "some1" ("x", int64T) [] (optionOf int64T) (fun g x -> g.Add(SemanticNodeKind.OptionSome x))
    let private ok1 = fn1 "ok1" ("x", int64T) [] outcomeT (fun g x -> g.Add(SemanticNodeKind.ResultOk(stringT, x)))
    let private list1 = fn1 "list1" ("x", int64T) [] (listOf int64T) (fun g x -> g.Add(SemanticNodeKind.ListConstruct(int64T, [ x ])))
    let private errMap = fn1 "errMap" ("e", stringT) [] stringT (fun _ e -> e)
    let private unitBody = fn1 "unitBody" ("u", unitT) [] outcomeT (fun g _ -> g.Add(SemanticNodeKind.ResultOk(stringT, g.Lit 1L)))
    let private implRelease = fn1 "implRelease" ("resource", int64T) [] unitT (fun g _ -> g.Add SemanticNodeKind.UnitLiteral)
    let private handler =
        fn1 "handler" ("e", generic BuiltinGenericType.Exception []) [] outcomeT (fun g e ->
            let isCancellation = g.Add(SemanticNodeKind.ExceptionIsCancellation e)
            g.Add(SemanticNodeKind.Conditional(isCancellation,
                graph (fun b -> b.Add(SemanticNodeKind.ExceptionRethrow(e, outcomeT))),
                graph (fun b -> b.Add(SemanticNodeKind.ResultErr(int64T, b.Add(SemanticNodeKind.ExceptionMessage e)))))))
    let private scopeBody =
        fn1 "scopeBody" ("scope", generic BuiltinGenericType.TaskScope []) [] outcomeT (fun g scope ->
            let task = g.Add(SemanticNodeKind.TaskStart(scope, ok1.Id, [], g.Lit 2L))
            let tasks = g.Add(SemanticNodeKind.ListConstruct(generic BuiltinGenericType.Task [ outcomeT ], [ task ]))
            g.Add(SemanticNodeKind.TaskWait(tasks, TaskWaitMode.All, Some(g.Add(SemanticNodeKind.OptionSome(g.Lit 5L))))) |> ignore
            g.Add(SemanticNodeKind.TaskCancel task) |> ignore
            let cell = g.Add(SemanticNodeKind.OneShotCreate(scope, int64T))
            g.Add(SemanticNodeKind.OneShotComplete(cell, g.Lit 3L)) |> ignore
            g.Add(SemanticNodeKind.OneShotWait(cell, None)) |> ignore
            g.Add(SemanticNodeKind.ResultOk(stringT, g.Lit 4L)))

    let private shared = [ fold2; fold3; lessThan; keep; entryValue; pred1; map1; some1; ok1; list1; errMap; unitBody; implRelease; handler; scopeBody ]

    // ---------------------------------------------------------------------------------------------
    // One function per kind
    // ---------------------------------------------------------------------------------------------

    /// A signature type with its variables fixed to equatable types.
    let rec private typeOf (t: OperationSignatures.SignatureType) =
        match t with
        | OperationSignatures.SignatureType.Primitive p -> TypeRef.Primitive p
        | OperationSignatures.SignatureType.Variable "E" -> stringT
        | OperationSignatures.SignatureType.Variable "K" -> stringT
        | OperationSignatures.SignatureType.Variable _ -> int64T
        | OperationSignatures.SignatureType.List item -> listOf (typeOf item)
        | OperationSignatures.SignatureType.Option item -> optionOf (typeOf item)
        | OperationSignatures.SignatureType.Result(ok, err) -> resultOf (typeOf ok) (typeOf err)
        | OperationSignatures.SignatureType.Map(key, value) -> generic BuiltinGenericType.Map [ typeOf key; typeOf value ]
        | OperationSignatures.SignatureType.Set item -> generic BuiltinGenericType.Set [ typeOf item ]

    let private unionCases = FSharpType.GetUnionCases(typeof<SemanticNodeKind>) |> Array.map (fun c -> c.Name, c) |> Map.ofArray

    /// The table kinds: the operands are the function's parameters, in signature order.
    let private tableFunctions =
        OperationSignatures.table
        |> List.map (fun entry ->
            let name = string (Char.ToUpperInvariant entry.Kind[0]) + entry.Kind.Substring 1
            let case = unionCases[name]
            Assert.Equal(entry.Operands.Length, case.GetFields().Length)
            let parameters = entry.Operands |> List.map (fun operand -> param name operand.Field (typeOf operand.Type))
            name,
            fn name parameters [] (typeOf entry.Result) (fun g ->
                let operands = parameters |> List.map (fun p -> box (g.Add(SemanticNodeKind.ParameterReference p.Id)))
                g.Add(FSharpValue.MakeUnion(case, Array.ofList operands) :?> SemanticNodeKind)))

    let private callback name (source: TypeRef) resultType (make: GraphNodeId -> SemanticNodeKind) =
        name, fn1 name ("source", source) [] resultType (fun g source -> g.Add(make source))

    let private fold name (source: TypeRef) (make: GraphNodeId -> GraphNodeId -> SemanticNodeKind) =
        name, fn1 name ("source", source) [] int64T (fun g source -> g.Add(make source (g.Lit 0L)))

    let private mapT = generic BuiltinGenericType.Map [ stringT; int64T ]
    let private setT = generic BuiltinGenericType.Set [ int64T ]
    let private capabilityParam = param "capability" "fs" (TypeRef.Capability fs.Id)

    let private handFunctions =
        [ "ParameterReference", fn1 "ParameterReference" ("x", int64T) [] int64T (fun _ x -> x)
          "StringLiteral", fn "StringLiteral" [] [] stringT (fun g -> g.Add(SemanticNodeKind.StringLiteral "a \"quoted\" é"))
          "BoolLiteral", fn "BoolLiteral" [] [] boolT (fun g -> g.Add(SemanticNodeKind.BoolLiteral false))
          "UnitLiteral", fn "UnitLiteral" [] [] unitT (fun g -> g.Add SemanticNodeKind.UnitLiteral)
          "Int64Literal", fn "Int64Literal" [] [] int64T (fun g -> g.Lit -7L)
          "Float64Literal", fn "Float64Literal" [] [] (TypeRef.Primitive PrimitiveType.Float64) (fun g -> g.Add(SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.5)))
          "ValueEqual", fn "ValueEqual" [] [] boolT (fun g -> g.Add(SemanticNodeKind.ValueEqual(g.Lit 1L, g.Lit 2L)))
          "ListEmpty", fn "ListEmpty" [] [] (listOf int64T) (fun g -> g.Add(SemanticNodeKind.ListEmpty int64T))
          "SetEmpty", fn "SetEmpty" [] [] setT (fun g -> g.Add(SemanticNodeKind.SetEmpty int64T))
          "MapEmpty", fn "MapEmpty" [] [] mapT (fun g -> g.Add(SemanticNodeKind.MapEmpty(stringT, int64T)))
          "ListConstruct", fn "ListConstruct" [] [] (listOf int64T) (fun g -> g.Add(SemanticNodeKind.ListConstruct(int64T, [ g.Lit 1L; g.Lit 2L ])))
          fold "ListFold" (listOf int64T) (fun s seed -> SemanticNodeKind.ListFold(s, seed, fold2.Id, []))
          "ListScan", fn1 "ListScan" ("source", listOf int64T) [] (listOf int64T) (fun g source -> g.Add(SemanticNodeKind.ListScan(source, g.Lit 0L, fold2.Id, [])))
          fold "MapFold" mapT (fun s seed -> SemanticNodeKind.MapFold(s, seed, fold3.Id, []))
          fold "SetFold" setT (fun s seed -> SemanticNodeKind.SetFold(s, seed, fold2.Id, []))
          fold "ListEffectFold" (listOf int64T) (fun s seed -> SemanticNodeKind.ListEffectFold(s, seed, fold2.Id, []))
          callback "ParallelAll" (listOf int64T) (resultOf (listOf int64T) stringT) (fun s -> SemanticNodeKind.ParallelAll(s, ok1.Id, []))
          callback "ListMap" (listOf int64T) (listOf int64T) (fun s -> SemanticNodeKind.ListMap(s, map1.Id, []))
          callback "ListZipWith" (listOf int64T) (listOf int64T) (fun s -> SemanticNodeKind.ListZipWith(s, s, fold2.Id, []))
          callback "ListFlatMap" (listOf int64T) (listOf int64T) (fun s -> SemanticNodeKind.ListFlatMap(s, list1.Id, []))
          callback "MapFromList" (listOf int64T) (generic BuiltinGenericType.Map [ int64T; int64T ]) (fun s -> SemanticNodeKind.MapFromList(s, map1.Id, []))
          callback "ListFilter" (listOf int64T) (listOf int64T) (fun s -> SemanticNodeKind.ListFilter(s, pred1.Id, []))
          callback "MapFilter" mapT mapT (fun s -> SemanticNodeKind.MapFilter(s, keep.Id, []))
          callback "SetFilter" setT setT (fun s -> SemanticNodeKind.SetFilter(s, pred1.Id, []))
          callback "MapMapValues" mapT mapT (fun s -> SemanticNodeKind.MapMapValues(s, entryValue.Id, []))
          callback "SetMap" setT setT (fun s -> SemanticNodeKind.SetMap(s, map1.Id, []))
          callback "ListSortBy" (listOf int64T) (listOf int64T) (fun s -> SemanticNodeKind.ListSortBy(s, lessThan.Id, []))
          callback "ListMinBy" (listOf int64T) (optionOf int64T) (fun s -> SemanticNodeKind.ListMinBy(s, lessThan.Id, []))
          callback "ListMaxBy" (listOf int64T) (optionOf int64T) (fun s -> SemanticNodeKind.ListMaxBy(s, lessThan.Id, []))
          callback "ListAny" (listOf int64T) boolT (fun s -> SemanticNodeKind.ListAny(s, pred1.Id, []))
          callback "ListFindIndex" (listOf int64T) (optionOf int64T) (fun s -> SemanticNodeKind.ListFindIndex(s, pred1.Id, []))
          callback "ListAll" (listOf int64T) boolT (fun s -> SemanticNodeKind.ListAll(s, pred1.Id, []))
          callback "OptionMap" (optionOf int64T) (optionOf int64T) (fun s -> SemanticNodeKind.OptionMap(s, map1.Id, []))
          callback "OptionAndThen" (optionOf int64T) (optionOf int64T) (fun s -> SemanticNodeKind.OptionAndThen(s, some1.Id, []))
          callback "ResultMap" outcomeT outcomeT (fun s -> SemanticNodeKind.ResultMap(s, map1.Id, []))
          callback "ResultMapErr" outcomeT outcomeT (fun s -> SemanticNodeKind.ResultMapErr(s, errMap.Id, []))
          callback "ResultAndThen" outcomeT outcomeT (fun s -> SemanticNodeKind.ResultAndThen(s, ok1.Id, []))
          "UseResource",
          fn "UseResource" [ capabilityParam ] [ io.Id ] outcomeT (fun g ->
              let acquire = g.Add(SemanticNodeKind.ResultOk(stringT, g.Lit 3L))
              let capability = g.Add(SemanticNodeKind.ParameterReference capabilityParam.Id)
              g.Add(SemanticNodeKind.UseResource(acquire, capability, release.Id, ok1.Id, [])))
          "ExceptionCatch", fn "ExceptionCatch" [] [] outcomeT (fun g -> g.Add(SemanticNodeKind.ExceptionCatch(unitBody.Id, [], handler.Id, [])))
          "CancellationShield", fn "CancellationShield" [] [] outcomeT (fun g -> g.Add(SemanticNodeKind.CancellationShield(unitBody.Id, [])))
          "TaskScope", fn "TaskScope" [] [] outcomeT (fun g -> g.Add(SemanticNodeKind.TaskScope(scopeBody.Id, [])))
          "DirectCall", fn "DirectCall" [] [] int64T (fun g -> g.Add(SemanticNodeKind.DirectCall(map1.Id, [ { TargetParameterId = map1.Parameters.Head.Id; ValueNodeId = g.Lit 1L } ])))
          "CapabilityConstruct", fn "CapabilityConstruct" [] [] (TypeRef.Capability fs.Id) (fun g -> g.Add(SemanticNodeKind.CapabilityConstruct(fs.Id, [ release.Id, implRelease.Id ])))
          "FunctionReference", fn "FunctionReference" [] [] (TypeRef.Function([ int64T ], int64T)) (fun g -> g.Add(SemanticNodeKind.FunctionReference map1.Id))
          "FunctionInvoke", fn "FunctionInvoke" [] [] int64T (fun g -> g.Add(SemanticNodeKind.FunctionInvoke(g.Add(SemanticNodeKind.FunctionReference map1.Id), [ g.Lit 1L ])))
          "CapabilityInvoke",
          fn "CapabilityInvoke" [ capabilityParam ] [ io.Id ] unitT (fun g ->
              let capability = g.Add(SemanticNodeKind.ParameterReference capabilityParam.Id)
              g.Add(SemanticNodeKind.CapabilityInvoke(capability, release.Id, [ { TargetCapabilityParameterId = resourceParam.Id; ValueNodeId = g.Lit 1L } ])))
          "ProductConstruct", fn "ProductConstruct" [] [] pairT (fun g -> g.Add(SemanticNodeKind.ProductConstruct(pair.Id, [], [ { TargetFieldId = fieldA.Id; ValueNodeId = g.Lit 1L }; { TargetFieldId = fieldB.Id; ValueNodeId = g.Add(SemanticNodeKind.StringLiteral "b") } ])))
          "FieldAccess", fn1 "FieldAccess" ("p", pairT) [] int64T (fun g p -> g.Add(SemanticNodeKind.FieldAccess(p, fieldA.Id)))
          "ProductUpdate", fn1 "ProductUpdate" ("p", pairT) [] pairT (fun g p -> g.Add(SemanticNodeKind.ProductUpdate(pair.Id, p, [ { TargetFieldId = fieldA.Id; ValueNodeId = g.Lit 1L } ])))
          "UnionConstruct", fn "UnionConstruct" [] [] shapeT (fun g -> g.Add(SemanticNodeKind.UnionConstruct(circle.Id, [], [ { TargetFieldId = radius.Id; ValueNodeId = g.Lit 1L } ])))
          "MatchUnion",
          fn1 "MatchUnion" ("u", shapeT) [] int64T (fun g u ->
              let r = binding "radius" int64T
              g.Add(SemanticNodeKind.MatchUnion(u,
                  [ { VariantIds = [ circle.Id ]; FieldBindings = [ { SourceFieldId = radius.Id; Binding = r } ]; Body = graph (fun b -> patternRef b r) }
                    { VariantIds = [ dot.Id ]; FieldBindings = []; Body = graph (fun b -> b.Lit 0L) } ])))
          "OptionNone", fn "OptionNone" [] [] (optionOf int64T) (fun g -> g.Add(SemanticNodeKind.OptionNone int64T))
          "OptionSome", fn "OptionSome" [] [] (optionOf int64T) (fun g -> g.Add(SemanticNodeKind.OptionSome(g.Lit 1L)))
          "ResultOk", fn "ResultOk" [] [] outcomeT (fun g -> g.Add(SemanticNodeKind.ResultOk(stringT, g.Lit 1L)))
          "ResultErr", fn "ResultErr" [] [] outcomeT (fun g -> g.Add(SemanticNodeKind.ResultErr(int64T, g.Add(SemanticNodeKind.StringLiteral "e"))))
          "Abort", fn "Abort" [] [] outcomeT (fun g -> g.Add(SemanticNodeKind.Abort(g.Add(SemanticNodeKind.StringLiteral "stop"))))
          "BoolAnd", fn "BoolAnd" [] [] boolT (fun g -> g.Add(SemanticNodeKind.BoolAnd(g.Add(SemanticNodeKind.BoolLiteral true), graph (fun b -> b.Add(SemanticNodeKind.BoolLiteral false)))))
          "BoolOr", fn "BoolOr" [] [] boolT (fun g -> g.Add(SemanticNodeKind.BoolOr(g.Add(SemanticNodeKind.BoolLiteral true), graph (fun b -> b.Add(SemanticNodeKind.BoolLiteral false)))))
          "Conditional", fn "Conditional" [] [] int64T (fun g -> g.Add(SemanticNodeKind.Conditional(g.Add(SemanticNodeKind.BoolLiteral true), graph (fun b -> b.Lit 1L), graph (fun b -> b.Lit 2L))))
          "Bind",
          fn "Bind" [] [] int64T (fun g ->
              let x = binding "bound" int64T
              g.Add(SemanticNodeKind.Bind(g.Lit 1L, { ValueBinding = x; Body = graph (fun b -> patternRef b x) })))
          "MatchOption",
          fn "MatchOption" [] [] int64T (fun g ->
              let x = binding "some" int64T
              g.Add(SemanticNodeKind.MatchOption(g.Add(SemanticNodeKind.OptionNone int64T), { ValueBinding = x; Body = graph (fun b -> patternRef b x) }, graph (fun b -> b.Lit 0L))))
          "MatchResult",
          fn "MatchResult" [] [] int64T (fun g ->
              let ok, err = binding "ok" int64T, binding "err" stringT
              g.Add(SemanticNodeKind.MatchResult(g.Add(SemanticNodeKind.ResultOk(stringT, g.Lit 1L)),
                  { ValueBinding = ok; Body = graph (fun b -> patternRef b ok) },
                  { ErrorBinding = err; Body = graph (fun b -> b.Add(SemanticNodeKind.StringLength(patternRef b err))) }))) ]

    /// Kinds that only occur inside a shared callback: the function that holds them.
    let private nestedKinds =
        [ yield "PatternReference", handFunctions |> List.find (fst >> (=) "Bind") |> snd
          for kind in [ "ExceptionIsCancellation"; "ExceptionMessage"; "ExceptionRethrow" ] -> kind, handler
          for kind in [ "TaskStart"; "TaskWait"; "TaskCancel"; "OneShotCreate"; "OneShotComplete"; "OneShotWait" ] -> kind, scopeBody ]

    let private functionsByKind = lazy (Map.ofList (tableFunctions @ handFunctions @ nestedKinds))

    let private snapshotFor (fn: SemanticFunction) : Snapshot =
        { Id = id<SnapshotTag> "snapshot"; ParentId = None; Types = [ pair; shape ]; Effects = [ io ]; Capabilities = [ fs ]
          Functions = shared @ (if List.contains fn shared then [] else [ fn ]); Obligations = [] }

    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected codec error: %A" error

    let private kindNames = FSharpType.GetUnionCases(typeof<SemanticNodeKind>) |> Array.map (fun c -> c.Name)

    let everyKind: obj array seq = kindNames |> Seq.map (fun name -> [| box name |])

    [<Theory>]
    [<MemberData(nameof everyKind)>]
    let ``record text round-trips a valid snapshot holding the kind`` (kind: string) =
        let fn =
            match Map.tryFind kind functionsByKind.Value with
            | Some fn -> fn
            | None -> failwithf "No valid-snapshot builder covers node kind %s; add one here." kind
        let built = snapshotFor fn
        Assert.Empty(SnapshotValidator.validate built |> List.map (fun issue -> $"{issue.Code}: {issue.Message}"))
        let original = SnapshotCodec.serialize built |> unwrap |> SnapshotCodec.deserialize |> unwrap
        let kinds = original.Functions |> List.collect (fun f -> ValueGraph.collectNodes f.Body) |> List.map (fun n -> SemanticNodeKind.name n.Kind)
        Assert.Contains(kind, kinds)
        let text = RecordCodec.serialize original |> unwrap
        let restored = RecordCodec.deserialize text |> unwrap
        Assert.Equal(original, restored)
        Assert.Equal(text, RecordCodec.serialize restored |> unwrap)
        Assert.Equal(SnapshotFingerprint.compute original |> unwrap, SnapshotFingerprint.compute restored |> unwrap)
        Assert.Equal(SemanticFingerprint.compute original |> unwrap, SemanticFingerprint.compute restored |> unwrap)

    [<Fact>]
    let ``every node kind has a builder and no builder names an unknown kind`` () =
        Assert.Equal<string list>(List.sort (List.ofArray kindNames), functionsByKind.Value |> Map.keys |> List.ofSeq |> List.sort)
