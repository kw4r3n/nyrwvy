# Bounded offline session input

**Status:** Observed result

Reviewed base: `f62782acd06188f35ba4ccb4fd6c3517a261a956`, tree
`1b5fe82c6d64bd7dfee934e7ff3afbdee0ae416a`. Candidate branch:
`feat/bounded-session-input`, in an independent local clone. Draft
**PR397 → PR396 → PR395** remain unmerged dependencies. No push, PR or merge
is authorized before independent review.

The [example](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-bounded-input/README.md) supplies
`maxBytes=16384` in the semantic graph's ordinary capability invocation. Its
Linux binding counts/stores payload bytes before strict UTF-8 decoding. This
fixes the independently reviewed 1 MiB unterminated-input gap using existing
capability parameters, task ownership, cells and cleanup. No core, backend,
runtime or schema feature changes. The owned-I/O author/compiler helpers gain
optional reuse paths; their inherited default behavior remains covered.

The requirement bounds a pending frame's raw payload to 16 KiB, excluding
CR/LF terminators, with a fixed 1 KiB syscall buffer. Exactly the limit waits
for framing completion, EOF or cancellation. The first excess payload byte
raises `InvalidDataException` before decoding/dispatch. The reader does not
drain/resynchronize oversized frames. Whole-session termination can still wait
for foreground work. This is a transport-owned input bound, not a total-memory,
peer-buffer, kernel-buffer, trace-size or application-completion bound.

| Fresh probe | Observed result |
| --- | --- |
| 1 MiB unterminated ASCII, bounded graph | First excess byte is 16385; buffered payload stays 16384; syscall read stops at 17408; no frame decoded; one release/descriptor close, zero providers, no successful terminal |
| Same 1 MiB, separately authored relaxed graph | Same binding reads all 1048576 bytes, buffers all of them without decoding or oversize fault; retained-stdin signal recovery joins cleanup |
| 16385 terminated bytes, relaxed graph | Complete oversized line reaches strict decoding and the parser; parse-error response and healthy EOF/release establish the causal policy failure |
| 16384 bytes with LF, split CRLF, or EOF | One exact-limit decode; inherited parser rejects oversized code-point input; subsequent ping remains ordered/correlated where supplied |
| Exact limit retained or partial UTF-8 at limit, then SIGTERM | No decode, no peer EOF, `TaskCanceledException`, joined read/provider cleanup |
| Valid 4096-code-point emoji ping | 16261 UTF-8 bytes; exact correlated successful reply |
| Split 2-, 3-, and 4-byte sequences | No premature frame/decoding; complete frame preserves exact Unicode ID |
| Limit crossed inside a multibyte sequence | Specific oversize fault at byte 16385 before any decode of that frame |
| Malformed/truncated UTF-8 | Specific `DecoderFallbackException`; previous valid reply survives; no following malformed-frame dispatch |
| Held foreground commit plus oversize | Reader rejects and ends; no outcome/HEAD/release during 350 ms; SIGTERM permits once-only protected HEAD/save, original input fault, joined cleanup |

The original and moved bundles each retain **24 distinct rows: 20 supported
probe contracts, three causal relaxed-policy controls and one unrelated-fault
guard**. One supported contract intentionally records the foreground fault
observation limit. Every row exits normally after one application release,
zero providers, ended reads, one actual input descriptor close, drained
transport close and process-terminal. No watchdog success is claimed.

The controls use a separate ordinary graph with `maxBytes=2097152` and the
same binding source. They prove actual oversized decoding or full incomplete
byte accumulation before failing the identical bounded contract. The guard
throws `NotSupportedException` on a small complete frame, with no oversize
event; it is refused as limit evidence. Member cases require once-only Member
cleanup before application release. The foreground case requires the actual
commit gate, exact absence observation, signal recovery, protected HEAD/save
and original error, rather than treating any exception as proof.

Moved replay reopens the snapshots through normal graph tools, re-emits exact
unedited C#, recompiles using installed Roslyn, and executes all 24 rows again.
It checks 19 exact original/moved artifact files, 20 artifact hashes and 27
source hashes. Actual read bytes are exact prefixes of supplied/queued bytes;
the bounded MiB case stops without draining the frame. Accepted OS write bytes
equal graph replies and observed peer output in these rows. Interleavings,
syscall fragmentation and timings may vary. Artifact closure excludes
transcripts/mutable scenarios; source closure excludes tool/compiler/reference
pack bytes and environment state. It is not environment closure.

Compact [proof](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-bounded-input/fixtures/proof.json),
[replay closure](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-bounded-input/fixtures/replay.json)
and [verification audit](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-bounded-input/fixtures/verification.json)
retain exact hashes, contracts and measurements. Complete bundles are under
`artifacts/bounded-input/{bundle,moved}` in the authoring checkout. The example
README gives clean reproduction commands.

Verification uses installed SDK 10.0.112, runtime/reference pack 10.0.12 and
167 reference assemblies. Fresh offline-cache restore and a single-node
Release build pass without warnings/errors. The fresh fast .NET suite passes
**864/864**. The new maintained regression and all four inherited typed,
JSON-RPC, pipe and owned-I/O regressions pass **5/5**, with test concurrency one.
The final audit verifies 20 artifact hashes, 27 source hashes and 15 passing
log hashes. Exact-limit/partial-UTF-8 cancellation to process close takes
35.70–36.69 ms across the retained original/moved executions, below the broad
2 s observation budget; these are observations, not a universal timing promise.

The next measured gap is prompt observation of owned reader faults while a
foreground fold is held. The byte bound works during that wait, but an owner
policy must surface/cancel the transport fault while preserving shielded
mutation completion. Existing primitives have not been shown insufficient;
no new core capability is justified by this result.

EOF behind queued input still waits for dispatch acknowledgement. OS write
completion still lacks peer acknowledgement. Durable outcome retention,
restart reconciliation and durable deduplication remain missing; explicit
fresh-process replay can repeat the inherited synthetic commit/HEAD/save.
Historical private Rrawrlet artifacts and full MCP/Python equivalence remain
absent. Physical Git/live providers, arbitrary EOF/SIGTERM races, uncooperative
effects, blocked stderr, other platforms, process groups and multi-client
transport remain unverified. The earlier aggregate reconstruction totals are
reused evidence, not fresh totals here.

Implementation and verification commands use Nushell; heavy checks are serialized. Node synchronous
`dotnet` calls reproduced sandbox `EPERM` with a successful SDK child exit;
narrow offline compiler/test escalations resolve that environment limitation.
The original checkout is not edited.
