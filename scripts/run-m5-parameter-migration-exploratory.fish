#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/NyamNyam.AuthoringSession/NyamNyam.AuthoringSession.fsproj"
set -l model "$NYAMNYAM_M5_MIGRATION_EVAL_MODEL"
set -l variant "$NYAMNYAM_M5_MIGRATION_EVAL_VARIANT"

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
    echo "Set NYAMNYAM_M5_MIGRATION_EVAL_MODEL to another available model if needed." >&2
    exit 1
end

echo "[m5-migration-eval] building the maintained authoring session..."
dotnet build "$project" --configuration Release
or exit $status

set -l stamp (date +%Y%m%d-%H%M%S)
set -l result_root "$HOME/.local/share/nyamnyam/m5-parameter-migration-exploratory/$stamp"
mkdir -p "$result_root"
or exit 1
# The existing bounded authoring bridge owns this result-root variable.
set -gx NYAMNYAM_M4_AUTHORING_RESULT_ROOT "$result_root"

set -l repository_revision (git rev-parse HEAD)
set -l opencode_version (opencode --version | string collect)
printf 'repository_revision=%s\nmodel=%s\nvariant=%s\nopencode_version=%s\nstarted_at=%s\n' \
    "$repository_revision" "$model" "$variant" "$opencode_version" (date --iso-8601=seconds) \
    >"$result_root/metadata.txt"

set -l prompt "Work entirely through the maintained NyamNyam authoring session. First author the same bounded validation/reporting program used by the M4 workload: create ValidationOutcome as a union with Accepted(message: String) and Rejected(reason: String); create ConsoleOutput; create Console.write_line(text: String) -> Unit associated with ConsoleOutput; create pure classify(input: String) -> ValidationOutcome that returns Accepted(\"accepted\") when input contains @ and Rejected(\"rejected\") otherwise; create process(input: String, console: Console) -> Unit requiring ConsoleOutput, calling classify, matching both variants, and sending the bound message/reason to write_line. Review and commit each proposal and rediscover published IDs rather than inventing them. Then evolve this existing program in place: create and commit a product type ValidationRequest with email: String. Use simulateParameterTypeMigration to change the existing classify input parameter from String to ValidationRequest while preserving the existing classify FunctionId and that existing input ParameterId. Its replacement body must read ValidationRequest.email and retain the same contains-@ conditional and Accepted/Rejected behavior. Supply the complete process caller rewrite in the same migration proposal: preserve process itself and its current signature, construct ValidationRequest from process's existing String input, pass that product to the migrated classify, and retain the existing union match plus Console.write_line behavior. Review the migration parameterChange, complete affectedCallSites, and bodyRewrites before committing it. After commit, rediscover declarations and explicitly verify that classify's FunctionId and input ParameterId are unchanged and the input type is now ValidationRequest, and that process's FunctionId is unchanged. Only after the migration is committed, restart the maintained session exactly once, rediscover the same persisted identities and migrated type, confirm execution readiness, confirm empty-profile C# emission fails specifically with MissingCapabilityBinding, then emit successfully with the explicit System.Console.WriteLine binding for the authored operation. Do not use emitted C# as semantic authoring authority. Finalize after these checks, and report any rejected request/retry or concrete protocol friction."

set -l stdout_log "$result_root/agent.jsonl"
set -l stderr_log "$result_root/agent.stderr.log"

echo "[m5-migration-eval] running one bounded M4 -> M5 exploratory attempt"
opencode --print-logs --log-level INFO run \
    --agent nyamnyam-m4-authoring-eval \
    --model "$model" \
    --variant "$variant" \
    --format json \
    --title "m5-parameter-migration-$stamp" \
    "$prompt" \
    >"$stdout_log" 2>"$stderr_log"
set -l run_code $status
printf '%s\n' "$run_code" >"$result_root/exit-code.txt"

set -l events "$result_root/protocol-events.jsonl"
set -l summary "$result_root/summary.tsv"
python3 -c '
import json, os, sys

