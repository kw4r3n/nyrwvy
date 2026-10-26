namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The `Bytes` value type (docs/plans/bytes-and-buffers.md, Decision 1): an immutable sequence of
/// integers 0..255, equatable by length then content, with total operations. A stored Int64 keeps
/// its low 8 bits; Get is None and Set unchanged outside [0, length); Slice clamps like ListSlice;
/// FromLatin1 reads one byte per code point (above U+00FF gives 0xFF). Run values are
/// `{"kind":"bytes","hex":"00ff7f"}`, and the plain JSON form is the hex string.
module BytesTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private bytesType = TypeRef.Primitive PrimitiveType.Bytes
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name
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
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-bytes-" + Guid.NewGuid().ToString("N"))
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
    let private node kind fields = obj (("kind", q kind) :: fields)
    let private prim name = node "primitive" [ "primitive", q name ]
    let private S, I, B = prim "string", prim "int64", prim "bytes"
    let private p name = node "parameter" [ "name", q name ]
    let private s (text: string) = node "stringLiteral" [ "value", q text ]

    /// The rules of Decision 1, independently of any backend.
    module private Model =
        let mask (value: int64) = byte (value &&& 255L)
        let fill (length: int64) value = if length <= 0L then [||] else Array.create (int length) (mask value)
        let get (bytes: byte[]) index = if index >= 0L && index < int64 bytes.Length then Some(int64 bytes[int index]) else None
        let set (bytes: byte[]) index value =
            if index >= 0L && index < int64 bytes.Length then Array.updateAt (int index) (mask value) bytes else bytes
        let append (bytes: byte[]) value = Array.append bytes [| mask value |]
        let slice (bytes: byte[]) start stop =
            let clamp bound = max 0L (min bound (int64 bytes.Length))
            let first, last = clamp start, clamp stop
            if last <= first then [||] else bytes[int first .. int last - 1]
        let fromLatin1 (text: string) =
            [| let mutable index = 0
               while index < text.Length do
                   let unit = text[index]
                   if Char.IsHighSurrogate unit && index + 1 < text.Length && Char.IsLowSurrogate text[index + 1] then index <- index + 1
                   yield if unit <= 'ÿ' then byte unit else 0xFFuy
                   index <- index + 1 |]
        let toLatin1 (bytes: byte[]) = String(bytes |> Array.map char)

    [<Fact>]
    let ``Bytes operations check operand types in recipes and persisted graphs`` () =
        withSession (fun session _ ->
            create session "fill" [ "length", intType; "value", intType ] bytesType (ExpressionRecipe.BytesFill(slot 0, slot 1)) |> ignore
            create session "get" [ "b", bytesType; "i", intType ] (optionOf intType) (ExpressionRecipe.BytesGet(slot 0, slot 1)) |> ignore
            let mismatch = "PureExpressionOperandTypeMismatch"
            Assert.Contains(mismatch, rejects session [ "a", bytesType; "b", intType ] bytesType (ExpressionRecipe.BytesFill(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", textType ] intType (ExpressionRecipe.BytesLength(slot 0)))
            Assert.Contains(mismatch, rejects session [ "a", bytesType; "b", bytesType ] (optionOf intType) (ExpressionRecipe.BytesGet(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", bytesType; "b", intType; "c", bytesType ] bytesType (ExpressionRecipe.BytesSet(slot 0, slot 1, slot 2)))
            Assert.Contains(mismatch, rejects session [ "a", intType; "b", intType ] bytesType (ExpressionRecipe.BytesAppend(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", bytesType; "b", textType ] bytesType (ExpressionRecipe.BytesConcat(slot 0, slot 1)))
            Assert.Contains(mismatch, rejects session [ "a", bytesType; "b", intType; "c", textType ] bytesType (ExpressionRecipe.BytesSlice(slot 0, slot 1, slot 2)))
            Assert.Contains(mismatch, rejects session [ "a", bytesType ] bytesType (ExpressionRecipe.BytesFromLatin1(slot 0)))
            Assert.Contains(mismatch, rejects session [ "a", textType ] textType (ExpressionRecipe.BytesToLatin1(slot 0)))
            // Bytes is neither a String nor a List<Int64>.
            rejects session [ "a", bytesType ] textType (slot 0) |> ignore
            rejects session [] (listOf intType) ExpressionRecipe.BytesEmpty |> ignore
            // Wire shapes: each kind takes exactly its named operands, and there is no literal.
            let signature = sprintf """"parameters":[{"displayName":"b","type":%s},{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[]""" B I B
            let create body = sprintf """{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"f",%s,"body":%s}""" signature body
            for body in
                [ """{"kind":"bytesEmpty","value":{"kind":"parameter","name":"b"}}"""
                  """{"kind":"bytesAppend","left":{"kind":"parameter","name":"b"},"right":{"kind":"parameter","name":"n"}}"""
                  """{"kind":"bytesSet","bytes":{"kind":"parameter","name":"b"},"index":{"kind":"parameter","name":"n"}}"""
                  """{"kind":"bytesFill","length":{"kind":"parameter","name":"n"}}"""
                  """{"kind":"bytesLiteral","hex":"00"}""" ] do
                Assert.Equal("ProtocolError", errorCode session (create body))
            use accepted = send session (create (node "bytesAppend" [ "bytes", p "b"; "value", p "n" ]))
            ())
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-bytes-" + name)
        let validate (nodes: SemanticNodeKind list) resultType =
            let body = nodes |> List.mapi (fun index kind -> ({ Id = nodeId (string index); Kind = kind }: SemanticNode))
            let fn: SemanticFunction =
                { Exported = true; Id = StableId.createForTesting<FunctionTag> "function-bytes-entry"; DisplayName = "entry"; Parameters = []
                  TypeParameters = []
                  ResultType = resultType; RequiredEffects = []
                  Body = { Nodes = body; ResultNodeId = (List.last body).Id } }
            let snapshot = { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-bytes") with Functions = [ fn ] }
            SnapshotValidator.validate snapshot |> List.map (fun issue -> issue.Code), snapshot
        let one = SemanticNodeKind.Int64Literal 1L
        let codes nodes resultType = fst (validate nodes resultType)
        Assert.Empty(codes [ SemanticNodeKind.BytesEmpty ] bytesType)
        Assert.Empty(codes [ one; SemanticNodeKind.BytesFill(nodeId "0", nodeId "0") ] bytesType)
        Assert.Empty(codes [ one; SemanticNodeKind.BytesFill(nodeId "0", nodeId "0"); SemanticNodeKind.BytesGet(nodeId "1", nodeId "0") ] (optionOf intType))
        Assert.Empty(codes [ one; SemanticNodeKind.BytesFill(nodeId "0", nodeId "0"); SemanticNodeKind.BytesSlice(nodeId "1", nodeId "0", nodeId "0") ] bytesType)
        Assert.Empty(codes [ SemanticNodeKind.StringLiteral "a"; SemanticNodeKind.BytesFromLatin1(nodeId "0"); SemanticNodeKind.BytesToLatin1(nodeId "1") ] textType)
        Assert.Contains("BytesOperandTypeMismatch", codes [ SemanticNodeKind.StringLiteral "1"; SemanticNodeKind.BytesLength(nodeId "0") ] intType)
        Assert.Contains("BytesOperandTypeMismatch", codes [ one; SemanticNodeKind.BytesFromLatin1(nodeId "0") ] bytesType)
        Assert.Contains("BytesOperandTypeMismatch", codes [ SemanticNodeKind.BytesEmpty; SemanticNodeKind.BytesSet(nodeId "0", nodeId "0", nodeId "0") ] bytesType)
        Assert.Contains("BytesOperandTypeMismatch", codes [ one; SemanticNodeKind.BytesConcat(nodeId "0", nodeId "0") ] bytesType)
        Assert.NotEmpty(codes [ SemanticNodeKind.BytesEmpty ] textType)
        // Bytes compares by content, so it is equatable and ValueEqual accepts it.
        Assert.True(EquatableTypes.isEquatable [] (listOf bytesType))
        Assert.Empty(codes [ SemanticNodeKind.BytesEmpty; SemanticNodeKind.ValueEqual(nodeId "0", nodeId "0") ] boolType)
        // A Bytes node, or the Bytes type alone in a signature, needs snapshot schema 23.
        Assert.Equal(23, (SnapshotSchema.required (snd (validate [ SemanticNodeKind.BytesEmpty ] bytesType))).Json)
        let typeOnly = snd (validate [ SemanticNodeKind.Int64Literal 0L ] intType)
        let typeOnly =
            { typeOnly with
                Functions =
                    typeOnly.Functions
                    |> List.map (fun fn -> { fn with Parameters = [ { Id = StableId.createForTesting<ParameterTag> "parameter-bytes"; DisplayName = "b"; Type = bytesType } ] }) }
        Assert.Equal(23, (SnapshotSchema.required typeOnly).Json)
        Assert.Equal(SnapshotSchema.baseline.Json, (SnapshotSchema.required (snd (validate [ one ] intType))).Json)

    [<Fact>]
    let ``every Bytes kind persists reopens projects lowers and emits`` () =
        withSession (fun session path ->
            let signature = sprintf """"parameters":[{"displayName":"t","type":%s},{"displayName":"n","type":%s}],"resultType":%s,"requiredEffects":[]""" S I S
            let b = node "bytesFromLatin1" [ "value", p "t" ]
            let joined =
                node "bytesConcat"
                    [ "left", node "bytesSet" [ "bytes", node "bytesAppend" [ "bytes", node "bytesEmpty" []; "value", p "n" ]; "index", p "n"; "value", p "n" ]
                      "right", node "bytesSlice" [ "bytes", node "bytesFill" [ "length", node "bytesLength" [ "value", b ]; "value", p "n" ]; "start", p "n"; "end", p "n" ] ]
            let body =
                node "matchOption"
                    [ "input", node "bytesGet" [ "bytes", joined; "index", p "n" ]
                      "someBody", node "bytesToLatin1" [ "value", b ]
                      "noneBody", s "" ]
            use named =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"mix",%s,"body":%s}""" signature body)
            session.Commit(named.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let kinds = [ "bytesEmpty"; "bytesFill"; "bytesLength"; "bytesGet"; "bytesSet"; "bytesAppend"; "bytesConcat"; "bytesSlice"; "bytesFromLatin1"; "bytesToLatin1" ]
            for kind in kinds do Assert.Contains($"\"kind\":\"{kind}\"", named.RootElement.GetRawText())
            let fn = find session "mix"
            let viewer = session.FocusFunction fn.Id |> unwrap |> ViewerProjection.focus
            let set = viewer.Graph.Nodes |> List.find (fun item -> item.Role = ViewerNodeRole.Operation "BytesSet")
            Assert.Equal<ValueDependencyRole list>(
                [ ValueDependencyRole.Source; ValueDependencyRole.Index; ValueDependencyRole.Item ],
                set.ValueDependencies |> List.map (fun item -> item.Role))
            let html = ViewerHtml.renderFocusDocument viewer
            for label in [ "bytes slice"; "bytes from latin-1"; "bytes to latin-1" ] do Assert.Contains(label, html)
            session.Close()
            let encoded = File.ReadAllText path
            for kind in kinds do Assert.Contains($"\"kind\":\"{kind}\"", encoded)
            Assert.Contains("\"bytesNodeId\"", encoded)
            Assert.Contains("\"schemaVersion\":23", encoded)
            let graph = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            Assert.Equal(23, (SnapshotSchema.required graph).Json)
            let record = RecordCodec.serialize graph |> unwrap
            for tag in [ "bytes-empty"; "bytes-fill"; "bytes-set"; "bytes-slice"; "bytes-from-latin1" ] do Assert.Contains(tag, record)
            let restored = RecordCodec.deserialize record |> unwrap
            Assert.Equal(record, RecordCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            // An older declared schema cannot hold Bytes.
            match SnapshotCodec.deserialize (encoded.Replace("\"schemaVersion\":23", "\"schemaVersion\":22")) with
            | Ok _ -> failwith "A schema 22 snapshot with Bytes decoded."
            | Error error -> Assert.Contains("Bytes", sprintf "%A" error)
            let typed = TypedSemanticIr.project restored |> unwrap
            let typedJson = TypedSemanticIrSerializer.serialize typed
            let execution = ExecutionIr.lower typed |> unwrap
            let executionJson = ExecutionIrSerializer.serialize execution
            for kind in kinds do
                Assert.Contains($"\"kind\":\"{kind}\"", typedJson)
                Assert.Contains($"\"kind\":\"{kind}\"", executionJson)
            let source = (CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap).Source
            for fragment in
                [ "public sealed class NyBytes"; "NyBytes.Empty()"; "NyBytes.Fill("; ".Get("; ".Set("; ".Append("; ".Concat("; ".Slice("
                  "NyBytes.FromLatin1("; ".ToLatin1()"; ".Length;" ] do
                Assert.Contains(fragment, source)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(fn.Body.ResultNodeId, (find reopened "mix").Body.ResultNodeId))

    [<Fact>]
    let ``Bytes run values accept only hexadecimal digit pairs`` () =
        withSession (fun session _ ->
            let identity = create session "identity" [ "value", bytesType ] bytesType (slot 0)
            let run value = sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[%s]}""" (StableId.value identity.Id) value
            for value in
                [ """{"kind":"bytes","hex":"0"}"""; """{"kind":"bytes","hex":"zz"}"""; """{"kind":"bytes","hex":"0x00"}"""
                  """{"kind":"bytes","hex":" 00"}"""; """{"kind":"bytes","hex":5}"""; """{"kind":"bytes","hex":null}"""
                  """{"kind":"bytes"}"""; """{"kind":"bytes","value":"00"}"""; """{"kind":"bytes","hex":"00","extra":0}"""
                  """{"kind":"list","itemKind":"bytes","items":[]}"""; "\"0\""; "\"0g\""; "[0]"; "null" ] do
                Assert.Equal("ProtocolError", errorCode session (run value))
            // Bytes where a String is declared is a type error, not a conversion.
            let textIdentity = create session "textIdentity" [ "value", textType ] textType (slot 0)
            Assert.Equal("RunArgumentTypeMismatch",
                errorCode session (sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[{"kind":"bytes","hex":"00"}]}""" (StableId.value textIdentity.Id))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Bytes operations follow their rules through runPure and the emitted C#`` () =
        withSession (fun session path ->
            let fill = create session "fill" [ "length", intType; "value", intType ] bytesType (ExpressionRecipe.BytesFill(slot 0, slot 1))
            let length = create session "length" [ "b", bytesType ] intType (ExpressionRecipe.BytesLength(slot 0))
            let get = create session "get" [ "b", bytesType; "i", intType ] (optionOf intType) (ExpressionRecipe.BytesGet(slot 0, slot 1))
            let set = create session "set" [ "b", bytesType; "i", intType; "v", intType ] bytesType (ExpressionRecipe.BytesSet(slot 0, slot 1, slot 2))
            let append = create session "append" [ "b", bytesType; "v", intType ] bytesType (ExpressionRecipe.BytesAppend(slot 0, slot 1))
            let concat = create session "concat" [ "a", bytesType; "b", bytesType ] bytesType (ExpressionRecipe.BytesConcat(slot 0, slot 1))
            let slice = create session "slice" [ "b", bytesType; "s", intType; "e", intType ] bytesType (ExpressionRecipe.BytesSlice(slot 0, slot 1, slot 2))
            let fromLatin1 = create session "fromLatin1" [ "t", textType ] bytesType (ExpressionRecipe.BytesFromLatin1(slot 0))
            let toLatin1 = create session "toLatin1" [ "b", bytesType ] textType (ExpressionRecipe.BytesToLatin1(slot 0))
            let empty = create session "empty" [] bytesType ExpressionRecipe.BytesEmpty
            let equal = create session "equal" [ "a", bytesType; "b", bytesType ] boolType (ExpressionRecipe.ValueEqual(slot 0, slot 1))
            // A constant is BytesFromLatin1 of a String literal; a set copy leaves the original intact.
            create session "constant" [] (listOf bytesType)
                (ExpressionRecipe.ListConstruct(bytesType,
                    [ ExpressionRecipe.BytesFromLatin1(ExpressionRecipe.StringLiteral "\u0000ÿ\u007f")
                      ExpressionRecipe.Bind(
                          ExpressionRecipe.BytesFromLatin1(ExpressionRecipe.StringLiteral "ab"),
                          ExpressionRecipe.BytesConcat(
                              ExpressionRecipe.BytesSet(ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 0x7AL),
                              ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value })) ])) |> ignore
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let bytes (value: byte[]) = PureRunValue.Bytes value
            let samples = [ [||]; [| 0uy |]; [| 0uy; 255uy; 127uy |]; Array.init 300 byte ]
            let values = [ 0L; 1L; 255L; 256L; 257L; -1L; -255L; -256L; 300L; Int64.MinValue; Int64.MaxValue ]
            let positions = [ Int64.MinValue; -2L; -1L; 0L; 1L; 2L; 3L; 299L; 300L; 301L; Int64.MaxValue ]
            for count in [ Int64.MinValue; -5L; -1L; 0L; 1L; 3L ] do
                for value in values do
                    Assert.Equal(bytes (Model.fill count value), run fill [ PureRunValue.Int64 count; PureRunValue.Int64 value ])
            for sample in samples do
                Assert.Equal(PureRunValue.Int64(int64 sample.Length), run length [ bytes sample ])
                Assert.Equal(bytes sample, run concat [ bytes sample; bytes [||] ])
                Assert.Equal(bytes sample, run concat [ bytes [||]; bytes sample ])
                Assert.Equal(bytes (Array.append sample [| 9uy; 8uy |]), run concat [ bytes sample; bytes [| 9uy; 8uy |] ])
                Assert.Equal(PureRunValue.String(Model.toLatin1 sample), run toLatin1 [ bytes sample ])
                Assert.Equal(PureRunValue.Bool true, run equal [ bytes sample; bytes (Array.copy sample) ])
                for value in values do
                    Assert.Equal(bytes (Model.append sample value), run append [ bytes sample; PureRunValue.Int64 value ])
                for index in positions do
                    Assert.Equal(PureRunValue.Int64Option(Model.get sample index), run get [ bytes sample; PureRunValue.Int64 index ])
                    Assert.Equal(bytes (Model.set sample index -1L), run set [ bytes sample; PureRunValue.Int64 index; PureRunValue.Int64 -1L ])
                    for stop in [ Int64.MinValue; -1L; 0L; 2L; 300L; Int64.MaxValue ] do
                        Assert.Equal(bytes (Model.slice sample index stop), run slice [ bytes sample; PureRunValue.Int64 index; PureRunValue.Int64 stop ])
            // Equality is by length, then content.
            Assert.Equal(PureRunValue.Bool false, run equal [ bytes [| 1uy |]; bytes [| 1uy; 0uy |] ])
            Assert.Equal(PureRunValue.Bool false, run equal [ bytes [| 1uy; 2uy |]; bytes [| 1uy; 3uy |] ])
            Assert.Equal(PureRunValue.Bool false, run equal [ bytes [||]; bytes [| 0uy |] ])
            Assert.Equal(bytes [||], run empty [])
            // Latin-1: one byte per code point; above U+00FF (a pair counts once, a lone surrogate too) is 0xFF.
            for text in [ ""; "abc"; "\u0000ÿĀ"; "é€😀"; "\uD800x\uDC00"; String('z', 300) ] do
                Assert.Equal(bytes (Model.fromLatin1 text), run fromLatin1 [ PureRunValue.String text ])
            Assert.Equal(bytes [| 0x61uy; 0xFFuy; 0xFFuy; 0xFFuy |], run fromLatin1 [ PureRunValue.String "a€😀\uD800" ])
            let constant = find reopened "constant"
            Assert.Equal(
                PureRunValue.ListValue(bytesType, [ bytes [| 0uy; 255uy; 127uy |]; bytes [| 0x7Auy; 0x62uy; 0x61uy; 0x62uy |] ]),
                run constant [])
            // The wire forms: tagged {kind:"bytes",hex} or the plain hex string in, lower-case tagged out.
            let runByName name arguments expected =
                let response = send reopened (sprintf """{"id":"run","op":"runPureByName","function":"%s","arguments":[%s],"expected":%s}""" name arguments expected)
                response.RootElement.GetProperty("result").Clone()
            let tagged = runByName "append" """{"kind":"bytes","hex":"00FF"},{"kind":"int64","value":"383"}""" """{"kind":"bytes","hex":"00ff7f"}"""
            Assert.True(tagged.GetProperty("matches").GetBoolean(), tagged.ToString())
            Assert.Equal("""{"kind":"bytes","hex":"00ff7f"}""", tagged.GetProperty("actual").GetRawText())
            let plain = runByName "concat" "\"00ff\",\"7F\"" "\"00ff7f\""
            Assert.True(plain.GetProperty("matches").GetBoolean(), plain.ToString())
            Assert.Equal("""{"kind":"bytes","hex":"00ff7f"}""", plain.GetProperty("actual").GetRawText())
            let nothing = runByName "empty" "" "\"\""
            Assert.True(nothing.GetProperty("matches").GetBoolean(), nothing.ToString())
            Assert.Equal("""{"kind":"bytes","hex":""}""", nothing.GetProperty("actual").GetRawText())
            let missed = runByName "fill" "2,-1" "\"fffe\""
            Assert.False(missed.GetProperty("matches").GetBoolean())
            Assert.Equal("\"ffff\"", missed.GetProperty("mismatch").GetProperty("actual").GetProperty("hex").GetRawText()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Bytes updates write in place only where no holder can see the old value`` () =
        withSession (fun session path ->
            let value = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
            let plus1 recipe = ExpressionRecipe.Int64WrappingAdd(recipe, ExpressionRecipe.Int64Literal 1L)
            // A tail-recursive fill: after the first copy, every turn writes into the same buffer.
            let fillLoop =
                create session "fillLoop" [ "b", bytesType; "i", intType; "n", intType ] bytesType
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64LessThan(slot 1, slot 2),
                        ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, slot 1, plus1 (slot 1)); 1, plus1 (slot 1); 2, slot 2 ],
                        slot 0))
            // The caller's buffer is unchanged by the loop it was passed to.
            let keepsArgument =
                create session "keepsArgument" [ "b", bytesType ] (listOf bytesType)
                    (ExpressionRecipe.ListConstruct(bytesType,
                        [ slot 0; ExpressionRecipe.DirectCall(fillLoop.Id, [ fillLoop.Parameters[0].Id, slot 0; fillLoop.Parameters[1].Id, ExpressionRecipe.Int64Literal 0L; fillLoop.Parameters[2].Id, ExpressionRecipe.Int64Literal 3L ]) ]))
            // A buffer stored into a list must not change when the loop updates it on a later turn.
            let snapshots =
                create session "snapshots" [ "buf", bytesType; "acc", listOf bytesType; "i", intType ] (listOf bytesType)
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64LessThan(slot 2, ExpressionRecipe.Int64Literal 3L),
                        ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, slot 2); 1, ExpressionRecipe.ListAppendItem(slot 1, slot 0); 2, plus1 (slot 2) ],
                        slot 1))
            // One buffer passed as two arguments of a tail self-call: updating one must not change the other.
            let twins =
                create session "twins" [ "a", bytesType; "c", bytesType; "i", intType ] (listOf bytesType)
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64LessThan(slot 2, ExpressionRecipe.Int64Literal 1L),
                        ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 1L),
                            ExpressionRecipe.SelfCall [ 0, value; 1, value; 2, ExpressionRecipe.Int64Literal 1L ]),
                        ExpressionRecipe.ListConstruct(bytesType, [ ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 5L); slot 1 ])))
            // The old value is read after the update: the update copies.
            let oldAndNew =
                create session "oldAndNew" [ "b", bytesType ] (listOf bytesType)
                    (ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 7L),
                        ExpressionRecipe.ListConstruct(bytesType, [ value; slot 0 ])))
            // The same read-after-update on a buffer the invocation already owns (second loop turn): a wrong
            // in-place decision would show up in the result here, not only in the plan.
            let ownedOldAndNew =
                create session "ownedOldAndNew" [ "b", bytesType; "i", intType ] (listOf bytesType)
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64LessThan(slot 1, ExpressionRecipe.Int64Literal 1L),
                        ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 1L, ExpressionRecipe.Int64Literal 1L); 1, plus1 (slot 1) ],
                        ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 7L),
                            ExpressionRecipe.ListConstruct(bytesType, [ value; slot 0 ]))))
            // Appending in a loop grows one buffer.
            let appendLoop =
                create session "appendLoop" [ "b", bytesType; "i", intType ] bytesType
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64LessThan(slot 1, ExpressionRecipe.Int64Literal 300L),
                        ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesAppend(slot 0, slot 1); 1, plus1 (slot 1) ],
                        slot 0))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let bytes (value: byte[]) = PureRunValue.Bytes value
            let listOfBytes items = PureRunValue.ListValue(bytesType, items |> List.map bytes)
            Assert.Equal(bytes [| 1uy; 2uy; 3uy; 0uy; 0uy |], run fillLoop [ bytes (Array.zeroCreate 5); PureRunValue.Int64 0L; PureRunValue.Int64 3L ])
            Assert.Equal(listOfBytes [ [| 9uy; 9uy; 9uy; 9uy |]; [| 1uy; 2uy; 3uy; 9uy |] ], run keepsArgument [ bytes [| 9uy; 9uy; 9uy; 9uy |] ])
            Assert.Equal(listOfBytes [ [| 9uy |]; [| 0uy |]; [| 1uy |] ], run snapshots [ bytes [| 9uy |]; PureRunValue.ListValue(bytesType, []); PureRunValue.Int64 0L ])
            Assert.Equal(listOfBytes [ [| 5uy |]; [| 1uy |] ], run twins [ bytes [| 0uy |]; bytes [| 0uy |]; PureRunValue.Int64 0L ])
            Assert.Equal(listOfBytes [ [| 7uy; 2uy |]; [| 1uy; 2uy |] ], run oldAndNew [ bytes [| 1uy; 2uy |] ])
            Assert.Equal(listOfBytes [ [| 7uy; 1uy |]; [| 0uy; 1uy |] ], run ownedOldAndNew [ bytes [| 0uy; 0uy |]; PureRunValue.Int64 0L ])
            Assert.Equal(bytes (Array.init 300 byte), run appendLoop [ bytes [||]; PureRunValue.Int64 0L ])
            // The plan behind those results.
            let graph = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let execution = TypedSemanticIr.project graph |> unwrap |> ExecutionIr.lower |> unwrap
            let planOf (fn: SemanticFunction) =
                execution.Functions |> List.find (fun item -> item.SemanticFunctionId = fn.Id) |> ExecutionIrBytesOwnership.plan |> Option.get
            for fn in [ fillLoop; appendLoop ] do
                let plan = planOf fn
                Assert.Single(plan.InPlace) |> ignore
                Assert.Empty(plan.CopiedUpdates)
            Assert.Single((planOf oldAndNew).CopiedUpdates) |> ignore
            Assert.Empty((planOf oldAndNew).InPlace)
            Assert.NotEmpty((planOf snapshots).Escapes)
            Assert.NotEmpty((planOf twins).TailCallReleases)
            // readFunction shows a forced copy; a function whose updates all write in place shows none.
            let copied (fn: SemanticFunction) =
                let response = send reopened (sprintf """{"id":"read","op":"readFunction","functionId":"%s","detail":"compact"}""" (StableId.value fn.Id))
                match response.RootElement.GetProperty("result").TryGetProperty "copiedUpdates" with
                | true, items -> items.GetArrayLength()
                | false, _ -> 0
            Assert.Equal(1, copied oldAndNew)
            Assert.Equal(0, copied fillLoop))
