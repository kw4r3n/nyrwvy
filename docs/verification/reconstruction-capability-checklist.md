# Reconstruction capability checklist

This checklist distinguishes demonstrated composition from missing application
behavior. It is scoped to offline Linux-pipe reconstruction with synthetic
provider/Git effects. Private historical Rrawrlet artifacts and physical-provider
equivalence are absent. Passing observations do not establish arbitrary race or
platform coverage. No Rrawrlet-specific core feature is proposed.

| Capability | Evidence / boundary | Status |
| --- | --- | --- |
| Reviewed graph authoring, saved snapshot reopen and exact C# emission | Inherited ordinary graph tools, plus moved replay in each continuation | Demonstrated |
| Ordered admission, independent Member tasks and supervision | Maintained member-concurrency and leader-team examples | Demonstrated with synthetic effects |
| Protected determined mutation and once-only cleanup | Inherited shields; held commit followed by HEAD/save and drained providers | Demonstrated with synthetic effects |
| JSON-RPC parsing, correlation, malformed-frame rejection | Maintained offline-leader-jsonrpc and pipe examples | Demonstrated in supported protocol subset |
| Owned read cancellation, bounded blocked output and descriptor release | Owned-I/O reconstruction; real nonblocking Linux syscalls | Demonstrated in measured cases |
| Input byte bound before strict UTF-8 decode | Reviewed 43507a5; 16 KiB payload plus 1 KiB read-ahead buffer | Demonstrated transport-storage bound |
| Reader failure observation during foreground work | Reader/dispatcher supervision using TaskStart, TaskWait(any) and existing scope cleanup; paired old-graph controls | Demonstrated; 0.99–1.79 ms reader-end-to-linked-provider cancellation in retained runs |
| Dispatcher/output failure observation while input stays open | Lost-response restart probe observes failing output, cancels/joins reader and closes once | Demonstrated in restart probe |
| Successful EOF while foreground command is held | Observer completes normally; owner waits for dispatcher and protected gate release | Supported; queued-frame EOF observation still waits for dispatch acknowledgement |
| Peer acknowledgement of outcome | OS accepted writes and correlation ID do not prove peer processing | Missing |
| Durable outcome retention and request deduplication | Versioned application journal, stable typed-ID digest, exact-frame fingerprint and retained actual outcomes; original/moved fresh-process lost-response proof has one mutation vs two with journal disabled | Demonstrated for synchronous integration in one retained single-writer domain |
| Restart reconciliation of interrupted synthetic mutations | Durable selected intent; absence/applied/partial/unavailable evidence; SIGKILL before mutation and after commit/save; conflicting payload and changed candidate stay blocked before retry | Demonstrated in measured windows; physical Git/provider binding remains missing |
| Complete live registry restoration and durable continuation | Prefix rebuilds Member state; completed request replay returns retained outcome without restoring the registry's integrated HEAD | Missing |
| Full historical application, MCP/Python and provider equivalence | Private source and real-provider artifacts not supplied | Unverified / unavailable |
| Arbitrary races, uncooperative effects, blocked stderr, other platforms and multiple clients | Outside bounded fixture execution | Unverified |

The [application journal milestone](../../examples/offline-session-journal/README.md)
now satisfies the lost-response acceptance contract with existing primitives.
Six explicit SIGKILL rows per bundle distinguish durable intent, partial commit
and completed save from journal outcome persistence. Request deduplication is
restricted to synchronous, non-null-ID integration requests. Reused reader
fixtures remain historical evidence; fresh journal reader rows preserve the
original cancellation outcome and exact reader exception.

Next substantial milestone: reconstruct a complete durable application
continuation, including Member/registry state and the integrated HEAD, then
prove the physical mutation/evidence binding in a separately approved isolated
offline fixture. The current synthetic effect ledger cannot prove physical Git
reconciliation or provider equivalence. Multi-writer exclusion, pruning,
power-loss behavior, peer acknowledgement and historical MCP/Python parity
remain outside this proof. No existing core primitive has been shown
insufficient, and no Rrawrlet-specific core policy is warranted.
