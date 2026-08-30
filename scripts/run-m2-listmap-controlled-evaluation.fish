#!/usr/bin/env fish

set -l condition_sha 23bf09d87c03b9c15dd4640b662a05b24aa5e330
set -l condition_branch eval/m2-listmap-condition-v1-20260831
set -l corpus_sha a2ffb5615d37d44bbd910c4f47c36136c31cdce4
set -l corpus_branch eval/m2-listmap-agent-corpus-v1-20260831
set -l orchestrator_branch eval/m2-listmap-orchestrator-v1-20260831
set -l protocol m2-listmap-structural-v1
set -l model openai/gpt-5.6-terra
set -l variant high
set -l expected_opencode 1.18.18
set -l prompt_revision m2-listmap-corpus-v1-seed-v1
set -l corpus_relative evaluation/m2-listmap-agent-corpus-v1/corpus.json

set -l stage ""
set -l dry_run 0

for argument in $argv
    switch $argument
        case A a
            if test -n "$stage"; and test "$stage" != A
                echo "ERROR: choose exactly one stage: A or B." >&2
                exit 2
            end
            set stage A
        case B b
            if test -n "$stage"; and test "$stage" != B
                echo "ERROR: choose exactly one stage: A or B." >&2
                exit 2
            end
            set stage B
        case --dry-run
            set dry_run 1
        case '*'
            echo "ERROR: unsupported argument '$argument'." >&2
            echo "Usage: fish scripts/run-m2-listmap-controlled-evaluation.fish <A|B> [--dry-run]" >&2
            exit 2
    end
end

if test -z "$stage"
    echo "ERROR: stage A or B is required." >&2
    echo "Usage: fish scripts/run-m2-listmap-controlled-evaluation.fish <A|B> [--dry-run]" >&2
    exit 2
end

set -l repo_root (git rev-parse --show-toplevel 2>/dev/null)
if test -z "$repo_root"
    echo "ERROR: run from the nyamnyam-lang repository." >&2
    exit 1
end
cd "$repo_root"
or exit 1

set -l dirty (git status --porcelain --untracked-files=all)
if test (count $dirty) -ne 0
    echo "ERROR: operator worktree must be clean." >&2
    printf '%s\n' $dirty >&2
    exit 1
end

function require_remote_freeze_ref --argument-names branch expected
    set -l actual (git rev-parse "refs/remotes/origin/$branch" 2>/dev/null)
    if test -z "$actual"
        echo "ERROR: origin/$branch is unavailable locally; run git fetch origin first." >&2
        return 1
    end
    if test "$actual" != "$expected"
        echo "ERROR: origin/$branch moved: expected $expected, got $actual." >&2
        return 1
    end
end

require_remote_freeze_ref "$condition_branch" "$condition_sha"
or exit 1
require_remote_freeze_ref "$corpus_branch" "$corpus_sha"
or exit 1

for revision in "$condition_sha" "$corpus_sha"
    git cat-file -e "$revision^{commit}" 2>/dev/null
    or begin
        echo "ERROR: required frozen commit $revision is unavailable locally." >&2
        exit 1
    end
end

set -l frozen_paths \
    evaluation/m2-listmap-agent-corpus-v1 \
    tests/NyamNyam.Core.Tests/M2ListMapStructuralEvaluationCorpus.fs \
    tools/NyamNyam.EvaluationFixtureExport/Program.fs \
    tools/NyamNyam.EvaluationFixtureExport/NyamNyam.EvaluationFixtureExport.fsproj \
    src/NyamNyam.Core/AgentListMapStructuralWorkflow.fs \
    tools/NyamNyam.AgentEval.ListMapStructural/Protocol.fs

git diff --quiet "$corpus_sha" "$condition_sha" -- $frozen_paths
or begin
    echo "ERROR: frozen corpus/protocol/workflow inputs differ between corpus and condition revisions." >&2
    exit 1
end

set -l current_revision (git rev-parse HEAD)
if test $dry_run -eq 0
    set -l orchestrator_revision (git rev-parse "refs/remotes/origin/$orchestrator_branch" 2>/dev/null)
    if test -z "$orchestrator_revision"
        echo "ERROR: origin/$orchestrator_branch is unavailable; actual scored runs require the frozen orchestrator branch." >&2
        exit 1
    end
    if test "$current_revision" != "$orchestrator_revision"
        echo "ERROR: scored runs must execute from frozen orchestrator revision $orchestrator_revision; current HEAD is $current_revision." >&2
        exit 1
    end