events_path, summary_path, workspace_path, exit_code = sys.argv[1:5]
requests = {}
counts = {}
restart_index = None
unexpected_errors = 0
execution_ready = False
missing_binding = False
console_emit = False
rediscovered = False

created_classify_id = None
created_classify_parameter_id = None
created_process_id = None
validation_request_type_id = None
migration_proposal_id = None
migration_function_id = None
migration_parameter_id = None
migration_accepted = False
migration_committed = False
affected_calls = None
body_rewrites = None
post_restart_classify_id = None
post_restart_classify_parameter_id = None
post_restart_classify_type_id = None
post_restart_process_id = None

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
        if isinstance(request_id, str) and isinstance(op, str):
            requests[request_id] = (op, request, index)
            counts[op] = counts.get(op, 0) + 1
        continue
    if kind != "response":
        continue

    response = event.get("response", {})
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
            if name == "classify":
                created_classify_id = created.get("id")
                params = created.get("parameters", [])
                if isinstance(params, list) and params and isinstance(params[0], dict):
                    created_classify_parameter_id = params[0].get("id")
            elif name == "process":
                created_process_id = created.get("id")

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
                if function.get("displayName") == "classify":
                    post_restart_classify_id = function.get("id")
                    params = function.get("parameters", [])
                    if isinstance(params, list) and params and isinstance(params[0], dict):
                        post_restart_classify_parameter_id = params[0].get("id")
                        type_value = params[0].get("type", {})
                        if isinstance(type_value, dict) and type_value.get("kind") == "named":
                            post_restart_classify_type_id = type_value.get("typeId")
                elif function.get("displayName") == "process":
                    post_restart_process_id = function.get("id")
        rediscovered = post_restart_classify_id is not None and post_restart_process_id is not None

    if op == "executionReadiness" and ok and isinstance(result, dict) and result.get("kind") == "ready":
        execution_ready = True

    if op == "emitCSharp":
        profile = request.get("profile", {})
        profile_kind = profile.get("kind") if isinstance(profile, dict) else None
        rendered = json.dumps(error, sort_keys=True) if error is not None else ""
        if profile_kind == "empty" and not ok and "MissingCapabilityBinding" in rendered:
            missing_binding = True
        if profile_kind == "systemConsoleWriteLine" and ok:
            console_emit = True
        if not ok and not (profile_kind == "empty" and "MissingCapabilityBinding" in rendered):
            unexpected_errors += 1
    elif not ok:
        unexpected_errors += 1

identity_preserved = (
    created_classify_id is not None
    and created_classify_parameter_id is not None
    and created_process_id is not None
    and migration_function_id == created_classify_id
    and migration_parameter_id == created_classify_parameter_id
    and post_restart_classify_id == created_classify_id
    and post_restart_classify_parameter_id == created_classify_parameter_id
    and post_restart_process_id == created_process_id
)
type_migrated = (
    validation_request_type_id is not None
    and post_restart_classify_type_id == validation_request_type_id
)

row = {
    "exit": exit_code,
    "workspace": "true" if os.path.isfile(workspace_path) else "false",
    "simulate_type": str(counts.get("simulateCreateType", 0)),
    "simulate_effect": str(counts.get("simulateCreateEffect", 0)),
    "simulate_capability": str(counts.get("simulateCreateCapability", 0)),
    "simulate_function": str(counts.get("simulateCreateFunction", 0)),
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
    "unexpected_errors": str(unexpected_errors),
}
with open(summary_path, "w", encoding="utf-8") as stream:
    stream.write("\t".join(row.keys()) + "\n")
    stream.write("\t".join(row.values()) + "\n")
print("=== M5 parameter migration exploratory summary ===")
print("\t".join(row.keys()))
print("\t".join(row.values()))
' "$events" "$summary" "$result_root/workspace.json" "$run_code"
set -l summarize_code $status

printf 'finished_at=%s\n' (date --iso-8601=seconds) >>"$result_root/metadata.txt"

echo
echo "results: $result_root"

if test $summarize_code -ne 0
    exit $summarize_code
end
exit $run_code
