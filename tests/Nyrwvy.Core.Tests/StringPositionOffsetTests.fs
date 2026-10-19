namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Code-point positions over long texts: StringLength, StringSlice, StringIndexOf, StringLastIndexOf,
/// StringSpan and StringSpanBack map positions to UTF-16 offsets through the runtime position map
/// (a no-surrogate flag, or a checkpoint every 64 code points). Positions around the checkpoints,
/// far into the text and past its end must agree with a plain code-point model, with surrogate
/// pairs counted once and lone surrogates counted as one code point each.
module StringPositionOffsetTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("string-position-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private boolType = TypeRef.Primitive PrimitiveType.Bool
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error
    let private text value = ExpressionRecipe.StringLiteral value
    let private lit value = ExpressionRecipe.Int64Literal value

    // The reference model: one element per code point, a valid pair together, a lone surrogate alone.
    let private codePoints (value: string) =
        let result = ResizeArray<string>()
        let mutable index = 0
        while index < value.Length do
            let width =
                if Char.IsHighSurrogate value.[index] && index + 1 < value.Length && Char.IsLowSurrogate value.[index + 1] then 2 else 1
            result.Add(value.Substring(index, width))
            index <- index + width
        result.ToArray()
    let private clamp (cps: string[]) (position: int64) = max 0L (min (int64 cps.Length) position) |> int
    let private slice (cps: string[]) start stop =
        let a, b = clamp cps start, clamp cps stop
        if b <= a then "" else String.Join("", cps.[a .. b - 1])
    let private matchesAt (cps: string[]) (needle: string[]) index =
        index + needle.Length <= cps.Length && Array.forall2 (=) needle cps.[index .. index + needle.Length - 1]
    let private indexOf cps (needle: string) from =
        let needle = codePoints needle
        seq { clamp cps from .. cps.Length - needle.Length } |> Seq.tryFind (matchesAt cps needle) |> Option.map int64
    let private lastIndexOf (cps: string[]) (needle: string) =
        let needle = codePoints needle
        seq { cps.Length - needle.Length .. -1 .. 0 } |> Seq.tryFind (matchesAt cps needle) |> Option.map int64
    let private span (cps: string[]) (set: string) from =
        let members = set |> codePoints |> Set.ofArray
        let mutable index = clamp cps from
        while index < cps.Length && members.Contains cps.[index] do index <- index + 1
        int64 index
    let private spanBack (cps: string[]) (set: string) stop =
        let members = set |> codePoints |> Set.ofArray
        let mutable index = clamp cps stop
        while index > 0 && members.Contains cps.[index - 1] do index <- index - 1
        int64 index

    [<Fact; Trait("Suite", "Extended")>]
    let ``code-point positions far into long texts match the code-point model around every checkpoint`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-string-position-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize (Snapshot.empty (id<SnapshotTag> "session")) |> unwrap "serialize")
            let session = AuthoringSession.openExisting path |> unwrap "open"
            let create name parameters resultType body =
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = (session.Status() |> unwrap "status").SnapshotId
                      TypeParameters = None
                      DisplayName = name
                      Parameters = parameters |> List.map (fun (displayName, parameterType) -> { DisplayName = displayName; Type = parameterType })
                      ResultType = resultType
                      RequiredEffects = []
                      Body = body }
                let proposal = session.SimulateCreateFunction request |> unwrap ("create " + name)
                session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore
            let functionId name = (session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)).Id
            let check name (expected: PureRunValue) =
                let result = PureRun.run session (functionId name) [] (Some expected) |> unwrap name
                Assert.True((result.Matches = Some true), sprintf "%s: %A" name result.Actual)
            // A structural recipe holds at most 256 nodes (six per slice check, four per search), so each
            // table is authored in chunks.
            let table name itemType (cases: (ExpressionRecipe * PureRunValue) list) =
                cases
                |> List.chunkBySize (if itemType = boolType then 40 else 60)
                |> List.iteri (fun index chunk ->
                    let chunkName = sprintf "%s%d" name index
                    create chunkName [] (listOf itemType) (ExpressionRecipe.ListConstruct(itemType, List.map fst chunk))
                    let expected = List.map snd chunk
                    let primitive pick = expected |> List.map (fun value -> pick value |> Option.defaultWith (fun () -> failwithf "%A" value))
                    check chunkName
                        (if itemType = intType then PureRunValue.Int64List(primitive (function PureRunValue.Int64 value -> Some value | _ -> None))
                         elif itemType = boolType then PureRunValue.BoolList(primitive (function PureRunValue.Bool value -> Some value | _ -> None))
                         else PureRunValue.ListValue(itemType, expected)))
            // Pairs, lone high and low surrogates (a lone high one right before the next chunk's "a"),
            // and a lone high surrogate at the very end. The long texts pass the 256-unit map threshold.
            let chunk = utf16 "ab😀c{D800}d{DC00}😀😀e{DBFF}"
            let mixed = String.replicate 120 chunk + utf16 "{D83D}"
            let shortMixed = String.replicate 5 chunk
            let ascii = String.init 2000 (fun index -> string (char (int 'a' + index % 26)))
            let texts = [ "mixed", mixed; "shortMixed", shortMixed; "ascii", ascii ]
            table "lengths" intType (texts |> List.map (fun (_, value) -> ExpressionRecipe.StringLength(text value), PureRunValue.Int64(int64 (codePoints value).Length)))
            for label, value in texts do
                let cps = codePoints value
                let n = int64 cps.Length
                let positions = [ -2L; 0L; 1L; 2L; 63L; 64L; 65L; 127L; 128L; 129L; n / 2L; n - 1L; n; n + 3L ] |> List.distinct
                table (label + "Slices") boolType
                    [ for start in positions do
                        for stop in [ start; start + 1L; start + 63L; start + 64L; start + 65L; n - 1L; n; n + 3L; 1L ] do
                            ExpressionRecipe.StringEqual(ExpressionRecipe.StringSlice(text value, lit start, lit stop), text (slice cps start stop)),
                            PureRunValue.Bool true ]
                let needles = [ "😀"; utf16 "{D800}d"; "e"; "😀😀"; utf16 "{DC00}"; "c"; utf16 "{DE00}"; "zz" ]
                table (label + "IndexOf") (optionOf intType)
                    [ for needle in needles do
                        for from in positions do
                            ExpressionRecipe.StringIndexOf(text value, text needle, lit from), PureRunValue.Int64Option(indexOf cps needle from) ]
                table (label + "LastIndexOf") (optionOf intType)
                    [ for needle in needles -> ExpressionRecipe.StringLastIndexOf(text value, text needle), PureRunValue.Int64Option(lastIndexOf cps needle) ]
                let sets = [ "ab😀c"; utf16 "😀e{DBFF}{D800}d{DC00}"; "abcdefghijklmnopqrstuvwxyz" ]
                table (label + "Spans") intType
                    [ for set in sets do
                        for position in positions do
                            ExpressionRecipe.StringSpan(text value, text set, lit position), PureRunValue.Int64(span cps set position)
                            ExpressionRecipe.StringSpanBack(text value, text set, lit position), PureRunValue.Int64(spanBack cps set position) ]
            // A 50,000-unit argument with pairs: positions deep in the text, as a stepping scan reaches them.
            let slot = ExpressionRecipe.ParameterSlot
            create "slice" [ "text", stringType; "start", intType; "end", intType ] stringType (ExpressionRecipe.StringSlice(slot 0, slot 1, slot 2))
            create "length" [ "text", stringType ] intType (ExpressionRecipe.StringLength(slot 0))
            create "indexFrom" [ "text", stringType; "sub", stringType; "from", intType ] (optionOf intType) (ExpressionRecipe.StringIndexOf(slot 0, slot 1, slot 2))
            let run name arguments = (PureRun.run session (functionId name) arguments None |> unwrap name).Actual
            let long = String.replicate 10000 "x😀yz"
            let cps = codePoints long
            Assert.Equal(PureRunValue.Int64 40000L, run "length" [ PureRunValue.String long ])
            for start in [ 39990L; 20001L; 64L * 311L; 64L * 311L - 1L ] do
                Assert.Equal(PureRunValue.String(slice cps start (start + 7L)), run "slice" [ PureRunValue.String long; PureRunValue.Int64 start; PureRunValue.Int64(start + 7L) ])
                Assert.Equal(PureRunValue.Int64Option(indexOf cps "yz" start), run "indexFrom" [ PureRunValue.String long; PureRunValue.String "yz"; PureRunValue.Int64 start ])
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)
