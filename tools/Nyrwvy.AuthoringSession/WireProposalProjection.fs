namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Text
open System.Text.Json
open Nyrwvy.Core

/// Bounded wire views of immutable pending reviews. This changes neither proposals nor commits.
module internal WireProposalProjection =
    open WireJson

    let compactMaxBytes = 16384
    let pageMaxBytes = 8192
    let private bytes value = Encoding.UTF8.GetByteCount(serialize value)
    let private options = JsonDocumentOptions(MaxDepth = responseMaxJsonDepth)
    let private escape (key: string) = key.Replace("~", "~0").Replace("/", "~1")
    let private identityPath (path: string) =
        let parts = path.Split('/')
        let identity (name: string) = name = "id" || name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Ids", StringComparison.Ordinal) || name.EndsWith("Fingerprint", StringComparison.Ordinal)
        identity parts[parts.Length - 1] || (parts.Length > 1 && identity parts[parts.Length - 2])

    let private project arrayLimit textLimit preserveReceipt rootPath (rootArrayOffset: int option) (root: JsonElement) =
        let omissions = ResizeArray<obj>()
        let omit path kind total shown =
            omissions.Add(objectValue [ "path", box path; "kind", box kind; "totalCount", box total; "shownCount", box shown ])
        let rec visit depth path (value: JsonElement): obj =
            if path = "/proposalId" || path = "/detail"
               || (path.EndsWith("/kind", StringComparison.Ordinal) && value.ValueKind = JsonValueKind.String && value.GetString().Length <= 64) then box (value.Clone())
            elif value.ValueKind = JsonValueKind.String && path <> rootPath && identityPath path then
                let text = value.GetString()
                if text.Length <= 256 then box (value.Clone())
                else
                    // An identity prefix is never presented as an exact identity.
                    omit path "stringUtf16" text.Length 0
                    null
            elif path = "/review/receipt" && rootPath = "" then
                if preserveReceipt then box (value.Clone())
                else
                    omit path "object" (value.EnumerateObject() |> Seq.length) 0
                    null
            elif depth >= 8 && value.ValueKind = JsonValueKind.Object then
                omit path "object" (value.EnumerateObject() |> Seq.length) 0
                null
            else
                match value.ValueKind with
                | JsonValueKind.Array ->
                    let total = value.GetArrayLength()
                    let shown = if path = rootPath && rootArrayOffset.IsSome then total else min total arrayLimit
                    if shown < total then omit path "array" total shown
                    value.EnumerateArray() |> Seq.truncate shown |> Seq.mapi (fun i item ->
                        let index = if path = rootPath then i + Option.defaultValue 0 rootArrayOffset else i
                        visit (depth + 1) (path + "/" + string index) item) |> Seq.toList |> arrayValue
                | JsonValueKind.Object ->
                    value.EnumerateObject() |> Seq.map (fun item ->
                        item.Name, visit (depth + 1) (path + "/" + escape item.Name) item.Value) |> Seq.toList |> objectValue
                | JsonValueKind.String ->
                    let text = value.GetString()
                    let mutable shown = min text.Length textLimit
                    if shown > 0 && shown < text.Length && Char.IsHighSurrogate text[shown - 1] then shown <- shown - 1
                    if shown < text.Length then omit path "stringUtf16" text.Length shown
                    box (text.Substring(0, shown))
                | _ -> box (value.Clone())
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
        let original = serialize value
        use document = JsonDocument.Parse(original, options)
        let raw = document.RootElement
        let uncut =
            raw.EnumerateObject() |> Seq.map (fun p -> p.Name, box (p.Value.Clone())) |> Seq.toList |> objectValue
            |> fun v -> withProjection v (metadata compactMaxBytes (Encoding.UTF8.GetByteCount original) [])
        if bytes uncut <= compactMaxBytes then uncut
        else
            [ true, 8, 256; true, 4, 128; true, 2, 64; true, 1, 32; true, 0, 0; false, 0, 0 ]
            |> List.tryPick (fun (receipt, arrays, text) ->
                let projected, omissions = project arrays text receipt "" None raw
                let result = withProjection projected (metadata compactMaxBytes (Encoding.UTF8.GetByteCount original) omissions)
                if bytes result <= compactMaxBytes then Some result else None)
            |> Option.defaultWith (fun () -> invalidOp "A compact proposal envelope exceeded its fixed wire budget.")

    let private select (root: JsonElement) (path: string) =
        if path = "" then root
        elif not (path.StartsWith("/", StringComparison.Ordinal)) then WireInput.fail "path must be an RFC 6901 JSON pointer, or empty for the review root."
        else
            path.Substring(1).Split('/') |> Array.fold (fun (value: JsonElement) part ->
                let mutable i = 0
                while i < part.Length do
                    if part[i] = '~' then
                        if i + 1 >= part.Length || (part[i + 1] <> '0' && part[i + 1] <> '1') then WireInput.fail "Invalid JSON pointer escape."
                        i <- i + 2
                    else i <- i + 1
                let key = part.Replace("~1", "/").Replace("~0", "~")
                match value.ValueKind with
                | JsonValueKind.Object ->
                    match value.TryGetProperty key with
                    | true, found -> found
                    | _ -> WireInput.fail $"No proposal review value at path '{path}'."
                | JsonValueKind.Array ->
                    match Int32.TryParse key with
                    | true, index when index >= 0 && index < value.GetArrayLength() && string index = key -> value[index]
                    | _ -> WireInput.fail $"No proposal review item at path '{path}'."
                | _ -> WireInput.fail $"Cannot traverse a scalar at path '{path}'.") root

    /// Pagination is bound to the pending proposal, not a subsequent committed graph. Inner
    /// omission paths refer to the original full review, including original array indices.
    let page (pending: AuthoringPendingProposalView) structuredReview fullValue path offset limit =
        use document = JsonDocument.Parse(serialize fullValue, options)
        let source = select document.RootElement path
        let total, kind =
            match source.ValueKind with
            | JsonValueKind.Array -> source.GetArrayLength(), "array"
            | JsonValueKind.Object -> source.EnumerateObject() |> Seq.length, "object"
            | JsonValueKind.String -> source.GetString().Length, "stringUtf16"
            | _ -> 1, "scalar"
        if offset < 0 || offset > total then WireInput.fail "offset must be within the selected value's totalCount."
        if limit < 1 || limit > (if kind = "stringUtf16" then 1024 else 20) then WireInput.fail "limit must be 1..20 items/properties, or 1..1024 UTF-16 code units for a String."
        let selected count =
            match source.ValueKind with
            | JsonValueKind.Array -> source.EnumerateArray() |> Seq.skip offset |> Seq.truncate count |> Seq.map (fun v -> box (v.Clone())) |> Seq.toList |> arrayValue
            | JsonValueKind.Object -> source.EnumerateObject() |> Seq.skip offset |> Seq.truncate count |> Seq.map (fun p -> p.Name, box (p.Value.Clone())) |> Seq.toList |> objectValue
            | JsonValueKind.String -> box (source.GetString().Substring(offset, count))
            | _ -> if count = 0 then null else box (source.Clone())
        let header count value projection =
            objectValue [ "proposalId", box pending.Proposal.ProposalId; "baseIsCurrent", box pending.BaseIsCommitted; "structuredReview", box structuredReview
                          "path", box path; "kind", box kind; "offset", box offset; "totalCount", box total; "returnedCount", box count
                          "nextOffset", (if offset + count < total then box (offset + count) else null)
                          "value", value; "projection", projection ]
        let mutable count = min limit (total - offset)
        if kind = "stringUtf16" then
            let text = source.GetString()
            if offset > 0 && offset < total && Char.IsLowSurrogate text[offset] && Char.IsHighSurrogate text[offset - 1] then WireInput.fail "offset splits a surrogate pair; use the returned nextOffset."
            if count > 0 && offset + count < total && Char.IsHighSurrogate text[offset + count - 1] then
                if count = 1 then WireInput.fail "limit would split a surrogate pair; use at least 2 UTF-16 code units."
                count <- count - 1
        let mutable answer = None
        while answer.IsNone do
            let value = selected count
            let originalBytes = bytes value
            let direct = header count value (metadata pageMaxBytes originalBytes [])
            if bytes direct <= pageMaxBytes then answer <- Some direct
            else
                use selectedDocument = JsonDocument.Parse(serialize value, options)
                answer <-
                    [ 4, 128; 2, 64; 1, 32; 0, 0 ] |> List.tryPick (fun (arrays, text) ->
                        let value, omissions = project arrays text false path (if kind = "array" then Some offset else None) selectedDocument.RootElement
                        let response = header count value (metadata pageMaxBytes originalBytes omissions)
                        if bytes response <= pageMaxBytes then Some response else None)
                if answer.IsNone then
                    if count <= 1 then invalidOp "A proposal page envelope exceeded its fixed wire budget."
                    count <- max 1 (count / 2)
        answer.Value
