# Owned reader failure propagation

**Status:** Observed result in the offline reconstruction.

Base: `43507a5aee1aa0f706ec4b86e19392936fdcd94a`, tree
`a5ac4707f764dcd2219e7d16badb57a29f5b18c3` (reviewed/pushed PR399, stacked
on PR398). Candidate branch: `feat/bounded-reader-propagation`, in a fresh
independent clone. No publication is performed before independent review.

The prior independent review found a reader oversize fault and read end followed
by 1,008 ms without foreground HEAD, outcome or release. The input driver was
inside `wireFold`, so it could observe the reader only after that call returned.
SIGTERM released the fixture's held commit and permitted protected completion.

The [continuation](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-reader-propagation/README.md)
uses the existing graph primitives. The dispatcher runs in an owned task; a
second owned task waits on the reader with the dispatcher's result type.
`TaskWait(any)` observes either worker while foreground work continues. An
exception exits the existing session execution scope, whose existing cleanup
cancels and joins all owned work and retains the body exception. Successful
reader EOF instead waits for dispatcher completion. The inherited determined
mutation shields and close-latch scope remain intact. No runtime, schema,
backend or core feature changes. The shared fixture compiler gains an optional
effects-template transform with an identity default.

The byte policy remains the reviewed 16 KiB payload limit before strict UTF-8
decode, plus a fixed 1 KiB syscall buffer. This change concerns failure
observation, rather than a new framing or total-memory bound.

| Fresh retained case | Original / moved observations |
| --- | --- |
| Reader oversize during held commit | Linked witness provider exits on cancellation 1.719 / 1.792 ms after recorded reader end |
| Reader malformed UTF-8 during held commit | Linked witness provider exits on cancellation 0.988 / 1.005 ms after recorded reader end |
| Same faults under separately authored old graph | No witness cancellation, HEAD, outcome or release during 350 ms hold; cancellation arrives only after external protected-gate release, approximately 384–387 ms after reader end |
| Supported fault rows while protected gate stays held | Witness cancellation is present; HEAD, outcome and application release remain absent during the 350 ms hold |
| Supported fault rows after peer releases protected gate | Check/apply/commit/HEAD/save each once; all protected steps see clear cancellation token; original reader exception object and throwing stack survive |
| Healthy held command or EOF during held command | No premature witness cancellation or terminal; gate release permits normal completion and terminal response |
| Idle oversize, malformed UTF-8, partial-input SIGTERM and fragmented valid ping | Expected outcome class, strict framing, joined read/provider cleanup and one actual descriptor close |
| Unrelated input fault | Original `NotSupportedException` is retained and refused as oversize evidence |
| Peer output closed before releasing commit, input kept open | Protected HEAD/save complete; integration response fails with `IOException`; dispatcher fault cancels/joins reader and closes once without EOF/SIGTERM |
| Same integration bytes/ID in a fresh process | Commit/HEAD/save occur again: two of each across the lost-response/retry pair |

The test gate is a peer-written marker file. Its host wait does not react to
reader failure or cancellation, and both supported and control graphs use an
identical binding after declared-symbol normalization. The witness is a
separate cooperative provider, so its cancellation proves scope propagation
before the protected effect is released. Measurements use receipt times of
stderr observation records. The 1 s acceptance budget is deliberately broad;
it is not an enforced runtime deadline or a universal scheduling guarantee.
An uncooperative protected effect can still hold completion indefinitely.

Each original/moved bundle retains **11 pipe rows: eight supported contracts,
two old-graph causal controls and one unrelated-fault guard**, plus two
fresh-process restart observations. Every supported failure has a failing
structured outcome and no successful wire terminal. The fixture deliberately
exits zero after recording that outcome; exit code alone is insufficient.
All rows require one application release, ended reads, zero active providers,
once-only Member cleanup where applicable, and joined transport descriptor
release. No watchdog termination is accepted as success.

Moved replay reopens/re-emits/recompiles the saved graph and checks 19 exact
artifact files. Hash closure covers 20 artifacts per primary proof and the
restart raw/proof pair separately, plus the imported source closure. Input
bytes equal the offered prefix; accepted writes equal peer output. Lost-output
accepted writes are a strict prefix of graph reply bytes. Source, read
fragmentation, task scheduling and timings have distinct roles; interleavings
and timings can vary. Closure excludes mutable scenarios, transcript,
tool/compiler/reference-pack binaries and environment state.

Retained compact [proof](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-reader-propagation/fixtures/proof.json),
[replay audit](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-reader-propagation/fixtures/replay.json),
[restart gap](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-reader-propagation/fixtures/restart.json)
and [verification manifest](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-reader-propagation/fixtures/verification.json)
identify the concrete runs. Complete evidence lives in
`artifacts/reader-propagation/{final,moved}` in the implementation checkout.

Fresh offline-cache restore and serial single-node Release build pass with zero
warnings/errors. The fresh fast .NET suite passes **864/864**, with no failures
or skips. All **six maintained reconstruction regressions** pass serially:
leader-team, JSON-RPC, transport, owned-I/O, bounded-input and reader-propagation.
Their combined reported duration is approximately 373.8 seconds. The final
audit checks **44 artifact hashes, 29 source hashes and 22 fresh passing-log
hashes**, including original/moved restart raw proofs. Moved replay checks the
same supplied bytes, actions, outputs and structured outcomes while permitting
different task interleavings, read fragments and timing.

The [capability checklist](reconstruction-capability-checklist.md) selects the
next substantial application milestone: a durable request journal and restart
reconciliation using ordinary storage capabilities. Its lost-response acceptance
must restart with retained state and identical request bytes, then return or
reconcile the original outcome with one total mutation. A disabled-journal
control must still repeat the effect. Request correlation and OS write
completion alone do not provide durable deduplication or peer acknowledgement.

The proof remains real Linux pipes plus synthetic application effects. Private
historical Rrawrlet artifacts, full MCP/Python and physical Git/provider
equivalence are absent. EOF behind a queued frame still waits for dispatch
acknowledgement. Arbitrary races, simultaneous unrelated faults, uncooperative
effects, blocked stderr, other platforms, process groups and multiple clients
remain unverified. No Rrawrlet-specific core feature is justified.

All commands use Nushell and heavy work is serial. Task7 confirmed no pending
render window before validation. The sandbox reproduced synchronous Node
`dotnet` spawning `EPERM` despite SDK exit zero, and denied VSTest's local
socket. Narrow offline compiler/test escalations succeeded; diagnostic logs are
retained separately from passing evidence. Local dependency caches were reused;
the Release assemblies, graph snapshots and test executions were fresh. The
reviewed task23 and original task20 checkouts were read only and remain clean.
