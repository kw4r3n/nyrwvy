#!/usr/bin/env fish

set -l model openai/gpt-5.6-terra
set -l variant high
set -l head (git rev-parse HEAD 2>/dev/null)

if test -z "$head"
    echo "ERROR: run from the nyrwvy repository."
    exit 1
end

set -l dirty (git status --porcelain --untracked-files=all)
if test (count $dirty) -ne 0
    echo "ERROR: worktree must be clean for the ListMap runtime preflight."
    printf '%s\n' $dirty
    exit 1
end

if not opencode models | string match -q -- "*$model*"
    echo "ERROR: required model $model is unavailable."
    exit 1
end

set -l oc_version (opencode --version | string trim)
if test -z "$oc_version"
    echo "ERROR: could not capture OpenCode version."
    exit 1
end

set -l stamp (date +%Y%m%d-%H%M%S)
set -l root "$HOME/.local/share/nyrwvy/m2-listmap-runtime-preflight/$stamp"
set -l artifact_root "$root/artifacts"
set -l stdout_log "$root/opencode.jsonl"
set -l stderr_log "$root/opencode.stderr.log"
set -l archive "/tmp/m2-listmap-runtime-preflight-$stamp.tar.gz"
mkdir -p "$artifact_root"
or exit 1

function fail_preflight --argument-names message code root archive stdout_log stderr_log
    echo "ERROR: $message"
    echo "stdout: $stdout_log"
    echo "stderr: $stderr_log"
    if test -d "$root"
        tar -C "$root" -czf "$archive" . 2>/dev/null
        if test -f "$archive"
            echo "archive: $archive"
        end
    end
    return $code
end

set -gx NYRWVY_LISTMAP_EVAL_TASK_ID M2-L1
set -gx NYRWVY_LISTMAP_EVAL_FIXTURE m2-listmap-ready
set -gx NYRWVY_LISTMAP_EVAL_ARTIFACT_ROOT "$artifact_root"
set -gx NYRWVY_LISTMAP_EVAL_MODEL "$model"
set -gx NYRWVY_LISTMAP_EVAL_PROVIDER_RUNTIME opencode
set -gx NYRWVY_LISTMAP_EVAL_REASONING_CONFIG "$variant"
set -gx NYRWVY_LISTMAP_EVAL_OPENCODE_VERSION "$oc_version"
set -gx NYRWVY_LISTMAP_EVAL_PROMPT_REVISION m2-listmap-condition-v1-preflight-v1
set -gx NYRWVY_LISTMAP_EVAL_TOOL_REVISION "$head"

set -l prompt "Harness preflight only; this is not a corpus attempt. Use the ListMap evaluation tools only. Start the configured session exactly once, send {\"command\":\"describeV1\"} exactly once through the request tool, then finalize exactly once. Do not simulate, commit, reject, or otherwise attempt any semantic change."

echo "=== M2 ListMap OpenCode runtime preflight ==="
echo "head:     $head"
echo "OpenCode: $oc_version"
echo "model:    $model"
echo "variant:  $variant"
echo "output:   $root"
echo

opencode --print-logs --log-level DEBUG run \
    --agent nyrwvy-listmap-eval \
    --model "$model" \
    --variant "$variant" \
    --format json \
    --title "m2-listmap-runtime-preflight-$stamp" \
    "$prompt" \
    >"$stdout_log" 2>"$stderr_log"
set -l code $status

if test $code -ne 0
    fail_preflight "OpenCode ListMap preflight exited with $code." $code "$root" "$archive" "$stdout_log" "$stderr_log"
    exit $status
end

set -l runs (find "$artifact_root" -mindepth 1 -maxdepth 1 -type d | sort)
if test (count $runs) -ne 1
    fail_preflight "expected exactly one ListMap harness run directory, found "(count $runs)"." 1 "$root" "$archive" "$stdout_log" "$stderr_log"
    exit $status
end

set -l run_dir $runs[1]
set -l events "$run_dir/events.jsonl"
set -l state "$run_dir/run-state.json"
set -l metadata "$run_dir/run-metadata.json"
set -l metrics "$run_dir/metrics.json"

for path in "$events" "$state" "$metadata" "$metrics"
    if not test -s "$path"
        fail_preflight "expected durable ListMap artifact is missing or empty: $path" 1 "$root" "$archive" "$stdout_log" "$stderr_log"
        exit $status
    end
end

for tool_name in nyrwvy_listmap_eval_start nyrwvy_listmap_eval_request nyrwvy_listmap_eval_finalize
    set -l tool_count (grep -F -c '"tool":"'$tool_name'"' "$stdout_log")
    if test "$tool_count" -ne 1
        fail_preflight "expected exactly one real $tool_name tool event, found $tool_count." 1 "$root" "$archive" "$stdout_log" "$stderr_log"
        exit $status
    end
end

if not grep -Fq '"request":"{\u0022command\u0022:\u0022describeV1\u0022}"' "$events"
    fail_preflight "ListMap runner event log does not contain the exact describeV1 request." 1 "$root" "$archive" "$stdout_log" "$stderr_log"
    exit $status
end

for metric_expectation in '"toolCalls":1' '"simulations":0' '"commitAttempts":0' '"commits":0' '"eventCount":1' '"protocolErrors":0'
    if not grep -Fq "$metric_expectation" "$metrics"
        fail_preflight "ListMap runner metrics violate preflight expectation $metric_expectation." 1 "$root" "$archive" "$stdout_log" "$stderr_log"
        exit $status
    end
end

for metadata_expectation in \
    '"corpusRevision":"a2ffb5615d37d44bbd910c4f47c36136c31cdce4"' \
    '"protocolVersion":"m2-listmap-structural-v1"' \
    '"model":"openai/gpt-5.6-terra"' \
    '"reasoningConfig":"high"'
    if not grep -Fq "$metadata_expectation" "$metadata"
        fail_preflight "ListMap run metadata is missing frozen expectation $metadata_expectation." 1 "$root" "$archive" "$stdout_log" "$stderr_log"
        exit $status
    end
end

if not grep -Eq '"state"[[:space:]]*:[[:space:]]*"terminal"' "$state"
    fail_preflight "ListMap run-state is not terminal." 1 "$root" "$archive" "$stdout_log" "$stderr_log"
    exit $status
end

tar -C "$root" -czf "$archive" .
or exit 1

echo
cat "$state"
echo
echo "=== LISTMAP RUNTIME PREFLIGHT PASSED ==="
echo "run-dir: $run_dir"
echo "stdout:  $stdout_log"
echo "stderr:  $stderr_log"
echo "archive: $archive"