end

set -l stage_name (string lower $stage)
set -l stamp (date +%Y%m%d-%H%M%S)
set -l stage_root ""
if test $dry_run -eq 1
    set stage_root "/tmp/m2-listmap-orchestrator-dry-run-$stage_name-$stamp"
else
    set stage_root "$HOME/.local/share/nyamnyam/m2-listmap-controlled/stage-$stage_name-$stamp"
end
mkdir -p "$stage_root"
or exit 1
mkdir -p "$stage_root/prompts"
or exit 1

set -l corpus_path "$stage_root/corpus.json"
git show "$condition_sha:$corpus_relative" >"$corpus_path"
or begin
    echo "ERROR: could not materialize frozen corpus from condition revision." >&2
    exit 1
end

set -l task_ids
for index in (seq 1 10)
    set -a task_ids "M2-L$index"
end

set -l attempts
if test "$stage" = A
    set attempts a1
else
    set attempts a2 a3
end

function task_field --argument-names corpus task expression
    python3 -c '
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    corpus = json.load(f)
task = next(item for item in corpus["tasks"] if item["id"] == sys.argv[2])
expression = sys.argv[3]
if expression == "fixture":
    value = task["fixture"]
elif expression == "prompt":
    value = task["prompt"]
elif expression == "target":
    value = corpus["fixtures"][task["fixture"]]["targetFunctionId"]
elif expression == "source":
    value = task["request"]["sourceParameterId"]
elif expression == "mapper":
    value = task["request"]["mapperFunctionId"]
else:
    raise SystemExit("unsupported task field")
print(value)
' "$corpus" "$task" "$expression"
end

set -l planned_tsv "$stage_root/planned-attempts.tsv"
printf 'task\tattempt\tfixture\tprompt_sha256\n' >"$planned_tsv"

for task_id in $task_ids
    set -l fixture (task_field "$corpus_path" "$task_id" fixture)
    set -l frozen_prompt (task_field "$corpus_path" "$task_id" prompt)
    set -l target_id (task_field "$corpus_path" "$task_id" target)
    set -l source_id (task_field "$corpus_path" "$task_id" source)
    set -l mapper_id (task_field "$corpus_path" "$task_id" mapper)
    if test -z "$fixture"; or test -z "$frozen_prompt"; or test -z "$target_id"; or test -z "$source_id"; or test -z "$mapper_id"
        echo "ERROR: failed to resolve complete frozen task binding for $task_id." >&2
        exit 1
    end

    set -l prompt "$frozen_prompt

Seed stable semantic IDs:
targetFunctionId: $target_id
sourceParameterId: $source_id
mapperFunctionId: $mapper_id"
    set -l prompt_path "$stage_root/prompts/$task_id.txt"
    printf '%s' "$prompt" >"$prompt_path"
    set -l prompt_hash (sha256sum "$prompt_path" | string split ' ' | head -n 1)

    for attempt in $attempts
        printf '%s\t%s\t%s\t%s\n' "$task_id" "$attempt" "$fixture" "$prompt_hash" >>"$planned_tsv"
    end
end

set -l attempt_list (string join ',' $attempts)
set -l metadata "$stage_root/stage-metadata.txt"
printf '%s\n' \
    "stage=$stage" \
    "dry_run=$dry_run" \
    "corpus_id=m2-listmap-agent-corpus-v1" \
    "corpus_revision=$corpus_sha" \
    "condition_revision=$condition_sha" \
    "operator_revision=$current_revision" \
    "protocol=$protocol" \
    "model=$model" \
    "variant=$variant" \
    "expected_opencode=$expected_opencode" \
    "prompt_revision=$prompt_revision" \
    "attempts=$attempt_list" \
    "task_count=10" >"$metadata"

if test $dry_run -eq 1
    echo "=== M2 ListMap controlled evaluation dry run ==="
    cat "$metadata"
    echo
    cat "$planned_tsv"
    echo
    echo "No OpenCode session was started."
    echo "dry-run-root: $stage_root"
    exit 0
end

set -l oc_version (opencode --version | string trim)
if test "$oc_version" != "$expected_opencode"
    echo "ERROR: OpenCode version changed: expected $expected_opencode, got $oc_version." >&2
    exit 1
