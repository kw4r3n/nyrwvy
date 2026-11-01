namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Nyrwvy.Core
open Xunit
open Xunit.Abstractions

/// The Zig model reader's `--validate` mode (tools/zig/model-reader) reports the issues of the
/// codes in `validation_codes.txt` exactly as `SnapshotValidator.validate` does on the shape-decoded
/// model, filtered to those codes.
module ZigValidatorSupport =
    let codes () = File.ReadAllLines(ZigModelReaderSupport.toolFile "validation_codes.txt") |> Array.filter (fun line -> line <> "") |> Set.ofArray

    let escape (text: string) = text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")

    let objectsIn (node: JsonNode) =
        ZigModelReaderSupport.containers node |> List.choose (function :? JsonObject as o -> Some o | _ -> None)

    /// The objects of each array property with this name, in document order.
    let arrayItems (name: string) (document: JsonNode) =
        objectsIn document
        |> List.collect (fun o ->
            match o[name] with
            | :? JsonArray as items -> [ items ]
            | _ -> [])

    let private obj (pairs: (string * JsonNode) list) =
        let o = JsonObject()
        for name, value in pairs do o[name] <- value
        o

    let private s (text: string) = JsonValue.Create text :> JsonNode

    let private typeParameter id name = obj [ "typeParameterId", s id; "displayName", s name ] :> JsonNode

    let private obligation id call parameter (resolution: string option) =
        obj
            [ yield "id", s id
              yield "kind", s "missingArgument"
              yield "originTransactionId", s "origin-transaction"
              yield "callNodeId", s call
              yield "parameterId", s parameter
              match resolution with
              | None -> yield "status", s "open"
              | Some value ->
                  yield "status", s "resolved"
                  yield "resolutionTransactionId", s "resolution-transaction"
                  yield "resolutionValueNodeId", s value ]
        :> JsonNode

    let private prim name = obj [ "kind", s "primitive"; "primitive", s name ] :> JsonNode
    let private gen name (arguments: JsonNode list) = obj [ "kind", s "builtinGeneric"; "generic", s name; "arguments", JsonArray(Array.ofList arguments) ] :> JsonNode
    let private named id (arguments: JsonNode list) =
        obj [ yield "kind", s "named"; yield "typeId", s id; if not arguments.IsEmpty then yield "arguments", JsonArray(Array.ofList arguments) ] :> JsonNode
    let private tparam id = obj [ "kind", s "typeParameter"; "typeParameterId", s id ] :> JsonNode
    let private field id (typeRef: JsonNode) = obj [ "id", s id; "displayName", s id; "type", typeRef ] :> JsonNode

    /// A literal node of a primitive type.
    let private literalNode id literal =
        match literal with
        | "string" -> obj [ "id", s id; "kind", s "stringLiteral"; "value", s "text" ]
        | "int64" -> obj [ "id", s id; "kind", s "int64Literal"; "value", JsonValue.Create 7L ]
        | "bool" -> obj [ "id", s id; "kind", s "boolLiteral"; "value", JsonValue.Create true ]
        | "float64" -> obj [ "id", s id; "kind", s "float64Literal"; "bits", s "3ff0000000000000" ]
        | _ -> obj [ "id", s id; "kind", s "unitLiteral" ]
        :> JsonNode

    /// Replaced by a `\ud800` escape in the written text, which JsonNode cannot write itself.
    let loneSurrogateMarker = "lone-surrogate-marker"

    /// The type references of one kind, anywhere in the document.
    let private typeRefs kind (root: JsonObject) =
        objectsIn root |> List.filter (fun o -> o.ContainsKey "kind" && string o["kind"] = kind && not (o.ContainsKey "id"))

    let private withTypeParameters (o: JsonObject) (declarations: JsonNode list) =
        let list = match o["typeParameters"] with :? JsonArray as items -> items | _ -> (let items = JsonArray() in o["typeParameters"] <- items; items)
        for declaration in declarations do list.Add declaration

    /// Named edits that keep the shape valid and break the ported rules. Each returns false when it
    /// does not apply to the document.
    let semanticEdits: (string * (JsonObject -> bool)) list =
        let first name (root: JsonObject) = arrayItems name root |> List.tryPick (fun items -> if items.Count > 0 then Some items else None)
        let functions (root: JsonObject) = match root["functions"] with :? JsonArray as items -> [ for f in items -> f :?> JsonObject ] | _ -> []
        let directCalls (root: JsonObject) =
            [ for f in functions root do
                  for node in ZigModelReaderSupport.graphNodes f do
                      if string (node["kind"]) = "directCall" then
                          match node["arguments"] with
                          | :? JsonArray as arguments when arguments.Count > 0 -> yield f, node, (arguments[0] :?> JsonObject)
                          | _ -> () ]
        let nodeOutside (root: JsonObject) (owner: JsonObject) =
            functions root |> List.filter (fun f -> not (Object.ReferenceEquals(f, owner))) |> List.tryPick (fun f -> ZigModelReaderSupport.graphNodes f |> List.tryHead)
        let addObligations (root: JsonObject) (make: JsonObject -> JsonObject -> JsonObject -> JsonNode list) =
            match directCalls root with
            | (owner, call, argument) :: _ ->
                let list = root["obligations"] :?> JsonArray
                for o in make owner call argument do list.Add o
                true
            | [] -> false
        // A function of the 3d edits: type parameters, (ID, type) parameters, result type, nodes and result node.
        let synthetic id (typeParameters: string list) (parameters: (string * JsonNode) list) (result: JsonNode) (nodes: JsonNode list) (resultNode: string) =
            let f =
                obj [ "id", s id; "displayName", s id
                      "parameters", JsonArray([| for pid, t in parameters -> obj [ "id", s pid; "displayName", s pid; "type", t ] :> JsonNode |])
                      "resultType", result; "requiredEffects", JsonArray()
                      "body", obj [ "nodes", JsonArray(Array.ofList nodes); "resultNodeId", s resultNode ] ]
            if not typeParameters.IsEmpty then withTypeParameters f [ for tp in typeParameters -> typeParameter tp tp ]
            f :> JsonNode
        let reference id parameter = obj [ "id", s id; "kind", s "parameterReference"; "parameterId", s parameter ] :> JsonNode
        let call id target (arguments: (string * string) list) =
            obj [ "id", s id; "kind", s "directCall"; "targetFunctionId", s target
                  "arguments", JsonArray([| for p, v in arguments -> obj [ "targetParameterId", s p; "valueNodeId", s v ] :> JsonNode |]) ] :> JsonNode
        [ "parent is self", (fun root -> root["parentSnapshotId"] <- s (string (root["snapshotId"])); true)
          for declarations in [ "types"; "effects"; "capabilities"; "functions"; "obligations" ] do
              $"duplicate {declarations} entry",
              (fun root ->
                  match root[declarations] with
                  | :? JsonArray as items when items.Count > 0 -> items.Add(items[0].DeepClone()); true
                  | _ -> false)
          for members in [ "fields"; "variants"; "operations"; "parameters"; "nodes" ] do
              $"second of {members} takes the first's ID",
              (fun root ->
                  match arrayItems members root |> List.tryFind (fun items -> items.Count > 1) with
                  | Some items -> items[1]["id"] <- s (string (items[0]["id"])); true
                  | None -> false)
          "a binding takes another's ID",
          (fun root ->
              let bindings = objectsIn root |> List.collect (fun o -> [ for name in [ "valueBinding"; "errorBinding"; "binding" ] do match o[name] with :? JsonObject as b -> yield b | _ -> () ])
              match bindings with
              | b1 :: b2 :: _ -> b2["id"] <- s (string (b1["id"])); true
              | _ -> false)
          for blank in [ ""; " "; "\u3000" ] do
              $"every display name is '{blank}'",
              (fun root ->
                  let named = objectsIn root |> List.filter (fun o -> o.ContainsKey "displayName")
                  for o in named do o["displayName"] <- s blank
                  not named.IsEmpty)
          "bindings get a blank display name",
          (fun root ->
              let bindings = objectsIn root |> List.collect (fun o -> [ for name in [ "valueBinding"; "errorBinding"; "binding" ] do match o[name] with :? JsonObject as b -> yield b | _ -> () ])
              for b in bindings do b["displayName"] <- s "\t"
              not bindings.IsEmpty)
          for name in [ "effectId"; "callNodeId"; "parameterId"; "resolutionValueNodeId"; "targetFunctionId" ] do
              $"{name} points nowhere",
              (fun root ->
                  match objectsIn root |> List.tryFind (fun o -> o.ContainsKey name) with
                  | Some o -> o[name] <- s "missing-reference"; true
                  | None -> false)
          "a required effect is unknown and one is listed twice",
          (fun root ->
              match functions root |> List.tryFind (fun f -> (f["requiredEffects"] :?> JsonArray).Count > 0) with
              | Some f ->
                  let effects = f["requiredEffects"] :?> JsonArray
                  effects.Add(effects[0].DeepClone())
                  effects.Add(s "missing-effect")
                  true
              | None -> false)
          "a function declares unused, duplicate and blank type parameters",
          (fun root ->
              match functions root with
              | f :: _ ->
                  withTypeParameters f [ typeParameter "tp-unused" "T"; typeParameter "tp-unused" "T"; typeParameter "tp-blank" " " ]
                  true
              | [] -> false)
          "a type declares duplicate and blank type parameters",
          (fun root ->
              match first "types" root with
              | Some types ->
                  withTypeParameters (types[0] :?> JsonObject) [ typeParameter "tp-type" "T"; typeParameter "tp-type" "T"; typeParameter "tp-type-blank" "" ]
                  true
              | None -> false)
          "two declarations share a type parameter",
          (fun root ->
              match functions root with
              | f :: _ ->
                  withTypeParameters f [ typeParameter "tp-shared" "S" ]
                  match root["types"] with
                  | :? JsonArray as types when types.Count > 0 -> withTypeParameters (types[0] :?> JsonObject) [ typeParameter "tp-shared" "S" ]
                  | _ -> withTypeParameters f [ typeParameter "tp-shared" "S" ]
                  true
              | [] -> false)
          "a capability operation uses a type parameter",
          (fun root ->
              match arrayItems "operations" root |> List.tryFind (fun items -> items.Count > 0) with
              | Some operations ->
                  operations[0]["resultType"] <- obj [ "kind", s "builtinGeneric"; "generic", s "list"; "arguments", JsonArray(obj [ "kind", s "typeParameter"; "typeParameterId", s "tp-op" ]) ]
                  true
              | None -> false)
          "an irregular recursive type",
          (fun root ->
              let self (arguments: JsonNode list) = obj [ "kind", s "named"; "typeId", s "rec-type"; "arguments", JsonArray(Array.ofList arguments) ] :> JsonNode
              let int64 () = obj [ "kind", s "primitive"; "primitive", s "int64" ] :> JsonNode
              let field id (typeRef: JsonNode) = obj [ "id", s id; "displayName", s id; "type", typeRef ] :> JsonNode
              (root["types"] :?> JsonArray).Add(
                  obj
                      [ "id", s "rec-type"
                        "displayName", s "Rec"
                        "typeParameters", JsonArray(typeParameter "rec-t" "T")
                        "kind", s "product"
                        "fields",
                        JsonArray(
                            field "rec-irregular" (self [ int64 () ]),
                            field "rec-regular" (self [ obj [ "kind", s "typeParameter"; "typeParameterId", s "rec-t" ] ]),
                            field "rec-nested" (obj [ "kind", s "builtinGeneric"; "generic", s "option"; "arguments", JsonArray(self [ self [ int64 () ] ]) ])) ])
              true)
          "obligations on a direct call",
          (fun root ->
              addObligations root (fun owner call argument ->
                  let callId = string (call["id"])
                  let parameter = string (argument["targetParameterId"])
                  let value = string (argument["valueNodeId"])
                  let outside = nodeOutside root owner |> Option.map (fun n -> string (n["id"])) |> Option.defaultValue "missing-node"
                  let other = ZigModelReaderSupport.graphNodes owner |> List.tryFind (fun n -> string (n["kind"]) <> "directCall") |> Option.map (fun n -> string (n["id"])) |> Option.defaultValue "missing-node"
                  [ obligation "ob-open-1" callId parameter None
                    obligation "ob-open-2" callId parameter None
                    obligation "ob-resolved" callId parameter (Some value)
                    obligation "ob-resolved-elsewhere" callId parameter (Some outside)
                    obligation "ob-foreign-parameter" callId "missing-parameter" None
                    obligation "ob-not-a-call" other parameter None
                    obligation "ob-unknown-node" "missing-node" parameter None
                    obligation "ob-unknown-node" "another-missing-node" parameter None ]))
          // A model that stays valid but incomplete: the lowering test's IncompleteSnapshot.
          "an open obligation for a dropped argument",
          (fun root ->
              addObligations root (fun _ call argument ->
                  (call["arguments"] :?> JsonArray).Remove argument |> ignore
                  [ obligation "ob-open-dropped" (string (call["id"])) (string (argument["targetParameterId"])) None ]))
          // Slice 3b: type references, equatability, task types, payload type parameters, cycles.
          for kind, key in [ "named", "typeId"; "capability", "capabilityId" ] do
              $"{kind} type references point nowhere",
              (fun root ->
                  let refs = typeRefs kind root
                  for r in refs do r[key] <- s $"missing-{kind}"
                  not refs.IsEmpty)
          "named type references gain an argument",
          (fun root ->
              let refs = typeRefs "named" root
              for r in refs do
                  match r["arguments"] with
                  | :? JsonArray as arguments -> arguments.Add(prim "int64")
                  | _ -> r["arguments"] <- JsonArray(prim "int64")
              not refs.IsEmpty)
          "builtin generics gain an argument",
          (fun root ->
              let refs = typeRefs "builtinGeneric" root
              for r in refs do (r["arguments"] :?> JsonArray).Add(prim "int64")
              not refs.IsEmpty)
          "builtin generics lose their arguments",
          (fun root ->
              let refs = typeRefs "builtinGeneric" root |> List.filter (fun r -> (r["arguments"] :?> JsonArray).Count > 0)
              for r in refs do r["arguments"] <- JsonArray()
              not refs.IsEmpty)
          "declared types hold task scopes",
          (fun root ->
              (root["types"] :?> JsonArray).Add(
                  obj
                      [ "id", s "scope-type"
                        "displayName", s "Scoped"
                        "kind", s "product"
                        "fields",
                        JsonArray(
                            field "scope-list" (gen "list" [ gen "taskScope" [] ]),
                            field "scope-oneshot" (gen "oneShot" [ gen "option" [ gen "taskScope" [] ] ]),
                            field "scope-argument" (named "scope-type" [ gen "taskScope" [] ])) ])
              true)
          "declared types use types that are not equatable",
          (fun root ->
              let types = root["types"] :?> JsonArray
              types.Add(
                  obj
                      [ "id", s "eq-union"
                        "displayName", s "EqUnion"
                        "kind", s "union"
                        "variants", JsonArray(obj [ "id", s "eq-variant"; "displayName", s "V"; "fields", JsonArray(field "eq-variant-field" (prim "float64")) ]) ])
              types.Add(
                  obj
                      [ "id", s "eq-type"
                        "displayName", s "Eq"
                        "typeParameters", JsonArray(typeParameter "eq-t" "T")
                        "kind", s "product"
                        "fields",
                        JsonArray(
                            field "eq-float" (gen "set" [ prim "float64" ]),
                            field "eq-function" (gen "map" [ gen "list" [ gen "option" [ gen "result" [ prim "int64"; obj [ "kind", s "function"; "parameters", JsonArray(prim "int64"); "resultType", prim "bool" ] ] ] ]; prim "int64" ]),
                            field "eq-self" (gen "set" [ named "eq-type" [ prim "int64" ] ]),
                            field "eq-parameter" (gen "map" [ tparam "eq-t"; prim "int64" ]),
                            field "eq-handle" (gen "set" [ gen "exception" [] ]),
                            field "eq-task" (gen "map" [ gen "task" [ prim "int64" ]; prim "int64" ]),
                            field "eq-capability" (gen "set" [ obj [ "kind", s "capability"; "capabilityId", s "missing-capability" ] ]),
                            field "eq-variant" (gen "set" [ gen "result" [ named "eq-union" []; prim "string" ] ]),
                            field "eq-nested" (gen "set" [ named "eq-type" [ prim "string" ] ])) ])
              true)
          "a capability operation passes task types",
          (fun root ->
              match arrayItems "operations" root |> List.tryFind (fun items -> items.Count > 0) with
              | Some operations ->
                  (root["types"] :?> JsonArray).Add(
                      obj [ "id", s "task-holder"; "displayName", s "Holder"; "kind", s "product"; "fields", JsonArray(field "task-held" (gen "option" [ gen "task" [ prim "int64" ] ])) ])
                  operations[0]["resultType"] <- named "task-holder" []
                  if operations.Count > 1 then operations[1]["resultType"] <- gen "exception" []
                  true
              | None -> false)
          "payload types and a field use undeclared type parameters",
          (fun root ->
              let payload = [ "itemType"; "keyType"; "valueType"; "resultType"; "okType"; "errorType" ]
              let mutable changed = false
              for node in ZigModelReaderSupport.graphNodes root do
                  for key in payload do
                      if node.ContainsKey key then (node[key] <- gen "list" [ tparam "tp-undeclared" ]; changed <- true)
                  if node.ContainsKey "typeArguments" then (node["typeArguments"] <- JsonArray(tparam "tp-undeclared", tparam "tp-undeclared"); changed <- true)
              match first "types" root with
              | Some types ->
                  let fields = objectsIn types[0] |> List.filter (fun o -> o.ContainsKey "type" && o.ContainsKey "displayName")
                  for f in fields |> List.truncate 1 do (f["type"] <- gen "result" [ tparam "tp-field"; tparam "tp-field" ]; changed <- true)
              | None -> ()
              changed)
          "a function uses another declaration's type parameter",
          (fun root ->
              match functions root, first "types" root with
              | f :: _, Some types ->
                  withTypeParameters (types[0] :?> JsonObject) [ typeParameter "tp-foreign" "F" ]
                  f["resultType"] <- tparam "tp-foreign"
                  true
              | _ -> false)
          "the last node with a node reference refers to itself",
          (fun root ->
              let referring = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> n |> Seq.exists (fun p -> p.Key.EndsWith "NodeId"))
              match List.tryLast referring with
              | Some node ->
                  let key = (node |> Seq.find (fun p -> p.Key.EndsWith "NodeId")).Key
                  node[key] <- s (string node["id"])
                  true
              | None -> false)
          "the last node with a reference list refers to itself",
          (fun root ->
              let lists = [ "itemNodeIds"; "arguments"; "context"; "bodyContext"; "handlerContext"; "fields" ]
              let listed (n: JsonObject) = lists |> List.tryPick (fun key -> match n[key] with :? JsonArray as items when items.Count > 0 -> Some items | _ -> None)
              match ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> (listed n).IsSome) |> List.tryLast with
              | Some node ->
                  let items = (listed node).Value
                  match items[0] with
                  | :? JsonObject as binding -> binding["valueNodeId"] <- s (string node["id"])
                  | _ -> items[0] <- s (string node["id"])
                  true
              | None -> false)
          "a nested node refers to its enclosing node",
          (fun root ->
              let enclosing =
                  ZigModelReaderSupport.graphNodes root
                  |> List.tryPick (fun n ->
                      ZigModelReaderSupport.graphNodes n
                      |> List.tryPick (fun inner -> inner |> Seq.tryFind (fun p -> p.Key.EndsWith "NodeId") |> Option.map (fun p -> n, inner, p.Key)))
              match enclosing with
              | Some(n, inner, key) -> inner[key] <- s (string n["id"]); true
              | None -> false)
          "declaration references of nodes take the node's own ID",
          (fun root ->
              let mutable changed = false
              for node in ZigModelReaderSupport.graphNodes root do
                  for key in [ for p in node -> p.Key ] do
                      if key.EndsWith "FunctionId" || key.EndsWith "OperationId" || key = "fieldId" || key = "variantId" || key = "parameterId" || key = "patternBindingId" || key = "capabilityId" then
                          (node[key] <- s (string node["id"]); changed <- true)
              changed)
          "OneShotComplete completes with a value of another type",
          (fun root ->
              let mutable changed = false
              for container in ZigModelReaderSupport.containers root do
                  match container with
                  | :? JsonObject as graph when graph.ContainsKey "nodes" && (graph["nodes"] :? JsonArray) ->
                      let nodes = graph["nodes"] :?> JsonArray
                      for item in List.ofSeq nodes do
                          match item with
                          | :? JsonObject as node when string node["kind"] = "oneShotComplete" ->
                              node["valueNodeId"] <- s "oneshot-text"
                              nodes.Add(literalNode "oneshot-text" "string")
                              changed <- true
                          | _ -> ()
                  | _ -> ()
              changed)
          // Slice 3c: literals of each type as every operand, graph results and scopes.
          for literal in [ "string"; "int64"; "bool"; "float64"; "unit" ] do
              $"every operand is a {literal} literal",
              (fun root ->
                  let mutable changed = false
                  for f in functions root do
                      let body = f["body"] :?> JsonObject
                      let id = $"lit-{literal}"
                      for node in ZigModelReaderSupport.graphNodes f do
                          for key in [ for p in node -> p.Key ] do
                              if key.EndsWith "NodeId" then (node[key] <- s id; changed <- true)
                      (body["nodes"] :?> JsonArray).Add(literalNode id literal)
                  changed)
          for rotation in 0 .. 2 do
              $"operands are literals of a type by position, rotated {rotation}",
              (fun root ->
                  let literals = [ "string"; "int64"; "bool"; "float64"; "unit" ]
                  let mutable changed = false
                  for f in functions root do
                      let nodes = (f["body"] :?> JsonObject)["nodes"] :?> JsonArray
                      for node in ZigModelReaderSupport.graphNodes f do
                          let keys = [ for p in node do if p.Key.EndsWith "NodeId" then yield p.Key ]
                          keys |> List.iteri (fun i key -> node[key] <- s ("pos-" + literals[(i + rotation) % literals.Length]); changed <- true)
                      for literal in literals do nodes.Add(literalNode ("pos-" + literal) literal)
                  changed)
          for rotation in [ 0; 1; 4; 5; 6; 7 ] do
              $"operands are typed containers by position, rotated {rotation}",
              (fun root ->
                  let prim name = obj [ "kind", s "primitive"; "primitive", s name ] :> JsonNode
                  let values =
                      [ obj [ "id", s "c-list-int64"; "kind", s "listEmpty"; "itemType", prim "int64" ]
                        obj [ "id", s "c-list-float"; "kind", s "listEmpty"; "itemType", prim "float64" ]
                        literalNode "c-string" "string" :?> JsonObject
                        literalNode "c-int64" "int64" :?> JsonObject
                        obj [ "id", s "c-set-string"; "kind", s "setEmpty"; "itemType", prim "string" ]
                        obj [ "id", s "c-map"; "kind", s "mapEmpty"; "keyType", prim "string"; "valueType", prim "int64" ]
                        obj [ "id", s "c-option"; "kind", s "optionNone"; "itemType", prim "int64" ]
                        obj [ "id", s "c-result"; "kind", s "resultErr"; "okType", prim "int64"; "errorNodeId", s "c-string" ] ]
                  let mutable changed = false
                  for f in functions root do
                      let nodes = (f["body"] :?> JsonObject)["nodes"] :?> JsonArray
                      for node in ZigModelReaderSupport.graphNodes f do
                          let keys = [ for p in node do if p.Key.EndsWith "NodeId" then yield p.Key ]
                          keys |> List.iteri (fun i key -> node[key] <- s (string (values[(i + rotation) % values.Length]["id"])); changed <- true)
                      for value in values do nodes.Add(value.DeepClone())
                  changed)
          "left operands are String and right operands Int64 literals",
          (fun root ->
              let mutable changed = false
              for f in functions root do
                  let nodes = (f["body"] :?> JsonObject)["nodes"] :?> JsonArray
                  for node in ZigModelReaderSupport.graphNodes f do
                      if node.ContainsKey "leftNodeId" && node.ContainsKey "rightNodeId" then
                          node["leftNodeId"] <- s "lit-left"
                          node["rightNodeId"] <- s "lit-right"
                          changed <- true
                  nodes.Add(literalNode "lit-left" "string")
                  nodes.Add(literalNode "lit-right" "int64")
              changed)
          "the first nested graph ends in a String literal and every body in a Unit literal",
          (fun root ->
              let graphs = objectsIn root |> List.filter (fun o -> o.ContainsKey "nodes" && o.ContainsKey "resultNodeId")
              let bodies = functions root |> List.map (fun f -> f["body"] :?> JsonObject)
              let endIn (graph: JsonObject) id literal =
                  (graph["nodes"] :?> JsonArray).Add(literalNode id literal)
                  graph["resultNodeId"] <- s id
              match graphs |> List.tryFind (fun g -> not (bodies |> List.exists (fun b -> Object.ReferenceEquals(b, g)))) with
              | Some nested -> endIn nested "nested-string" "string"
              | None -> ()
              bodies |> List.iteri (fun i body -> endIn body $"body-unit-{i}" "unit")
              not bodies.IsEmpty)
          "a node uses an abort node and a body result points into a nested graph",
          (fun root ->
              match functions root with
              | f :: _ ->
                  let body = f["body"] :?> JsonObject
                  let nodes = body["nodes"] :?> JsonArray
                  if nodes.Count = 0 then false
                  else
                      let first = nodes[0] :?> JsonObject
                      nodes.Add(obj [ "id", s "abort-node"; "kind", s "abort"; "errorNodeId", s (string first["id"]) ])
                      match ZigModelReaderSupport.graphNodes f |> List.filter (fun n -> n |> Seq.exists (fun p -> p.Key.EndsWith "NodeId")) |> List.tryLast with
                      | Some user -> user[(user |> Seq.find (fun p -> p.Key.EndsWith "NodeId")).Key] <- s "abort-node"
                      | None -> ()
                      let nested = ZigModelReaderSupport.graphNodes f |> List.filter (fun n -> not (nodes |> Seq.exists (fun t -> Object.ReferenceEquals(t, n))))
                      match nested with
                      | inner :: _ ->
                          body["resultNodeId"] <- s (string inner["id"])
                          first |> Seq.tryFind (fun p -> p.Key.EndsWith "NodeId") |> Option.iter (fun p -> first[p.Key] <- s (string inner["id"]))
                      | [] -> body["resultNodeId"] <- s "missing-node"
                      true
              | [] -> false)
          "in each function the last node with a node reference uses an abort node",
          (fun root ->
              let mutable changed = false
              for f in functions root do
                  let nodes = (f["body"] :?> JsonObject)["nodes"] :?> JsonArray
                  match ZigModelReaderSupport.graphNodes f |> List.filter (fun n -> n |> Seq.exists (fun p -> p.Key.EndsWith "NodeId")) |> List.tryLast with
                  | Some user when nodes.Count > 0 ->
                      let abortId = "abort-" + string (f["id"])
                      nodes.Insert(0, obj [ "id", s abortId; "kind", s "abort"; "errorNodeId", s (string (nodes[0]["id"])) ])
                      user[(user |> Seq.find (fun p -> p.Key.EndsWith "NodeId")).Key] <- s abortId
                      changed <- true
                  | _ -> ()
              changed)
          "product nodes target an unknown type",
          (fun root ->
              let products = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> n.ContainsKey "typeId")
              for n in products do n["typeId"] <- s "missing-type"
              not products.IsEmpty)
          "product nodes target a union type",
          (fun root ->
              let unions = match root["types"] with :? JsonArray as types -> [ for t in types do if string (t["kind"]) = "union" then yield string (t["id"]) ] | _ -> []
              let products = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> n.ContainsKey "typeId")
              match unions with
              | union :: _ when not products.IsEmpty -> (for n in products do n["typeId"] <- s union); true
              | _ -> false)
          for mode in 0 .. 3 do
            $"field bindings are duplicated, retargeted, emptied or given a String value ({mode})",
            (fun root ->
              let nodes = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> match n["fields"] with :? JsonArray as f -> f.Count > 0 | _ -> false)
              nodes |> List.iter (fun n ->
                  let fields = n["fields"] :?> JsonArray
                  match mode with
                  | 0 -> fields.Add(fields[0].DeepClone())
                  | 1 -> fields[0]["targetFieldId"] <- s "missing-field"
                  | 2 -> if string (n["kind"]) = "productUpdate" then fields.Clear() else fields.RemoveAt 0
                  | _ ->
                      ((n.Parent :?> JsonArray).Add(literalNode "field-string" "string"))
                      for f in fields do f["valueNodeId"] <- s "field-string")
              not nodes.IsEmpty)
          for mode in 0 .. 3 do
            $"union match arms are duplicated, dropped, merged or bind fields twice ({mode})",
            (fun root ->
              let matches = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> string (n["kind"]) = "matchUnion")
              matches |> List.iter (fun n ->
                  let arms = n["arms"] :?> JsonArray
                  match mode with
                  | 0 -> arms.Add(arms[0].DeepClone())
                  | 1 -> if arms.Count > 1 then arms.RemoveAt 0
                  | 2 ->
                      let arm = arms[0] :?> JsonObject
                      match arm["variantId"] with
                      | null -> ()
                      | v ->
                          arm.Remove "variantId" |> ignore
                          arm["variantIds"] <- JsonArray(s (string v), s "other-variant")
                  | _ ->
                      let bindings = arms[0]["fieldBindings"] :?> JsonArray
                      if bindings.Count > 0 then
                          let copy = bindings[0].DeepClone()
                          copy["binding"]["id"] <- s "copied-binding"
                          bindings.Add copy)
              not matches.IsEmpty)
          "pattern binding types become Bytes",
          (fun root ->
              let bindings = objectsIn root |> List.collect (fun o -> [ for name in [ "valueBinding"; "errorBinding"; "binding" ] do match o[name] with :? JsonObject as b -> yield b | _ -> () ])
              for b in bindings do b["type"] <- prim "bytes"
              not bindings.IsEmpty)
          // Callbacks: signatures, purity and context bindings.
          "every function gains an Int64 parameter",
          (fun root ->
              for f in functions root do (f["parameters"] :?> JsonArray).Add(obj [ "id", s ("extra-" + string (f["id"])); "displayName", s "extra"; "type", prim "int64" ])
              not (functions root).IsEmpty)
          "every function loses its first parameter",
          (fun root ->
              let mutable changed = false
              for f in functions root do
                  let parameters = f["parameters"] :?> JsonArray
                  if parameters.Count > 0 then (parameters.RemoveAt 0; changed <- true)
              changed)
          "function parameter types become Bytes",
          (fun root ->
              let mutable changed = false
              for f in functions root do
                  for p in f["parameters"] :?> JsonArray do (p["type"] <- prim "bytes"; changed <- true)
              changed)
          "function results become Bytes",
          (fun root ->
              for f in functions root do f["resultType"] <- prim "bytes"
              not (functions root).IsEmpty)
          "every function requires the first effect",
          (fun root ->
              match root["effects"] with
              | :? JsonArray as effects when effects.Count > 0 ->
                  for f in functions root do (f["requiredEffects"] :?> JsonArray).Add(s (string (effects[0]["id"])))
                  true
              | _ -> false)
          for mode in 0 .. 2 do
              $"context bindings are duplicated, retargeted or given a String value ({mode})",
              (fun root ->
                  let mutable changed = false
                  for f in functions root do
                      for node in ZigModelReaderSupport.graphNodes f do
                          for name in [ "context"; "bodyContext"; "handlerContext"; "arguments" ] do
                              match node[name] with
                              | :? JsonArray as bindings when bindings.Count > 0 && (bindings[0] :? JsonObject) ->
                                  changed <- true
                                  match mode with
                                  | 0 -> bindings.Add(bindings[0].DeepClone())
                                  | 1 ->
                                      let target = if (bindings[0] :?> JsonObject).ContainsKey "targetCapabilityParameterId" then "targetCapabilityParameterId" else "targetParameterId"
                                      bindings[0][target] <- s "missing-parameter"
                                  | _ ->
                                      ((f["body"] :?> JsonObject)["nodes"] :?> JsonArray).Add(literalNode ("ctx-" + string (node["id"])) "string")
                                      bindings[0]["valueNodeId"] <- s ("ctx-" + string (node["id"]))
                              | _ -> ()
                  changed)
          "waits get a String limit",
          (fun root ->
              let mutable changed = false
              for f in functions root do
                  for node in ZigModelReaderSupport.graphNodes f do
                      if string (node["kind"]) = "oneShotWait" || string (node["kind"]) = "taskWait" then
                          ((f["body"] :?> JsonObject)["nodes"] :?> JsonArray).Add(literalNode ("limit-" + string (node["id"])) "string")
                          node["limitNodeId"] <- s ("limit-" + string (node["id"]))
                          changed <- true
              changed)
          "every function declares a type parameter",
          (fun root ->
              for f in functions root do withTypeParameters f [ typeParameter ("tp-" + string (f["id"])) "T" ]
              not (functions root).IsEmpty)
          "every function calls itself with String values",
          (fun root ->
              for f in functions root do
                  let nodes = (f["body"] :?> JsonObject)["nodes"] :?> JsonArray
                  let literal = "self-arg-" + string (f["id"])
                  nodes.Add(literalNode literal "string")
                  let arguments = JsonArray()
                  for p in f["parameters"] :?> JsonArray do arguments.Add(obj [ "targetParameterId", s (string (p["id"])); "valueNodeId", s literal ])
                  let call = obj [ "id", s ("self-call-" + string (f["id"])); "kind", s "directCall"; "targetFunctionId", s (string (f["id"])) ]
                  if arguments.Count > 0 then call["arguments"] <- arguments
                  nodes.Add call
              not (functions root).IsEmpty)
          for mode in 0 .. 2 do
              $"capability implementations are duplicated, dropped or point nowhere ({mode})",
              (fun root ->
                  let nodes = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> match n["implementations"] with :? JsonArray as i -> i.Count > 0 | _ -> false)
                  for n in nodes do
                      let implementations = n["implementations"] :?> JsonArray
                      match mode with
                      | 0 -> implementations.Add(implementations[0].DeepClone())
                      | 1 -> implementations.RemoveAt 0
                      | _ -> implementations[0]["functionId"] <- s "missing-function"
                  not nodes.IsEmpty)
          // Slice 3d: generic functions added whole, so every original carries the shapes.
          "a task-bound capability crosses into tasks, directly and through a generic call",
          (fun root ->
              root["schemaVersion"] <- JsonValue.Create 24
              (root["capabilities"] :?> JsonArray).Add(obj [ "id", s "x3d-cap"; "displayName", s "X3dCap"; "operations", JsonArray(); "taskBound", JsonValue.Create true ])
              let caps = gen "list" [ obj [ "kind", s "capability"; "capabilityId", s "x3d-cap" ] ]
              let list = root["functions"] :?> JsonArray
              list.Add(synthetic "x3d-worker" [ "x3d-T" ] [ "x3d-item", tparam "x3d-T" ] (gen "result" [ tparam "x3d-T"; prim "string" ]) [ reference "w1" "x3d-item" ] "w1")
              list.Add(
                  synthetic "x3d-crosser" [ "x3d-U" ] [ "x3d-xs", gen "list" [ tparam "x3d-U" ] ] (prim "unit")
                      [ reference "c1" "x3d-xs"
                        obj [ "id", s "c2"; "kind", s "parallelAll"; "sourceListNodeId", s "c1"; "workerFunctionId", s "x3d-worker" ]
                        literalNode "c3" "unit" ] "c3")
              list.Add(
                  synthetic "x3d-caller" [] [ "x3d-cs", caps ] (prim "unit")
                      [ reference "g1" "x3d-cs"
                        call "g2" "x3d-crosser" [ "x3d-xs", "g1" ]
                        obj [ "id", s "g3"; "kind", s "parallelAll"; "sourceListNodeId", s "g1"; "workerFunctionId", s "x3d-worker"
                              "context", JsonArray(obj [ "targetParameterId", s "x3d-missing"; "valueNodeId", s "g1" ]) ]
                        literalNode "g4" "unit"
                        obj [ "id", s "g5"; "kind", s "taskStart"; "scopeNodeId", s "g4"; "workerFunctionId", s "x3d-worker"; "itemNodeId", s "g1"
                              "context", JsonArray(obj [ "targetParameterId", s "x3d-missing"; "valueNodeId", s "g1" ]) ] ] "g4")
              true)
          "TaskScope reaches a OneShot payload, directly, as a callback and through a generic call",
          (fun root ->
              let scopes = gen "list" [ gen "taskScope" [] ]
              let list = root["functions"] :?> JsonArray
              list.Add(synthetic "x3d-payload" [ "x3d-P" ] [ "x3d-x", tparam "x3d-P" ] (gen "oneShot" [ tparam "x3d-P" ]) [ reference "p1" "x3d-x" ] "p1")
              list.Add(synthetic "x3d-forward" [ "x3d-V" ] [ "x3d-v", tparam "x3d-V" ] (prim "unit") [ reference "r1" "x3d-v"; call "r2" "x3d-payload" [ "x3d-x", "r1" ]; literalNode "r3" "unit" ] "r3")
              list.Add(
                  synthetic "x3d-scoped" [] [ "x3d-scopes", scopes ] (prim "unit")
                      [ reference "q1" "x3d-scopes"
                        call "q2" "x3d-payload" [ "x3d-x", "q1" ]
                        obj [ "id", s "q3"; "kind", s "listMap"; "sourceListNodeId", s "q1"; "mapperFunctionId", s "x3d-payload" ]
                        call "q4" "x3d-forward" [ "x3d-v", "q1" ]
                        literalNode "q5" "unit" ] "q5")
              true)
          "every function must not wait, every operation may wait, and a call, a callback and an operation reach a wait",
          (fun root ->
              root["schemaVersion"] <- JsonValue.Create 24
              let tasks () = gen "list" [ gen "task" [ prim "int64" ] ]
              let list = root["functions"] :?> JsonArray
              list.Add(synthetic "x3d-wait-leaf" [] [ "x3d-ts", tasks () ] (prim "unit") [ reference "h1" "x3d-ts"; obj [ "id", s "h2"; "kind", s "taskWait"; "mode", s "all"; "tasksNodeId", s "h1" ] ] "h2")
              list.Add(synthetic "x3d-wait-caller" [] [ "x3d-ts2", tasks () ] (prim "unit") [ reference "k1" "x3d-ts2"; call "k2" "x3d-wait-leaf" [ "x3d-ts", "k1" ] ] "k2")
              list.Add(synthetic "x3d-wait-mapper" [] [ "x3d-ts3", tasks () ] (prim "unit") [ reference "m1" "x3d-ts3"; obj [ "id", s "m2"; "kind", s "listMap"; "sourceListNodeId", s "m1"; "mapperFunctionId", s "x3d-wait-leaf" ] ] "m2")
              let operation = obj [ "id", s "x3d-op"; "displayName", s "Block"; "parameters", JsonArray(); "resultType", prim "unit"; "effectId", s "x3d-effect" ]
              (root["capabilities"] :?> JsonArray).Add(obj [ "id", s "x3d-waitcap"; "displayName", s "Waiter"; "operations", JsonArray(operation) ])
              list.Add(
                  synthetic "x3d-blocker" [] [ "x3d-w", obj [ "kind", s "capability"; "capabilityId", s "x3d-waitcap" ] ] (prim "unit")
                      [ reference "b1" "x3d-w"; obj [ "id", s "b2"; "kind", s "capabilityInvoke"; "capabilityValueNodeId", s "b1"; "operationId", s "x3d-op"; "arguments", JsonArray() ] ] "b2")
              for f in functions root do f["mustNotWait"] <- JsonValue.Create true
              for operations in arrayItems "operations" root do
                  for operation in operations do operation["mayWait"] <- JsonValue.Create true
              true)
          "string literals hold a lone surrogate",
          (fun root ->
              let literals = ZigModelReaderSupport.graphNodes root |> List.filter (fun n -> string n["kind"] = "stringLiteral")
              for n in literals do n["value"] <- s loneSurrogateMarker
              not literals.IsEmpty) ]

    /// The validator test's inputs: the originals, named edits, random combinations of edits and shape mutations.
    let corpus () =
        let kindNames = File.ReadAllLines(ZigModelReaderSupport.toolFile "model_kinds.txt") |> Array.map (fun line -> line.Split('|')[0])
        let root = ZigModelReaderSupport.root ()
        let allKinds = ZigModelReaderSupport.allKindsJson ()
        let fixtures =
            Directory.GetFiles(Path.Combine(root, "tests", "Nyrwvy.Core.Tests", "fixtures", "record-format"), "*.json")
            |> Array.map File.ReadAllText |> List.ofArray
        let repository =
            Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories)
            |> Array.map (File.ReadAllText >> RecordCodec.toJson >> ZigModelReaderSupport.unwrap) |> List.ofArray
        let originals = allKinds @ fixtures @ repository
        let apply (edits: (JsonObject -> bool) list) (json: string) =
            let document = JsonNode.Parse json :?> JsonObject
            let changed = edits |> List.fold (fun changed edit -> edit document || changed) false
            if changed then Some(document.ToJsonString().Replace(loneSurrogateMarker, "\\ud800")) else None
        let targeted = [ for json in originals do for _, edit in semanticEdits do yield! Option.toList (apply [ edit ] json) ]
        // Two or three random edits together, so rules meet each other's damage.
        let combined =
            originals |> List.mapi (fun index json ->
                let random = Random(1000 + index)
                [ for _ in 1 .. 6 do
                      let edits = [ for _ in 1 .. random.Next(2, 4) -> snd semanticEdits[random.Next semanticEdits.Length] ]
                      yield! Option.toList (apply edits json) ])
            |> List.concat
        // The slice-2 shape mutations as a sanity subset: shape failures pass through unchanged.
        let shape = originals |> List.mapi (fun index json -> let random = Random index in [ for _ in 1 .. 2 -> ZigModelReaderSupport.mutate kindNames random json ]) |> List.concat
        originals, targeted, combined, shape

