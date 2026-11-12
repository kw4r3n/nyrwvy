// The Events capability's Zig implementation for the threaded-state-loop example, bound by
// events.zig.json. It serves the harness's script: one event code, a fixed number of times,
// then -1 (end of stream). The harness sets the script through eventsScript before each run.
const std = @import("std");

var script_code: i64 = 0;
var script_remaining: i64 = 0;

pub fn eventsScript(code: i64, count: i64) void {
    script_code = code;
    script_remaining = count;
}

/// The next event's code, or -1 once the script is served.
pub fn eventsNext(_: std.mem.Allocator) i64 {
    if (script_remaining <= 0) return -1;
    script_remaining -= 1;
    return script_code;
}
