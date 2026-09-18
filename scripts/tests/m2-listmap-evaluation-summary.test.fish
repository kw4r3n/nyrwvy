#!/usr/bin/env fish

set -l script_dir (path dirname (status --current-filename))
source "$script_dir/../lib/m2-listmap-evaluation-summary.fish"

set -l root (mktemp -d /tmp/nyrwvy-listmap-summary-test-XXXXXX)
or exit 1

function cleanup --on-event fish_exit
    rm -rf "$root"
end

for required_nonempty in run-state.json run-metadata.json metrics.json events.jsonl
    printf 'x\n' >"$root/$required_nonempty"
end

# Expected-rejection tasks legitimately have no harness-owned event.
touch "$root/harness-events.jsonl"

m2_listmap_artifact_set_is_durable "$root"
or begin
    echo "FAIL: existing empty harness-events.jsonl must be accepted." >&2
    exit 1
end

rm "$root/harness-events.jsonl"
if m2_listmap_artifact_set_is_durable "$root"
    echo "FAIL: missing harness-events.jsonl must reject." >&2
    exit 1
end

touch "$root/harness-events.jsonl"
printf '' >"$root/events.jsonl"
if m2_listmap_artifact_set_is_durable "$root"
    echo "FAIL: empty protocol events.jsonl must reject." >&2
    exit 1
end

printf 'x\n' >"$root/events.jsonl"
m2_listmap_artifact_set_is_durable "$root"
or begin
    echo "FAIL: complete artifact set must pass." >&2
    exit 1
end

echo "PASS: ListMap operator artifact durability classification"
