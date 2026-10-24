namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Text.Json
open Nyrwvy.Core

/// Bounded wire views of immutable pending reviews. This changes neither proposals nor commits.
module internal WireProposalProjection =
    open WireJson

    let compactMaxBytes = 16384
    let pageMaxBytes = 8192
    let private bytes = byteCount

    // Review encoders produce dictionaries, object arrays and scalar wire values. Project these
    // directly: serializing/parsing the whole review first defeats a page's memory budget.
    let private valueKind (value: obj) =
        match value with
        | :? Collections.Generic.IDictionary<string, obj> -> JsonValueKind.Object
        | :? (obj array) -> JsonValueKind.Array
        | :? string | :? LooseText -> JsonValueKind.String
        | :? JsonElement as item -> item.ValueKind
        | _ -> JsonValueKind.Undefined // Other scalars pass through unchanged.
    let private properties (value: obj) =
        match value with
        | :? Collections.Generic.IDictionary<string, obj> as fields -> fields |> Seq.map (fun p -> p.Key, p.Value)
        | :? JsonElement as item -> item.EnumerateObject() |> Seq.map (fun p -> p.Name, box (p.Value.Clone()))
        | _ -> invalidOp "Expected a review object."
    let private items (value: obj) =
        match value with
        | :? (obj array) as values -> values
        | :? JsonElement as item -> item.EnumerateArray() |> Seq.map (fun v -> box (v.Clone())) |> Seq.toArray
        | _ -> invalidOp "Expected a review array."
    let private text (value: obj) =
        match value with
        | :? string as value -> value
        | :? LooseText as value -> value.Text
        | :? JsonElement as value -> WireInput.runText value
        | _ -> invalidOp "Expected review text."
    let private escape (key: string) = key.Replace("~", "~0").Replace("/", "~1")
    let private identityPath (path: string) =
        let parts = path.Split('/')
        let identity (name: string) = name = "id" || name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Ids", StringComparison.Ordinal) || name.EndsWith("Fingerprint", StringComparison.Ordinal)
        identity parts[parts.Length - 1] || (parts.Length > 1 && identity parts[parts.Length - 2])

    let private project objectDepth arrayLimit textLimit preserveReceipt rootPath (rootArrayOffset: int option) (root: obj) =
        let omissions = ResizeArray<obj>()
        let omit path kind total shown =
            omissions.Add(objectValue [ "path", box path; "kind", box kind; "totalCount", box total; "shownCount", box shown ])
        let rec visit depth path (value: obj): obj =
            if path = "/proposalId" || path = "/detail"
               || (path.EndsWith("/kind", StringComparison.Ordinal) && valueKind value = JsonValueKind.String && (text value).Length <= 64) then value
            elif valueKind value = JsonValueKind.String && path <> rootPath && identityPath path then
                let text = text value
                if text.Length <= 256 then value
                else
                    // An identity prefix is never presented as an exact identity.
                    omit path "stringUtf16" text.Length 0
                    null
            elif path = "/review/receipt" && rootPath = "" then
                if preserveReceipt then value
                else
                    omit path "object" ((properties value) |> Seq.length) 0
                    null
            elif depth >= objectDepth && valueKind value = JsonValueKind.Object then
                omit path "object" ((properties value) |> Seq.length) 0
                null
            else
                match valueKind value with
                | JsonValueKind.Array ->
                    let total = (items value).Length
                    let shown = if path = rootPath && rootArrayOffset.IsSome then total else min total arrayLimit
                    if shown < total then omit path "array" total shown
                    (items value :> seq<obj>) |> Seq.truncate shown |> Seq.mapi (fun i item ->
                        let index = if path = rootPath then i + Option.defaultValue 0 rootArrayOffset else i
                        visit (depth + 1) (path + "/" + string index) item) |> Seq.toList |> arrayValue
                | JsonValueKind.Object ->
                    (properties value) |> Seq.map (fun (name, item) ->
                        name, visit (depth + 1) (path + "/" + escape name) item) |> Seq.toList |> objectValue
                | JsonValueKind.String ->
                    let text = text value
                    let mutable shown = min text.Length textLimit
                    if shown > 0 && shown < text.Length && Char.IsHighSurrogate text[shown - 1] && Char.IsLowSurrogate text[shown] then shown <- shown - 1
                    if shown < text.Length then omit path "stringUtf16" text.Length shown
                    runString (text.Substring(0, shown))
                | _ -> value
        visit 0 rootPath root, List.ofSeq omissions

    let private metadata maxBytes originalBytes omissions =
        let shown = omissions |> List.truncate 32
        objectValue [ "complete", box omissions.IsEmpty; "requiresDetailedReview", box (not omissions.IsEmpty)
                      "maxBytes", box maxBytes; "originalBytes", box originalBytes
                      "omissionCount", box omissions.Length; "omittedOmissionCount", box (omissions.Length - shown.Length)
                      "omissions", arrayValue shown; "readOperation", box "readProposal" ]

    let private withProjection (value: obj) projection =
        let fields = value :?> Collections.Generic.IDictionary<string, obj>
        fields["projection"] <- projection
        value

    let compact value =
        let originalBytes = bytes value
        let raw = value
        let uncut = properties raw |> Seq.toList |> objectValue
                    |> fun v -> withProjection v (metadata compactMaxBytes originalBytes [])
        if bytes uncut <= compactMaxBytes then uncut
        else
            // The last levels cut whole objects, so a review with very wide objects still returns
            // its proposalId instead of an error that leaves the stored proposal unexplained.
            [ 8, true, 8, 256; 8, true, 4, 128; 8, true, 2, 64; 8, true, 1, 32; 8, true, 0, 0; 8, false, 0, 0; 2, false, 0, 0; 1, false, 0, 0 ]
            |> List.tryPick (fun (depth, receipt, arrays, text) ->
                let projected, omissions = project depth arrays text receipt "" None raw
                let result = withProjection projected (metadata compactMaxBytes originalBytes omissions)
                if bytes result <= compactMaxBytes then Some result else None)
            |> Option.defaultWith (fun () -> invalidOp "A compact proposal envelope exceeded its fixed wire budget.")

    let private select (root: obj) (path: string) =
        if path = "" then root
        elif not (path.StartsWith("/", StringComparison.Ordinal)) then WireInput.fail "path must be an RFC 6901 JSON pointer, or empty for the review root."
        else
            path.Substring(1).Split('/') |> Array.fold (fun (value: obj) part ->
                let mutable i = 0
                while i < part.Length do
                    if part[i] = '~' then
                        if i + 1 >= part.Length || (part[i + 1] <> '0' && part[i + 1] <> '1') then WireInput.fail "Invalid JSON pointer escape."
                        i <- i + 2
                    else i <- i + 1
                let key = part.Replace("~1", "/").Replace("~0", "~")
                match valueKind value with
                | JsonValueKind.Object ->
                    match properties value |> Seq.tryFind (fst >> (=) key) with
                    | Some(_, found) -> found
                    | None -> WireInput.fail $"No proposal review value at path '{path}'."
                | JsonValueKind.Array ->
                    match Int32.TryParse key with
                    | true, index when index >= 0 && index < (items value).Length && string index = key -> (items value)[index]
                    | _ -> WireInput.fail $"No proposal review item at path '{path}'."
                | _ -> WireInput.fail $"Cannot traverse a scalar at path '{path}'.") root

    /// Pagination is bound to the pending proposal, not a subsequent committed graph. Inner
    /// omission paths refer to the original full review, including original array indices.
    let page (pending: AuthoringPendingProposalView) structuredReview fullValue path offset limit =
        let source = select fullValue path
        let total, kind =
            match valueKind source with
            | JsonValueKind.Array -> (items source).Length, "array"
            | JsonValueKind.Object -> (properties source) |> Seq.length, "object"
            | JsonValueKind.String -> (text source).Length, "stringUtf16"
            | _ -> 1, "scalar"
        if offset < 0 || offset > total then WireInput.fail "offset must be within the selected value's totalCount."
        if limit < 1 || limit > (if kind = "stringUtf16" then 1024 else 20) then WireInput.fail "limit must be 1..20 items/properties, or 1..1024 UTF-16 code units for a String."
        let selected count =
            match valueKind source with
            | JsonValueKind.Array -> (items source :> seq<obj>) |> Seq.skip offset |> Seq.truncate count |> Seq.toList |> arrayValue
            | JsonValueKind.Object -> (properties source) |> Seq.skip offset |> Seq.truncate count |> Seq.toList |> objectValue
            | JsonValueKind.String -> runString ((text source).Substring(offset, count))
            | _ -> if count = 0 then null else source
        let header count value projection =
            objectValue [ "proposalId", box pending.Proposal.ProposalId; "baseIsCurrent", box pending.BaseIsCommitted; "structuredReview", box structuredReview
                          "path", box path; "kind", box kind; "offset", box offset; "totalCount", box total; "returnedCount", box count
                          "nextOffset", (if offset + count < total then box (offset + count) else null)
                          "value", value; "projection", projection ]
        let mutable count = min limit (total - offset)
        if kind = "stringUtf16" then
            let text = text source
            if offset > 0 && offset < total && Char.IsLowSurrogate text[offset] && Char.IsHighSurrogate text[offset - 1] then WireInput.fail "offset splits a surrogate pair; use the returned nextOffset."
            if count > 0 && offset + count < total && Char.IsHighSurrogate text[offset + count - 1] && Char.IsLowSurrogate text[offset + count] then
                if count = 1 then WireInput.fail "limit would split a surrogate pair; use at least 2 UTF-16 code units."
                count <- count - 1
        let mutable answer = None
        while answer.IsNone do
            let value = selected count
            let originalBytes = bytes value
            let direct = header count value (metadata pageMaxBytes originalBytes [])
            if bytes direct <= pageMaxBytes then answer <- Some direct
            else
                answer <-
                    [ 4, 128; 2, 64; 1, 32; 0, 0 ] |> List.tryPick (fun (arrays, text) ->
                        let value, omissions = project 8 arrays text false path (if kind = "array" then Some offset else None) value
                        let response = header count value (metadata pageMaxBytes originalBytes omissions)
                        if bytes response <= pageMaxBytes then Some response else None)
                if answer.IsNone then
                    if count <= 1 then invalidOp "A proposal page envelope exceeded its fixed wire budget."
                    count <- max 1 (count / 2)
        answer.Value
