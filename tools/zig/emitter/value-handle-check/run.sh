#!/bin/bash
# The slice 3 check (docs/decisions/zig-stateful-exports.md, "How to test"): generate a library
# whose exports cross a recursive union as opaque values, then run a harness over it. The harness
# builds values over several calls, checks old versions still read correctly, that a box of the
# wrong type and a null box are refused with status 1, releases everything, and asserts a checking
# allocator has nothing live. It runs in a scratch copy of the emitter so nothing is written back.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d "$here/.check.XXXXXX")
trap 'rm -rf "$work"' EXIT
cp "$here"/../*.zig "$work"/
cp "$here/gen.zig" "$here/harness.zig" "$work"/
cd "$work"
zig run gen.zig
cat lib.zig harness.zig > check.zig
zig run check.zig
