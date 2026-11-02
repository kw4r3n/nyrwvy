// Appended to runtime.zig and case_mapping.zig by scripts/test-typed-host.mjs (it is not a file of its own):
// every value the runtime allocates is released, checked by DebugAllocator.

const Node = union(enum) {
    end: struct {},
    link: struct { value: i64, next: *const Node },
    pub const ny_ids = [_][]const u8{ "a", "b" };
};
const Pair = struct { text: []const u8, items: []const []const u8 };

test "counted runtime frees everything" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        const a1 = nyInt64ToString(12345);
        const b1 = nyConcat(a1, nyStatic(" x "));
        const c1 = nyConcat(b1, a1);
        nyRelease(b1);
        const parts = nyStringSplit(c1, nyStatic(" "));
        const joined = nyStringJoin(parts, nyStatic("-"));
        try std.testing.expectEqualStrings("12345-x-12345", joined);
        const pair = nyBox(Pair, .{ .text = nyShare(joined), .items = nyShare(parts) });
        nyRelease(parts);
        nyRelease(joined);
        const sorted = nySortBy([]const u8, pair.items, {}, struct {
            fn less(_: void, l: []const u8, r: []const u8) bool {
                return std.mem.lessThan(u8, l, r);
            }
        }.less);
        nyRelease(sorted);
        nyRelease(pair);
        const trimmed = nyStringTrim(nyStatic("  hi  "));
        nyRelease(trimmed);
        var set = NySet([]const u8){ .items = &.{} };
        set = nySetInsertOwned([]const u8, set, nyInt64ToString(1));
        set = nySetInsertOwned([]const u8, set, nyInt64ToString(1));
        set = nySetInsertOwned([]const u8, set, nyInt64ToString(2));
        try std.testing.expectEqual(@as(usize, 2), set.items.len);
        const kept = nySetKeep([]const u8, set, .{ .items = &.{} }, true);
        nyRelease(kept);
        var map = NyMap([]const u8, i64){ .keys = &.{}, .values = &.{} };
        map = nyMapInsertOwned([]const u8, i64, map, nyInt64ToString(1), 1);
        map = nyMapInsertOwned([]const u8, i64, map, nyInt64ToString(1), 2);
        const u = nyMapUnion([]const u8, i64, map, map);
        nyRelease(u);
        nyRelease(map);
        nyRelease(set);
        const lines = nyStringSplitLines(nyStatic("a\nb\r\nc"), nyStatic("\r\n"));
        nyRelease(lines);
        nyRelease(nyFloat64ToString(1.5));
        nyRelease(nyFloat64ToString(0));
        nyRelease(nyMapCase(c1, &ny_upper_runs));
        nyRelease(nyStringDecodeUtf8Lossy(c1));
        nyRelease(nyStringCodePoints(c1));
        nyRelease(c1);
        nyRelease(a1);
        // A long chain releases without recursion.
        var chain: *const Node = nyBox(Node, .{ .end = .{} });
        for (0..1_000_000) |i| chain = nyBox(Node, .{ .link = .{ .value = @intCast(i), .next = chain } });
        nyRelease(chain);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}
