#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/NyamNyam.AgentEval.PureExpression/NyamNyam.AgentEval.PureExpression.fsproj"
set -l model "$NYAMNYAM_PURE_CONTROL_EVAL_MODEL"
set -l variant "$NYAMNYAM_PURE_CONTROL_EVAL_VARIANT"

if test -z "$model"
    set model openai/gpt-5.6-terra
end
if test -z "$variant"
    set variant high
end

cd "$repo_root"
or exit 1

if not command -q opencode
    echo "ERROR: opencode is not available." >&2
    exit 1
end

if not opencode models | string match -q -- "*$model*"
    echo "ERROR: model '$model' is not available to OpenCode." >&2
    echo "Set NYAMNYAM_PURE_CONTROL_EVAL_MODEL to another available model if needed." >&2
    exit 1
end

echo "[pure-control-eval] building the eval-only runner..."
dotnet build "$project" --configuration Release
or exit $status

set -l stamp (date +%Y%m%d-%H%M%S)
set -l result_root "$HOME/.local/share/nyamnyam/pure-control-flow-exploratory/$stamp"
mkdir -p "$result_root"
or exit 1
set -l summary "$result_root/summary.tsv"
printf 'task\texit\tproposed\tcommitted\tpattern_reference\tlexical_diagnostic\tlog\n' >"$summary"

set -l task_ids PCF-E1 PCF-E2 PCF-E3 PCF-E4

for task_id in $task_ids
    set -l prompt ""
    switch $task_id
        case PCF-E1
            set prompt "Replace target with a conditional that returns 1 when true and 2 otherwise. Complete the task through the evaluation protocol and review the proposed diff before committing."
        case PCF-E2
            set prompt "Match option. Return the contained value for Some and return 0 for None. Complete the task through the evaluation protocol and review the proposed diff before committing."
        case PCF-E3
            set prompt "Match union. For first(value), return the bound value field. For second, return 0. Complete the task through the evaluation protocol and review the proposed diff before committing."
        case PCF-E4
            set prompt "Match option. In the Some arm, perform a nested match over OptionNone<Int64>; both nested arms must return the outer Some value. The outer None arm returns 0. Complete the task through the evaluation protocol and review the proposed diff before committing."
    end

    set -l stdout_log "$result_root/$task_id.jsonl"
    set -l stderr_log "$result_root/$task_id.stderr.log"

    echo "[pure-control-eval] $task_id"
    opencode --print-logs --log-level INFO run \
        --agent nyamnyam-pure-control-eval \
        --model "$model" \
        --variant "$variant" \
        --format json \
        --title "pure-control-$task_id-$stamp" \
        "$prompt" \
        >"$stdout_log" 2>"$stderr_log"
    set -l run_code $status

    set -l indicators (python3 - "$stdout_log" <<'PY'
import json, sys
path = sys.argv[1]
strings = []

def collect(value):
    if isinstance(value, str):
        strings.append(value)
    elif isinstance(value, dict):
        for item in value.values():
            collect(item)
    elif isinstance(value, list):
        for item in value:
            collect(item)

with open(path, encoding="utf-8") as stream:
    for line in stream:
        line = line.strip()
        if not line:
            continue
        try:
            collect(json.loads(line))
        except json.JSONDecodeError:
            strings.append(line)

text = "\n".join(strings)
print("true" if '"status":"proposed"' in text else "false")
print("true" if '"status":"committed"' in text else "false")
print("true" if 'PatternReference' in text else "false")
lexical = sorted({
    code for code in (
        "PureExpressionPatternScopeDepthInvalid",
        "PureExpressionPatternScopeNotFound",
        "PureExpressionPatternSlotNotFound",
    ) if code in text
})
print(",".join(lexical) if lexical else "none")
PY
)

    set -l proposed $indicators[1]
    set -l committed $indicators[2]
    set -l pattern_reference $indicators[3]
    set -l lexical_diagnostic $indicators[4]

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$task_id" "$run_code" "$proposed" "$committed" "$pattern_reference" "$lexical_diagnostic" "$stdout_log" \
        >>"$summary"

    echo "  exit=$run_code proposed=$proposed committed=$committed patternReference=$pattern_reference lexicalDiagnostic=$lexical_diagnostic"
end

echo
echo "=== scoped pure-control exploratory summary ==="
cat "$summary"
echo
echo "results: $result_root"
