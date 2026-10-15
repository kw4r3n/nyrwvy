# Offline Leader → Team → Member session

One executable, ordinarily authored graph composes scripted Leader turns with
the maintained Request relay, concurrent admission, default Member lifecycle,
integration ledger, reporting and shutdown. It adds no language/runtime nodes.
The scripted Leader is typed `List<LeaderTurn>` input. The graph reports its
current registry before each turn and dispatches that turn's commands through
the same admission path; registry and request tickets survive across turns.

The main script spawns Member 1 in turn one and Member 2 in turn two. Both
synthetic providers overlap. It observes progress, completes Member 1, and holds
its integration commit. During that hold it cancels an independently queued
spawn before admission, chooses `Err("command-cancelled")`, waits/stops/joins
Member 2, and queues a duplicate integration. The duplicate returns the retained
HEAD with no Git effects. Turn four closes the Team, rejects a new spawn while
drain is held, and joins close. The graph also auto-closes scripts that omit an
explicit close, joins every command worker and supervisor, then inspects the
terminal registry inside the scope. It publishes the success report only after
the owned resource releases.

The terminal Result and published report must both equal:

```text
1,completed,w1,base,0,false,commit1,passed,integrated-head;2,stopped,w2,base,0,false,,,;
```

The application explicitly chooses cancellation outcomes only for command
workers, using #394's worker-local `ExceptionCatch`/`ExceptionIsCancellation`
composition. One session uses an actual token-bearing OCE before admission;
another cancels the pending `OneShotWait` directly. Ordinary exceptions are
rethrown with their original object and throwing stack. Command cleanup is
sequenced outside that adapter. Member workers retain the maintained default
policy, determined settlement shield and original parent cancellation linkage.
The close latch alone has the inherited protected lifetime.

From the repository root, with locked Node dependencies and the Release
AuthoringSession built, run serially in Nushell:

```nu
node examples/offline-leader-team/run.mjs artifacts/leader-session/bundle
node examples/offline-leader-team/test-runtime.mjs artifacts/leader-session/bundle
cp -r artifacts/leader-session/bundle artifacts/leader-session/moved
node examples/offline-leader-team/run.mjs --replay artifacts/leader-session/moved
node examples/offline-leader-team/test-runtime.mjs artifacts/leader-session/moved
node examples/offline-leader-team/verify.mjs artifacts/leader-session/bundle artifacts/leader-session/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-offline-leader-team.test.mjs
```

Use fresh directories for authoring. The bundle includes the semantic snapshot,
every tool request/review/commit, declarations, symbol manifest, unedited emitted
C#, typed cases, virtual evidence, compiled assembly, per-request real effect
traces and compact proof. Compilation uses the installed .NET 10 compiler and
reference pack without restore or downloads. Moved replay checks snapshot,
source, symbols, case data and deterministic assembly bytes exactly. It compares
contract observations rather than forcing a total order on independent replies.

Nine real controls cover the two sessions, command and Member ordinary faults
versus competing release faults, ordinary/OCE command cleanup faults outside the
adapter, a raw release OCE, and positive/wrong-boundary cancellation placement.
The compiled wait-only catch executes and returns a local chosen Err, but its
owned child remains faulted and scope drainage rethrows its original OCE. The
same cancellation acceptance rejects that control. Ten-second waits bound
failures; no sleeps, polling retries or retries until pass are used. Virtual
timing is supplementary and does not establish real barrier ordering.

This milestone uses the maintained typed Request relay. The full historical
JSON-RPC relay from probe 9 was documented with private scripts/snapshots outside
this repository; no reusable local artifact was found. JSON envelope parsing,
keyword binding and full historical JSON-RPC composition are **unverified here**.
The [separate JSON-RPC continuation](../offline-leader-jsonrpc/README.md) now
re-authors and tests a documented bounded wire subset through this same session.
The scripted Leader consumes graph reports in script order; it does not perform LLM
planning. No whole-owner Python policy equivalence is claimed. Python's retained
mutator shutdown is a separate application policy, not a missing primitive.

The recipe retains all request tickets until scope exit and scans immutable
snapshots. It is bounded evidence, with linear retained storage and quadratic
total scanning for longer scripts. Fairness, unbounded service admission, live
providers, physical Git, credentials, budgets, arbitrary host faults and restart
recovery remain outside this session. No provider process or network-backed
effect runs. See the [capability checklist and verification record](../../docs/verification/offline-leader-team-session-20261014.md).