module ZigValidatorTests =
    [<Fact>]
    let ``validation_codes.txt names validator codes`` () =
        // CapabilityImplementations.validate reports for the validator's CapabilityConstruct rule.
        let source = [ "Validation.fs"; "CapabilityImplementations.fs" ] |> List.map (fun file -> File.ReadAllText(Path.Combine(ZigModelReaderSupport.root (), "src", "Nyrwvy.Semantics", file))) |> String.concat "\n"
        for code in ZigValidatorSupport.codes () do
            Assert.True(source.Contains $"\"{code}\"", $"{code} is not a code of Validation.fs or CapabilityImplementations.fs")

    /// `node_references.txt` lists, per node kind, the properties `directValueReferences` reads, in
    /// its operand order (a list property stands for its items, a binding list for their values).
    [<Fact>]
    let ``node_references.txt follows directValueReferences`` () =
        let table =
            File.ReadAllLines(ZigModelReaderSupport.toolFile "node_references.txt")
            |> Array.filter (fun line -> line <> "")
            |> Array.map (fun line -> let parts = line.Split('|') in parts[0], parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
            |> Map.ofArray
        let mutable checkedKinds = Set.empty
        let mutable mismatches = []
        // Every reference gets its own ID and every list two items, so the order is observable.
        for json in ZigModelReaderSupport.allKindsJson () do
            let document = JsonNode.Parse json
            for o in ZigModelReaderSupport.graphNodes document do
                let kind = string o["kind"]
                for name in table[kind] do
                    if not (name.EndsWith "NodeId") && isNull o[name] then o[name] <- JsonArray()
                    match o[name] with
                    | :? JsonArray as items ->
                        let strings = kind = "listConstruct" || kind = "functionInvoke"
                        let target = if name = "fields" then "targetFieldId" elif kind = "capabilityInvoke" then "targetCapabilityParameterId" else "targetParameterId"
                        items.Clear()
                        for i in 1 .. 2 do
                            if strings then items.Add(JsonValue.Create $"ref-{name}-{i}")
                            else
                                let binding = JsonObject()
                                binding[target] <- JsonValue.Create $"target-{name}-{i}"
                                binding["valueNodeId"] <- JsonValue.Create $"ref-{name}-{i}"
                                items.Add binding
                    | _ -> o[name] <- JsonValue.Create $"ref-{name}"
            let snapshot = SnapshotCodec.readUnvalidated (document.ToJsonString()) |> Result.mapError string |> ZigModelReaderSupport.unwrap
            let jsonNodes = ZigModelReaderSupport.graphNodes document |> List.map (fun n -> string n["id"], n) |> Map.ofList
            for f in snapshot.Functions do
                for node in ValueGraph.collectNodes f.Body do
                    let o = jsonNodes[StableId.value node.Id]
                    let kind = string o["kind"]
                    let fromTable =
                        table[kind]
                        |> List.collect (fun name ->
                            match o[name] with
                            | null -> []
                            | :? JsonArray as items -> [ for item in items -> match item with :? JsonObject as b -> string b["valueNodeId"] | v -> string v ]
                            | v -> [ string v ])
                    let actual = SemanticNodeShape.directValueReferences node |> List.map StableId.value
                    if fromTable <> actual then mismatches <- $"{kind}: {String.Join(' ', fromTable)} <> {String.Join(' ', actual)}" :: mismatches
                    checkedKinds <- checkedKinds.Add kind
        Assert.True(mismatches.IsEmpty, String.Join("\n", List.distinct mismatches))
        Assert.Equal<string list>(table.Keys |> List.ofSeq, checkedKinds |> List.ofSeq)

/// The extended comparison (a class, so that it can report its counts).
type ZigValidatorComparison(output: ITestOutputHelper) =

    [<Fact; Trait("Suite", "Extended")>]
    member _.``The Zig validator matches the F# validator on the ported codes`` () =
        let codes = ZigValidatorSupport.codes ()
        let originals, targeted, combined, shape = ZigValidatorSupport.corpus ()
        let texts = originals @ targeted @ combined @ shape |> List.distinct
        let reader = ZigTools.tool "model-reader" |> ZigModelReaderSupport.unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-validator-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let info = ProcessStartInfo(reader, RedirectStandardOutput = true)
            info.ArgumentList.Add "--validate"
            texts |> List.iteri (fun index text ->
                File.WriteAllText(Path.Combine(dir, $"{index}.in"), text, Text.UTF8Encoding false)
                info.ArgumentList.Add(Path.Combine(dir, $"{index}.in")))
            use child = Process.Start info
            let lines = child.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            child.WaitForExit()
            Assert.Equal(0, child.ExitCode)
            // One result per input: a single reader line, or issue lines closed by `end`.
            let results =
                let results = ResizeArray<string list>()
                let mutable pending = []
                for line in lines do
                    if line.StartsWith "issue " then pending <- line :: pending
                    elif line = "end" then results.Add(List.rev (line :: pending)); pending <- []
                    else
                        Assert.True(pending.IsEmpty, $"a reader line inside an issue list: {line}")
                        results.Add [ line ]
                results
            Assert.Equal(texts.Length, results.Count)
            let caseOnly = [ "error InvalidJson"; "error InvalidShape: Invalid JSON string encoding:" ]
            let mutable clean, failing, rejected, skipped, throws = 0, 0, 0, 0, 0
            let produced = Collections.Generic.Dictionary<string, int>()
            texts |> List.iteri (fun index text ->
                let actual = results[index]
                if actual.Head.StartsWith "skip legacy schema " then skipped <- skipped + 1
                else
                    match SnapshotCodec.readUnvalidated text with
                    | Error error ->
                        rejected <- rejected + 1
                        let line =
                            ZigValidatorSupport.escape <|
                            match error with
                            | SnapshotCodecError.InvalidJson message -> $"error InvalidJson: {message}"
                            | SnapshotCodecError.InvalidShape message -> $"error InvalidShape: {message}"
                            | SnapshotCodecError.UnsupportedSchemaVersion version -> $"error UnsupportedSchemaVersion: {version}"
                            | SnapshotCodecError.MaximumDepthExceeded depth -> $"error MaximumDepthExceeded: {depth}"
                            | SnapshotCodecError.InvalidSnapshot issues -> failwithf "readUnvalidated validated: %A" issues
                        match caseOnly |> List.tryFind line.StartsWith with
                        | Some prefix -> Assert.True(actual.Head.StartsWith prefix, $"case {index}: F# says {line}, Zig says {actual.Head}\n{text}")
                        | None -> Assert.True([ line ] = actual, $"case {index}: F# says {line}, Zig says %A{actual}\n{text}")
                    | Ok snapshot ->
                        let issues =
                            // Excluded analyses (type inference, effect order, …) run too and may throw on a mutated model.
                            try Some(SnapshotValidator.validate snapshot)
                            with error ->
                                output.WriteLine($"case {index}: the F# validator threw {error.GetType().Name}: {error.Message}")
                                None
                        match issues with
                        | None -> throws <- throws + 1
                        | Some issues ->
                            let ported = issues |> List.filter (fun issue -> codes.Contains issue.Code)
                            if ported.IsEmpty then clean <- clean + 1 else failing <- failing + 1
                            for issue in ported do produced[issue.Code] <- (match produced.TryGetValue issue.Code with | true, n -> n + 1 | _ -> 1)
                            let expected = [ for issue in ported -> ZigValidatorSupport.escape $"issue {issue.Code}: {issue.Message}" ] @ [ "end" ]
                            let show (lines: string list) = String.concat "\n" lines
                            Assert.True((expected = actual), $"case {index}: F# says\n{show expected}\nZig says\n{show actual}\n{text}"))
            output.WriteLine($"{texts.Length} texts ({originals.Length} originals, {targeted.Length} targeted, {combined.Length} combined, {shape.Length} shape): {clean} clean, {failing} with ported issues, {rejected} shape rejections, {skipped} legacy skipped, {throws} F# validator failures")
            for KeyValue(code, count) in produced |> Seq.sortBy (fun pair -> pair.Key) do output.WriteLine($"{count,6} {code}")
            let missing = codes - set produced.Keys
            Assert.True(missing.IsEmpty, "codes the corpus never produces: " + String.Join(", ", missing))
            Assert.True(clean > 20 && failing > 100 && rejected > 20, $"clean {clean}, failing {failing}, rejected {rejected}")
            Assert.True(throws * 20 < texts.Length, $"the F# validator threw on {throws} of {texts.Length} texts")
        finally Directory.Delete(dir, true)
