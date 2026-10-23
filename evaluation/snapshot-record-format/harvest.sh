#!/usr/bin/env bash
# Harvests the snapshots the authoring examples commit into fixtures/validated/.
# Run from anywhere after `npm ci` and `dotnet build tools/Nyrwvy.AuthoringSession -c Release`
# (each example's README gives its command). Optional argument: a work directory for the
# example bundles and logs (default: a fresh temporary directory). An example that fails, or a
# snapshot the JSON codec cannot validate on its own (a pinned dependency), is skipped with its reason.
set -u
harness=$(cd "$(dirname "$0")" && pwd)
work=${1:-$(mktemp -d)}
mkdir -p "$work" "$harness/fixtures/validated"
cd "$harness/../.." || exit 1
dotnet build "$harness" -c Release -nologo -v q || exit 1
codec="$harness/bin/Release/net10.0/Nyrwvy.SnapshotRecordFormat.dll"
declare -A seen
harvest() { # name dir
  local name=$1 dir=$2 found=0 f src rel target hash
  while IFS= read -r f; do
    src=$f
    rel=$(realpath --relative-to="$dir" "$f" | tr / -)
    # A pinned-dependency sidecar embeds the dependency snapshot, itself a standalone document.
    if jq -e 'has("snapshotJson")' "$f" > /dev/null 2>&1; then
      rel="${rel%.json.dependency.json}-dependency.json"; src="$work/$name-$rel"; jq -r .snapshotJson "$f" > "$src"
    elif ! jq -e 'has("schemaVersion") and has("snapshotId")' "$f" > /dev/null 2>&1; then continue; fi
    hash=$(sha256sum "$src" | cut -c1-16)
    [[ -n ${seen[$hash]:-} ]] && continue
    seen[$hash]=1
    target="$harness/fixtures/validated/$name-$rel"
    if dotnet "$codec" encode "$src" > /dev/null 2> "$work/$name.encode.err"; then
      cp "$src" "$target"; echo "harvested $target"; found=1
    else
      echo "skip $f: $(head -c 300 "$work/$name.encode.err")"
    fi
  done < <(find "$dir" -name '*.json' | sort)
  [[ $found = 1 ]] || echo "skip $name: no snapshot harvested"
}
run() { # name command...
  local name=$1; shift
  echo "== $name"
  if "$@" > "$work/$name.log" 2>&1; then harvest "$name" "$work/$name"
  else echo "skip $name: command failed (exit $?), see $work/$name.log"; fi
}
run bounded-stream node examples/bounded-stream/run.mjs "$work/bounded-stream"
run bounded-worker-stream node examples/bounded-worker-stream/run.mjs "$work/bounded-worker-stream"
run cleanup-shield node --experimental-strip-types examples/cleanup-shield/run.mjs "$work/cleanup-shield"
run config-request-batch node examples/config-request-batch/run.mjs "$work/config-request-batch"
run config-request-batch-v2 node examples/config-request-batch/run.mjs --v2 "$work/config-request-batch-v2"
run change-verification-batch node examples/change-verification-batch/run.mjs "$work/config-request-batch-v2/moved" "$work/change-verification-batch"
run dynamic-worker-collector node examples/dynamic-worker-collector/run.mjs "$work/dynamic-worker-collector"
run function-abort node --experimental-strip-types examples/function-abort/run.mjs "$work/function-abort"
run scoped-result-handoff node examples/scoped-result-handoff/run.mjs "$work/scoped-result-handoff"
echo "work directory: $work"
