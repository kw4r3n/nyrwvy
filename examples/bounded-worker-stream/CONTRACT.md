# Two-worker bounded-stream contract

## Ownership and capacity

There are exactly two long-lived producers and one collector in one TaskScope.
Each producer has an independent OneShot chain. A producer reads one record,
appends it and waits for its sink acknowledgement before reading another. A
successful OneShotComplete accepting a Data link is the send's linearization
point. At most one accepted, unacknowledged Data belongs to each producer,
hence at most two total. Control links are not Data records.

The host provides input, sink and resource I/O. Its observation and abort-signal
operations are test instrumentation. It does not store a mailbox, choose a
cursor, admit a record, route to a producer, or implement cancellation policy.
Test scheduling gates only choose when an I/O operation is allowed to finish.

The collector alternates streams while both are active and continues the
remaining stream after one ends. This is deliberately head-of-line blocking:
a ready second stream waits behind an idle first input. No work-conserving or
independently progressing merge is promised. Per-producer order is checked by
worker ID and consecutive sequence number before every sink call.

## Normal delivery

After a successful sink return, the collector completes that record's ack.
The producer advances to the next cell only after observing its ack. It emits
Done after its final record's acknowledgement. The collector ends normally
after consuming both Done markers.

Normal execution delivers every accepted Data record exactly once in this
fixture, with each producer's order preserved. This is not a distributed
exactly-once transaction: an arbitrary external sink could commit a write and
then report failure or be cancelled. Such a side effect is neither rolled back
nor retried by this graph.

## Graceful Stop

StopAfter K means: after K successful sink calls, insert one Stop marker into
each stream not yet observed ended. If Done already won, that existing terminal
marker is drained instead. A Stop races with that stream's producer through the
same append protocol. Each successful Stop completion is a separate per-stream
cutoff. The operation is not one atomic cutoff across both streams.

A producer that encounters Stop or Done rejects the attempted append. The
collector keeps its original unread cursors while inserting Stop, then drains
all accepted Data before each terminal marker. It must not replace an unread
cursor with the Stop append's returned tail, which would skip racing records.
The final delivered count can exceed K by up to the outstanding capacity of
two. No Data accepted after a stream's Stop is possible.

Once the collector has drained both terminal prefixes, scope exit closes any
remaining waits, cancels and joins producers. A producer may observe a Stop
rejection or cooperative cancellation during this final settlement; both stop
future work. The real Stop-first fixture ensures a blocked producer actually
observes rejection before scope exit. Graceful success does not require an
input I/O operation that was already in flight to finish normally.

## Abort and errors

An expected source or sink Err observed by the coordinator before selecting its
terminal outcome propagates as an error. Cooperative-cancellation Err results
from producers during final settlement after collector success may be ignored.
The coordinator uses Wait(any) over the collector and still-active producers; successful
producer completion removes that task from later wait sets rather than ending
the entire scope. Worker errors cannot leave it waiting only for the collector.
Unexpected host exceptions propagate through the scope.

Parent scope-body exit and external parent-token cancellation are aborts.
Unresolved OneShots close and every task is cancelled/joined. Prompt settlement
requires cooperative host I/O. After a successful open, the resource closes
exactly once, after active I/O has settled. A failed acquire does not call close. Abort does not reconstruct an accepted-history report and does
not promise delivery of accepted but unacknowledged records. Completed sink
side effects are not rolled back. A success report is never saved after the
graph observes cancellation.

## Retention and work

A shared MPSC chain with one cached cursor per producer is unsuitable here:
a paused producer could keep a cursor into arbitrarily many completed records
from its peer. Separate SPSC chains avoid that cross-producer retention. The
one-outstanding-record rule bounds the distance between each producer and its
collector cursor, even when either task is descheduled or its I/O stalls.

TaskStart's consuming-ref entry fix is required to discard initial callback
captures. The launch helper returns only task handles; a graph-owned start gate
opens after that helper returns, so streams cannot advance while a suspended
launch frame retains their initial heads. The collector has one common self-
tail-call site. Branch-specific processing returns through a helper rather than
leaving a dormant side-branch cursor in the long-lived collector frame.

Completed cells unregister from their owner's pending registry. The owner
retains exactly three task handles for the entire stream. The graph keeps no
accepted-record log and never reconstructs history on shutdown.

The append loop allocates next and ack exactly once per logical send, including
retries. Normal appends take one completion attempt each. A Stop can traverse
at most the one outstanding Data in its stream before being inserted. Measured
successful-append attempt counts should therefore grow linearly with records,
not with cumulative head re-walking. Rejected-attempt traversal counts are not
reported by the current scalar error result.

A capacity bound is not a byte-size bound. Record sizes are not constrained by
this example; host buffers, test observation logs, retained caller data and
runtime allocation/GC behavior are outside the graph's backlog contract. The
fixture intentionally records sink/observation history to verify delivery, so
total process heap is not claimed constant. Runtime weak-reference checks and
pending-registry counts supplement the graph-root analysis; neither is a
standalone proof of arbitrary-program memory behavior.
