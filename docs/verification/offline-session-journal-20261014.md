# Durable application journal — 2026-10-14

Historical candidate `6f4c2c3` evidence. Its complete-effect-to-success inference
was invalidated by independent review: cancellation followed by SIGKILL before
outcome persistence could become success on restart. The measured 17 contracts
below did not cover that combination. The
[actual-outcome repair](offline-session-actual-outcome-20261014.md) supersedes
that recovery behavior; actual receipt evidence is now required.

The synchronous integration application now retains request identity, exact
frame payload identity, selected mutation intent and actual outcomes across
process restart. A lost integration response followed by identical request
bytes/ID produces one total synthetic commit/HEAD/save. A graph with journaling
disabled, using the identical binding, produces two of each. Pending requests
reconcile observed effects before retry: absence plus matching intent permits
retry; a complete receipt returns the original outcome; partial/unavailable
evidence and changed intent remain unresolved. Conflicting payloads fail before
effect identity binding, evidence observation or dispatch.

This is ordinary application graph composition, with generic external storage,
digest, correlation and inspection contracts. No missing core primitive was
demonstrated, and no core, runtime, schema, backend or Rrawrlet-specific policy
was added. The shared synthetic-fixture compiler gets an optional host method
map and Option result mapping; its existing defaults remain the same.

The isolated feature checkout is
`/home/kw4/Documents/Codex/2026-10-14/task-31/durable-journal`, branch
`feat/durable-application-journal`. Its exact base is reviewed PR400 commit
`8e33e1553397f926d95b335d2bc5f86740b2f9f0`, tree
`365e6a1cdacc4094529adadf0a18e845e23258f3`, from
`feat/bounded-reader-propagation`. A local clone with independent files preserves
the user checkouts. Ancestor instructions and the handoff were read; no
repository AGENTS.md or applicable repository skill files were found. Commands
use Nushell. Locked installed dependencies/build assets were copied locally;
the final Release build uses no restore or network.

## Application and evidence boundary

The graph persists a versioned pending record before dispatch. Its identity is
a scoped SHA256 of canonical typed JSON-RPC ID; the payload fingerprint covers
the decoded frame before the line delimiter. The selected intent contains
target, workspace, candidate commit, eligibility state and prior integrated
HEAD. The stable effect token links synthetic effect observations to that
request. The inherited typed Outcome is retained under that token inside a
shield before inherited state publication. The final record is persisted
before wire output. Completed-record replay returns the retained result.

Pending-record reconciliation inspects independent effect evidence, then uses
an existing retained outcome or a complete effect receipt. If neither exists,
only authoritative absence **and** unchanged selected intent permit retry.
Different candidate commit with identical request bytes remains unresolved.
Malformed records fail closed. Non-null IDs and synchronous integration are
required; notifications and asynchronous begin-integrate do not dispatch.

The external fixture implements opaque-key file reads/replacement writes,
SHA256, AsyncLocal effect correlation and inspection of flushed synthetic
effect rows. Replacement flushes the file and containing directory. The rows
are the synthetic effects themselves; no journal policy lives in that binding.
Actual Git commit/HEAD/save may have a separate receipt crash window which this
fixture cannot demonstrate. Authoritative absence assumes intact retained
effect storage and one writer. There is no interprocess transaction or locking
claim, or claim that a missing physical Git receipt means no Git mutation.

## Fresh causal proof

The final original bundle is `artifacts/journal/review`; the moved bundle is
`artifacts/journal/moved-review`. Each executes 33 fresh processes and 17
contracts, including six SIGKILL interruptions at explicit checkpoints.

| Request/window | Effect evidence and resulting behavior |
| --- | --- |
| Response pipe closes at held commit; input stays open | Commit/HEAD/save and outcome persist, output throws IOException, session joins/closes; restart returns original outcome with one of each effect |
| Same lost-response bytes with journal disabled | Two total commit/HEAD/save effects across two processes |
| Intent durable; mutation has not begun | Absence is observable; restart performs one mutation |
| Save durable; outcome record has not been written | Complete tagged receipt is observable; restart records/returns the original outcome without mutation |
| Commit durable; HEAD/save have not occurred | Partial effect is observable; restart remains pending/unresolved with no repeat |
| Pending plus unavailable evidence | Unresolved and unchanged; later authoritative absence can retry |
| Identical payload plus different selected commit | Absence alone is insufficient; unresolved and unchanged |
| Conflicting payload with pending or completed record | Conflict before effect observation; journal unchanged; original request still resolves/replays |
| Reader oversize or invalid UTF-8 during held integration | Linked witness cancels before gate release; no mutation/release during another 350 ms hold; protected mutation and actual outcome persist after release |

