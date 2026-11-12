# Zig product constructor storage reuse

Status: accepted for the bounded emitter optimization described below.

Product constructors currently allocate a box even when a fold has just finished reading
an owned box of the same instantiated product type. Explicit ProductUpdate already
implements the required reference-count check, shared-source fallback and field drops.

Before ownership analysis and Zig statement generation, lower a constructor internally
to an all-fields ProductUpdate when a same-type source has its last borrowed use in a
field access earlier in the same block and is dead after the constructor. Exclude constructor operands and
sources whose latest preceding use consumes them. Copy the emission IR before changing
it; persisted graphs and validation are unchanged. The added consumed source operand
extends its lifetime through ordinary ownership analysis, including error cleanup.

Use the existing ProductUpdate implementation. It preserves aliases by allocating when
the source is shared; unique sources release their old fields before taking the already
owned replacement fields. Field accesses retain nested counted values before this point.
Search at most 64 preceding instructions per constructor to bound scan cost.
Do not infer reuse across blocks or across consumed references, and do not introduce a
runtime pool or change allocation failure behavior.
A non-entry block entered only by a plain jump from one predecessor counts as part of that
predecessor (the emitter fuses them before this lowering, renaming the block parameters to the
jump arguments when their types are the same); no control flow separates them, so this is not
reuse across blocks. Reads separated by a branch or a merge point stay separate. Delaying a source release can increase
peak live memory until the constructor; it must not change structural values.

Only complete constructors with exactly one binding per declared field are eligible;
partial, duplicate and unknown field bindings keep their original lowering and diagnostics.

Validation covers eligible lowering, shared and nested field semantics through the
existing update path, later source uses, consuming uses, branch liveness and failures
between the last read and construction. Allocation and latency claims require the
separate layout benchmark; emitted-source shape alone is not a performance measurement.
The emitter regression also compiles and executes generated Zig with DebugAllocator:
unique storage is reused, shared storage preserves its alias, nested List<String> fields
survive replacement, and an intervening exception releases the deferred source and fields.
