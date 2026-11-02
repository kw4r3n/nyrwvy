#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")

dotnet test "$repo_root/Nyrwvy.slnx" \
    --configuration Release \
    --settings "$repo_root/tests/extended.runsettings" \
    $argv
or exit $status

cd "$repo_root"
or exit $status
if not contains -- --no-build $argv; and test -z "$NYRWVY_HOST_EXECUTABLE"
    npm run build:host
    or exit $status
end
# The suite partition audit reads the built test assemblies, so it runs after the dotnet stage.
node "$script_dir/check-test-suite-partition.mjs"
or exit $status
node "$script_dir/test-examples-zig.mjs"
or exit $status
exec node "$script_dir/run-node-tests.mjs" extended