end
if not opencode models | string match -q -- "*$model*"
    echo "ERROR: required model $model is unavailable." >&2
    exit 1
end

set -l worktree_parent (mktemp -d "/tmp/nyamnyam-m2-listmap-$stage_name-XXXXXX")
or exit 1
set -l condition_worktree "$worktree_parent/condition"

set -g __nyamnyam_orchestrator_repo "$repo_root"
set -g __nyamnyam_condition_worktree "$condition_worktree"
set -g __nyamnyam_worktree_parent "$worktree_parent"
function __nyamnyam_cleanup --on-event fish_exit
    if test -n "$__nyamnyam_condition_worktree"; and test -d "$__nyamnyam_condition_worktree"
        git -C "$__nyamnyam_orchestrator_repo" worktree remove --force "$__nyamnyam_condition_worktree" >/dev/null 2>&1
    end
    if test -n "$__nyamnyam_worktree_parent"; and test -d "$__nyamnyam_worktree_parent"
        rm -rf "$__nyamnyam_worktree_parent"
    end
end

git worktree add --detach "$condition_worktree" "$condition_sha"
or begin
    echo "ERROR: could not create detached condition worktree." >&2
    exit 1
end

pushd "$condition_worktree" >/dev/null
or exit 1
dotnet restore NyamNyam.slnx
set -l restore_code $status
popd >/dev/null
if test $restore_code -ne 0
    echo "ERROR: restore failed in frozen condition worktree." >&2
    exit $restore_code
end

set -l summary_tsv "$stage_root/attempt-summary.tsv"
printf 'task\tattempt\topencode_exit\trun_count\tterminal\tmetadata_ok\toracle_passed\tzero_churn\tprotocol_errors\tcommits\trun_dir\n' >"$summary_tsv"