The reader rows preserve the exact InvalidDataException/DecoderFallbackException
object and throwing stack, joined providers/reads, one descriptor close and one
Host close. Their actual integration outcome is `err:cancelled`, retained and
returned after restart. An early probe assertion incorrectly expected success;
it was corrected to preserve the inherited cancellation contract. The new
rows establish ordering while held, without a numerical cancellation-latency
claim. The inherited reader-propagation regression keeps its existing bounded
observation contract and causal controls. SIGKILL rows assert retained state,
not cooperative resource cleanup.

Saved snapshots reopen and re-emit exact unedited Generated.cs and symbols.
Moved bundles recompile to the same 18 artifact files, repeat the fresh process
probes from new empty state directories and agree on stable actions, outputs
and retained state. Actual peer input and accepted output bytes are checked.
Supported and journal-disabled Program.cs agree after declared-symbol
normalization. The graph's wireFold Boolean selects the journal or inherited
path; storage and mutation bindings are shared.

The [proof fixture](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-journal/fixtures/proof.json)
retains contract results and raw/source hashes. The
[replay fixture](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-journal/fixtures/replay.json)
retains artifact hashes and 31 transitive example-source hashes. Complete
snapshots, transcripts, raw events and retained state remain in the local
original/moved artifact directories. The external task31 audit checks their
bytes, the final durable effect ledger against observed events, source hashes,
passing validation logs, candidate file scope and preserved checkout state.

## Validation and limits

Final validation uses the installed .NET SDK `10.0.112`, Node `v26.11.1` and
Nushell `0.116.1`. Heavy work runs serially. Release build is single-node with
zero warnings/errors; the fast .NET suite passes 864 tests, with no failures or
skips. Six inherited reconstruction regressions and the new journal regression
run serially. The handoff and external audit identify final log hashes and
counts. The new regression independently authors original/control snapshots,
reopens a moved copy and runs the 33-process proof in both locations.

Historical reader/restart fixtures from PR400 remain reused baseline evidence;
they are not claimed as fresh journal observations. Earlier development
bundles under `artifacts/journal/dev*`, `original`, `final` and `moved-final` are
intermediate attempts, excluded from the final proof. A sandbox compiler
child-process attempt failed; narrowly approved offline compiler/test execution
succeeded. No automatic approval rejection or uncertain operation remains.

Limits include one writer/domain, inherited parser bounds (4096 code points,
depth 16), transport bounds (16 KiB plus inherited read-ahead), ordinary supported
outcome sizes, selected crash windows, Linux and synthetic effects. Power loss,
arbitrary interrupts, physical Git/provider behavior, journal-write faults,
uncooperative storage/effects, simultaneous unrelated faults, compaction,
multi-client exclusion and peer acknowledgement remain unverified. Hash closure
covers examples and artifacts, excluding core/tool implementation, compiler,
reference packs and environment; implementation sources stay pinned to the
base commit. Flushing does not by itself prove hardware power-loss behavior.

Complete live registry restoration is still missing: the fixture rebuilds
Member state from a scripted prefix, while completed request replay returns a
retained outcome without restoring the new registry's integrated HEAD. The
next substantial milestone is a durable application continuation, followed by
physical mutation/evidence binding proof only in separately approved isolated
offline scope. Historical Rrawrlet/MCP/Python parity remains unavailable. No
Rrawrlet alias/access was broadened, and Rrawrlet was not invoked for this
journal proof; no relevant journaling oracle was supplied in this scope.

No live provider, credentials, broad host access, physical Git mutation tests,
push, PR creation or merge occurred. Candidate commit/tree and the review
handoff are returned separately. Independent review is required before any
publication.
