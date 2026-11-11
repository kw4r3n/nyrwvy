
// ---- slice 3 check harness (appended to the generated library, like run-zig.mjs) ----
var check_alloc: std.heap.DebugAllocator(.{}) = .init;

pub fn main() !void {
    ny_values = check_alloc.allocator();
    var b1: ?*NyValue = null;
    var b2: ?*NyValue = null;
    var b3: ?*NyValue = null;
    var bx: ?*NyValue = null;
    if (ny_leaf(7, &b1) != 0 or b1 == null) return error.LeafFailed;
    if (ny_echoTree(b1, &b2) != 0 or b2 == null) return error.EchoFailed;
    // The old version stays valid after the update: echo the first box again.
    if (ny_echoTree(b2, &b3) != 0 or b3 == null) return error.Echo2Failed;
    if (ny_echoTree(b1, &bx) != 0 or bx == null) return error.OldVersionFailed;
    ny_release(bx);
    bx = null;
    // A box of one type is refused by an export expecting another, and so is null.
    if (ny_echoSet(b1, &bx) != 1) return error.WrongTagAccepted;
    if (ny_echoTree(null, &bx) != 1) return error.NullAccepted;
    ny_release(null);
    ny_release(b3);
    ny_release(b2);
    ny_release(b1);
    if (check_alloc.deinit() == .leak) return error.Leaked;
    std.debug.print("slice3 check: ok\n", .{});
}
