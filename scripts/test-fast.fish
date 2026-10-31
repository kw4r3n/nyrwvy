#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")

dotnet test "$repo_root/tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj" \
    --configuration Release \
    --settings "$repo_root/tests/fast.runsettings" \
    $argv
or exit $status

cd "$repo_root"
or exit $status
if not contains -- --no-build $argv; and test -z "$NYRWVY_HOST_EXECUTABLE"
    npm run build:host
    or exit $status
end
exec node "$script_dir/run-node-tests.mjs" fast-contracts
