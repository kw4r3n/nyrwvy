"""Measure Rhifill layoutFrame in a scratch copy of an emitted runner (never edit the input).
Usage: python bench.py /absolute/path/to/runner.zig
Requires the Rhifill layoutFrame IDs used by the 2026-11-05 verification.
"""
import pathlib, subprocess, sys, tempfile

source = pathlib.Path(sys.argv[1]).read_text()
entry = 'f_01a171a7_2d_fecf_2d_7469_2d_b7ed_2d_1b6eb5353e4a'
width_type = 'T_01a17734_2d_005d_2d_7577_2d_abbf_2d_e8bddd0d4406'
assert 'fn ' + entry in source and 'const ' + width_type in source

def patch(text, old, new, count=1):
    assert text.count(old) == count, f'Runtime changed: {old}'
    return text.replace(old, new)
source = source[:source.index('pub fn main(init: std.process.Init)')]
harness = r'''
pub fn main(init: std.process.Init) !void {
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    const cols = try std.fmt.parseInt(usize, args[1], 10);
    const rows = try std.fmt.parseInt(usize, args[2], 10);
    ny_io = init.io;
    nyUseValueAllocator();
    const input = nyAlloc([]const i64, rows);
    for (input) |*row| { const cells = nyAlloc(i64, cols); @memset(cells, 1); row.* = cells; }
    var tokens = NyTokens{ .it = std.mem.splitScalar(u8, "1 2", ' ') };
    const widths = nyDecode(*const WIDTH_TYPE, &tokens);
    var checksum: u64 = 0; var elapsed: i96 = 0;
    for (0..1100) |i| {
        nyRetain(input); nyRetain(widths);
        const started = std.Io.Clock.Timestamp.now(init.io, .awake);
        const result = ENTRY(input, widths);
        const call_ns = started.durationTo(.now(init.io, .awake)).raw.nanoseconds;
        for (result) |row| for (row) |x| { checksum +%= @intCast(x); };
        const freeing = std.Io.Clock.Timestamp.now(init.io, .awake);
        nyRelease(result);
        const ns = call_ns + freeing.durationTo(.now(init.io, .awake)).raw.nanoseconds;
        if (i >= 100) elapsed += ns;
    }
    if (checksum != 1100 * rows * cols * (cols + 1) / 2) @panic("checksum");
    nyRetain(input); nyRetain(widths);
    const check = ENTRY(input, widths);
    if (check.len != rows) @panic("row count");
    for (check) |row| { if (row.len != cols + 1) @panic("column count"); for (row, 0..) |x, i| if (x != i) @panic("position"); }
    nyRelease(check); nyRelease(input); nyRelease(widths);
    if (@import("builtin").mode == .safe and CHECKED) std.debug.assert(ny_debug_allocator.detectLeaks() == 0);
    std.debug.print("{d}x{d}: {d} ns/frame\n", .{cols, rows, @divTrunc(elapsed, 1000)});
}
'''.replace('WIDTH_TYPE', width_type).replace('ENTRY', entry)
with tempfile.TemporaryDirectory(prefix='nyrwvy-layout-cost-') as tmp:
    tmp = pathlib.Path(tmp)
    modes = [('counted', 'ReleaseSafe', True)] if '--count' in sys.argv else [('checked', 'ReleaseSafe', True), ('safe-smp', 'ReleaseSafe', False), ('fast-smp', 'ReleaseFast', False)]
    for label, mode, checked in modes:
        text = source + harness.replace('CHECKED', str(checked).lower())
        if not checked:
            text = patch(text, 'ny_values = if (@import("builtin").mode == .safe) ny_debug_allocator.allocator() else std.heap.smp_allocator;', 'ny_values = std.heap.smp_allocator;')
        if label == 'counted':
            text = 'var bench_reallocs: usize = 0; var bench_allocs: usize = 0; var bench_bytes: usize = 0; var bench_boxes: usize = 0;\n' + text
            text = patch(text, 'if (count == 0) return &.{};', 'if (count == 0) return &.{}; bench_allocs += 1; bench_bytes += @sizeOf(NyHeader) + count * @sizeOf(T);', 1)
            text = patch(text, 'const bytes = nyRaw().realloc(', 'bench_reallocs += 1; const bytes = nyRaw().realloc(', 2)
            text = patch(text, 'const slot = &nyAlloc(T, 1)[0];', 'bench_boxes += 1; const slot = &nyAlloc(T, 1)[0];', 1)
            text = patch(text, 'var checksum: u64 = 0; var elapsed: i96 = 0;', 'bench_reallocs = 0; bench_allocs = 0; bench_bytes = 0; bench_boxes = 0; var checksum: u64 = 0; var elapsed: i96 = 0;')
            text = patch(text, 'nyRetain(input); nyRetain(widths);\n    const check', 'std.debug.print("nyAlloc/frame={d} requested-bytes/frame={d} boxes/frame={d} reallocs/frame={d}\\n", .{bench_allocs / 1100, bench_bytes / 1100, bench_boxes / 1100, bench_reallocs / 1100});\n    nyRetain(input); nyRetain(widths);\n    const check')
        path = tmp / 'bench.zig'; path.write_text(text)
        binary = tmp / label
        subprocess.run(['zig', 'build-exe', str(path), '-O', mode, '-femit-bin=' + str(binary)], check=True)
        for cols, rows in [(80, 24), (300, 100)]:
            for repeat in range(3):
                result = subprocess.run([str(binary), str(cols), str(rows)], capture_output=True, text=True, check=True)
                print(label, repeat, result.stderr.strip(), flush=True)
