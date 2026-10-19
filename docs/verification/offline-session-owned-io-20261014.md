# Owned input and bounded offline pipe I/O

**Status:** Observed result

Reviewed base: `b655d24ff70f67334cdbc9d43c40a0ddf64d1fe8`, tree
`a294493609f2ee63fdba2cd9fd2080bf7bec9c31`, branch
`feat/offline-session-transport`. Draft **PR397 → PR396 → PR395** are unmerged
dependencies. Candidate branch: `feat/offline-session-owned-io` in an isolated
local clone. No push, PR creation or merge is part of this result.

The [example](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-owned-io/README.md) adds a separate
graph reader task with acknowledged `OneShot` frame links and an ordered driver.
The inherited parser, command fold, admission, Member workers, integration ledger,
shielded mutation tail, close intent, joins and application release are reused.
Only example code, documentation and a Node regression change. There is no core,
backend, runtime, schema, inherited authoring or default-entry change.

## Measurement and change

A fresh compiled baseline and the independent review's boundary probe reproduced
all three selected gaps. In `artifacts/owned-io/boundary-probes.json`:

| Probe | Observed baseline boundary |
| --- | --- |
| Retained peer after SIGTERM | Application release completed with zero providers; transport/process stayed open for the 300 ms observation until peer EOF drained the pending Console read |
| Foreground integration plus EOF | EOF could not reach `InputEnded`, `head` or release during the 300 ms observation; SIGTERM released the synthetic gate and protected save completed once |
| Closed stdout during held integration | Synthetic commit/head/save and release completed once; the Console host reported success while the peer received no terminal |

Existing tasks/scopes/cells express input/dispatch separation. Each frame has an
acknowledgement; acknowledging before the foreground fold allows one following
frame or EOF to be read. A queued frame then waits for its own acknowledgement,
bounding prefetch. EOF behind queued work remains dependent on dispatch.

A prototype without the dispatch cancellation check emitted a receipt for a
prefetched spawn after cancellation, though it acquired no workspace. Its raw
trace is retained at `artifacts/owned-io/prefetch-without-cutoff.json`, with the
prototype graph in `artifacts/owned-io/bundle-v2`. The final graph joins the
cancelled reader before taking the next frame. The gated prefetch case records
one extra frame during a 100 ms held fold, then cancellation, zero admission or
mutation for that frame, protected HEAD/save, and joined release.

The Linux host uses nonblocking libc `read`/`write` with 25 ms input poll steps
and a 250 ms monotonic output deadline. No pending OS read is detached. After
graph drainage, host close checks zero active reads and closes the actual input
descriptor once. An invalid UTF-8 reader fault is surfaced through timed cell
observation and task-result observation instead of leaving an unresolved-cell
wait. There is no new generic primitive or application-specific core feature.

## Completed evidence

The retained original and moved bundles each pass **20 positive pipe cases,
six causal compiled controls and one unrelated-fault guard**. Each supported
execution has one application release, zero providers at release, every read
ended, one actual input descriptor close and one process-terminal observation.
The compiled blocking control is deliberately watchdog-reaped instead.

The new cases establish foreground EOF/cancellation, the prefetch cutoff,
closed stdout, stalled stdout without peer EOF, and deliberate fresh-process
replay after a lost terminal. Protected integration completes its synthetic
`commit`, `head`, and `save` once; cancellation retains the inherited
`err:cancelled` command outcome even when HEAD was saved. A receipt may precede
that escaped cancellation and is not durable completion acknowledgement.

Closed stdout after integration gives `LocalCompleted=true`,
`TerminalWritten=false`, and observable `IOException` (`write errno=32`). Stalled
stdout receives valid requests under the inherited 4096-code-point parser bound;
their exact correlated replies fill the pipe, then a specific `TimeoutException`
occurs after the 250 ms budget. Earlier accepted bytes form an exact reply prefix.
The reader joins and the child closes while the peer still retains stdin and
queued input. Cancellation-to-process-close and write-failure durations are
retained in raw evidence and checked against a broad 2 s OS-test budget.

`LocalCompleted` means the post-release local report completed. `TerminalWritten`
means all terminal bytes were accepted by the OS. Exact peer stdout lines establish
receipt in the tested execution. The protocol has no peer acknowledgement after
OS write success. Mutation completion is also visible in internal command-result
and save events; it must not be inferred solely from a dispatch receipt.
The host sets local completion only after application release with zero active
providers. A user command named `terminal` followed by cancellation cannot set
that flag early; a real pipe case checks this instrumentation boundary.

