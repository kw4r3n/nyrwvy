namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Waiting operations, must-not-wait functions and task-bound capabilities
/// (docs/decisions/wait-and-task-bound-attributes.md).
module WaitAttributesTests =
    let private unwrap label = function Ok value -> value | Error error -> failwithf "%s: %A" label error
    /// A symbolic fixture name becomes a stable UUID (MD5 of the name), which the record codec requires.
    let private uuid (name: string) =
        Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes name)).ToString("D")
    let private id<'tag> value = StableId.createForTesting<'tag> (uuid value)
    let private at name = $"node '{uuid name}'"
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private int64Type = TypeRef.Primitive PrimitiveType.Int64
    let private unitType = TypeRef.Primitive PrimitiveType.Unit
    let private generic kind arguments = TypeRef.BuiltinGeneric(kind, arguments)
    let private resultOf ok = generic BuiltinGenericType.Result [ ok; stringType ]

    let private io = id<EffectTag> "effect-io"
    let private fsId = id<CapabilityTag> "capability-fs"
    let private readTextId = id<CapabilityOperationTag> "operation-read-text"
    let private sizeId = id<CapabilityOperationTag> "operation-size"
    let private pathId = id<CapabilityParameterTag> "parameter-path"
    let private uiId = id<CapabilityTag> "capability-ui"
    let private holderId = id<TypeTag> "type-holder"

    let private node name kind : SemanticNode = { Id = id<GraphNodeTag> name; Kind = kind }
    let private parameter name typeRef : SemanticParameter = { Id = id<ParameterTag> name; DisplayName = name; Type = typeRef }
    let private bind target value : ArgumentBinding = { TargetParameterId = id<ParameterTag> target; ValueNodeId = id<GraphNodeTag> value }

    let private fn name mustNotWait (parameters: SemanticParameter list) resultType effects (nodes: SemanticNode list) : SemanticFunction =
        { Id = id<FunctionTag> name
          DisplayName = name
          Exported = true
          MustNotWait = mustNotWait
          TypeParameters = []
          Parameters = parameters
          ResultType = resultType
          RequiredEffects = effects
          Body = { Nodes = nodes; ResultNodeId = (List.last nodes).Id } }

    let private fsType = TypeRef.Capability fsId
    let private tasksType = generic BuiltinGenericType.List [ generic BuiltinGenericType.Task [ int64Type ] ]
    let private waitedType = generic BuiltinGenericType.List [ generic BuiltinGenericType.Option [ int64Type ] ]

    /// `handler` reaches `Fs.readText` through `caller` and `read`; `shielded` reaches a TaskWait through
    /// the body of a cancellation shield; `quiet` only invokes an operation that does not wait.
    let private waitingSnapshot (handlerMustNotWait: bool) : Snapshot =
        let fs: SemanticCapability =
            { Id = fsId
              DisplayName = "Fs"
              Exported = true
              TaskBound = false
              Operations =
                [ { Id = readTextId; DisplayName = "readText"; Parameters = [ { Id = pathId; DisplayName = "path"; Type = stringType } ]
                    ResultType = stringType; FailureType = None; EffectId = io; MayWait = true }
                  { Id = sizeId; DisplayName = "size"; Parameters = []; ResultType = int64Type; FailureType = None; EffectId = io; MayWait = false } ] }
        { Snapshot.empty (id<SnapshotTag> "snapshot-wait") with
            Effects = [ { Id = io; DisplayName = "io"; Exported = true } ]
            Capabilities = [ fs ]
            Functions =
              [ fn "read" false [ parameter "read-fs" fsType ] stringType [ io ]
                    [ node "read-p" (SemanticNodeKind.ParameterReference(id "read-fs"))
                      node "read-path" (SemanticNodeKind.StringLiteral "a")
                      node "read-invoke" (SemanticNodeKind.CapabilityInvoke(id "read-p", readTextId, [ { TargetCapabilityParameterId = pathId; ValueNodeId = id "read-path" } ])) ]
                fn "caller" false [ parameter "caller-fs" fsType ] stringType [ io ]
                    [ node "caller-p" (SemanticNodeKind.ParameterReference(id "caller-fs"))
                      node "caller-call" (SemanticNodeKind.DirectCall(id "read", [ bind "read-fs" "caller-p" ])) ]
                fn "handler" handlerMustNotWait [ parameter "handler-fs" fsType ] stringType [ io ]
                    [ node "handler-p" (SemanticNodeKind.ParameterReference(id "handler-fs"))
                      node "handler-call" (SemanticNodeKind.DirectCall(id "caller", [ bind "caller-fs" "handler-p" ])) ]
                fn "quiet" true [ parameter "quiet-fs" fsType ] int64Type [ io ]
                    [ node "quiet-p" (SemanticNodeKind.ParameterReference(id "quiet-fs"))
                      node "quiet-invoke" (SemanticNodeKind.CapabilityInvoke(id "quiet-p", sizeId, [])) ]
                fn "waitBody" false [ parameter "wait-unit" unitType; parameter "wait-tasks" tasksType ] (resultOf waitedType) []
                    [ node "wait-tasks-p" (SemanticNodeKind.ParameterReference(id "wait-tasks"))
                      node "wait-wait" (SemanticNodeKind.TaskWait(id "wait-tasks-p", TaskWaitMode.All, None))
                      node "wait-ok" (SemanticNodeKind.ResultOk(stringType, id "wait-wait")) ]
                fn "shielded" true [ parameter "shielded-tasks" tasksType ] (resultOf waitedType) []
                    [ node "shielded-p" (SemanticNodeKind.ParameterReference(id "shielded-tasks"))
                      node "shielded-shield" (SemanticNodeKind.CancellationShield(id "waitBody", [ bind "wait-tasks" "shielded-p" ])) ] ] }

    [<Fact>]
    let ``may wait propagates through calls and a shield body to a waiting operation or a builtin wait`` () =
        let snapshot = waitingSnapshot false
        let waiting = Waiting.derive snapshot
        let mayWait name = Map.containsKey (id<FunctionTag> name) waiting
        Assert.True(mayWait "read" && mayWait "caller" && mayWait "handler" && mayWait "waitBody" && mayWait "shielded")
        Assert.False(mayWait "quiet")
        let handlerPath = Map.find (id<FunctionTag> "handler") waiting
        Assert.Equal<FunctionId list>([ id "caller"; id "read" ], handlerPath.Via |> List.map snd)
        Assert.Equal(WaitSource.Operation readTextId, handlerPath.Source)
        Assert.Equal(WaitSource.Builtin "TaskWait", (Map.find (id<FunctionTag> "shielded") waiting).Source)
        // Only `shielded` is marked among the waiting functions here.
        let issues = SnapshotValidator.validate snapshot
        let issue = Assert.Single(issues)
        Assert.Equal("MustNotWaitFunctionWaits", issue.Code)
        Assert.Contains("shielded", issue.Message)
        Assert.Contains("waitBody", issue.Message)
        Assert.Contains($"""TaskWait ({at "wait-wait"})""", issue.Message)

    [<Fact>]
    let ``a must-not-wait function that reaches a waiting operation is rejected with the path`` () =
        let issues = SnapshotValidator.validate (waitingSnapshot true) |> List.filter (fun issue -> issue.Message.Contains "handler")
        let issue = Assert.Single(issues)
        Assert.Equal("MustNotWaitFunctionWaits", issue.Code)
        Assert.Contains($"""caller ({at "handler-call"}) -> read ({at "caller-call"}) -> waiting operation Fs.readText ({at "read-invoke"})""", issue.Message)

    [<Fact>]
    let ``each builtin that blocks counts as waiting`` () =
        let kinds =
            [ SemanticNodeKind.TaskWait(id "x", TaskWaitMode.Any, None)
              SemanticNodeKind.OneShotWait(id "x", None)
              SemanticNodeKind.ParallelAll(id "x", id "w", [])
              SemanticNodeKind.TaskScope(id "w", []) ]
        for kind in kinds do Assert.True((Waiting.builtinWait (node "n" kind)).IsSome, sprintf "%A" kind)
        Assert.True((Waiting.builtinWait (node "n" (SemanticNodeKind.TaskCancel(id "x")))).IsNone)
        Assert.True((Waiting.builtinWait (node "n" (SemanticNodeKind.TaskStart(id "s", id "w", [], id "x")))).IsNone)

    // ---- Task-bound capabilities --------------------------------------------------------------

    let private uiType = TypeRef.Capability uiId

    /// `start` passes a value of `Ui` (directly or inside a Product) to another task in the way `crossing` names.
    let private taskBoundSnapshot (taskBound: bool) (crossing: string) : Snapshot =
        let ui: SemanticCapability = { Id = uiId; DisplayName = "Ui"; Exported = true; TaskBound = taskBound; Operations = [] }
        let holder: SemanticType =
            { Id = holderId; DisplayName = "Holder"; Exported = true; TypeParameters = []
              Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "field-ui"; DisplayName = "ui"; Type = uiType } ] }
        let holderType = TypeRef.Named(holderId, [])
        let worker =
            fn "worker" false [ parameter "worker-ui" uiType; parameter "worker-item" int64Type ] (resultOf int64Type) []
                [ node "worker-item-p" (SemanticNodeKind.ParameterReference(id "worker-item"))
                  node "worker-ok" (SemanticNodeKind.ResultOk(stringType, id "worker-item-p")) ]
        let itemWorker =
            fn "itemWorker" false [ parameter "item-holder" holderType ] (resultOf int64Type) []
                [ node "item-lit" (SemanticNodeKind.Int64Literal 1L)
                  node "item-ok" (SemanticNodeKind.ResultOk(stringType, id "item-lit")) ]
        let start =
            let common =
                [ node "start-ui" (SemanticNodeKind.ParameterReference(id "start-ui-p"))
                  node "start-holder" (SemanticNodeKind.ParameterReference(id "start-holder-p"))
                  node "start-scope" (SemanticNodeKind.ParameterReference(id "start-scope-p"))
                  node "start-one" (SemanticNodeKind.Int64Literal 1L)
                  node "start-ints" (SemanticNodeKind.ListConstruct(int64Type, [ id "start-one" ]))
                  node "start-holders" (SemanticNodeKind.ListConstruct(holderType, [ id "start-holder" ])) ]
            let crossingNode =
                match crossing with
                | "task item" -> SemanticNodeKind.TaskStart(id "start-scope", id "itemWorker", [], id "start-holder")
                | "task context" -> SemanticNodeKind.TaskStart(id "start-scope", id "worker", [ bind "worker-ui" "start-ui" ], id "start-one")
                | "parallel context" -> SemanticNodeKind.ParallelAll(id "start-ints", id "worker", [ bind "worker-ui" "start-ui" ])
                | "parallel source" -> SemanticNodeKind.ParallelAll(id "start-holders", id "itemWorker", [])
                | other -> failwith other
            fn "start" false
                [ parameter "start-ui-p" uiType; parameter "start-holder-p" holderType; parameter "start-scope-p" (generic BuiltinGenericType.TaskScope []) ]
                unitType [] (common @ [ node "start-cross" crossingNode; node "start-unit" SemanticNodeKind.UnitLiteral ])
        { Snapshot.empty (id<SnapshotTag> "snapshot-task-bound") with
            Types = [ holder ]
            Capabilities = [ ui ]
            Functions = [ worker; itemWorker; start ] }

    [<Theory>]
    [<InlineData("task item", "item")>]
    [<InlineData("task context", "context binding")>]
    [<InlineData("parallel context", "context binding")>]
    [<InlineData("parallel source", "source")>]
    let ``a task-bound value cannot cross into another task`` (crossing: string) (role: string) =
        let codes taskBound = SnapshotValidator.validate (taskBoundSnapshot taskBound crossing) |> List.filter (fun issue -> issue.Code = "TaskBoundValueCrossesTask")
        Assert.Empty(codes false)
        let issue = Assert.Single(codes true)
        Assert.Contains(role, issue.Message)

    // ---- Persistence ---------------------------------------------------------------------------

    [<Fact>]
    let ``the markers persist at schema 24 in both codecs and are absent otherwise`` () =
        let plain = waitingSnapshot false |> fun snapshot -> { snapshot with Functions = snapshot.Functions |> List.map (fun item -> { item with MustNotWait = false }) }
        let plain = { plain with Capabilities = plain.Capabilities |> List.map (fun capability -> { capability with Operations = capability.Operations |> List.map (fun op -> { op with MayWait = false }) }) }
        // Without markers nothing changes: no property, the version of the other constructs, the same fingerprints.
        let plainJson = SnapshotCodec.serializeCanonical plain |> unwrap "plain json"
        Assert.DoesNotContain("mayWait", plainJson)
        Assert.DoesNotContain("mustNotWait", plainJson)
        Assert.DoesNotContain("taskBound", plainJson)
        Assert.Empty(SnapshotSchema.usedFeatures plain |> Set.filter ((=) SnapshotSchemaFeature.WaitAttributes))
        Assert.True((SnapshotSchema.required plain).Json < 24)
        Assert.Equal(plainJson, SnapshotCodec.deserialize plainJson |> unwrap "plain read" |> SnapshotCodec.serializeCanonical |> unwrap "plain again")

        // `quiet` is a valid must-not-wait function; `shielded` is unmarked so that the snapshot validates.
        let marked = waitingSnapshot false
        let marked = { marked with Functions = marked.Functions |> List.map (fun item -> if item.DisplayName = "shielded" then { item with MustNotWait = false } else item) }
        let marked = { marked with Capabilities = marked.Capabilities |> List.map (fun capability -> { capability with TaskBound = true }) }
        Assert.Equal(24, (SnapshotSchema.required marked).Json)
        let json = SnapshotCodec.serializeCanonical marked |> unwrap "marked json"
        Assert.Contains("\"schemaVersion\":24", json)
        Assert.Contains("\"mayWait\":true", json)
        Assert.Contains("\"mustNotWait\":true", json)
        Assert.Contains("\"taskBound\":true", json)
        Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap "marked read" |> SnapshotCodec.serializeCanonical |> unwrap "marked again")
        Assert.Equal(
            Error(SnapshotCodecError.InvalidShape $"{SnapshotSchema.featureName SnapshotSchemaFeature.WaitAttributes} requires snapshot schemaVersion 24."),
            SnapshotCodec.deserialize (json.Replace("\"schemaVersion\":24", "\"schemaVersion\":23")))
        Assert.True(Result.isError (SnapshotCodec.deserialize (json.Replace("\"mayWait\":true", "\"mayWait\":false"))))
        let record = RecordCodec.serialize marked |> unwrap "record"
        Assert.Contains("may-wait=true", record)
        Assert.Contains("must-not-wait=true", record)
        Assert.Contains("task-bound=true", record)
        Assert.Equal(json, RecordCodec.deserialize record |> unwrap "record read" |> SnapshotCodec.serializeCanonical |> unwrap "record again")
        // The markers are part of both fingerprints.
        Assert.NotEqual(SnapshotFingerprint.compute plain |> unwrap "plain", SnapshotFingerprint.compute marked |> unwrap "marked")
        Assert.NotEqual(SemanticFingerprint.compute plain |> unwrap "plain", SemanticFingerprint.compute marked |> unwrap "marked")

    // ---- Authoring wire forms ----------------------------------------------------------------

    let private execute (session: AuthoringSession) (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore
        proposal

    let private failure session line =
        use reply = execute session line
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.GetRawText())
        reply.RootElement.GetRawText()

    let private stringJson = """{"kind":"primitive","primitive":"string"}"""
    let private fsJson = """{"kind":"capability","name":"Fs"}"""
    let private invoke operation = $"""{{"kind":"capabilityInvoke","capabilityValue":{{"kind":"parameter","name":"fs"}},"capability":"Fs","operation":"{operation}","arguments":[]}}"""
    let private functionJson name (mustNotWait: bool) operation =
        let marker = if mustNotWait then """"mustNotWait":true,""" else ""
        $"""{{"id":"{name}","op":"simulateCreateFunctionByName",{marker}"displayName":"{name}","parameters":[{{"displayName":"fs","type":{fsJson}}}],"resultType":{stringJson},"requiredEffects":["io"],"body":{invoke operation}}}"""

    [<Fact>]
    let ``creation and set operations carry the markers and the review shows may wait`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-wait-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap "create"
            publish session """{"id":"e","op":"simulateCreateEffect","displayName":"io"}""" |> ignore
            publish session $"""{{"id":"c","op":"simulateCreateCapabilityByName","displayName":"Fs","taskBound":true,"operations":[{{"displayName":"read","parameters":[],"resultType":{stringJson},"effect":"io","mayWait":true}},{{"displayName":"peek","parameters":[],"resultType":{stringJson},"effect":"io"}}]}}""" |> ignore
            let fs = session.Declarations().Capabilities |> List.exactlyOne
            Assert.True(fs.TaskBound)
            Assert.Equal<(string * bool) list>([ "peek", false; "read", true ], fs.Operations |> List.map (fun op -> op.DisplayName, op.MayWait) |> List.sort)
            let listed = (result session """{"id":"d","op":"declarations"}""").GetProperty("capabilities")[0]
            Assert.True(listed.GetProperty("taskBound").GetBoolean())

            // A must-not-wait function that invokes a waiting operation is rejected; one that does not is accepted.
            let rejected = failure session (functionJson "handler" true "read")
            Assert.Contains("MustNotWaitFunctionWaits", rejected)
            Assert.Contains("Fs.read", rejected)
            publish session (functionJson "handler" true "peek") |> ignore
            publish session (functionJson "loader" false "read") |> ignore
            let handler = session.Declarations().Functions |> List.find (fun item -> item.DisplayName = "handler")
            Assert.True(handler.MustNotWait)

            // The focus of a waiting function shows the path; a quiet one shows nothing.
            let focus name = (result session $"""{{"id":"r","op":"readFunction","function":"{name}"}}""")
            Assert.Contains("Fs.read", (focus "loader").GetProperty("waitPath").GetString())
            Assert.False((focus "handler").TryGetProperty("mayWait") |> fst)
            Assert.True((focus "handler").GetProperty("function").GetProperty("mustNotWait").GetBoolean())

            // Marking `peek` waiting would make `handler` wait: rejected. The exact form then unmarks
            // `handler` in the same batch; the review lists both attributeChanges.
            let peek = fs.Operations |> List.find (fun op -> op.DisplayName = "peek")
            let setPeek = $"""{{"kind":"setMayWait","operationId":"{StableId.value peek.Id}","mayWait":true}}"""
            Assert.Contains("MustNotWaitFunctionWaits", failure session $"""{{"id":"x","op":"simulateOperations","operations":[{setPeek}]}}""")
            let review = publish session $"""{{"id":"x","op":"simulateOperations","operations":[{{"kind":"setMustNotWait","functionId":"{StableId.value handler.Id}","mustNotWait":false}},{setPeek}]}}"""
            let changes = review.GetProperty("review").GetProperty("diff").GetProperty("attributeChanges")
            Assert.Equal(2, changes.GetArrayLength())
            Assert.Equal("mayWait", changes[0].GetProperty("attribute").GetString())
            Assert.Equal("Fs.peek", changes[0].GetProperty("displayName").GetString())
            Assert.True(changes[0].GetProperty("value").GetBoolean())
            Assert.Equal("mustNotWait", changes[1].GetProperty("attribute").GetString())
            Assert.False(changes[1].GetProperty("value").GetBoolean())
            Assert.True(session.Declarations().Capabilities.Head.Operations |> List.forall (fun op -> op.MayWait))
            publish session """{"id":"y","op":"simulateOperationsByName","operations":[{"kind":"setTaskBound","capability":"Fs","taskBound":false},{"kind":"setMayWait","capability":"Fs","operation":"read","mayWait":false}]}""" |> ignore
            let after = session.Declarations()
            Assert.False(after.Capabilities.Head.TaskBound)
            Assert.False((after.Functions |> List.find (fun item -> item.DisplayName = "handler")).MustNotWait)
            Assert.Contains("AttributeUnchanged", failure session """{"id":"z","op":"simulateOperationsByName","operations":[{"kind":"setTaskBound","capability":"Fs","taskBound":false}]}""")
            Assert.Contains("NoChange", failure session """{"id":"z","op":"simulateOperationsByName","operations":[{"kind":"setTaskBound","capability":"Fs","taskBound":true},{"kind":"setTaskBound","capability":"Fs","taskBound":false}]}""")
            session.Close()
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``a dependent cannot reach an imported waiting operation from a must-not-wait function`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-wait-dependency-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let libraryPath = Path.Combine(root, "library.json")
            using (AuthoringSession.createNew libraryPath |> unwrap "library") (fun library ->
                publish library """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}""" |> ignore
                publish library $"""{{"id":"c","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Fs","operations":[{{"displayName":"read","parameters":[],"resultType":{stringJson},"effect":"io","mayWait":true}}]}}""" |> ignore
                library.Close())
            use consumer = AuthoringSession.createNewWithDependency (Path.Combine(root, "consumer.json")) (Some libraryPath) |> unwrap "consumer"
            let rejected = failure consumer (functionJson "handler" true "read")
            Assert.Contains("MustNotWaitFunctionWaits", rejected)
            Assert.Contains("waiting operation Fs.read", rejected)
            // The creation review shows the derived fact.
            let review = (publish consumer (functionJson "loader" false "read")).GetProperty("review")
            Assert.True(review.GetProperty("mayWait").GetBoolean())
            Assert.Contains("Fs.read", review.GetProperty("waitPath").GetString())
            // The imported operation's marker is read-only.
            let read = (consumer.Declarations().Capabilities |> List.exactlyOne).Operations |> List.exactlyOne
            Assert.Contains(
                "ImportedDeclarationModified",
                failure consumer $"""{{"id":"x","op":"simulateOperations","operations":[{{"kind":"setMayWait","operationId":"{StableId.value read.Id}","mayWait":false}}]}}""")
            consumer.Close()
        finally
            Directory.Delete(root, true)

    /// `spawnAll<T>` runs a ParallelAll over `List<T>`; `outer<T>` forwards to it. `direct` and
    /// `forwarded` instantiate T with the capability `Ui`.
    let private genericCrossingSnapshot (taskBound: bool) : Snapshot =
        let ui: SemanticCapability = { Id = uiId; DisplayName = "Ui"; Exported = true; TaskBound = taskBound; Operations = [] }
        let tp name = id<TypeParameterTag> name
        let listOf item = generic BuiltinGenericType.List [ item ]
        let genericFn name (typeParameter: string) parameters resultType nodes =
            { fn name false parameters resultType [] nodes with TypeParameters = [ tp typeParameter, typeParameter ] }
        let spawnResult = resultOf (listOf int64Type)
        let worker =
            genericFn "genericWorker" "U" [ parameter "gw-x" (TypeRef.TypeParameter(tp "U")) ] (resultOf int64Type)
                [ node "gw-lit" (SemanticNodeKind.Int64Literal 1L)
                  node "gw-ok" (SemanticNodeKind.ResultOk(stringType, id "gw-lit")) ]
        let spawnAll =
            genericFn "spawnAll" "T" [ parameter "sa-xs" (listOf (TypeRef.TypeParameter(tp "T"))) ] spawnResult
                [ node "sa-p" (SemanticNodeKind.ParameterReference(id "sa-xs"))
                  node "sa-all" (SemanticNodeKind.ParallelAll(id "sa-p", id "genericWorker", [])) ]
        let outer =
            genericFn "outer" "V" [ parameter "o-xs" (listOf (TypeRef.TypeParameter(tp "V"))) ] spawnResult
                [ node "o-p" (SemanticNodeKind.ParameterReference(id "o-xs"))
                  node "o-call" (SemanticNodeKind.DirectCall(id "spawnAll", [ bind "sa-xs" "o-p" ])) ]
        let user name target targetParameter =
            fn name false [ parameter (name + "-uis") (listOf uiType) ] spawnResult []
                [ node (name + "-p") (SemanticNodeKind.ParameterReference(id (name + "-uis")))
                  node (name + "-call") (SemanticNodeKind.DirectCall(id target, [ bind targetParameter (name + "-p") ])) ]
        { Snapshot.empty (id<SnapshotTag> "snapshot-generic-crossing") with
            Capabilities = [ ui ]
            Functions = [ worker; spawnAll; outer; user "direct" "spawnAll" "sa-xs"; user "forwarded" "outer" "o-xs" ] }

    [<Fact>]
    let ``a generic function that moves its type parameter across tasks rejects a task-bound instantiation, also when forwarded`` () =
        Assert.Empty(SnapshotValidator.validate (genericCrossingSnapshot false))
        let issues = SnapshotValidator.validate (genericCrossingSnapshot true)
        Assert.All(issues, fun issue -> Assert.Equal("TaskBoundValueCrossesTask", issue.Code))
        let messages = issues |> List.map (fun issue -> issue.Message)
        Assert.Equal(2, messages.Length)
        Assert.Contains(messages, fun message -> message.Contains(uuid "direct-call"))
        Assert.Contains(messages, fun message -> message.Contains(uuid "forwarded-call"))
