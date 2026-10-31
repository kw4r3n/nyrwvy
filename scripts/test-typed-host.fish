#!/usr/bin/env fish

set -l repo_root (path resolve (dirname (status filename))/..)
cd "$repo_root"
or exit $status

set -l zig zig
if set -q NYRWVY_ZIG; and test -n "$NYRWVY_ZIG"
    set zig "$NYRWVY_ZIG"
end

$zig test -O ReleaseSafe tools/zig/model-reader/view.zig
or exit $status

exec $zig test -O ReleaseSafe \
    --test-filter 'typed host reads' \
    --test-filter 'fresh committed and stale proposal' \
    --test-filter 'selected typed dependencies retain source member order' \
    --test-filter 'operation intermediates' \
    --test-filter 'typed operation name lookup' \
    --test-filter 'path-copy edits' \
    --test-filter 'candidate shape failures' \
    --test-filter 'run declarations hide imported private members' \
    --test-filter 'mismatch references report visible ownership and sources' \
    --test-runner tools/zig/host/test_runner.zig \
    --dep host=root --dep model_reader --dep snapshot_reader --dep zig_emitter \
    --dep csharp_emitter --dep viewer_asset --dep build_info \
    -Mroot=tools/zig/host/main.zig \
    -O ReleaseSafe -Mmodel_reader=tools/zig/model-reader/main.zig \
    -O ReleaseSafe -Msnapshot_reader=tools/zig/snapshot-reader/main.zig \
    -O ReleaseSafe -Mzig_emitter=tools/zig/emitter/main.zig \
    -O ReleaseSafe -Mcsharp_emitter=tools/zig/csharp-emitter/root.zig \
    -O ReleaseSafe -Mviewer_asset=src/Nyrwvy.Viewer/asset.zig \
    -O ReleaseSafe -Mbuild_info=tools/zig/host/build_info.zig
