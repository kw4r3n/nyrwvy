namespace Nyrwvy.Core

/// The case-mapping tables the Zig emitter and the Zig host embed, generated from .NET's mapping
/// (the C# backend's) and checked against the files by `ZigBackendTests`.
[<RequireQualifiedAccess>]
module ZigRuntime =
    /// The simple (one-to-one) case mapping the C# backend's NyText.ToLower/ToUpper apply: .NET's
    /// invariant mapping per code point, except U+0130 -> i, U+0131 -> I and U+00DF -> U+1E9E.
    let private mapCase (lower: bool) (code: int) =
        if code < 0x10000 then
            let unit = char code
            if lower then (if code = 0x130 then 0x69 else int (System.Char.ToLowerInvariant unit))
            elif code = 0x131 then 0x49
            elif code = 0xDF then 0x1E9E
            else int (System.Char.ToUpperInvariant unit)
        else
            let rune = System.Text.Rune code
            (if lower then System.Text.Rune.ToLowerInvariant rune else System.Text.Rune.ToUpperInvariant rune).Value

    /// Runs of mapped code points { first, last, step, delta }: every step-th code point from first
    /// to last maps to itself plus delta. Runs take consecutive mapped code points, so a step-2 run
    /// skips only unmapped ones, runs never overlap and a binary search over `first` finds the only
    /// candidate.
    let private runsOf (map: int -> int) =
        let mapped =
            [| for code in 0 .. 0x10FFFF do
                 if code < 0xD800 || code > 0xDFFF then
                     let target = map code
                     if target <> code then code, target - code |]
        let runs = ResizeArray<string>()
        let mutable index = 0
        while index < mapped.Length do
            let first, delta = mapped[index]
            let mutable last = first
            let mutable step = 1
            if index + 1 < mapped.Length then
                let next, nextDelta = mapped[index + 1]
                if nextDelta = delta && next - first = 2 then step <- 2
            while index + 1 < mapped.Length
                  && fst mapped[index + 1] = last + step
                  && snd mapped[index + 1] = delta do
                index <- index + 1
                last <- last + step
            index <- index + 1
            runs.Add $".{{ {first}, {last}, {step}, {delta} }}"
        System.String.Join(", ", runs)

    let private caseRuns (lower: bool) = runsOf (mapCase lower)

    /// .NET's own mappings for the Zig session host (`tools/zig/host/case_runs.zig`): `lower` is
    /// `ToLowerInvariant` per code point, `upper` the invariant upper case where
    /// `StringComparison.OrdinalIgnoreCase` takes it as equal, as `search` and name hints compare.
    let caseRunTables =
        lazy
            (let text code = System.Text.Rune(code: int).ToString()
             let lower code = (System.Text.Rune.ToLowerInvariant(System.Text.Rune(code: int))).Value
             let upper code =
                 let target = (System.Text.Rune.ToUpperInvariant(System.Text.Rune(code: int))).Value
                 if System.String.Equals(text code, text target, System.StringComparison.OrdinalIgnoreCase) then target else code
             "pub const lower = [_][4]i32{ " + runsOf lower + " };\n" + "pub const upper = [_][4]i32{ " + runsOf upper + " };\n")

    /// Zig case mapping, emitted only for a program that maps case.
    let caseMapping =
        lazy
            ("const ny_lower_runs = [_][4]i32{ " + caseRuns true + " };\n"
             + "const ny_upper_runs = [_][4]i32{ " + caseRuns false + " };\n"
             + """
fn nyMapCase(text: []const u8, runs: []const [4]i32) []const u8 {
    var out: std.ArrayList(u8) = .empty;
    var view = std.unicode.Utf8View.initUnchecked(text);
    var points = view.iterator();
    while (points.nextCodepoint()) |point| {
        const code: i32 = point;
        var low: usize = 0;
        var high: usize = runs.len;
        while (low < high) {
            const middle = (low + high) / 2;
            if (runs[middle][0] <= code) low = middle + 1 else high = middle;
        }
        var mapped = code;
        if (low > 0) {
            const run = runs[low - 1];
            if (code <= run[1] and @mod(code - run[0], run[2]) == 0) mapped = code + run[3];
        }
        var buffer: [4]u8 = undefined;
        const size = std.unicode.utf8Encode(@intCast(mapped), &buffer) catch unreachable;
        out.appendSlice(nyRaw(), buffer[0..size]) catch nyOom();
    }
    return nyAdoptList(u8, &out);
}
""")
