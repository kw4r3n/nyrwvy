#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/NyamNyam.AuthoringSession/NyamNyam.AuthoringSession.fsproj"
set -l model "$NYAMNYAM_M6_AUTHORING_EVAL_MODEL"
set -l variant "$NYAMNYAM_M6_AUTHORING_EVAL_VARIANT"

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
    echo "Set NYAMNYAM_M6_AUTHORING_EVAL_MODEL to another available model if needed." >&2
    exit 1
end

echo "[m6-authoring-eval] building the maintained authoring session..."
dotnet build "$project" --configuration Release
or exit $status

set -l stamp (date +%Y%m%d-%H%M%S)
set -l result_root "$HOME/.local/share/nyamnyam/m6-medium-scale-authoring-exploratory/$stamp"
mkdir -p "$result_root"
or exit 1
# Reuse the existing bounded authoring bridge and its isolated result-root contract.
set -gx NYAMNYAM_M4_AUTHORING_RESULT_ROOT "$result_root"

set -l repository_revision (git rev-parse HEAD)
set -l opencode_version (opencode --version | string collect)
set -l started_epoch (date +%s)
printf 'repository_revision=%s\nmodel=%s\nvariant=%s\nopencode_version=%s\nstarted_at=%s\n' \
    "$repository_revision" "$model" "$variant" "$opencode_version" (date --iso-8601=seconds) \
    >"$result_root/metadata.txt"

set -l prompt "Work entirely through the maintained NyamNyam authoring session. This is one bounded medium-scale authoring/evolution probe; do not inspect repository source or use any authority outside the maintained protocol. Start from the fresh snapshot. Create ValidationOutcome as a union with Accepted(message: String) and Rejected(reason: String). Create ConsoleOutput. Create Console.write_line(text: String) -> Unit associated with ConsoleOutput. Create a pure classify(input: String) -> ValidationOutcome that returns Accepted(\"accepted\") when input contains @ and Rejected(\"rejected\") otherwise. Then create five pure String -> ValidationOutcome entry-point functions named validate_signup, validate_profile, validate_recovery, validate_invite, and validate_contact. Each must preserve its own String parameter and directly call the published classify function. Create process(input: String, console: Console) -> Unit requiring ConsoleOutput; it must directly call classify, match both ValidationOutcome variants, and send the bound message/reason to write_line. Review and commit every proposal and rediscover published IDs rather than inventing them. After the initial program is committed, create and commit a product type ValidationRequest with email: String. Use simulateParameterTypeMigration to change the existing classify input parameter from String to ValidationRequest while preserving the existing classify FunctionId and input ParameterId. Its replacement body must read ValidationRequest.email and retain the same contains-@ Accepted/Rejected behavior. Supply the complete caller rewrite set in that one migration proposal: all five validate_* functions and process must keep their existing FunctionIds and signatures, construct ValidationRequest from their existing String input, pass it to the migrated classify, and otherwise retain their behavior. Review the migration parameterChange, complete affectedCallSites, and bodyRewrites before committing it. After commit, rediscover declarations and verify classify's FunctionId/input ParameterId continuity and ValidationRequest input type, plus the existing IDs of all six callers. Restart the maintained session exactly once, rediscover the persisted state and identities, confirm execution readiness, confirm empty-profile C# emission fails specifically with MissingCapabilityBinding, then emit successfully with the explicit System.Console.WriteLine binding. Finalize after these checks. In your final response report every rejected request/retry and any concrete friction in discovery, review, migration, or payload size that you noticed."

set -l stdout_log "$result_root/agent.jsonl"
set -l stderr_log "$result_root/agent.stderr.log"

echo "[m6-authoring-eval] running one bounded medium-scale exploratory attempt"
opencode --print-logs --log-level INFO run \
    --agent nyamnyam-m4-authoring-eval \
    --model "$model" \
    --variant "$variant" \
    --format json \
    --title "m6-medium-scale-authoring-$stamp" \
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
restart_index = None
unexpected_errors = 0
rejected_requests = 0
execution_ready = False
missing_binding = False
console_emit = False
rediscovered = False

initial_function_ids = {}
initial_parameter_ids = {}
validation_request_type_id = None
migration_proposal_id = None
migration_function_id = None
migration_parameter_id = None
migration_accepted = False
migration_committed = False
affected_calls = None
body_rewrites = None
post_restart_function_ids = {}
post_restart_parameter_ids = {}
post_restart_classify_type_id = None

request_bytes = 0
response_bytes = 0
declarations_response_bytes = 0
declarations_response_max_bytes = 0
focus_response_bytes = 0

tracked_functions = {
    "classify",
    "validate_signup",
    "validate_profile",
    "validate_recovery",
    "validate_invite",
    "validate_contact",
    "process",
}

if os.path.exists(events_path):
    with open(events_path, encoding="utf-8") as stream:
        events = [json.loads(line) for line in stream if line.strip()]
else:
    events = []

