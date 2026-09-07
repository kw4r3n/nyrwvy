#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/NyamNyam.AgentEval.PureExpression/NyamNyam.AgentEval.PureExpression.fsproj"
set -l model "$NYAMNYAM_PURE_CONTROL_EVAL_MODEL"
set -l variant "$NYAMNYAM_PURE_CONTROL_EVAL_VARIANT"

function analyze_log --argument-names stdout_log
    python3 -c '
import json, sys

path = sys.argv[1]
statuses = set()
roles = set()
codes = set()
seen_strings = set()
lexical_codes = {
    "PureExpressionPatternScopeDepthInvalid",
    "PureExpressionPatternScopeNotFound",
    "PureExpressionPatternSlotNotFound",
}

def collect(value):
    if isinstance(value, dict):
        status = value.get("status")
        role = value.get("role")
        code = value.get("code")
        if isinstance(status, str):
            statuses.add(status)
        if isinstance(role, str):
            roles.add(role)
        if isinstance(code, str):
            codes.add(code)
        for item in value.values():
            collect(item)
    elif isinstance(value, list):
        for item in value:
            collect(item)
    elif isinstance(value, str):
        if "PatternReference" in value:
            roles.add("PatternReference")
        for code in lexical_codes:
            if code in value:
                codes.add(code)
        text = value.strip()
        if text in seen_strings or not text or text[0] not in "{[":
            return
        seen_strings.add(text)
        try:
            nested = json.loads(text)
        except json.JSONDecodeError:
            return
        collect(nested)

with open(path, encoding="utf-8") as stream:
    for line in stream:
        line = line.strip()
        if not line:
            continue
        try:
            collect(json.loads(line))
        except json.JSONDecodeError:
            collect(line)

print("true" if "proposed" in statuses else "false")
print("true" if "committed" in statuses else "false")
print("true" if "PatternReference" in roles else "false")
lexical = sorted(codes & lexical_codes)
print(",".join(lexical) if lexical else "none")
' "$stdout_log"
end

function summarize_results --argument-names result_root
    set -l task_ids PCF-E1 PCF-E2 PCF-E3 PCF-E4

    if not test -d "$result_root"
        echo "ERROR: result directory '$result_root' does not exist." >&2
        return 1
    end

    set -l previous_summary "$result_root/summary.tsv"
    set -l next_summary (mktemp "$result_root/summary.XXXXXX")
    or return 1
    printf 'task\texit\tproposed\tcommitted\tpattern_reference\tlexical_diagnostic\tlog\n' >"$next_summary"

    for task_id in $task_ids
        set -l stdout_log "$result_root/$task_id.jsonl"
        if not test -f "$stdout_log"
            echo "ERROR: missing log '$stdout_log'." >&2
            rm -f "$next_summary"
            return 1
        end

        set -l run_code unknown
        set -l exit_path "$result_root/$task_id.exit-code.txt"
        if test -f "$exit_path"
            set run_code (string trim <"$exit_path")
        else if test -f "$previous_summary"
            set -l previous_code (awk -F '\t' -v task="$task_id" 'NR > 1 && $1 == task { print $2; exit }' "$previous_summary")
            if test -n "$previous_code"
                set run_code "$previous_code"
            end
        end

        set -l indicators (analyze_log "$stdout_log")
        or begin
            rm -f "$next_summary"
            return 1
        end

        set -l proposed $indicators[1]
        set -l committed $indicators[2]
        set -l pattern_reference $indicators[3]
        set -l lexical_diagnostic $indicators[4]

        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
            "$task_id" "$run_code" "$proposed" "$committed" "$pattern_reference" "$lexical_diagnostic" "$stdout_log" \
            >>"$next_summary"

        echo "  $task_id exit=$run_code proposed=$proposed committed=$committed patternReference=$pattern_reference lexicalDiagnostic=$lexical_diagnostic"
    end

    mv "$next_summary" "$previous_summary"
    or return 1

    echo
    echo "=== scoped pure-control exploratory summary ==="
    cat "$previous_summary"
    echo
    echo "results: $result_root"
end

if test (count $argv) -gt 0
    if test (count $argv) -ne 2; or test "$argv[1]" != --summarize
        echo "Usage: ./scripts/run-pure-control-flow-exploratory.fish [--summarize <existing-result-directory>]" >&2
        exit 2
    end
    summarize_results (path resolve "$argv[2]")
    exit $status
end

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
    printf '%s\n' "$run_code" >"$result_root/$task_id.exit-code.txt"
end

summarize_results "$result_root"
