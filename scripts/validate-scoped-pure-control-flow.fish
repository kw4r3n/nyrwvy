#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/Nyrwvy.AgentEval.PureExpression/Nyrwvy.AgentEval.PureExpression.fsproj"
set -l protocol pure-expression-control-flow-v1

set -l temporary (mktemp -d "/tmp/nyrwvy-pure-control-XXXXXX")
or exit 1
set -l input "$temporary/input.jsonl"
set -l output "$temporary/output.jsonl"

function fail_validation --argument-names message
    echo "ERROR: $message" >&2
    rm -rf "$temporary"
    exit 1
end

echo "[scoped-control-flow] 1/2 validating the eval adapter..."
dotnet build "$project" --configuration Release
or fail_validation "pure-expression evaluation runner build failed."

printf '%s\n' \
    '{"command":"describeV1"}' \
    '{"command":"simulatePureExpressionV1","targetFunctionId":"function-pure-control-target","expectedBaseSnapshotId":"snapshot-pure-control-initial","expectedCurrentResultNodeId":"node-pure-control-old-result","replacement":{"kind":"matchOption","input":{"kind":"parameter","parameterId":"parameter-pure-control-option"},"someBody":{"kind":"matchOption","input":{"kind":"optionNone","itemType":{"kind":"primitive","name":"Int64"}},"someBody":{"kind":"patternReference","scopeDepth":1,"slot":{"kind":"value"}},"noneBody":{"kind":"patternReference","scopeDepth":1,"slot":{"kind":"value"}}},"noneBody":{"kind":"int64Literal","value":0}}}' \
    '{"command":"commitPureExpressionV1","proposalHandle":"pure-proposal-1"}' \
    >"$input"


dotnet run --project "$project" --configuration Release --no-build -- --protocol "$protocol" <"$input" >"$output"
or fail_validation "pure-expression evaluation runner smoke session failed."

set -l smoke (string collect <"$output")
string match -q '*"emptyFramesConsumeDepth":true*' -- "$smoke"
or fail_validation "protocol description did not expose the empty-frame lexical-depth rule."
string match -q '*"status":"proposed"*' -- "$smoke"
or fail_validation "nested lexical-scope recipe was not proposed."
string match -q '*"role":"PatternReference"*' -- "$smoke"
or fail_validation "recursive review did not expose branch-local PatternReference nodes."
string match -q '*"status":"committed"*' -- "$smoke"
or fail_validation "reviewed nested lexical-scope proposal did not commit."

rm -rf "$temporary"

echo "[scoped-control-flow] 1/2 passed: eval adapter builds and nested lexical scope survives simulate/review/commit."
echo "[scoped-control-flow] 2/2 running the maintained extended suite once..."
"$script_dir/test-extended.fish"
or exit $status

echo "[scoped-control-flow] validation passed."
