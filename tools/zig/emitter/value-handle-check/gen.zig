const std = @import("std");
const emitter = @import("main.zig");

const tree = "{\"kind\":\"named\",\"typeId\":\"Tree\",\"arguments\":[]}";

pub fn main() !void {
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const ir = try std.mem.concat(a, u8, &.{
        "{\"types\":[{\"id\":\"Tree\",\"kind\":\"union\",\"variants\":[{\"id\":\"leaf\",\"fields\":[{\"id\":\"value\",\"type\":{\"kind\":\"primitiveInt64\"}}]},{\"id\":\"node\",\"fields\":[{\"id\":\"left\",\"type\":",
        tree,
        "},{\"id\":\"right\",\"type\":",
        tree,
        "}]}]}],\"capabilities\":[],\"functions\":[{\"semanticFunctionId\":\"leaf\",\"parameters\":[{\"semanticParameterId\":\"p\",\"value\":{\"id\":\"v\",\"type\":{\"kind\":\"primitiveInt64\"}}}],\"resultType\":",
        tree,
        ",\"entryBlockId\":\"entry\",\"blocks\":[{\"id\":\"entry\",\"parameters\":[],\"instructions\":[{\"result\":{\"id\":\"x\",\"type\":",
        tree,
        "},\"kind\":\"unionConstruct\",\"variantId\":\"leaf\",\"fields\":[{\"targetFieldId\":\"value\",\"valueId\":\"v\"}]}],\"terminator\":{\"kind\":\"return\",\"valueId\":\"x\"}}]},{\"semanticFunctionId\":\"echoTree\",\"parameters\":[{\"semanticParameterId\":\"p\",\"value\":{\"id\":\"v\",\"type\":",
        tree,
        "}}],\"resultType\":",
        tree,
        ",\"entryBlockId\":\"entry\",\"blocks\":[{\"id\":\"entry\",\"parameters\":[],\"instructions\":[],\"terminator\":{\"kind\":\"return\",\"valueId\":\"v\"}}]},{\"semanticFunctionId\":\"echoSet\",\"parameters\":[{\"semanticParameterId\":\"p\",\"value\":{\"id\":\"v\",\"type\":{\"kind\":\"set\",\"itemType\":{\"kind\":\"primitiveInt64\"}}}}],\"resultType\":{\"kind\":\"set\",\"itemType\":{\"kind\":\"primitiveInt64\"}},\"entryBlockId\":\"entry\",\"blocks\":[{\"id\":\"entry\",\"parameters\":[],\"instructions\":[],\"terminator\":{\"kind\":\"return\",\"valueId\":\"v\"}}]}]}",
    });
    const result = try emitter.library(a, ir, &.{ .{ .id = "leaf", .symbol = "ny_leaf" }, .{ .id = "echoTree", .symbol = "ny_echoTree" }, .{ .id = "echoSet", .symbol = "ny_echoSet" } }, .{});
    switch (result) {
        .unsupported => |items| {
            for (items) |item| std.debug.print("unsupported: {s}\n", .{item});
            return error.Unsupported;
        },
        .source => |source| {
            const io = std.Io.Threaded.global_single_threaded.io();
            try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = "lib.zig", .data = source });
            if (emitter.takeLibraryHeader()) |header| try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = "lib.h", .data = header });
            std.debug.print("wrote lib.zig ({d} bytes)\n", .{source.len});
        },
    }
}
