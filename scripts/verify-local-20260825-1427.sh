#!/usr/bin/env bash
set -Eeuo pipefail

# Opt-in full-solution verification with one test run and a shareable log.
# Routine development uses scripts/test-fast.fish instead.

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

This opt-in milestone check requires a .NET 10 SDK and runs:
  1. full-solution restore
  2. Release build with project warnings-as-errors
  3. the full test suite once, including the extended test project
  4. the JSON/Nyason comparison report produced by the test suite

Use scripts/test-fast.fish for routine development.
A timestamped log is written under ${TMPDIR:-/tmp}/nyamnyam-verification/.
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
LOG_DIR="${TMPDIR:-/tmp}/nyamnyam-verification"
LOG_FILE="$LOG_DIR/local-verify-$TIMESTAMP.log"
mkdir -p "$LOG_DIR"

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

echo "=== NyamNyam local verification ==="
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
dotnet restore NyamNyam.slnx
echo

echo "--- Release build ---"
dotnet build NyamNyam.slnx --configuration Release --no-restore
echo

echo "--- Full test suite (once, including extended) ---"
dotnet test NyamNyam.slnx --configuration Release --no-build --no-restore
echo

ENCODING_REPORT="${TMPDIR:-/tmp}/nyamnyam-encoding-comparison.txt"
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
