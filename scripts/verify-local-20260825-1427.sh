#!/usr/bin/env bash
set -Eeuo pipefail

# Local verification fallback for Nayamnyam when GitHub Actions is unavailable.
# Mirrors CI, then explicitly re-runs phase-specific test modules and verifies
# that the expected tests actually appeared in the TRX result.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

CLEAN=0
ALLOW_DIRTY=0

usage() {
  cat <<'EOF'
Usage: scripts/verify-local-20260825-1427.sh [--clean] [--allow-dirty]

Options:
  --clean        Remove all bin/ and obj/ directories before restore.
  --allow-dirty  Permit verification with uncommitted tracked changes.
  -h, --help     Show this help.

The script requires a .NET 10 SDK and runs:
  1. dotnet restore
  2. Release build with project warnings-as-errors
  3. the full test suite
  4. ExecutionIrTests explicitly (at least 6 passing tests)
  5. ExecutionIrCodecTests explicitly (at least 3 passing tests)
  6. CSharpBackendTests explicitly (at least 4 passing tests, including generated C# build/run)
  7. SemanticQueryTests explicitly (at least 5 passing tests)
  8. ChangeReviewQueryTests explicitly (at least 3 passing tests)
  9. the JSON/Nyason comparison report produced by the test suite

A timestamped log is written under ${TMPDIR:-/tmp}/nayamnyam-verification/.
EOF
}

for arg in "$@"; do
  case "$arg" in
    --clean) CLEAN=1 ;;
    --allow-dirty) ALLOW_DIRTY=1 ;;
    -h|--help) usage; exit 0 ;;
    *)
      echo "Unknown argument: $arg" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet was not found in PATH." >&2
  echo "Install a .NET 10 SDK before running this verification." >&2
  exit 1
fi

DOTNET_VERSION="$(dotnet --version)"
if [[ ! "$DOTNET_VERSION" =~ ^10\.0\. ]]; then
  echo "ERROR: expected .NET SDK 10.0.x, found: $DOTNET_VERSION" >&2
  exit 1
fi

if command -v git >/dev/null 2>&1 && git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  BRANCH="$(git branch --show-current || true)"
  HEAD_SHA="$(git rev-parse HEAD)"
  DIRTY="$(git status --short --untracked-files=no)"

  if [[ -n "$DIRTY" && "$ALLOW_DIRTY" -ne 1 ]]; then
    echo "ERROR: tracked files have uncommitted changes." >&2
    echo "Run with --allow-dirty only when that is intentional." >&2
    echo "$DIRTY" >&2
    exit 1
  fi
else
  BRANCH="(git unavailable)"
  HEAD_SHA="(git unavailable)"
fi

TIMESTAMP="$(date '+%Y%m%d-%H%M%S')"
LOG_DIR="${TMPDIR:-/tmp}/nayamnyam-verification"
LOG_FILE="$LOG_DIR/local-verify-$TIMESTAMP.log"
RESULT_DIR="$LOG_DIR/results-$TIMESTAMP"
mkdir -p "$LOG_DIR" "$RESULT_DIR"

exec > >(tee -a "$LOG_FILE") 2>&1

on_error() {
  local exit_code=$?
  echo
  echo "=== VERIFICATION FAILED ==="
  echo "exit-code: $exit_code"
  echo "log: $LOG_FILE"
  exit "$exit_code"
}
trap on_error ERR

run_required_filter() {
  local label="$1"
  local filter="$2"
  local minimum="$3"
  local trx_name="$4"
  local trx_path="$RESULT_DIR/$trx_name"

  echo "--- $label ---"
  dotnet test tests/Nayamnyam.Core.Tests/Nayamnyam.Core.Tests.fsproj \
    --configuration Release \
    --no-build \
    --no-restore \
    --filter "$filter" \
    --logger "trx;LogFileName=$trx_name" \
    --results-directory "$RESULT_DIR"

  if [[ ! -f "$trx_path" ]]; then
    echo "ERROR: expected TRX result was not produced: $trx_path" >&2
    exit 1
  fi

  local passed_count
  passed_count="$(grep -o 'outcome="Passed"' "$trx_path" | wc -l | tr -d ' ')"
  if (( passed_count < minimum )); then
    echo "ERROR: $label executed only $passed_count passing tests; expected at least $minimum." >&2
    exit 1
  fi
  echo "verified-passed-count: $passed_count"
  echo
}

echo "=== Nayamnyam local verification ==="
echo "time: $(date --iso-8601=seconds)"
echo "repo: $ROOT_DIR"
echo "branch: $BRANCH"
echo "head: $HEAD_SHA"
echo "dotnet-sdk: $DOTNET_VERSION"
echo "clean: $CLEAN"
echo

if [[ "$CLEAN" -eq 1 ]]; then
  echo "--- Clean bin/obj ---"
  while IFS= read -r -d '' dir; do
    echo "remove: ${dir#$ROOT_DIR/}"
    rm -rf "$dir"
  done < <(find "$ROOT_DIR" -type d \( -name bin -o -name obj \) -print0)
  echo
fi

echo "--- Restore ---"
dotnet restore Nayamnyam.slnx
echo

echo "--- Release build ---"
dotnet build Nayamnyam.slnx --configuration Release --no-restore
echo

echo "--- Full test suite ---"
dotnet test Nayamnyam.slnx --configuration Release --no-build --no-restore
echo

run_required_filter \
  "Phase I execution lowering tests" \
  "FullyQualifiedName~ExecutionIrTests" \
  6 \
  "phase-i-execution.trx"

run_required_filter \
  "Phase I canonical Execution IR codec tests" \
  "FullyQualifiedName~ExecutionIrCodecTests" \
  3 \
  "phase-i-codec.trx"

run_required_filter \
  "Phase J C# backend tests" \
  "FullyQualifiedName~CSharpBackendTests" \
  4 \
  "phase-j-csharp-backend.trx"

run_required_filter \
  "Phase K semantic query tests" \
  "FullyQualifiedName~SemanticQueryTests" \
  5 \
  "phase-k-semantic-query.trx"

run_required_filter \
  "Phase K change review query tests" \
  "FullyQualifiedName~ChangeReviewQueryTests" \
  3 \
  "phase-k-change-review.trx"

ENCODING_REPORT="${TMPDIR:-/tmp}/nayamnyam-encoding-comparison.txt"
if [[ ! -f "$ENCODING_REPORT" ]]; then
  echo "ERROR: expected encoding comparison report was not produced: $ENCODING_REPORT" >&2
  exit 1
fi

echo "--- Encoding comparison ---"
cat "$ENCODING_REPORT"
echo

echo "=== VERIFICATION PASSED ==="
echo "branch: $BRANCH"
echo "head: $HEAD_SHA"
echo "dotnet-sdk: $DOTNET_VERSION"
echo "log: $LOG_FILE"