The harness explicitly replays the complete lost-terminal script in a new process
using identical request IDs and bytes. It observes another synthetic
commit/head/save: two across the lost-response and replay executions. This
measures absent durable deduplication, not physical Git behavior. The application
contains no automatic retry or restart recovery policy.

The controls require their actual defect and the remainder of their paired
lifecycle before failing the corresponding positive contract:

| Compiled control | Required cause / rejected contract |
| --- | --- |
| Dropped tail | Exact tail was dropped after the recorded EOF probe; healthy local result/release and delivered terminal; missing correlated reply |
| Wrong ID | Correct graph reply observed before host rewrite; healthy lifecycle; response correlation/order |
| Double close | Two actual application close effects; healthy local result; release count |
| Uninterruptible read | Reader was waiting when SIGTERM arrived; Member cleanup observed, no release, no peer EOF, actual watchdog SIGKILL; application outcome |
| Suppressed write fault | Actual EPIPE on terminal after local completion; host swallowed it and reported success; output failure observability |
| Sequential reader | Separately authored old graph under the identical new host; peer EOF plus a 300 ms no-EOF/no-head/no-release observation; signal recovery/save/release verified; foreground EOF progress |

The compiled unrelated `NotSupportedException` is refused as tail-loss evidence.
Replay verification caught a harness bookkeeping defect that retained the
watchdog row twice; the final harness retains it once and requires exactly the
declared positive/control/guard rows before checking byte and hash closure.

Retained original/moved replay verifies saved snapshot reopen, exact C#
re-emission, deterministic compilation and equal required artifact bytes,
application source closure, intended peer actions, wire outputs and outcomes.
Every ordinary stream's read bytes equal peer-supplied bytes. Stalled output
may stop with only an exact prefix of queued input. Accepted output bytes are
checked against graph replies in each run; ordinary peer receipt equals those
bytes. Timings and concurrent effect interleavings may differ.

The maintained new regression independently authors both graphs, compiles them,
executes both bundle locations and verifies their replay/closure. The three
existing typed/decoded/pipe session regressions are also freshly run serially.
Both checks passed: **1/1** new maintained regression and **3/3** existing related
regressions. The retained cancellation cases closed in approximately **12–32 ms**
after SIGTERM; the retained stalled write expired after **251 ms** against its
250 ms budget. These are measurements from this run, not scheduling guarantees.
The check records and final observed results are retained with
[`fixtures/proof.json`](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-owned-io/fixtures/proof.json).

The reviewed **864/864 fast .NET suite**, clean Release build and **47/47 prior
aggregate** remain reused evidence. They are not fresh candidate runs. Read-only
comparison confirms their core/backend/runtime/tool sources and all inherited
examples remain unchanged. No backend semantic change requires the extended
backend suite. The new graph, host and maintained workflow have fresh compiled
pipe evidence and related regressions instead.

## Reproduction and limits

The README contains serial Nushell commands. Local checks used cached locked
Node dependencies and the reviewed unchanged authoring tool binaries. The
installed .NET SDK is `10.0.112`, runtime/reference pack `10.0.12` with 167
reference assemblies. Compilation uses installed Roslyn without restore,
downloads or new SDK installation. Local child execution used approved sandbox
exceptions. No external network, application listener, live model, credentials
or original-checkout edit occurred.

Local durable evidence: `artifacts/owned-io/final` and `artifacts/owned-io/moved`,
including `pipe-raw.json`, `pipe-proof.json`, `verified.json`, snapshots,
transcripts, symbols/declarations, generated source, binding source, assemblies,
runtime configuration, cases and compilation records. The sequential control's
corresponding artifacts are nested under `sequential/`. Fresh authoring IDs vary;
retained hashes identify these executions. The compact committed proof records
**20 artifact hashes**, **25 application-source hashes** and **11 check-log
hashes**. Closure excludes transcripts,
mutable scenario files, tool binaries, compiler/reference-pack bytes and general
environment state.

The [capability checklist](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-owned-io/README.md#capability-checklist)
retains the important boundaries: EOF behind queued work waits for dispatch;
frame size before decoding is unbounded; OS writes have no peer acknowledgement;
durable result retention/restart reconciliation is absent; arbitrary simultaneous
EOF/SIGTERM ordering is unverified. The budget bounds the tested host I/O wait,
not uncooperative provider/Git/stderr behavior or whole-application shutdown.
Effects and gates remain synthetic; real Linux byte streams/signals/EOF are
distinct from live providers and physical Git. Windows/macOS, multi-client
transport, process groups, full MCP/JSON-RPC/Python equivalence and the historical
private Rrawrlet artifact reconstruction remain outside this evidence.
