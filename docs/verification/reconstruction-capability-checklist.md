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
| Durable outcome retention and request deduplication | Same exact integration bytes/ID in a fresh process repeat synthetic mutation after response loss | Missing; next substantial milestone |
| Restart reconciliation of interrupted external mutations | No journal or real Git HEAD/worktree reconciliation | Missing |
| Full historical application, MCP/Python and provider equivalence | Private source and real-provider artifacts not supplied | Unverified / unavailable |
| Arbitrary races, uncooperative effects, blocked stderr, other platforms and multiple clients | Outside bounded fixture execution | Unverified |

Next milestone: compose an application-level durable request journal with
ordinary external storage capabilities. Persist stable request identity,
mutation intent and determined outcome; reopen and reconcile before retrying.
The acceptance probe must lose an integration response after HEAD/save, restart
with the same retained journal and request bytes, then return/reconcile the
original outcome with one total mutation. A paired journal-disabled graph must
still repeat the effect. Physical Git interruption/reconciliation requires its
own later binding proof. Existing core primitives have not been shown
insufficient.
