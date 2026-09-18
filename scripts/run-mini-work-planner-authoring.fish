#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/NyamNyam.AuthoringSession/NyamNyam.AuthoringSession.fsproj"
set -l model "$NYAMNYAM_MINI_WORK_PLANNER_MODEL"
set -l variant "$NYAMNYAM_MINI_WORK_PLANNER_VARIANT"

if test -z "$model"
    set model opencode/muse-spark-1.3-contributor-free
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
    echo "Set NYAMNYAM_MINI_WORK_PLANNER_MODEL to another available model if needed." >&2
    exit 1
end

echo "[mini-work-planner] building the maintained authoring session..."
dotnet build "$project" --configuration Release
or exit $status

set -l stamp (date +%Y%m%d-%H%M%S)
set -l result_root "$HOME/.local/share/nyamnyam/mini-work-planner-authoring/$stamp"
mkdir -p "$result_root"
or exit 1

# Reuse the existing isolated bounded-authoring bridge.
set -gx NYAMNYAM_M4_AUTHORING_RESULT_ROOT "$result_root"

set -l repository_revision (git rev-parse HEAD)
set -l opencode_version (opencode --version | string collect)
set -l started_epoch (date +%s)
printf 'repository_revision=%s\nmodel=%s\nvariant=%s\nopencode_version=%s\nstarted_at=%s\n' \
    "$repository_revision" "$model" "$variant" "$opencode_version" (date --iso-8601=seconds) \
    >"$result_root/metadata.txt"

set -l prompt "Work entirely through the maintained NyamNyam AuthoringSession. Do not inspect repository source, tests, snapshot JSON, generated C# source, or any authority outside the maintained protocol. Start from the fresh snapshot and build one pure Mini Work Planner.

Create these types:
1. PrerequisiteState union: Ready, Waiting, Failed.
2. ExecutionState union: NotStarted, Running, Failed, Succeeded.
3. ValidationState union: NotRequired, Pending, Passed, Failed.
4. WorkItem product with prerequisites: PrerequisiteState, execution: ExecutionState, validation: ValidationState, attempts: Int64, maxAttempts: Int64.
5. NextAction union: Start, Wait, Retry, Validate, Complete, NeedsHuman.

Create these pure functions:
- can_retry(attempts: Int64, maxAttempts: Int64) -> Bool, true exactly when attempts < maxAttempts.
- next_action(item: WorkItem) -> NextAction.
- action_label(action: NextAction) -> String, using the lower-case labels start, wait, retry, validate, complete, needs-human.

Initial next_action rules:
- prerequisite Failed -> NeedsHuman.
- prerequisite Waiting -> Wait.
- prerequisite Ready then inspect execution.
- NotStarted -> Start.
- Running -> Wait.
- execution Failed -> call can_retry with WorkItem.attempts/maxAttempts; Retry when true, otherwise NeedsHuman.
- Succeeded then inspect validation.
- validation NotRequired or Passed -> Complete.
- validation Pending -> Validate.
- validation Failed -> NeedsHuman.

Review every proposal fully before commit. After every creation commit, rediscover declarations and use only published TypeId, VariantId, FieldId, FunctionId, and ParameterId values. Never invent production IDs.

After the initial program is committed, focus next_action and perform exactly one maintenance change using simulateReplaceBody, preserving the existing next_action FunctionId and parameter identity. The replacement body must keep all existing rules except: when execution is Succeeded and validation is Failed, call the existing can_retry using the WorkItem attempts/maxAttempts fields and return Retry while budget remains, otherwise NeedsHuman. For simulateReplaceBody use the focused function's current resultNodeId as expectedCurrentResultNodeId and refer to the existing parameter by its published ParameterId. Review the complete base/proposed focus before committing.

