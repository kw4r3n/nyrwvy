namespace Nyrwvy.Tools.AuthoringSession

open System.Text.Json
open System.Text.Json.Nodes

/// Named pattern references (docs/decisions/named-pattern-references.md): before a recipe is decoded,
/// `{ "kind": "patternReference", "name" }` becomes the `scopeDepth` and `slot` of the innermost binder in
/// the same recipe that declares the name (`bind.name`, `matchOption.someName`, `matchResult.okName` and
/// `errName`), and the binder names are dropped. Frames are counted as inference pushes them: Bind bodies,
/// both MatchOption and MatchResult bodies, and every MatchUnion arm including `otherwise`.
module internal WireRecipeNames =
    open WireInput

    /// One frame: the slot ("value" or "error") a name refers to, or no name.
    type private Frame = (string * string) option

    /// The property's value; None when it is absent or JSON null.
    let private tryGet (node: JsonObject) (key: string) : JsonNode option =
        let mutable value: JsonNode = null
        if node.TryGetPropertyValue(key, &value) then Option.ofObj value else None

    let private takeName (node: JsonObject) (key: string) =
        if not (node.ContainsKey key) then None
        else
            let value = tryGet node key
            node.Remove key |> ignore
            match value with
            | Some(:? JsonValue as text) when text.GetValueKind() = JsonValueKind.String -> Some(text.GetValue<string>())
            | _ -> fail $"Property '{key}' must be a binding name string."

    let rec private visit (frames: Frame list) (node: JsonNode) =
        match node with
        | :? JsonArray as items -> for item in items do visit frames item
        | :? JsonObject as item ->
            let kind =
                match tryGet item "kind" with
                | Some(:? JsonValue as value) when value.GetValueKind() = JsonValueKind.String -> value.GetValue<string>()
                | _ -> ""
            let child key (frame: Frame) = tryGet item key |> Option.iter (visit (frame :: frames))
            let rest keys =
                for KeyValue(key, value) in Seq.toList item do
                    if not (List.contains key keys) && not (isNull value) then visit frames value
            match kind with
            | "patternReference" ->
                match takeName item "name" with
                | None -> ()
                | Some name ->
                    if item.ContainsKey "scopeDepth" || item.ContainsKey "slot" then
                        fail $"Pattern reference '{name}' gives both a name and scopeDepth or slot; use one form."
                    match frames |> List.tryFindIndex (fun frame -> frame |> Option.exists (fun (bound, _) -> bound = name)) with
                    | Some depth ->
                        item["scopeDepth"] <- JsonValue.Create depth
                        item["slot"] <- JsonValue.Create(snd (frames[depth] |> Option.get))
                    | None ->
                        let inScope = frames |> List.choose (Option.map fst) |> List.distinct
                        let listed = if List.isEmpty inScope then "none" else inScope |> String.concat ", "
                        fail $"No binder in this recipe names '{name}' (names in scope: {listed}). A binding outside the recipe still needs scopeDepth; nyrwvy_inspect view scopes (op bindingScopes) lists the bindings visible at a node with their scopeDepth."
            | "bind" ->
                let name = takeName item "name"
                rest [ "body" ]
                child "body" (name |> Option.map (fun name -> name, "value"))
            | "matchOption" ->
                let name = takeName item "someName"
                rest [ "someBody"; "noneBody" ]
                child "someBody" (name |> Option.map (fun name -> name, "value"))
                child "noneBody" None
            | "matchResult" ->
                let okName, errName = takeName item "okName", takeName item "errName"
                rest [ "okBody"; "errBody" ]
                child "okBody" (okName |> Option.map (fun name -> name, "value"))
                child "errBody" (errName |> Option.map (fun name -> name, "error"))
            | "matchUnion" ->
                rest [ "arms"; "otherwise" ]
                match tryGet item "arms" with
                | Some(:? JsonArray as arms) ->
                    for arm in arms do
                        match arm with
                        | :? JsonObject as arm ->
                            for KeyValue(key, value) in Seq.toList arm do
                                if not (isNull value) then visit (if key = "body" then None :: frames else frames) value
                        | other -> visit frames other
                | Some other -> visit frames other
                | None -> ()
                child "otherwise" None
            | _ -> rest []
        | _ -> ()

    /// The recipe with every named pattern reference rewritten; unchanged when it uses no names.
    let resolve (element: JsonElement): JsonElement =
        let raw = element.GetRawText()
        if not (raw.Contains "Name\"" || raw.Contains "\"name\"") then element
        else
            // The request line was parsed with requestMaxJsonDepth, so the copies here must allow it too.
            let root = JsonNode.Parse(raw, System.Nullable(), JsonDocumentOptions(MaxDepth = requestMaxJsonDepth))
            visit [] root
            use document =
                JsonDocument.Parse(
                    root.ToJsonString(JsonSerializerOptions(MaxDepth = requestMaxJsonDepth)),
                    JsonDocumentOptions(MaxDepth = requestMaxJsonDepth))
            document.RootElement.Clone()
