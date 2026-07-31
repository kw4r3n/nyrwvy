#!/usr/bin/env python3
"""Run reproducible public conformance checks for one language-spike workspace."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ElementTree


SUPPORTED_LANGUAGES = ("fsharp", "csharp")
_TOP_LEVEL_ORDER = (
    "schema_version",
    "id",
    "types",
    "functions",
    "calls",
    "obligations",
)
_SUMMARY_ORDER = (
    "schema_version",
    "snapshot_id",
    "type_count",
    "function_count",
    "call_count",
    "obligation_count",
)
_SKIPPED_DIRECTORIES = frozenset(
    {
        ".git",
        ".nayamnyam-evaluation",
        "bin",
        "obj",
        "__pycache__",
    }
)
_FORBIDDEN_PATTERNS = {
    "fsharp": (
        ("unchecked_default", re.compile(r"\bUnchecked\.defaultof\b")),
        ("unbox", re.compile(r"\bunbox\b")),
        ("active_pattern", re.compile(r"\blet\s+\(\|")),
        (
            "custom_operator",
            re.compile(r"\blet\s+\(\s*[!%&*+\-./<=>?@^|~:]+\s*\)"),
        ),
        (
            "computation_expression",
            re.compile(r"\b(?:async|task|seq)\s*\{"),
        ),
        ("hidden_union_case", re.compile(r"^\s*\|\s*_\s*->")),
        ("warning_suppression", re.compile(r"#nowarn\b", re.IGNORECASE)),
        ("nuget_reference", re.compile(r'#r\s+"nuget:', re.IGNORECASE)),
    ),
    "csharp": (
        ("dynamic", re.compile(r"\bdynamic\b")),
        ("unchecked", re.compile(r"\bunchecked\s*\(")),
        (
            "reflection",
            re.compile(r"\b(?:using\s+)?System\.Reflection\b"),
        ),
        ("null_forgiving", re.compile(r"\bnull!")),
        (
            "warning_suppression",
            re.compile(r"#pragma\s+warning\s+disable\b", re.IGNORECASE),
        ),
        ("hidden_default_case", re.compile(r"\bdefault\s*:")),
        ("hidden_discard_arm", re.compile(r"\b_\s*=>")),
    ),
}


class PublicCheckError(ValueError):
    """Raised when public checks cannot be started safely."""


@dataclass(frozen=True, slots=True)
class WorkspaceLayout:
    workspace: Path
    public_root: Path
    expected_root: Path | None
    isolated: bool
    solution: Path
    tests_project: Path
    cli_project: Path
    core_project: Path


def run_public_checks(
    language: str,
    workspace: Path,
    *,
    output_path: Path | None = None,
) -> dict[str, object]:
    """Run all public checks and write their detailed JSON result."""
    layout = resolve_workspace(language, workspace)
    output = output_path or layout.workspace / "public-checks.json"
    output = output.resolve()
    artifact_root = layout.workspace / ".nayamnyam-evaluation" / "public"
    artifact_root.mkdir(parents=True, exist_ok=True)

    checks: list[dict[str, object]] = []
    checks.append(
        capture_command(
            name="build",
            command=("dotnet", "build", layout.solution.name, "--nologo"),
            cwd=layout.workspace,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )
    checks.append(
        capture_command(
            name="baseline_tests",
            command=(
                "dotnet",
                "run",
                "--project",
                _relative(layout.tests_project, layout.workspace),
                "--no-build",
            ),
            cwd=layout.workspace,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )

    fixture = layout.public_root / "fixtures" / "initial-snapshot-v1.json"
    fixture_argument = _relative(fixture, layout.workspace)
    canonical_check = capture_command(
        name="canonical_fixture",
        command=(
            "dotnet",
            "run",
            "--project",
            _relative(layout.cli_project, layout.workspace),
            "--no-build",
            "--",
            "canonical",
            fixture_argument,
        ),
        cwd=layout.workspace,
        workspace=layout.workspace,
        artifact_root=artifact_root,
    )
    checks.append(canonical_check)

    mode = "unknown"

    def validate_canonical() -> str:
        nonlocal mode
        if canonical_check["exit_code"] != 0:
            raise PublicCheckError("canonical CLI command did not exit successfully")
        canonical_output = layout.workspace / str(canonical_check["stdout_path"])
        mode = _validate_canonical_output(
            canonical_output.read_bytes(),
            fixture.read_bytes(),
            (
                layout.expected_root / "initial-canonical.json"
                if layout.expected_root is not None
                else None
            ),
        )
        return f"canonical fixture satisfies {mode} mode\n"

    checks.append(
        capture_internal_check(
            name="canonical_contract",
            command=("internal", "validate-canonical-fixture"),
            validator=validate_canonical,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )

    summary_check = capture_command(
        name="summary_fixture",
        command=(
            "dotnet",
            "run",
            "--project",
            _relative(layout.cli_project, layout.workspace),
            "--no-build",
            "--",
            "summary",
            fixture_argument,
        ),
        cwd=layout.workspace,
        workspace=layout.workspace,
        artifact_root=artifact_root,
    )
    checks.append(summary_check)

    def validate_summary() -> str:
        if summary_check["exit_code"] != 0:
            raise PublicCheckError("summary CLI command did not exit successfully")
        summary_output = layout.workspace / str(summary_check["stdout_path"])
        expected_summary = (
            layout.expected_root / "initial-summary.json"
            if layout.expected_root is not None
            else None
        )
        _validate_summary_output(
            summary_output.read_bytes(),
            fixture.read_bytes(),
            mode,
            expected_summary,
        )
        return f"summary satisfies {mode} mode\n"

    checks.append(
        capture_internal_check(
            name="summary_contract",
            command=("internal", "validate-summary"),
            validator=validate_summary,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )

    forbidden_findings: list[dict[str, object]] = []

    def scan_forbidden() -> str:
        forbidden_findings.extend(_scan_forbidden_patterns(language, layout.workspace))
        if forbidden_findings:
            raise PublicCheckError(
                f"found {len(forbidden_findings)} forbidden source pattern(s)"
            )
        return "no forbidden source patterns found\n"

    checks.append(
        capture_internal_check(
            name="forbidden_patterns",
            command=("internal", "scan-forbidden-patterns"),
            validator=scan_forbidden,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )

    project_findings: list[str] = []

    def validate_projects() -> str:
        project_findings.extend(_project_findings(language, layout.workspace))
        if project_findings:
            raise PublicCheckError("; ".join(project_findings))
        return "project warning settings and package references are valid\n"

    checks.append(
        capture_internal_check(
            name="project_settings",
            command=("internal", "validate-project-settings"),
            validator=validate_projects,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )

    modified_protected_files: list[str] = []

    def validate_protected() -> str:
        changes, errors = _protected_file_changes(layout)
        modified_protected_files.extend(changes)
        if errors:
            raise PublicCheckError("; ".join(errors))
        if modified_protected_files:
            raise PublicCheckError(
                "modified protected files: " + ", ".join(modified_protected_files)
            )
        return "shared/public matches the protected baseline\n"

    checks.append(
        capture_internal_check(
            name="protected_files",
            command=("internal", "validate-protected-files"),
            validator=validate_protected,
            workspace=layout.workspace,
            artifact_root=artifact_root,
        )
    )

    checks.sort(key=lambda check: str(check["name"]))
    failures = [
        str(check["name"])
        for check in checks
        if not bool(check["passed"])
    ]
    result: dict[str, object] = {
        "schema_version": 1,
        "language": language,
        "mode": mode,
        "passed": not failures,
        "checks": checks,
        "failures": failures,
        "forbidden_findings": forbidden_findings,
        "project_findings": sorted(project_findings),
        "modified_protected_files": sorted(set(modified_protected_files)),
    }
    write_json(output, result)
    return result


def resolve_workspace(language: str, workspace: Path) -> WorkspaceLayout:
    """Resolve either a source baseline directory or an isolated trial root."""
    if language not in SUPPORTED_LANGUAGES:
        raise PublicCheckError(
            f"unsupported language: {language!r}; expected one of: "
            + ", ".join(SUPPORTED_LANGUAGES)
        )
    workspace = workspace.resolve()
    if not workspace.is_dir():
        raise PublicCheckError(f"workspace does not exist: {workspace}")

    isolated_public = workspace / "shared" / "public"
    source_public = workspace.parent / "shared" / "public"
    if isolated_public.is_dir():
        public_root = isolated_public
        expected_root = None
        isolated = True
    elif source_public.is_dir():
        public_root = source_public
        candidate_expected = workspace.parent / "shared" / "expected"
        expected_root = candidate_expected if candidate_expected.is_dir() else None
        isolated = False
    elif (workspace / ".git").exists():
        public_root = isolated_public
        expected_root = None
        isolated = True
    else:
        raise PublicCheckError(
            "workspace has neither isolated shared/public nor source-layout "
            f"shared/public: {workspace}"
        )

    if language == "fsharp":
        solution_name = "Nayamnyam.Spike.FSharp.sln"
        project_suffix = "fsproj"
    else:
        solution_name = "Nayamnyam.Spike.CSharp.sln"
        project_suffix = "csproj"

    solution = workspace / solution_name
    tests_project = (
        workspace
        / "tests"
        / "Nayamnyam.Spike.Tests"
        / f"Nayamnyam.Spike.Tests.{project_suffix}"
    )
    cli_project = (
        workspace
        / "src"
        / "Nayamnyam.Spike.Cli"
        / f"Nayamnyam.Spike.Cli.{project_suffix}"
    )
    core_project = (
        workspace
        / "src"
        / "Nayamnyam.Spike.Core"
        / f"Nayamnyam.Spike.Core.{project_suffix}"
    )
    return WorkspaceLayout(
        workspace=workspace,
        public_root=public_root,
        expected_root=expected_root,
        isolated=isolated,
        solution=solution,
        tests_project=tests_project,
        cli_project=cli_project,
        core_project=core_project,
    )


def capture_command(
    *,
    name: str,
    command: tuple[str, ...],
    cwd: Path,
    workspace: Path,
    artifact_root: Path,
    display_command: tuple[str, ...] | None = None,
) -> dict[str, object]:
    """Run one command and persist both output streams."""
    started = time.perf_counter_ns()
    stdout = b""
    stderr = b""
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        exit_code = completed.returncode
        stdout = completed.stdout
        stderr = completed.stderr
    except FileNotFoundError as error:
        exit_code = 127
        stderr = f"{error}\n".encode("utf-8", errors="replace")
    except OSError as error:
        exit_code = 126
        stderr = f"{error}\n".encode("utf-8", errors="replace")
    duration_ms = max(0, (time.perf_counter_ns() - started) // 1_000_000)
    return _write_check_artifacts(
        name=name,
        command=display_command or command,
        exit_code=exit_code,
        duration_ms=duration_ms,
        stdout=stdout,
        stderr=stderr,
        workspace=workspace,
        artifact_root=artifact_root,
    )


def capture_internal_check(
    *,
    name: str,
    command: tuple[str, ...],
    validator: object,
    workspace: Path,
    artifact_root: Path,
) -> dict[str, object]:
    """Run one deterministic in-process validator with captured evidence."""
    started = time.perf_counter_ns()
    stdout = b""
    stderr = b""
    exit_code = 0
    try:
        message = validator()
        if not isinstance(message, str):
            raise TypeError("internal validator must return a status string")
        stdout = message.encode("utf-8")
    except (OSError, ValueError, TypeError, json.JSONDecodeError, ElementTree.ParseError) as error:
        exit_code = 1
        stderr = f"{error}\n".encode("utf-8", errors="replace")
    duration_ms = max(0, (time.perf_counter_ns() - started) // 1_000_000)
    return _write_check_artifacts(
        name=name,
        command=command,
        exit_code=exit_code,
        duration_ms=duration_ms,
        stdout=stdout,
        stderr=stderr,
        workspace=workspace,
        artifact_root=artifact_root,
    )


def write_json(path: Path, value: object) -> None:
    """Write one UTF-8/LF deterministic JSON artifact."""
    path.parent.mkdir(parents=True, exist_ok=True)
    serialized = json.dumps(
        value,
        ensure_ascii=False,
        indent=2,
        separators=(",", ": "),
    )
    path.write_text(serialized + "\n", encoding="utf-8", newline="\n")


def _write_check_artifacts(
    *,
    name: str,
    command: tuple[str, ...],
    exit_code: int,
    duration_ms: int,
    stdout: bytes,
    stderr: bytes,
    workspace: Path,
    artifact_root: Path,
) -> dict[str, object]:
    stdout_path = artifact_root / f"{name}.stdout"
    stderr_path = artifact_root / f"{name}.stderr"
    stdout_path.write_bytes(stdout)
    stderr_path.write_bytes(stderr)
    return {
        "name": name,
        "passed": exit_code == 0,
        "command": list(command),
        "exit_code": exit_code,
        "duration_ms": duration_ms,
        "stdout_path": _relative(stdout_path, workspace),
        "stderr_path": _relative(stderr_path, workspace),
    }


def _validate_canonical_output(
    actual: bytes,
    fixture: bytes,
    expected_path: Path | None,
) -> str:
    actual_document = _decode_canonical_json(actual, "canonical output")
    fixture_document = json.loads(fixture)
    if list(actual_document) != list(_TOP_LEVEL_ORDER):
        raise PublicCheckError("canonical snapshot properties are not in contract order")

    schema_version = actual_document.get("schema_version")
    if schema_version == 1:
        expected = (
            expected_path.read_bytes()
            if expected_path is not None and expected_path.is_file()
            else fixture
        )
        if actual != expected:
            raise PublicCheckError(
                "untouched baseline canonical bytes differ from the v1 expected fixture"
            )
        return "untouched_baseline"
    if schema_version != 2:
        raise PublicCheckError(
            f"canonical output has unsupported schema_version: {schema_version!r}"
        )

    for key in ("id", "types", "functions", "calls"):
        if actual_document.get(key) != fixture_document.get(key):
            raise PublicCheckError(
                f"post-maintenance canonical output changed fixture field {key!r}"
            )
    if actual_document.get("obligations") != fixture_document.get("obligations"):
        raise PublicCheckError(
            "post-maintenance canonical output changed the fixture obligations"
        )
    for obligation in actual_document["obligations"]:
        if "origin_transaction_id" not in obligation:
            raise PublicCheckError(
                "post-maintenance obligation lacks origin_transaction_id"
            )
    return "post_maintenance"


def _validate_summary_output(
    actual: bytes,
    fixture: bytes,
    mode: str,
    expected_path: Path | None,
) -> None:
    document = _decode_canonical_json(actual, "summary output")
    if list(document) != list(_SUMMARY_ORDER):
        raise PublicCheckError("summary properties are not in contract order")
    fixture_document = json.loads(fixture)
    schema_version = 1 if mode == "untouched_baseline" else 2
    if mode not in ("untouched_baseline", "post_maintenance"):
        raise PublicCheckError("canonical mode could not be established")
    expected = {
        "schema_version": schema_version,
        "snapshot_id": fixture_document["id"],
        "type_count": len(fixture_document["types"]),
        "function_count": len(fixture_document["functions"]),
        "call_count": len(fixture_document["calls"]),
        "obligation_count": len(fixture_document["obligations"]),
    }
    if document != expected:
        raise PublicCheckError("summary values do not match the canonical fixture")
    if (
        mode == "untouched_baseline"
        and expected_path is not None
        and expected_path.is_file()
        and actual != expected_path.read_bytes()
    ):
        raise PublicCheckError("untouched baseline summary bytes differ from expected")


def _decode_canonical_json(contents: bytes, label: str) -> dict[str, object]:
    if contents.startswith(b"\xef\xbb\xbf"):
        raise PublicCheckError(f"{label} contains a UTF-8 byte-order mark")
    if b"\r" in contents:
        raise PublicCheckError(f"{label} contains non-LF line endings")
    if not contents.endswith(b"\n") or contents.endswith(b"\n\n"):
        raise PublicCheckError(f"{label} must end with exactly one LF")
    try:
        text = contents.decode("utf-8")
    except UnicodeDecodeError as error:
        raise PublicCheckError(f"{label} is not UTF-8") from error
    document = json.loads(text)
    if not isinstance(document, dict):
        raise PublicCheckError(f"{label} must be a JSON object")
    if _canonical_json_bytes(document) != contents:
        raise PublicCheckError(
            f"{label} is not two-space indented canonical JSON"
        )
    return document


def _canonical_json_bytes(document: object) -> bytes:
    return (
        json.dumps(
            document,
            ensure_ascii=False,
            indent=2,
            separators=(",", ": "),
        )
        + "\n"
    ).encode("utf-8")


def _scan_forbidden_patterns(
    language: str,
    workspace: Path,
) -> list[dict[str, object]]:
    suffix = ".fs" if language == "fsharp" else ".cs"
    findings: list[dict[str, object]] = []
    for path in _source_files(workspace, suffix):
        relative_path = _relative(path, workspace)
        if path.is_symlink():
            findings.append(
                {
                    "path": relative_path,
                    "line": 0,
                    "column": 0,
                    "rule": "symlink_source",
                    "match": path.readlink().as_posix(),
                }
            )
            continue
        contents = path.read_text(encoding="utf-8")
        for line_number, line in enumerate(contents.splitlines(), start=1):
            for rule, pattern in _FORBIDDEN_PATTERNS[language]:
                match = pattern.search(line)
                if match is not None:
                    findings.append(
                        {
                            "path": relative_path,
                            "line": line_number,
                            "column": match.start() + 1,
                            "rule": rule,
                            "match": match.group(0),
                        }
                    )
    findings.sort(
        key=lambda finding: (
            str(finding["path"]),
            int(finding["line"]),
            int(finding["column"]),
            str(finding["rule"]),
        )
    )
    return findings


def _source_files(workspace: Path, suffix: str) -> list[Path]:
    paths: list[Path] = []
    for directory, directory_names, file_names in os.walk(
        workspace,
        topdown=True,
        followlinks=False,
    ):
        directory_names[:] = sorted(
            name
            for name in directory_names
            if name not in _SKIPPED_DIRECTORIES
            and not name.startswith(".nayamnyam-hidden-")
        )
        for file_name in sorted(file_names):
            if file_name.endswith(suffix):
                paths.append(Path(directory) / file_name)
    return paths


def _project_findings(language: str, workspace: Path) -> list[str]:
    expected_suffix = ".fsproj" if language == "fsharp" else ".csproj"
    projects = sorted(
        (
            path
            for path in workspace.rglob(f"*{expected_suffix}")
            if not any(part in _SKIPPED_DIRECTORIES for part in path.parts)
        ),
        key=lambda path: path.as_posix(),
    )
    if not projects:
        return [f"no {expected_suffix} projects found"]

    build_files = sorted(
        {
            *projects,
            *(
                path
                for pattern in ("*.props", "*.targets")
                for path in workspace.rglob(pattern)
                if not any(part in _SKIPPED_DIRECTORIES for part in path.parts)
            ),
        },
        key=lambda path: path.as_posix(),
    )
    findings: list[str] = []
    required = {
        "TargetFramework": "net10.0",
        "TreatWarningsAsErrors": "true",
        "Deterministic": "true",
        "ContinuousIntegrationBuild": "true",
    }
    if language == "csharp":
        required["Nullable"] = "enable"

    for build_file in build_files:
        relative = _relative(build_file, workspace)
        root = ElementTree.parse(build_file).getroot()
        elements: dict[str, list[str]] = {}
        for element in root.iter():
            name = element.tag.rsplit("}", 1)[-1]
            text = (element.text or "").strip()
            elements.setdefault(name, []).append(text)
        if build_file in projects:
            for property_name, expected_value in required.items():
                actual_values = elements.get(property_name, [])
                if actual_values != [expected_value]:
                    findings.append(
                        f"{relative}: {property_name} must occur once as "
                        f"{expected_value!r}, found {actual_values!r}"
                    )
        else:
            for property_name, expected_value in required.items():
                actual_values = elements.get(property_name, [])
                if any(value != expected_value for value in actual_values):
                    findings.append(
                        f"{relative}: {property_name} may not override "
                        f"{expected_value!r}, found {actual_values!r}"
                    )
        for suppression in ("NoWarn", "WarningsNotAsErrors"):
            if suppression in elements:
                findings.append(f"{relative}: {suppression} is forbidden")
        if "PackageReference" in elements:
            findings.append(f"{relative}: PackageReference is forbidden")
    return sorted(findings)


def _protected_file_changes(
    layout: WorkspaceLayout,
) -> tuple[list[str], list[str]]:
    try:
        git_root = Path(
            _git(layout.workspace, "rev-parse", "--show-toplevel").strip()
        ).resolve()
        public_relative = layout.public_root.resolve().relative_to(git_root)
        if layout.isolated:
            roots = [
                line
                for line in _git(
                    layout.workspace,
                    "rev-list",
                    "--max-parents=0",
                    "--reverse",
                    "HEAD",
                ).splitlines()
                if line
            ]
            if len(roots) != 1:
                return [], [
                    "isolated workspace must have exactly one root baseline commit"
                ]
            baseline = roots[0]
        else:
            baseline = "HEAD"

        changed = set(
            _nonempty_lines(
                _git(
                    layout.workspace,
                    "diff",
                    "--name-only",
                    "--diff-filter=ACDMRTUXB",
                    baseline,
                    "--",
                    public_relative.as_posix(),
                )
            )
        )
        changed.update(
            _nonempty_lines(
                _git(
                    layout.workspace,
                    "ls-files",
                    "--others",
                    "--exclude-standard",
                    "--",
                    public_relative.as_posix(),
                )
            )
        )
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        return [], [f"could not verify shared/public against Git baseline: {error}"]

    normalized = []
    prefix = public_relative.as_posix().rstrip("/") + "/"
    for path in changed:
        if path == public_relative.as_posix():
            normalized.append("shared/public")
        elif path.startswith(prefix):
            normalized.append("shared/public/" + path[len(prefix) :])
        else:
            normalized.append(path)
    return sorted(set(normalized)), []


def _git(cwd: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ("git", *arguments),
        cwd=cwd,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return completed.stdout


def _nonempty_lines(value: str) -> list[str]:
    return [line for line in value.splitlines() if line]


def _relative(path: Path, root: Path) -> str:
    return Path(os.path.relpath(path, root)).as_posix()


def _parse_arguments(arguments: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--language", required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    return parser.parse_args(arguments)


def main(arguments: list[str] | None = None) -> int:
    parsed = _parse_arguments(arguments)
    try:
        result = run_public_checks(
            parsed.language,
            parsed.workspace,
            output_path=parsed.output,
        )
    except PublicCheckError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    output = parsed.output or parsed.workspace.resolve() / "public-checks.json"
    print(output.resolve())
    return 0 if bool(result["passed"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