Useful protocol syntax not listed in the generic guide:
- Int64 TypeRef is {\"kind\":\"primitive\",\"primitive\":\"int64\"}.
- Int64 comparison recipe is {\"kind\":\"int64LessThan\",\"left\":<recipe>,\"right\":<recipe>}.
- simulateReplaceBody requires id, op, functionId, expectedCurrentResultNodeId, replacement.

Once the initial program and maintenance edit are both committed, restart the maintained session exactly once. Rediscover declarations and confirm all five TypeIds, their variant/field identities, all three FunctionIds, and function ParameterIds remain stable. Confirm executionReadiness is ready. Emit C# with the empty profile and confirm the response succeeds and contains a symbols manifest covering at least the published WorkItem TypeId and next_action FunctionId. Treat emitted C# only as an output artifact, not semantic authority. Finalize exactly once.

In the final response state whether all five types and three functions were committed, whether the one maintenance body replacement was committed, whether any maintained request was rejected/retried, whether restart/re-discovery preserved identities, whether execution readiness was ready, whether empty-profile C# emission and symbol manifest checks succeeded, and any concrete authoring/review friction."

set -l stdout_log "$result_root/agent.jsonl"
set -l stderr_log "$result_root/agent.stderr.log"

echo "[mini-work-planner] running one bounded authoring + maintenance attempt"
opencode --print-logs --log-level INFO run \
    --agent nyamnyam-m4-authoring-eval \
    --model "$model" \
    --variant "$variant" \
    --format json \
    --title "mini-work-planner-$stamp" \
    "$prompt" \
    >"$stdout_log" 2>"$stderr_log"
set -l run_code $status
printf '%s\n' "$run_code" >"$result_root/exit-code.txt"

set -l finished_epoch (date +%s)
set -l events "$result_root/protocol-events.jsonl"
set -l summary "$result_root/summary.tsv"

python3 -c '
import json, os, sys

events_path, summary_path, workspace_path, exit_code, started_epoch, finished_epoch = sys.argv[1:7]
requests = {}
counts = {}
request_bytes = 0
response_bytes = 0
declarations_response_bytes = 0
declarations_response_max_bytes = 0
focus_response_bytes = 0
rejected_requests = 0
unexpected_errors = 0
restart_index = None
maintenance_proposal_id = None
maintenance_owner_id = None
maintenance_committed = False
post_restart_ready = False
manifest_ok = False

tracked_types = {
    "PrerequisiteState",
    "ExecutionState",
    "ValidationState",
    "WorkItem",
    "NextAction",
}
tracked_functions = {
    "can_retry",
    "next_action",
    "action_label",
}

initial_types = {}
initial_functions = {}
initial_parameters = {}
post_restart_types = {}
post_restart_functions = {}
post_restart_parameters = {}

def payload_size(value):
    return len(json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))

def type_identity(value):
    if not isinstance(value, dict):
        return None
    result = {"id": value.get("id"), "displayName": value.get("displayName")}
    kind = value.get("typeKind", {})
    if isinstance(kind, dict) and kind.get("kind") == "product":
        fields = kind.get("fields", [])
        result["fields"] = {
            field.get("displayName"): field.get("id")
            for field in fields
            if isinstance(field, dict)
        }
    elif isinstance(kind, dict) and kind.get("kind") == "union":
        variants = kind.get("variants", [])
        result["variants"] = {}
        for variant in variants:
            if not isinstance(variant, dict):
                continue
            result["variants"][variant.get("displayName")] = {
                "id": variant.get("id"),
                "fields": {
                    field.get("displayName"): field.get("id")
                    for field in variant.get("fields", [])
                    if isinstance(field, dict)
                },
            }
    return result

if os.path.exists(events_path):
    with open(events_path, encoding="utf-8") as stream:
        events = [json.loads(line) for line in stream if line.strip()]
else:
    events = []

for index, event in enumerate(events):
    kind = event.get("kind")
    if kind == "restart":
        restart_index = index
        continue
    if kind == "request":
        request = event.get("request", {})
        request_id = request.get("id")
        op = request.get("op")
        request_bytes += payload_size(request)
        if isinstance(request_id, str) and isinstance(op, str):
            requests[request_id] = (op, request, index)
            counts[op] = counts.get(op, 0) + 1
        continue
    if kind != "response":
        continue

    response = event.get("response", {})
    response_bytes += payload_size(response)
    if not isinstance(response, dict):
        continue
    request_id = response.get("id")
    info = requests.get(request_id)
    if not info:
        continue
    op, request, request_index = info
    ok = response.get("ok") is True
    result = response.get("result")

    response_size = payload_size(response)
    if op == "declarations":
        declarations_response_bytes += response_size
        declarations_response_max_bytes = max(declarations_response_max_bytes, response_size)
    elif op == "focusFunction":
        focus_response_bytes += response_size

    if not ok:
        rejected_requests += 1
        unexpected_errors += 1
        continue

    if op == "simulateCreateType" and isinstance(result, dict):
        review = result.get("review", {})
        created = review.get("createdType", {}) if isinstance(review, dict) else {}
        name = created.get("displayName") if isinstance(created, dict) else None
        if name in tracked_types:
            initial_types[name] = type_identity(created)

    if op == "simulateCreateFunction" and isinstance(result, dict):
        review = result.get("review", {})
        created = review.get("createdFunction", {}) if isinstance(review, dict) else {}
        name = created.get("displayName") if isinstance(created, dict) else None
        if name in tracked_functions:
            initial_functions[name] = created.get("id")
            params = created.get("parameters", [])
            if isinstance(params, list):
                initial_parameters[name] = {
                    param.get("displayName"): param.get("id")
                    for param in params
                    if isinstance(param, dict)
                }

    if op == "simulateReplaceBody" and isinstance(result, dict):
        review = result.get("review", {})
        if isinstance(review, dict) and review.get("kind") == "replaceBody":
            diff = review.get("diff", {})
            owner = diff.get("ownerFunctionId") if isinstance(diff, dict) else None
            if owner == initial_functions.get("next_action"):
                maintenance_proposal_id = result.get("proposalId")
                maintenance_owner_id = owner

    if op == "commit" and maintenance_proposal_id is not None:
        if request.get("proposalId") == maintenance_proposal_id:
            maintenance_committed = True

    if restart_index is not None and request_index > restart_index and op == "declarations" and isinstance(result, dict):
        for item in result.get("types", []):
            if isinstance(item, dict) and item.get("displayName") in tracked_types:
                post_restart_types[item["displayName"]] = type_identity(item)
        for item in result.get("functions", []):
            if not isinstance(item, dict) or item.get("displayName") not in tracked_functions:
                continue
            name = item["displayName"]
            post_restart_functions[name] = item.get("id")
            params = item.get("parameters", [])
            if isinstance(params, list):
                post_restart_parameters[name] = {
                    param.get("displayName"): param.get("id")
                    for param in params
                    if isinstance(param, dict)
                }

    if restart_index is not None and request_index > restart_index and op == "executionReadiness":
        if isinstance(result, dict) and result.get("kind") == "ready":
            post_restart_ready = True

    if restart_index is not None and request_index > restart_index and op == "emitCSharp":
        if isinstance(result, dict):
            symbols = result.get("symbols", {})
            if isinstance(symbols, dict):
                type_ids = {
                    item.get("id")
                    for item in symbols.get("types", [])
                    if isinstance(item, dict)
                }
                function_ids = {
                    item.get("id")
                    for item in symbols.get("functions", [])
                    if isinstance(item, dict)
                }
                work_item_id = (initial_types.get("WorkItem") or {}).get("id")
                next_action_id = initial_functions.get("next_action")
                manifest_ok = work_item_id in type_ids and next_action_id in function_ids

identity_preserved = (
    tracked_types.issubset(initial_types.keys())
    and tracked_types.issubset(post_restart_types.keys())
    and all(post_restart_types.get(name) == initial_types.get(name) for name in tracked_types)
    and tracked_functions.issubset(initial_functions.keys())
    and tracked_functions.issubset(post_restart_functions.keys())
    and all(post_restart_functions.get(name) == initial_functions.get(name) for name in tracked_functions)
    and all(post_restart_parameters.get(name) == initial_parameters.get(name) for name in tracked_functions)
)
maintenance_owner_preserved = (
    maintenance_owner_id is not None
    and maintenance_owner_id == initial_functions.get("next_action")
    and post_restart_functions.get("next_action") == initial_functions.get("next_action")
)

try:
    duration_seconds = max(0, int(finished_epoch) - int(started_epoch))
except ValueError:
    duration_seconds = -1

row = {
    "exit": exit_code,
    "workspace": "true" if os.path.isfile(workspace_path) else "false",
    "duration_seconds": str(duration_seconds),
    "simulate_type": str(counts.get("simulateCreateType", 0)),
    "simulate_function": str(counts.get("simulateCreateFunction", 0)),
    "replace_body_attempts": str(counts.get("simulateReplaceBody", 0)),
    "commits": str(counts.get("commit", 0)),
    "declarations_calls": str(counts.get("declarations", 0)),
    "focus_calls": str(counts.get("focusFunction", 0)),
    "request_bytes": str(request_bytes),
    "response_bytes": str(response_bytes),
    "declarations_response_bytes": str(declarations_response_bytes),
    "declarations_response_max_bytes": str(declarations_response_max_bytes),
    "focus_response_bytes": str(focus_response_bytes),
    "maintenance_committed": "true" if maintenance_committed else "false",
    "maintenance_owner_preserved": "true" if maintenance_owner_preserved else "false",
    "restart": "true" if restart_index is not None else "false",
    "identity_preserved": "true" if identity_preserved else "false",
    "execution_ready": "true" if post_restart_ready else "false",
    "symbol_manifest": "true" if manifest_ok else "false",
    "rejected_requests": str(rejected_requests),
    "unexpected_errors": str(unexpected_errors),
}

with open(summary_path, "w", encoding="utf-8") as stream:
    stream.write("\t".join(row.keys()) + "\n")
    stream.write("\t".join(row.values()) + "\n")

print("=== Mini Work Planner authoring summary ===")
print("\t".join(row.keys()))
print("\t".join(row.values()))
' "$events" "$summary" "$result_root/workspace.json" "$run_code" "$started_epoch" "$finished_epoch"
set -l summarize_code $status

printf 'finished_at=%s\n' (date --iso-8601=seconds) >>"$result_root/metadata.txt"

echo
echo "results: $result_root"

if test $summarize_code -ne 0
    exit $summarize_code
end
exit $run_code
