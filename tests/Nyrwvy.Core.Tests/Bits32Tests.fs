namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The `Bits32` domain (post-M2 order 30): conversions from Int64 (low 32 bits) and to Int64
/// (unsigned), Xor/And/Or/Not, wrapping Add, RotateRight (amount floor-modulo 32) and logical
/// shifts (amount <= 0 unchanged, >= 32 zero). Every operation is total. Run values are
/// `{"kind":"bits32","value": <0..4294967295 as a JSON integer or decimal string>}`, printed as a
/// decimal string like Int64.
module Bits32Tests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private wordType = TypeRef.Primitive PrimitiveType.Bits32
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private word (value: uint32) = PureRunValue.Bits32 value
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
          TypeParameters = None
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = []; Body = body }
    let private create session name parameters resultType body =
        request session name parameters resultType body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private rejects (session: AuthoringSession) parameters resultType body =
        match session.SimulateCreateFunction(request session "rejected" parameters resultType body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwith "The function was accepted."
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-bits32-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private execute session line = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
    let private send session line =
        let response = execute session line
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private errorCode session line =
        use response = execute session line
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response.RootElement.GetProperty("error").GetProperty("code").GetString()

    // A small name-resolved recipe writer, so wire requests read like the recipes they send.
    let private q (text: string) = JsonSerializer.Serialize text
    let private obj (fields: (string * string) list) = "{" + (fields |> List.map (fun (key, value) -> q key + ":" + value) |> String.concat ",") + "}"
    let private arr (items: string list) = "[" + String.concat "," items + "]"
    let private node kind fields = obj (("kind", q kind) :: fields)
    let private prim name = node "primitive" [ "primitive", q name ]
    let private S, I, W = prim "string", prim "int64", prim "bits32"
    let private L item = node "builtinGeneric" [ "generic", q "list"; "arguments", arr [ item ] ]
    let private N name = node "named" [ "name", q name ]
    let private p name = node "parameter" [ "name", q name ]
    let private i (value: int64) = node "int64Literal" [ "value", string value ]
    let private s (text: string) = node "stringLiteral" [ "value", q text ]
    let private v depth = node "patternReference" [ "scopeDepth", string depth; "slot", q "value" ]
    let private un kind value = node kind [ "value", value ]
    let private bin kind left right = node kind [ "left", left; "right", right ]
    let private shift kind value amount = node kind [ "value", value; "amount", amount ]
    let private w32 = un "bits32FromInt64"
    let private toI = un "bits32ToInt64"
    let private word32 (value: int64) = w32 (i value)
    let private bxor, band, bor, badd = bin "bits32Xor", bin "bits32And", bin "bits32Or", bin "bits32Add"
    let private rotr, shr, shl = shift "bits32RotateRight", shift "bits32ShiftRight", shift "bits32ShiftLeft"
    let private add, sub, mul = bin "int64WrappingAdd", bin "int64WrappingSubtract", bin "int64WrappingMultiply"
    let private div, md, eq, lt = bin "int64FloorDivide", bin "int64FloorModulo", bin "int64Equal", bin "int64LessThan"
    let private args (items: (string * string) list) = arr (items |> List.map (fun (name, value) -> obj [ "parameter", q name; "value", value ]))
    let private call name items = node "directCall" [ "function", q name; "arguments", args items ]
    let private field source typeName name = node "fieldAccess" [ "source", source; "type", q typeName; "field", q name ]
    let private product typeName (items: (string * string) list) =
        node "productConstruct" [ "type", q typeName; "fields", arr (items |> List.map (fun (name, value) -> obj [ "field", q name; "value", value ])) ]
    let private bind value body = node "bind" [ "value", value; "body", body ]
    let private cond test whenTrue whenFalse = node "conditional" [ "condition", test; "whenTrue", whenTrue; "whenFalse", whenFalse ]
    let private list itemType items = node "listConstruct" [ "itemType", itemType; "items", arr items ]
    let private fold source seed name context =
        node "listFold" ([ "source", source; "seed", seed; "function", q name ] @ (if List.isEmpty context then [] else [ "context", args context ]))

    [<Fact>]
    let ``Bits32 operations check operand types and arity in recipes and persisted graphs`` () =
        withSession (fun session _ ->
            let fromInt = ExpressionRecipe.Bits32FromInt64
            create session "low" [ "value", intType ] wordType (fromInt (slot 0)) |> ignore
            create session "rotate" [ "value", wordType; "amount", intType ] wordType (ExpressionRecipe.Bits32RotateRight(slot 0, slot 1)) |> ignore
            let mismatch = "PureExpressionOperandTypeMismatch"
            Assert.Contains(mismatch, rejects session [ "value", wordType ] wordType (fromInt (slot 0)))
            Assert.Contains(mismatch, rejects session [ "value", intType ] intType (ExpressionRecipe.Bits32ToInt64(slot 0)))
            Assert.Contains(mismatch, rejects session [ "a", wordType; "b", intType ] wordType (ExpressionRecipe.Bits32Xor(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", intType; "b", intType ] wordType (ExpressionRecipe.Bits32Add(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", intType ] wordType (ExpressionRecipe.Bits32Not(slot 0)))
            // The amount is an Int64, not a Bits32, and the value is not an Int64.
            Assert.Contains(mismatch, rejects session [ "a", wordType; "b", wordType ] wordType (ExpressionRecipe.Bits32ShiftRight(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", intType; "b", intType ] wordType (ExpressionRecipe.Bits32ShiftLeft(slot 0, slot 1)))
            // Results are Bits32, and a Bits32 is not an Int64.
            rejects session [ "value", intType ] intType (fromInt (slot 0)) |> ignore
            rejects session [ "value", wordType ] intType (slot 0) |> ignore
            // Wire arity: binary kinds need left and right, shifts value and amount; nothing else is accepted.
            let signature = sprintf """"parameters":[{"displayName":"a","type":%s},{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[]""" W I W
            let create body = sprintf """{"id":"c","op":"simulateCreateFunctionByName","displayName":"f",%s,"body":%s}""" signature body
            for body in
                [ """{"kind":"bits32Xor","left":{"kind":"parameter","name":"a"}}"""
                  """{"kind":"bits32Not","value":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"a"}}"""
                  """{"kind":"bits32RotateRight","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"n"}}"""
                  """{"kind":"bits32ShiftLeft","value":{"kind":"parameter","name":"a"}}"""
                  """{"kind":"bits32Literal","value":1}""" ] do
                Assert.Equal("ProtocolError", errorCode session (create body))
            use accepted = send session (create (rotr (p "a") (p "n")))
            ())
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-bits-" + name)
        let validate (nodes: SemanticNodeKind list) resultType =
            let body = nodes |> List.mapi (fun index kind -> ({ Id = nodeId (string index); Kind = kind }: SemanticNode))
            let fn: SemanticFunction =
                { Id = StableId.createForTesting<FunctionTag> "function-bits-entry"; DisplayName = "entry"; Parameters = []
                  TypeParameters = []
                  ResultType = resultType; RequiredEffects = []
                  Body = { Nodes = body; ResultNodeId = (List.last body).Id } }
            SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-bits") with Functions = [ fn ] }
            |> List.map (fun issue -> issue.Code)
        let one = SemanticNodeKind.Int64Literal 1L
        let word = SemanticNodeKind.Bits32FromInt64(nodeId "0")
        Assert.Empty(validate [ one; word ] wordType)
        Assert.Empty(validate [ one; word; SemanticNodeKind.Bits32RotateRight(nodeId "1", nodeId "0") ] wordType)
        Assert.Empty(validate [ one; word; SemanticNodeKind.Bits32ToInt64(nodeId "1") ] intType)
        Assert.Contains("Bits32OperandTypeMismatch", validate [ SemanticNodeKind.StringLiteral "1"; SemanticNodeKind.Bits32FromInt64(nodeId "0") ] wordType)
        Assert.Contains("Bits32OperandTypeMismatch", validate [ one; SemanticNodeKind.Bits32Not(nodeId "0") ] wordType)
        Assert.Contains("Bits32OperandTypeMismatch", validate [ one; word; SemanticNodeKind.Bits32Xor(nodeId "1", nodeId "0") ] wordType)
        Assert.Contains("Bits32OperandTypeMismatch", validate [ one; word; SemanticNodeKind.Bits32ShiftRight(nodeId "1", nodeId "1") ] wordType)
        Assert.Contains("Bits32OperandTypeMismatch", validate [ one; word; SemanticNodeKind.Bits32ShiftLeft(nodeId "0", nodeId "0") ] wordType)
        Assert.NotEmpty(validate [ one; word ] intType)
        // Bits32 has no NaN, so it is equatable like Int64 and ValueEqual accepts it.
        Assert.True(EquatableTypes.isEquatable [] (listOf wordType))
        Assert.Empty(validate [ one; word; SemanticNodeKind.ValueEqual(nodeId "1", nodeId "1") ] (TypeRef.Primitive PrimitiveType.Bool))

    [<Fact>]
    let ``every Bits32 kind persists reopens projects lowers and emits`` () =
        withSession (fun session path ->
            let signature = sprintf """"parameters":[{"displayName":"a","type":%s},{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[]""" I I I
            let a = w32 (p "a")
            let body = toI (badd (bxor (band a (un "bits32Not" a)) (bor a a)) (rotr (shr (shl a (p "n")) (p "n")) (p "n")))
            use named =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"mix",%s,"body":%s}""" signature body)
            session.Commit(named.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let kinds = [ "bits32FromInt64"; "bits32ToInt64"; "bits32Xor"; "bits32And"; "bits32Or"; "bits32Not"; "bits32Add"; "bits32RotateRight"; "bits32ShiftRight"; "bits32ShiftLeft" ]
            for kind in kinds do Assert.Contains($"\"kind\":\"{kind}\"", named.RootElement.GetRawText())
            let fn = find session "mix"
            let viewer = session.FocusFunction fn.Id |> unwrap |> ViewerProjection.focus
            let rotate = viewer.Graph.Nodes |> List.find (fun item -> item.Role = ViewerNodeRole.Operation "Bits32RotateRight")
            Assert.Equal<ValueDependencyRole list>([ ValueDependencyRole.Source; ValueDependencyRole.Amount ], rotate.ValueDependencies |> List.map (fun item -> item.Role))
            let html = ViewerHtml.renderFocusDocument viewer
            for label in [ "bits32 rotate right"; "bits32 shift left"; "bits32 to int64" ] do Assert.Contains(label, html)
            session.Close()
            let encoded = File.ReadAllText path
            for kind in kinds do Assert.Contains($"\"kind\":\"{kind}\"", encoded)
            Assert.Contains("\"amountNodeId\"", encoded)
            let graph = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            for tag in [ "bits32-from-int64"; "bits32-rotate-right"; "bits32-shift-left" ] do Assert.Contains(tag, nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            let typedJson = TypedSemanticIrSerializer.serialize typed
            let execution = ExecutionIr.lower typed |> unwrap
            let executionJson = ExecutionIrSerializer.serialize execution
            for kind in kinds do
                Assert.Contains($"\"kind\":\"{kind}\"", typedJson)
                Assert.Contains($"\"kind\":\"{kind}\"", executionJson)
            Assert.Contains("\"primitiveBits32\"", executionJson)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            for fragment in [ "uint "; "unchecked((uint)"; "(long)"; " ^ "; " & "; " | "; "~"; "System.Numerics.BitOperations.RotateRight("; " & 31L))"; " >= 32L ? 0u : "; " >> (int)"; " << (int)" ] do
                Assert.Contains(fragment, source)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(fn.Body.ResultNodeId, (find reopened "mix").Body.ResultNodeId))

    [<Fact>]
    let ``Bits32 run values accept only unsigned 32-bit integers`` () =
        withSession (fun session _ ->
            let identity = create session "identity" [ "value", wordType ] wordType (slot 0)
            let run value = sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[%s]}""" (StableId.value identity.Id) value
            for value in
                [ """{"kind":"bits32","value":-1}"""; """{"kind":"bits32","value":4294967296}"""; """{"kind":"bits32","value":"-1"}"""
                  """{"kind":"bits32","value":"4294967296"}"""; """{"kind":"bits32","value":"0x10"}"""; """{"kind":"bits32","value":1.5}"""
                  """{"kind":"bits32","value":"+1"}"""; """{"kind":"bits32","value":null}"""; """{"kind":"bits32"}"""
                  """{"kind":"bits32","value":1,"extra":0}"""; """{"kind":"list","itemKind":"bits32","items":[]}"""
                  """{"kind":"option","itemKind":"bits32","hasValue":false}""" ] do
                Assert.Equal("ProtocolError", errorCode session (run value))
            // A Bits32 where an Int64 is declared is a type error, not a conversion.
            let intIdentity = create session "intIdentity" [ "value", intType ] intType (slot 0)
            Assert.Equal("RunArgumentTypeMismatch",
                errorCode session (sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[{"kind":"bits32","value":1}]}""" (StableId.value intIdentity.Id))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Bits32 operations are total at every edge through runPure`` () =
        withSession (fun session path ->
            let roundTrip = create session "roundTrip" [ "value", intType ] intType (ExpressionRecipe.Bits32ToInt64(ExpressionRecipe.Bits32FromInt64(slot 0)))
            let roundTripAll = create session "roundTripAll" [ "values", listOf intType ] (listOf intType) (ExpressionRecipe.ListMap(slot 0, roundTrip.Id, []))
            let byAmount name constructor =
                let step = create session name [ "value", wordType; "amount", intType ] wordType (constructor (slot 0, slot 1))
                create session (name + "All") [ "value", wordType; "amounts", listOf intType ] (listOf wordType)
                    (ExpressionRecipe.ListMap(slot 1, step.Id, [ step.Parameters.Head.Id, slot 0 ]))
            let rotateAll = byAmount "rotate" ExpressionRecipe.Bits32RotateRight
            let shiftRightAll = byAmount "shiftRight" ExpressionRecipe.Bits32ShiftRight
            let shiftLeftAll = byAmount "shiftLeft" ExpressionRecipe.Bits32ShiftLeft
            create session "mix" [ "a", wordType; "b", wordType ] (listOf wordType)
                (ExpressionRecipe.ListConstruct(wordType,
                    [ ExpressionRecipe.Bits32Xor(slot 0, slot 1); ExpressionRecipe.Bits32And(slot 0, slot 1); ExpressionRecipe.Bits32Or(slot 0, slot 1)
                      ExpressionRecipe.Bits32Not(slot 0); ExpressionRecipe.Bits32Add(slot 0, slot 1) ])) |> ignore
            create session "unsigned" [ "value", wordType ] intType (ExpressionRecipe.Bits32ToInt64(slot 0)) |> ignore
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            // FromInt64 keeps the low 32 bits of the two's complement form; ToInt64 reads them unsigned.
            let two32 = 4294967296L
            Assert.Equal(
                PureRunValue.Int64List [ 0L; 1L; 4294967295L; 4294967294L; 0L; 0L; 5L; 4294967295L; 0L; 4294967295L; 2147483648L ],
                run roundTripAll [ PureRunValue.Int64List [ 0L; 1L; -1L; -2L; two32; -two32; two32 + 5L; two32 - 1L; Int64.MinValue; Int64.MaxValue; -2147483648L ] ])
            let amounts = [ 0L; 1L; 31L; 32L; 33L; -1L; -31L; 64L; (1L <<< 62) + 1L; Int64.MinValue; Int64.MaxValue ]
            let value = word 0x80000001u
            // Rotation takes the amount floor-modulo 32: -1 is 31 (rotate left by one) and MinValue is 0.
            Assert.Equal(
                PureRunValue.ListValue(wordType,
                    [ 0x80000001u; 0xC0000000u; 0x00000003u; 0x80000001u; 0xC0000000u; 0x00000003u; 0xC0000000u; 0x80000001u; 0xC0000000u; 0x80000001u; 0x00000003u ]
                    |> List.map word),
                run rotateAll [ value; PureRunValue.Int64List amounts ])
            // Logical shifts: amount <= 0 keeps the value, amount >= 32 gives zero.
            let shiftAmounts = [ -1L; 0L; 1L; 31L; 32L; 64L; Int64.MinValue; Int64.MaxValue ]
            Assert.Equal(
                PureRunValue.ListValue(wordType, [ 0x80000001u; 0x80000001u; 0x40000000u; 1u; 0u; 0u; 0x80000001u; 0u ] |> List.map word),
                run shiftRightAll [ value; PureRunValue.Int64List shiftAmounts ])
            Assert.Equal(
                PureRunValue.ListValue(wordType, [ 0x80000001u; 0x80000001u; 2u; 0x80000000u; 0u; 0u; 0x80000001u; 0u ] |> List.map word),
                run shiftLeftAll [ value; PureRunValue.Int64List shiftAmounts ])
            // The wire form: an unsigned decimal string out, a JSON integer or decimal string in; Add wraps.
            let runByName name arguments expected =
                let response = send reopened (sprintf """{"id":"run","op":"runPureByName","function":"%s","arguments":[%s],"expected":%s}""" name arguments expected)
                response.RootElement.GetProperty("result").Clone()
            let words (values: string list) = sprintf """{"kind":"list","itemType":%s,"items":[%s]}""" W (values |> List.map (sprintf """{"kind":"bits32","value":"%s"}""") |> String.concat ",")
            let mixed = runByName "mix" """{"kind":"bits32","value":4294967295},{"kind":"bits32","value":"2"}""" (words [ "4294967293"; "2"; "4294967295"; "0"; "1" ])
            Assert.True(mixed.GetProperty("matches").GetBoolean(), mixed.ToString())
            let printed = mixed.GetProperty("actual").GetProperty("items").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("value").GetRawText()) |> Seq.toList
            Assert.Equal<string list>([ "\"4294967293\""; "\"2\""; "\"4294967295\""; "\"0\""; "\"1\"" ], printed)
            let high = runByName "unsigned" """{"kind":"bits32","value":"3735928559"}""" """{"kind":"int64","value":"3735928559"}"""
            Assert.True(high.GetProperty("matches").GetBoolean(), high.ToString())
            let missed = runByName "mix" """{"kind":"bits32","value":0},{"kind":"bits32","value":0}""" (words [ "0"; "0"; "0"; "0"; "0" ])
            Assert.False(missed.GetProperty("matches").GetBoolean())
            Assert.Equal("\"4294967295\"", missed.GetProperty("mismatch").GetProperty("actual").GetProperty("value").GetRawText()))

    /// SHA-256 of the UTF-8 bytes of a String, authored by name with Bits32 (probe 12's structure,
    /// with its Int64 bit emulation replaced by Bits32 operations).
    let private authorSha256 (session: AuthoringSession) =
        let mutable nodes = 0
        let commit (response: JsonDocument) =
            let result = response.RootElement.GetProperty("result")
            session.Commit(result.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            result
        let fn name (parameters: (string * string) list) result body =
            let parameters = parameters |> List.map (fun (name, t) -> obj [ "displayName", q name; "type", t ]) |> arr
            use response =
                send session (sprintf """{"id":"%s","op":"simulateCreateFunctionByName","displayName":"%s","parameters":%s,"resultType":%s,"requiredEffects":[],"body":%s}""" name name parameters result body)
            nodes <- nodes + (commit response).GetProperty("review").GetProperty("createdFunction").GetProperty("body").GetProperty("nodeCount").GetInt32()
        let fields (items: (string * string) list) = items |> List.map (fun (name, t) -> obj [ "displayName", q name; "type", t ]) |> arr
        let productType name items = obj [ "displayName", q name; "typeKind", node "product" [ "fields", fields items ] ]
        use types =
            send session (sprintf """{"id":"types","op":"simulateCreateTypesByName","types":%s}"""
                (arr [ productType "Sha" [ "h", L W; "cur", W; "words", L W; "count", I ]
                       productType "Rnd" ([ for name in "abcdefgh" -> string name, W ] @ [ "t", I ]) ]))
        commit types |> ignore
        let K =
            [ 0x428a2f98L; 0x71374491L; 0xb5c0fbcfL; 0xe9b5dba5L; 0x3956c25bL; 0x59f111f1L; 0x923f82a4L; 0xab1c5ed5L
              0xd807aa98L; 0x12835b01L; 0x243185beL; 0x550c7dc3L; 0x72be5d74L; 0x80deb1feL; 0x9bdc06a7L; 0xc19bf174L
              0xe49b69c1L; 0xefbe4786L; 0x0fc19dc6L; 0x240ca1ccL; 0x2de92c6fL; 0x4a7484aaL; 0x5cb0a9dcL; 0x76f988daL
              0x983e5152L; 0xa831c66dL; 0xb00327c8L; 0xbf597fc7L; 0xc6e00bf3L; 0xd5a79147L; 0x06ca6351L; 0x14292967L
              0x27b70a85L; 0x2e1b2138L; 0x4d2c6dfcL; 0x53380d13L; 0x650a7354L; 0x766a0abbL; 0x81c2c92eL; 0x92722c85L
              0xa2bfe8a1L; 0xa81a664bL; 0xc24b8b70L; 0xc76c51a3L; 0xd192e819L; 0xd6990624L; 0xf40e3585L; 0x106aa070L
              0x19a4c116L; 0x1e376c08L; 0x2748774cL; 0x34b0bcb5L; 0x391c0cb3L; 0x4ed8aa4aL; 0x5b9cca4fL; 0x682e6ff3L
              0x748f82eeL; 0x78a5636fL; 0x84c87814L; 0x8cc70208L; 0x90befffaL; 0xa4506cebL; 0xbef9a3f7L; 0xc67178f2L ]
        let H0 = [ 0x6a09e667L; 0xbb67ae85L; 0x3c6ef372L; 0xa54ff53aL; 0x510e527fL; 0x9b05688cL; 0x1f83d9abL; 0x5be0cd19L ]
        let x = p "x"
        fn "at" [ "xs", L W; "n", I ] W (node "matchOption" [ "input", node "listItemAt" [ "list", p "xs"; "index", p "n" ]; "someBody", v 0; "noneBody", word32 0L ])
        fn "hexd" [ "n", I ] S (node "stringSlice" [ "value", s "0123456789abcdef"; "start", p "n"; "end", add (p "n") (i 1L) ])
        let sigma name r1 r2 third = fn name [ "x", W ] W (bxor (bxor (rotr x (i r1)) (rotr x (i r2))) third)
        sigma "bigS0" 2L 13L (rotr x (i 22L))
        sigma "bigS1" 6L 11L (rotr x (i 25L))
        sigma "smallS0" 7L 18L (shr x (i 3L))
        sigma "smallS1" 17L 19L (shr x (i 10L))
        fn "choose" [ "e", W; "f", W; "g", W ] W (bxor (band (p "e") (p "f")) (band (un "bits32Not" (p "e")) (p "g")))
        fn "majority" [ "a", W; "b", W; "c", W ] W (bxor (bxor (band (p "a") (p "b")) (band (p "a") (p "c"))) (band (p "b") (p "c")))
        fn "kList" [] (L I) (list I (K |> List.map i))
        let wAt offset = call "at" [ "xs", p "w"; "n", sub (un "listLength" (p "w")) (i offset) ]
        fn "schedStep" [ "w", L W; "k", I ] (L W)
            (node "listAppendItem" [ "list", p "w"; "item", badd (badd (call "smallS1" [ "x", wAt 2L ]) (wAt 7L)) (badd (call "smallS0" [ "x", wAt 15L ]) (wAt 16L)) ])
        let st name = field (p "st") "Rnd" name
        fn "roundStep" [ "st", N "Rnd"; "k", I; "w", L W ] (N "Rnd")
            (bind (badd (badd (badd (st "h") (call "bigS1" [ "x", st "e" ])) (call "choose" [ "e", st "e"; "f", st "f"; "g", st "g" ]))
                        (badd (w32 (p "k")) (call "at" [ "xs", p "w"; "n", st "t" ])))
                (bind (badd (call "bigS0" [ "x", st "a" ]) (call "majority" [ "a", st "a"; "b", st "b"; "c", st "c" ]))
                    (product "Rnd"
                        [ "a", badd (v 1) (v 0); "b", st "a"; "c", st "b"; "d", st "c"; "e", badd (st "d") (v 1)
                          "f", st "e"; "g", st "f"; "h", st "g"; "t", add (st "t") (i 1L) ])))
        let hAt n = call "at" [ "xs", p "h"; "n", i n ]
        let letters = [ for name in "abcdefgh" -> string name ]
        fn "compress" [ "h", L W; "words", L W ] (L W)
            (bind (call "kList" [])
                (bind (fold (node "listSlice" [ "list", v 0; "start", i 16L; "end", i 64L ]) (p "words") "schedStep" [])
                    (bind (fold (v 1) (product "Rnd" ((letters |> List.mapi (fun n name -> name, hAt (int64 n))) @ [ "t", i 0L ])) "roundStep" [ "w", v 0 ])
                        (list W (letters |> List.mapi (fun n name -> badd (hAt (int64 n)) (field (v 0) "Rnd" name)))))))
        let sh name = field (p "st") "Sha" name
        fn "feed" [ "st", N "Sha"; "byte", I ] (N "Sha")
            (bind (bor (shl (sh "cur") (i 8L)) (w32 (p "byte")))
                (bind (add (sh "count") (i 1L))
                    (cond (un "boolNot" (eq (md (v 0) (i 4L)) (i 0L)))
                        (product "Sha" [ "h", sh "h"; "cur", v 1; "words", sh "words"; "count", v 0 ])
                        (bind (node "listAppendItem" [ "list", sh "words"; "item", v 1 ])
                            (cond (eq (un "listLength" (v 0)) (i 16L))
                                (product "Sha" [ "h", call "compress" [ "h", sh "h"; "words", v 0 ]; "cur", word32 0L; "words", node "listEmpty" [ "itemType", W ]; "count", v 1 ])
                                (product "Sha" [ "h", sh "h"; "cur", word32 0L; "words", v 0; "count", v 1 ]))))))
        let feed state byte = call "feed" [ "st", state; "byte", byte ]
        let cp = v 0
        let cont shiftBy = add (i 0x80L) (md (div cp (i shiftBy)) (i 64L))
        fn "feedCp" [ "st", N "Sha"; "c", S ] (N "Sha")
            (bind (node "matchOption" [ "input", un "stringToCodePoint" (p "c"); "someBody", v 0; "noneBody", i -1L ])
                (cond (lt cp (i 0x80L)) (feed (p "st") cp)
                    (cond (lt cp (i 0x800L)) (feed (feed (p "st") (add (i 0xC0L) (div cp (i 64L)))) (cont 1L))
                        (cond (lt cp (i 0x10000L))
                            (feed (feed (feed (p "st") (add (i 0xE0L) (div cp (i 4096L)))) (cont 64L)) (cont 1L))
                            (feed (feed (feed (feed (p "st") (add (i 0xF0L) (div cp (i 262144L)))) (cont 4096L)) (cont 64L)) (cont 1L))))))
        fn "feedZero" [ "st", N "Sha"; "k", I ] (N "Sha") (feed (p "st") (i 0L))
        fn "feedLen" [ "st", N "Sha"; "d", I; "bits", I ] (N "Sha") (feed (p "st") (md (div (p "bits") (p "d")) (i 256L)))
        fn "hexNibble" [ "acc", S; "k", I; "w", W ] S
            (bin "stringConcat" (p "acc") (call "hexd" [ "n", toI (band (shr (p "w") (sub (i 28L) (mul (p "k") (i 4L)))) (word32 15L)) ]))
        fn "hexWord" [ "acc", S; "w", W ] S (fold (list I [ for n in 0L .. 7L -> i n ]) (p "acc") "hexNibble" [ "w", p "w" ])
        let count = field (v 0) "Sha" "count"
        fn "sha256hex" [ "text", S ] S
            (bind (fold (un "stringCodePoints" (p "text")) (product "Sha" [ "h", list W (H0 |> List.map word32); "cur", word32 0L; "words", node "listEmpty" [ "itemType", W ]; "count", i 0L ]) "feedCp" [])
                (bind (mul count (i 8L))
                    (bind (feed (v 1) (i 0x80L))
                        (bind (md (sub (i 56L) (md count (i 64L))) (i 64L))
                            (bind (fold (node "listSlice" [ "list", call "kList" []; "start", i 0L; "end", v 0 ]) (v 1) "feedZero" [])
                                (bind (fold (list I [ for n in 7 .. -1 .. 0 -> i (1L <<< (8 * n)) ]) (v 0) "feedLen" [ "bits", v 3 ])
                                    (fold (field (v 0) "Sha" "h") (s "") "hexWord" [])))))))
        let shaNodes = nodes
        // One million "a", built inside the program by doubling so the run argument stays small.
        fn "rep" [ "chunk", S; "n", I ] (L S)
            (cond (eq (p "n") (i 0L)) (node "listEmpty" [ "itemType", S ])
                (cond (eq (md (p "n") (i 2L)) (i 1L))
                    (node "listAppendItem" [ "list", call "rep" [ "chunk", p "chunk"; "n", sub (p "n") (i 1L) ]; "item", p "chunk" ])
                    (bind (call "rep" [ "chunk", p "chunk"; "n", div (p "n") (i 2L) ]) (bin "listConcat" (v 0) (v 0)))))
        fn "millionA" [ "chunk", S ] S
            (call "sha256hex" [ "text", node "stringJoin" [ "list", call "rep" [ "chunk", p "chunk"; "n", i 1000000L ]; "separator", s "" ] ])
        shaNodes

    [<Fact; Trait("Suite", "Extended")>]
    let ``SHA-256 authored by name with Bits32 matches the NIST vectors`` () =
        withSession (fun session _ ->
            let nodes = authorSha256 session
            // Probe 12's Int64 port spent 622 nodes on the digest; the Bits32 port needs fewer.
            Assert.InRange(nodes, 400, 600)
            let digest (text: string) = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()
            let check name arguments (expected: string) =
                use response =
                    send session (sprintf """{"id":"run","op":"runPureByName","function":"%s","arguments":[%s],"expected":{"kind":"string","value":"%s"}}""" name arguments expected)
                let result = response.RootElement.GetProperty("result")
                Assert.True(result.GetProperty("matches").GetBoolean(), result.ToString())
            let text (value: string) = sprintf """{"kind":"string","value":%s}""" (q value)
            // FIPS 180-4 / NIST CSRC example digests.
            let vectors =
                [ "", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                  "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                  "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
                  "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu",
                  "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1" ]
            for input, expected in vectors do
                Assert.Equal(expected, digest input)
                check "sha256hex" (text input) expected
            // Multi-byte UTF-8 and the 55/56/64-byte padding edges, against .NET's SHA-256.
            for input in [ "é€😀"; String('a', 55); String('a', 56); String('a', 64); String('a', 119) ] do
                check "sha256hex" (text input) (digest input)
            // One million "a" (the FIPS 180-2 long-message vector), built inside the program.
            check "millionA" (text "a") "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0")