def payload_size(value):
    return len(json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))

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
    error = response.get("error")

    response_size = payload_size(response)
    if op == "declarations":
        declarations_response_bytes += response_size
        declarations_response_max_bytes = max(declarations_response_max_bytes, response_size)
    elif op == "focusFunction":
        focus_response_bytes += response_size

    if ok and op == "simulateCreateType" and isinstance(result, dict):
        review = result.get("review", {})
        created = review.get("createdType", {}) if isinstance(review, dict) else {}
        if isinstance(created, dict) and created.get("displayName") == "ValidationRequest":
            validation_request_type_id = created.get("id")

    if ok and op == "simulateCreateFunction" and isinstance(result, dict):
        review = result.get("review", {})
        created = review.get("createdFunction", {}) if isinstance(review, dict) else {}
        if isinstance(created, dict):
            name = created.get("displayName")
            if name in tracked_functions:
                initial_function_ids[name] = created.get("id")
                params = created.get("parameters", [])
                if isinstance(params, list) and params and isinstance(params[0], dict):
                    initial_parameter_ids[name] = params[0].get("id")

    if op == "simulateParameterTypeMigration" and ok and isinstance(result, dict):
        migration_accepted = True
        migration_proposal_id = result.get("proposalId")
        review = result.get("review", {})
        diff = review.get("diff", {}) if isinstance(review, dict) else {}
        if isinstance(diff, dict):
            change = diff.get("parameterChange", {})
            if isinstance(change, dict):
                migration_function_id = change.get("functionId")
                migration_parameter_id = change.get("parameterId")
            calls = diff.get("affectedCallSites")
            rewrites = diff.get("bodyRewrites")
            if isinstance(calls, list):
                affected_calls = len(calls)
            if isinstance(rewrites, list):
                body_rewrites = len(rewrites)

    if op == "commit" and ok and isinstance(result, dict):
        if migration_proposal_id is not None and request.get("proposalId") == migration_proposal_id:
            migration_committed = True

    if restart_index is not None and request_index > restart_index and op == "declarations" and ok and isinstance(result, dict):
        functions = result.get("functions", [])
        if isinstance(functions, list):
            for function in functions:
                if not isinstance(function, dict):
                    continue
                name = function.get("displayName")
                if name not in tracked_functions:
                    continue
                post_restart_function_ids[name] = function.get("id")
                params = function.get("parameters", [])
                if isinstance(params, list) and params and isinstance(params[0], dict):
                    post_restart_parameter_ids[name] = params[0].get("id")
                    if name == "classify":
                        type_value = params[0].get("type", {})
                        if isinstance(type_value, dict) and type_value.get("kind") == "named":
                            post_restart_classify_type_id = type_value.get("typeId")
        rediscovered = tracked_functions.issubset(post_restart_function_ids.keys())

    if op == "executionReadiness" and ok and isinstance(result, dict) and result.get("kind") == "ready":
        execution_ready = True

    if op == "emitCSharp":
        profile = request.get("profile", {})
        profile_kind = profile.get("kind") if isinstance(profile, dict) else None
        rendered = json.dumps(error, sort_keys=True) if error is not None else ""
        expected_missing_binding = profile_kind == "empty" and not ok and "MissingCapabilityBinding" in rendered
        if expected_missing_binding:
            missing_binding = True
        if profile_kind == "systemConsoleWriteLine" and ok:
            console_emit = True
        if not ok and not expected_missing_binding:
            rejected_requests += 1
            unexpected_errors += 1
    elif not ok:
        rejected_requests += 1
        unexpected_errors += 1

identity_preserved = (
    tracked_functions.issubset(initial_function_ids.keys())
    and all(post_restart_function_ids.get(name) == initial_function_ids.get(name) for name in tracked_functions)
    and migration_function_id == initial_function_ids.get("classify")
    and migration_parameter_id == initial_parameter_ids.get("classify")
    and post_restart_parameter_ids.get("classify") == initial_parameter_ids.get("classify")
)
type_migrated = (
    validation_request_type_id is not None
    and post_restart_classify_type_id == validation_request_type_id
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
    "simulate_effect": str(counts.get("simulateCreateEffect", 0)),
    "simulate_capability": str(counts.get("simulateCreateCapability", 0)),
    "simulate_function": str(counts.get("simulateCreateFunction", 0)),
    "commits": str(counts.get("commit", 0)),
    "declarations_calls": str(counts.get("declarations", 0)),
    "focus_calls": str(counts.get("focusFunction", 0)),
    "request_bytes": str(request_bytes),
    "response_bytes": str(response_bytes),
    "declarations_response_bytes": str(declarations_response_bytes),
    "declarations_response_max_bytes": str(declarations_response_max_bytes),
    "focus_response_bytes": str(focus_response_bytes),
    "migration_attempts": str(counts.get("simulateParameterTypeMigration", 0)),
    "migration_accepted": "true" if migration_accepted else "false",
    "migration_committed": "true" if migration_committed else "false",
    "affected_calls": str(affected_calls if affected_calls is not None else "unknown"),
    "body_rewrites": str(body_rewrites if body_rewrites is not None else "unknown"),
    "identity_preserved": "true" if identity_preserved else "false",
    "type_migrated": "true" if type_migrated else "false",
    "restart": "true" if restart_index is not None else "false",
    "rediscovered": "true" if rediscovered else "false",
    "execution_ready": "true" if execution_ready else "false",
    "missing_binding": "true" if missing_binding else "false",
    "console_emit": "true" if console_emit else "false",
    "rejected_requests": str(rejected_requests),
    "unexpected_errors": str(unexpected_errors),
}
with open(summary_path, "w", encoding="utf-8") as stream:
    stream.write("\t".join(row.keys()) + "\n")
    stream.write("\t".join(row.values()) + "\n")
print("=== M6 medium-scale authoring exploratory summary ===")
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
