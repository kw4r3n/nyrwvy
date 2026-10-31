//! `--dep host=root` keeps @import("root") callbacks pointed at the production host in tests.
const std = @import("std");
const builtin = @import("builtin");

pub const writeString = @import("host").writeString;

pub fn main(init: std.process.Init.Minimal) !void {
    if (builtin.test_functions.len == 0) return error.NoTestsDiscovered;
    var leaked = false;
    for (builtin.test_functions) |case| {
        std.testing.allocator_instance = .init(std.heap.page_allocator, .{
            .canary = 0xc3a701ba,
            .check_write_after_free = true,
        });
        std.testing.io_instance = .init(std.testing.allocator, .{ .argv0 = .init(init.args), .environ = init.environ });
        std.testing.environ = init.environ;
        @import("host").ids.init(std.testing.io, null);
        defer {
            std.testing.io_instance.deinit();
            if (std.testing.allocator_instance.deinit() != 0) leaked = true;
        }
        std.debug.print("{s}...", .{case.name});
        try case.func();
        std.debug.print("OK\n", .{});
    }
    if (leaked) return error.MemoryLeak;
}
