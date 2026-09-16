#!/usr/bin/env fish

set -l script_dir (path resolve (dirname (status filename)))
set -l repo_root (path resolve "$script_dir/..")
set -l project "$repo_root/tools/NyamNyam.AuthoringSession/NyamNyam.AuthoringSession.fsproj"
set -l model "$NYAMNYAM_M4_AUTHORING_EVAL_MODEL"
set -l variant "$NYAMNYAM_M4_AUTHORING_EVAL_VARIANT"

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
    echo "Set NYAMNYAM_M4_AUTHORING_EVAL_MODEL to another available model if needed." >&2
    exit 1
end

echo "[m4-authoring-eval] building the maintained authoring session..."
dotnet build "$project" --configuration Release
or exit $status

set -l stamp (date +%Y%m%d-%H%M%S)
set -l result_root "$HOME/.local/share/nyamnyam/m4-authoring-exploratory/$stamp"
mkdir -p "$result_root"
or exit 1
set -gx NYAMNYAM_M4_AUTHORING_RESULT_ROOT "$result_root"

set -l repository_revision (git rev-parse HEAD)
set -l opencode_version (opencode --version | string collect)
printf 'repository_revision=%s\nmodel=%s\nvariant=%s\nopencode_version=%s\nstarted_at=%s\n' \
    "$repository_revision" "$model" "$variant" "$opencode_version" (date --iso-8601=seconds) \
    >"$result_root/metadata.txt"

set -l prompt "Author a small validation/reporting program entirely through the maintained NyamNyam authoring session. Start from the fresh snapshot. Create a union named ValidationOutcome with Accepted(message: String) and Rejected(reason: String). Create an effect named ConsoleOutput. Create a Console capability with one write_line(text: String) -> Unit operation associated with ConsoleOutput. Create a pure classify(input: String) -> ValidationOutcome function that returns Accepted(\"accepted\") when input contains @ and Rejected(\"rejected\") otherwise. Create process(input: String, console: Console) -> Unit requiring ConsoleOutput; it must call classify, match both union variants, and send the bound message/reason to write_line. Use only published IDs returned/discovered through the maintained protocol and review each proposal before commit. After the complete program is committed, restart the maintained session once, rediscover the persisted state, confirm execution readiness, confirm that empty-profile C# emission fails specifically with MissingCapabilityBinding, then emit successfully with the explicit System.Console.WriteLine binding for the authored operation. Do not use emitted C# as semantic authoring authority. Finalize after the requested checks."

set -l stdout_log "$result_root/agent.jsonl"
set -l stderr_log "$result_root/agent.stderr.log"

echo "[m4-authoring-eval] running one bounded exploratory attempt"
opencode --print-logs --log-level INFO run \
    --agent nyamnyam-m4-authoring-eval \
    --model "$model" \
    --variant "$variant" \
    --format json \
    --title "m4-authoring-$stamp" \
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
rediscovered = False
execution_ready = False
missing_binding = False
console_emit = False
unexpected_errors = 0

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

    if restart_index is not None and request_index > restart_index and op == "declarations" and ok:
        rediscovered = True
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

workspace = "true" if os.path.isfile(workspace_path) else "false"
row = {
    "exit": exit_code,
    "workspace": workspace,
    "simulate_type": str(counts.get("simulateCreateType", 0)),
    "simulate_effect": str(counts.get("simulateCreateEffect", 0)),
    "simulate_capability": str(counts.get("simulateCreateCapability", 0)),
    "simulate_function": str(counts.get("simulateCreateFunction", 0)),
    "commits": str(counts.get("commit", 0)),
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
print("=== M4 authoring exploratory summary ===")
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
