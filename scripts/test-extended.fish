#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")

# A changed host takes one core for minutes to build, so the build runs beside dotnet test.
# fish's `wait` drops the exit status, so the job writes it to a file.
set -l host_build
set -l host_status 0
if not contains -- --no-build $argv; and test -z "$NYRWVY_HOST_EXECUTABLE"
    set host_build (mktemp -d)
    sh -c 'cd "$1" && npm run build:host >"$2/log" 2>&1; echo $? >"$2/status"' sh "$repo_root" "$host_build" &
    # A background job ignores Ctrl-C, so an interrupted wrapper stops the build itself.
    function stop_host_build --on-signal INT --inherit-variable last_pid
        pkill -TERM -P $last_pid
        exit 130
    end
end

dotnet test "$repo_root/Nyrwvy.slnx" \
    --configuration Release \
    --settings "$repo_root/tests/extended.runsettings" \
    $argv
set -l test_status $status

if test -n "$host_build"
    wait
    cat "$host_build/log"
    set host_status (cat "$host_build/status" 2>/dev/null; or echo 1)
    rm -r "$host_build"
end
test $test_status -eq 0; or exit $test_status
test $host_status -eq 0; or exit $host_status

cd "$repo_root"
or exit $status
# The suite partition audit reads the built test assemblies, so it runs after the dotnet stage.
node "$script_dir/check-test-suite-partition.mjs"
or exit $status
node "$script_dir/test-examples-zig.mjs"
or exit $status
# Compares the F# C# emitter (the dotnet stage built it) with the standalone Zig one.
set -l oracle src/Nyrwvy.Backend.CSharp/bin/Release/net10.0
set -l emitter_dir (mktemp -d)
set -l emitter $emitter_dir/nyrwvy-csharp-emitter
zig build-exe tools/zig/csharp-emitter/main.zig -O Debug -femit-bin=$emitter
or begin
    set -l code $status
    rm -r $emitter_dir
    exit $code
end
dotnet fsi --reference:"$oracle/Nyrwvy.Semantics.dll" \
    --reference:"$oracle/Nyrwvy.Execution.dll" \
    --reference:"$oracle/Nyrwvy.Backend.CSharp.dll" \
    tools/zig/csharp-emitter/parity.fsx $emitter \
    examples/mini-work-planner/planner.snapshot.nyrec \
    examples/verification-planner/planner.snapshot.nyrec \
    examples/task-cancellation-outcome/fixtures/task-outcomes.nyrec
set -l fsi_status $status
rm -r $emitter_dir
test $fsi_status -eq 0
or exit $fsi_status
exec node "$script_dir/run-node-tests.mjs" extended
