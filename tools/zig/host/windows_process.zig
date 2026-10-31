//! Windows child trees are assigned while suspended, before they can launch descendants.
const std = @import("std");
const w = std.os.windows;
const BasicLimits = extern struct {
    process_time: i64 = 0,
    job_time: i64 = 0,
    flags: u32 = 0x2000, // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    minimum_working_set: usize = 0,
    maximum_working_set: usize = 0,
    active_process_limit: u32 = 0,
    affinity: usize = 0,
    priority: u32 = 0,
    scheduling: u32 = 0,
};
const ExtendedLimits = extern struct {
    basic: BasicLimits = .{},
    io_counters: [6]u64 = @splat(0),
    process_memory: usize = 0,
    job_memory: usize = 0,
    peak_process_memory: usize = 0,
    peak_job_memory: usize = 0,
};
extern "kernel32" fn CreateJobObjectW(?*const w.SECURITY_ATTRIBUTES, ?[*:0]const u16) callconv(.winapi) ?w.HANDLE;
extern "kernel32" fn SetInformationJobObject(w.HANDLE, c_int, *const anyopaque, w.DWORD) callconv(.winapi) w.BOOL;
extern "kernel32" fn AssignProcessToJobObject(w.HANDLE, w.HANDLE) callconv(.winapi) w.BOOL;
extern "kernel32" fn TerminateJobObject(w.HANDLE, w.UINT) callconv(.winapi) w.BOOL;
pub extern "kernel32" fn SetConsoleCtrlHandler(?*const fn (w.DWORD) callconv(.winapi) w.BOOL, w.BOOL) callconv(.winapi) w.BOOL;

pub fn create(process: w.HANDLE) !w.HANDLE {
    const job = CreateJobObjectW(null, null) orelse return error.ChildJobUnavailable;
    errdefer w.CloseHandle(job);
    const limits: ExtendedLimits = .{};
    // JobObjectExtendedLimitInformation (9), from the Windows SDK JOBOBJECTINFOCLASS.
    if (!SetInformationJobObject(job, 9, &limits, @sizeOf(ExtendedLimits)).toBool()) return error.ChildJobUnavailable;
    if (!AssignProcessToJobObject(job, process).toBool()) return error.ChildJobAssignmentFailed;
    return job;
}
pub fn kill(job: w.HANDLE) void {
    _ = TerminateJobObject(job, 1);
}
pub fn close(job: w.HANDLE) void {
    kill(job);
    w.CloseHandle(job);
}
pub fn resumeChild(child: *std.process.Child) !void {
    if (w.ntdll.NtResumeThread(child.thread_handle, null) != .SUCCESS) return error.ChildResumeFailed;
}
pub fn waitForExit(io: std.Io, process: w.HANDLE) !void {
    var timeout: w.LARGE_INTEGER = -500_000; // 50 ms, relative, in 100 ns units.
    while (true) switch (w.ntdll.NtWaitForSingleObject(process, .FALSE, &timeout)) {
        .SUCCESS => return,
        .TIMEOUT => try io.checkCancel(),
        else => return error.Unexpected,
    };
}