for task_id in $task_ids
    set -l fixture (task_field "$corpus_path" "$task_id" fixture)
    set -l prompt_path "$stage_root/prompts/$task_id.txt"
    set -l prompt (string collect <"$prompt_path")

    for attempt in $attempts
        set -l attempt_dir "$stage_root/attempts/$task_id-$attempt"
        set -l artifact_root "$attempt_dir/artifacts"
        mkdir -p "$artifact_root"
        or exit 1
        set -l stdout_log "$attempt_dir/opencode.jsonl"
        set -l stderr_log "$attempt_dir/opencode.stderr.log"

        set -gx NYAMNYAM_LISTMAP_EVAL_TASK_ID "$task_id"
        set -gx NYAMNYAM_LISTMAP_EVAL_FIXTURE "$fixture"
        set -gx NYAMNYAM_LISTMAP_EVAL_ARTIFACT_ROOT "$artifact_root"
        set -gx NYAMNYAM_LISTMAP_EVAL_MODEL "$model"
        set -gx NYAMNYAM_LISTMAP_EVAL_PROVIDER_RUNTIME opencode
        set -gx NYAMNYAM_LISTMAP_EVAL_REASONING_CONFIG "$variant"
        set -gx NYAMNYAM_LISTMAP_EVAL_OPENCODE_VERSION "$oc_version"
        set -gx NYAMNYAM_LISTMAP_EVAL_PROMPT_REVISION "$prompt_revision"
        set -gx NYAMNYAM_LISTMAP_EVAL_TOOL_REVISION "$condition_sha"

        echo "=== $task_id / $attempt ==="
        pushd "$condition_worktree" >/dev/null
        opencode --print-logs --log-level DEBUG run \
            --agent nyamnyam-listmap-eval \
            --model "$model" \
            --variant "$variant" \
            --format json \
            --title "m2-listmap-$stage_name-$task_id-$attempt-$stamp" \
            "$prompt" \
            >"$stdout_log" 2>"$stderr_log"
        set -l run_code $status
        popd >/dev/null
        printf '%s\n' "$run_code" >"$attempt_dir/opencode.exit-code.txt"

        set -l runs (find "$artifact_root" -mindepth 1 -maxdepth 1 -type d | sort)
        set -l run_count (count $runs)
        set -l terminal false
        set -l metadata_ok false
        set -l oracle_passed unavailable
        set -l zero_churn unavailable
        set -l protocol_errors unavailable
        set -l commits unavailable
        set -l run_dir ""

        if test $run_count -eq 1
            set run_dir $runs[1]
            set -l state_path "$run_dir/run-state.json"
            set -l run_metadata_path "$run_dir/run-metadata.json"
            set -l metrics_path "$run_dir/metrics.json"
            set -l events_path "$run_dir/events.jsonl"
            set -l harness_events_path "$run_dir/harness-events.jsonl"

            set -l durable 1
            for required_path in "$state_path" "$run_metadata_path" "$metrics_path" "$events_path" "$harness_events_path"
                if not test -s "$required_path"
                    set durable 0
                end
            end

            if test $durable -eq 1
                set terminal (python3 -c 'import json,sys; print(str(json.load(open(sys.argv[1])).get("state") == "terminal").lower())' "$state_path")
                set metadata_ok (python3 -c '
import json, sys
with open(sys.argv[1]) as f: d=json.load(f)
expected={
 "corpusRevision":sys.argv[2],
 "protocolVersion":sys.argv[3],
 "model":sys.argv[4],
 "reasoningConfig":sys.argv[5],
 "openCodeVersion":sys.argv[6],
 "promptRevision":sys.argv[7],
 "toolRevision":sys.argv[8],
 "repositoryRevision":sys.argv[8],
}
print(str(all(d.get(k)==v for k,v in expected.items())).lower())
' "$run_metadata_path" "$corpus_sha" "$protocol" "$model" "$variant" "$oc_version" "$prompt_revision" "$condition_sha")
                set oracle_passed (python3 -c 'import json,sys; print(str(json.load(open(sys.argv[1])).get("oraclePassed")).lower())' "$metrics_path")
                set zero_churn (python3 -c 'import json,sys; print(str(json.load(open(sys.argv[1])).get("zeroUnrelatedSemanticChurn")).lower())' "$metrics_path")
                set protocol_errors (python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("protocolErrors","unavailable"))' "$metrics_path")
                set commits (python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("commits","unavailable"))' "$metrics_path")
            end
        end

        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
            "$task_id" "$attempt" "$run_code" "$run_count" "$terminal" "$metadata_ok" \
            "$oracle_passed" "$zero_churn" "$protocol_errors" "$commits" "$run_dir" >>"$summary_tsv"

        echo "exit=$run_code runs=$run_count terminal=$terminal metadata=$metadata_ok oracle=$oracle_passed"
    end
end

set -l condition_valid_count (awk -F '\t' 'NR>1 && $3==0 && $4==1 && $5=="true" && $6=="true" {count++} END {print count+0}' "$summary_tsv")
set -l total_attempts (math (count $task_ids) '*' (count $attempts))
set -l oracle_pass_count (awk -F '\t' 'NR>1 && $7=="true" {count++} END {print count+0}' "$summary_tsv")

printf '%s\n' \
    "attempted_sessions=$total_attempts" \
    "terminal_pinned_attempts=$condition_valid_count" \
    "machine_oracle_passes=$oracle_pass_count" >>"$metadata"

set -l archive "/tmp/m2-listmap-condition-v1-stage-$stage_name-$stamp.tar.gz"
tar -C "$stage_root" -czf "$archive" .
or begin
    echo "ERROR: failed to package stage evidence." >&2
    exit 1
end
set -l archive_sha (sha256sum "$archive" | string split ' ' | head -n 1)
set -l archive_bytes (stat -c %s "$archive")

printf '%s\n' \
    "archive=$archive" \
    "archive_bytes=$archive_bytes" \
    "archive_sha256=$archive_sha" >>"$metadata"

# Remove the detached condition worktree only after all evidence is packaged.
git -C "$repo_root" worktree remove --force "$condition_worktree" >/dev/null 2>&1
set -g __nyamnyam_condition_worktree ""
rm -rf "$worktree_parent"
set -g __nyamnyam_worktree_parent ""


echo
echo "=== M2 LISTMAP STAGE $stage COMPLETE ==="
echo "attempts:                $total_attempts"
echo "terminal+pinned:         $condition_valid_count"
echo "machine oracle passes:   $oracle_pass_count"
echo "stage-root:              $stage_root"
echo "summary:                 $summary_tsv"
echo "archive:                 $archive"
echo "archive-bytes:           $archive_bytes"
echo "archive-sha256:          $archive_sha"
