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
exec node "$script_dir/run-node-tests.mjs" fast-contracts
