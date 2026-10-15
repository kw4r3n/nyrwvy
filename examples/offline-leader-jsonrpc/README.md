# Offline JSON-RPC → Leader → Team → Member session

This bounded application re-authors the JSON parser, envelope checks, keyword
binding and serialization as ordinary graph recipes, then feeds validated
`Request` values into PR395's existing `sessionCommandFold`. The same Member,
admission, task ownership, cancellation, duplicate integration and close graph
executes. The host receives raw synthetic text and records raw graph output;
its other responsibilities remain synthetic provider/Git effects and test gates.
No core, runtime, schema or domain primitive is added.

The separate [offline pipe continuation](../offline-session-transport/README.md)
probes byte framing, EOF and root cancellation using this parser and command
fold. The finite decoded-text claim below remains the scope of this example.

This feature depends on **PR395** until that PR merges. Its isolated branch
starts at reviewed `e2fc3a0488171d9978a781f519c7a0dca20e1206`; it does not merge,
modify or replace that PR. Repository history for probe 9 contains only the
verification document (commits `78aaf06`, `a6a7d94`, `5ea98c2`), not its private
scripts or snapshots. Those artifacts were not recovered. This is a newly
authored maintained boundary, not reuse or a Python-equivalence rerun of probe 9.

## Tested protocol

Input is decoded text supplied in scripted `LeaderTurn.lines`, one JSON document
per list item. No socket, byte decoder, framing, EOF, token authorization,
timeout, MCP handshake or transport lifetime is implemented. The supported
application protocol deliberately differs from the historical Python relay.

The parser recognizes objects, arrays, strings, JSON numeric tokens, booleans
and null. It checks JSON whitespace, delimiters, numeric grammar including
leading zeros, escapes and Unicode scalar strings. Duplicate object keys,
unpaired surrogate escapes, more than 4,096 code points or nesting beyond 16
containers are rejected. Both the 4,096-character and 16-container boundary
cases are tested, alongside rejected oversized/deeper inputs. Numeric text is
retained; Float64 parsing checks grammar only. IDs and typed numeric arguments
must parse as Int64 integer tokens; fractions, exponents, booleans and strings
do not bind to integers. This does not promise arbitrary-precision numbers,
Python permissive NaN/Infinity, duplicate-key behavior or full JSON-RPC compliance.

These are rejection bounds, not a completeness or performance guarantee for
every possible document below them. The evidence is the finite checked case set.

Envelopes require `"jsonrpc":"2.0"` and a string `method`, and permit only
`jsonrpc`, `method`, `params`, `id`. IDs are strings, Int64 integer tokens or
null. An absent ID identifies a notification. Malformed JSON or an invalid
envelope produces a null-ID error; a structurally valid notification executes
only a valid dispatch and emits no response, including method/parameter errors.
Explicit null IDs are requests and receive a null-ID response. Strings are
escaped by the graph; Unicode scalar text stays Unicode and integers serialize
canonically. Exact response correlation covers quoted/control/Unicode IDs,
negative and maximum Int64 IDs, and null versus missing IDs.

| Method | Keyword binding | Successful response |
| --- | --- | --- |
| `ping` | `params` absent or an empty object | `{"jsonrpc":"2.0","id":7,"result":{}}` |
| `tools/call` | `params` contains only `name`, `arguments`; name is `session_command` and arguments is an object | `{"jsonrpc":"2.0","id":7,"result":{"dispatched":true}}` |

`session_command` binds exactly required string `op`, string `label`, optional
nonnegative Int64 `target=0`, `previous=0`, `limit=5000`, and optional string
`mode="all"`. Unknown/missing keywords and wrong types are rejected. Supported
ops are `spawn`, `inspect`, `wait`, `stop`, `integrate`, `discard`, `finish`,
`begin`, `join`, `cancel`, `gate`. These names expose the existing bounded typed
test-session vocabulary. `begin` requires a `mode` naming one of the first seven
operations; it schedules that request in `target`'s ticket slot and uses
`previous` as its Member target. `join`/`cancel` address ticket slots; `gate`
coordinates synthetic gates. Ordinary operations retain the typed session's
meaning. This is not the real rrawrlet tool catalog or Python keyword semantics.

`dispatched` is a receipt that the typed command fold returned. For `begin`, it
means scheduled, not completed; for an application rejection, it does not imply
success. Retained HEAD, closing rejection and Member results remain the typed
session's graph-built event/report evidence. No host interprets these to choose
wire policy. After the entire session succeeds and its resource releases, the
graph emits one application notification:

```json
{"jsonrpc":"2.0","method":"session/terminal","params":{"report":"1,completed,w1,base,0,false,commit1,passed,integrated-head;2,stopped,w2,base,0,false,,,;"}}
```

Errors use exact compact JSON with codes `-32700` / `Parse error`, `-32600` /
`Invalid Request`, `-32601` / `Method not found`, and `-32602` / `Invalid params`.
Validation finishes before `sessionCommandFold`, request start/registration,
workspace creation or integration effects. This is checked against effects,
not just error text.

## Reproduction and evidence

Use Nushell, locked Node dependencies and the Release AuthoringSession. Execute
heavy checks serially. Compilation uses installed Roslyn/reference assemblies
without restore/downloads; generated C# is never edited.

```nu
node examples/offline-leader-jsonrpc/run.mjs artifacts/wire/bundle
node examples/offline-leader-jsonrpc/test-runtime.mjs artifacts/wire/bundle
cp -r artifacts/wire/bundle artifacts/wire/moved
node examples/offline-leader-jsonrpc/run.mjs --replay artifacts/wire/moved
node examples/offline-leader-jsonrpc/test-runtime.mjs artifacts/wire/moved
node examples/offline-leader-jsonrpc/verify.mjs artifacts/wire/bundle artifacts/wire/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-offline-leader-jsonrpc.test.mjs
```

The bundle retains the snapshot, proposals/reviews/commits, declarations, symbols,
unedited source, exact parser cases/outputs, generated host, compiled assembly,
per-request effect trace and proof with artifact/source hash closure. Replay
checks exact snapshot/source/symbol/case/declaration/host/assembly bytes and
identical observations. Transcripts also retain the additional replay calls.
`authoring.json` records creation-node counts, including recursive placeholders;
the transcript and snapshot retain their reviewed, committed replacement bodies.

Two real wire sessions spawn two overlapping Members (Member 2 is spawned by a
notification), hold integration, cancel a queued spawn, wait/stop/join Member 2
while integration remains held, integrate once, return retained HEAD for the
duplicate without Git effects, publish the closing cutoff, reject late spawn,
drain and release once, then publish the exact terminal notification. Invalid
requests and notifications precede the first spawn with zero dispatch/mutation.
Independent replies use happens-before requirements, not an imposed total order.

Thirteen compiled controls retain the two cancellation forms, ordinary Member
and command faults, cleanup ordinary/OCE faults, release OCE and wait-only catch
rejection from PR395. Additional positive/wrong controls run the whole parser
case set in compiled C# and reject wrong response IDs. A validation-after-dispatch
control emits the same error but creates a workspace; the identical no-dispatch
acceptance rejects it. `wrongBoundary=true` and `wirePreviewControl(wrongId=true)`
are deliberately unsafe test variants, outside the supported execution path.

No live provider, physical integration Git, sockets, security/access changes,
restart policy, broad shutdown equivalence or unbounded fairness is claimed.
The parser uses bounded recursive calls and immutable string building; these
cases do not establish throughput for larger inputs or arbitrary host failures.
