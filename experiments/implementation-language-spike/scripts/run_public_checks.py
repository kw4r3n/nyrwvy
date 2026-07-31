#!/usr/bin/env python3
"""Run reproducible public conformance checks for one language-spike workspace."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import errno
import json
import os
from pathlib import Path
import re
import secrets
import stat
import subprocess
import sys
import time
from typing import Callable, Iterable
import xml.etree.ElementTree as ElementTree


EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
SUPPORTED_LANGUAGES = ("fsharp", "csharp")
PUBLIC_CHECK_NAMES = (
    "build",
    "baseline_tests",
    "canonical_fixture",
    "canonical_contract",
    "summary_fixture",
    "summary_contract",
    "forbidden_patterns",
    "project_settings",
    "protected_files",
)
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
        (
            "nullable_suppression",
            re.compile(r"#nullable\s+(?:disable|restore)\b", re.IGNORECASE),
        ),
        ("hidden_default_case", re.compile(r"\bdefault\s*:")),
        ("hidden_discard_arm", re.compile(r"\b_\s*=>")),
    ),
}


class PublicCheckError(ValueError):
    """Raised when public checks cannot be started safely."""


@dataclass(frozen=True, slots=True)
class WorkspaceLayout:
    language: str
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
    output = _absolute_path(output_path or layout.workspace / "public-checks.json")
    artifact_root = layout.workspace / ".nayamnyam-evaluation" / "public"
    validate_artifact_paths(
        (
            output,
            *check_artifact_paths(artifact_root, PUBLIC_CHECK_NAMES),
        )
    )

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
            read_artifact_bytes(canonical_output),
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
            read_artifact_bytes(summary_output),
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
        language=language,
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
    validator: Callable[[], str],
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
    serialized = json.dumps(
        value,
        ensure_ascii=False,
        indent=2,
        separators=(",", ": "),
    )
    atomic_write_bytes(path, (serialized + "\n").encode("utf-8"))


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
    atomic_write_bytes(stdout_path, stdout)
    atomic_write_bytes(stderr_path, stderr)
    return {
        "name": name,
        "passed": exit_code == 0,
        "command": list(command),
        "exit_code": exit_code,
        "duration_ms": duration_ms,
        "stdout_path": _relative(stdout_path, workspace),
        "stderr_path": _relative(stderr_path, workspace),
    }


def check_artifact_paths(
    artifact_root: Path,
    names: Iterable[str],
) -> tuple[Path, ...]:
    """Return the fixed stdout/stderr artifact paths for named checks."""
    return tuple(
        path
        for name in names
        for path in (
            artifact_root / f"{name}.stdout",
            artifact_root / f"{name}.stderr",
        )
    )


def validate_artifact_paths(paths: Iterable[Path]) -> None:
    """Reject unsafe artifact parents and existing non-regular destinations."""
    for path in sorted(
        {_absolute_path(path) for path in paths},
        key=lambda item: item.as_posix(),
    ):
        parent_file_descriptor = _open_artifact_parent(path)
        try:
            _validate_artifact_destination(
                parent_file_descriptor,
                path.name,
                path,
            )
        finally:
            os.close(parent_file_descriptor)


def atomic_write_bytes(path: Path, contents: bytes) -> None:
    """Atomically replace one regular artifact without following symlinks."""
    destination = _absolute_path(path)
    parent_file_descriptor = _open_artifact_parent(destination)
    temporary_name: str | None = None
    temporary_file_descriptor: int | None = None
    try:
        _validate_artifact_destination(
            parent_file_descriptor,
            destination.name,
            destination,
        )
        for _ in range(128):
            temporary_name = (
                f".{destination.name}.{secrets.token_hex(12)}.tmp"
            )
            try:
                temporary_file_descriptor = os.open(
                    temporary_name,
                    os.O_WRONLY
                    | os.O_CREAT
                    | os.O_EXCL
                    | os.O_CLOEXEC
                    | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=parent_file_descriptor,
                )
                break
            except FileExistsError:
                temporary_name = None
        if temporary_file_descriptor is None or temporary_name is None:
            raise OSError(
                errno.EEXIST,
                "could not allocate exclusive artifact temporary",
            )
        _write_all(temporary_file_descriptor, contents)
        os.fsync(temporary_file_descriptor)
        os.close(temporary_file_descriptor)
        temporary_file_descriptor = None
        _validate_artifact_destination(
            parent_file_descriptor,
            destination.name,
            destination,
        )
        os.replace(
            temporary_name,
            destination.name,
            src_dir_fd=parent_file_descriptor,
            dst_dir_fd=parent_file_descriptor,
        )
        temporary_name = None
        os.fsync(parent_file_descriptor)
    except PublicCheckError:
        raise
    except OSError as error:
        raise PublicCheckError(
            f"could not write artifact safely: {destination}: {error}"
        ) from error
    finally:
        if temporary_file_descriptor is not None:
            os.close(temporary_file_descriptor)
        if temporary_name is not None:
            try:
                os.unlink(temporary_name, dir_fd=parent_file_descriptor)
            except FileNotFoundError:
                pass
        os.close(parent_file_descriptor)


def read_artifact_bytes(path: Path) -> bytes:
    """Read one regular artifact without following its path or final component."""
    artifact = _absolute_path(path)
    parent_file_descriptor = _open_artifact_parent(
        artifact,
        create_missing=False,
    )
    file_descriptor: int | None = None
    try:
        expected = _artifact_metadata(
            parent_file_descriptor,
            artifact.name,
            artifact,
        )
        if not stat.S_ISREG(expected.st_mode):
            raise PublicCheckError(
                f"artifact destination is not a regular file: {artifact}"
            )
        try:
            file_descriptor = os.open(
                artifact.name,
                os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW,
                dir_fd=parent_file_descriptor,
            )
        except OSError as error:
            raise PublicCheckError(
                f"could not open artifact safely: {artifact}"
            ) from error
        actual = os.fstat(file_descriptor)
        if (
            not stat.S_ISREG(actual.st_mode)
            or actual.st_dev != expected.st_dev
            or actual.st_ino != expected.st_ino
        ):
            raise PublicCheckError(
                f"artifact changed while opening: {artifact}"
            )
        chunks: list[bytes] = []
        while True:
            chunk = os.read(file_descriptor, 65536)
            if not chunk:
                return b"".join(chunks)
            chunks.append(chunk)
    finally:
        if file_descriptor is not None:
            os.close(file_descriptor)
        os.close(parent_file_descriptor)


def _open_artifact_parent(
    destination: Path,
    *,
    create_missing: bool = True,
) -> int:
    parent = destination.parent
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    try:
        file_descriptor = os.open(parent.anchor, flags)
    except OSError as error:
        raise PublicCheckError(
            f"artifact directory component is not a real directory: "
            f"{Path(parent.anchor)}"
        ) from error
    display = Path(parent.anchor)
    try:
        for component in parent.parts[1:]:
            display /= component
            if create_missing:
                try:
                    os.mkdir(component, mode=0o700, dir_fd=file_descriptor)
                except FileExistsError:
                    pass
            try:
                next_file_descriptor = os.open(
                    component,
                    flags,
                    dir_fd=file_descriptor,
                )
            except OSError as error:
                raise PublicCheckError(
                    "artifact directory component is not a real directory: "
                    f"{display}"
                ) from error
            os.close(file_descriptor)
            file_descriptor = next_file_descriptor
        return file_descriptor
    except BaseException:
        os.close(file_descriptor)
        raise


def _validate_artifact_destination(
    parent_file_descriptor: int,
    name: str,
    destination: Path,
) -> None:
    try:
        details = _artifact_metadata(
            parent_file_descriptor,
            name,
            destination,
        )
    except FileNotFoundError:
        return
    if stat.S_ISLNK(details.st_mode):
        raise PublicCheckError(
            f"artifact destination is a symlink: {destination}"
        )
    if not stat.S_ISREG(details.st_mode):
        raise PublicCheckError(
            f"artifact destination is not a regular file: {destination}"
        )


def _artifact_metadata(
    parent_file_descriptor: int,
    name: str,
    destination: Path,
) -> os.stat_result:
    try:
        return os.stat(
            name,
            dir_fd=parent_file_descriptor,
            follow_symlinks=False,
        )
    except FileNotFoundError:
        raise
    except OSError as error:
        raise PublicCheckError(
            f"could not inspect artifact destination: {destination}"
        ) from error


def _write_all(file_descriptor: int, contents: bytes) -> None:
    remaining = memoryview(contents)
    while remaining:
        try:
            written = os.write(file_descriptor, remaining)
        except InterruptedError:
            continue
        if written <= 0:
            raise OSError(
                errno.EIO,
                "artifact temporary write made no progress",
            )
        remaining = remaining[written:]


def _absolute_path(path: Path) -> Path:
    return Path(os.path.abspath(path))


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
    if language not in SUPPORTED_LANGUAGES:
        raise PublicCheckError(f"unsupported language for source scan: {language}")
    findings: list[dict[str, object]] = []
    for source_language, suffix in (("fsharp", ".fs"), ("csharp", ".cs")):
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
                for rule, pattern in _FORBIDDEN_PATTERNS[source_language]:
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
    projects = _workspace_files(
        workspace,
        (".fsproj", ".csproj"),
    )
    findings: list[str] = []
    if not any(path.suffix.lower() == expected_suffix for path in projects):
        findings.append(f"no {expected_suffix} projects found")

    seed_build_files = {
        *projects,
        *_workspace_files(workspace, (".props", ".targets")),
    }
    build_files, import_findings = _discover_build_inputs(
        workspace,
        seed_build_files,
    )
    findings.extend(import_findings)
    common_required = {
        "TargetFramework": "net10.0",
        "TreatWarningsAsErrors": "true",
        "Deterministic": "true",
        "ContinuousIntegrationBuild": "true",
    }

    for build_file in build_files:
        relative = _relative(build_file, workspace)
        try:
            details = build_file.lstat()
        except OSError as error:
            findings.append(f"{relative}: could not inspect build input: {error}")
            continue
        if stat.S_ISLNK(details.st_mode):
            findings.append(f"{relative}: build input symlink is forbidden")
            continue
        if not stat.S_ISREG(details.st_mode):
            findings.append(f"{relative}: build input must be a regular file")
            continue
        try:
            root = ElementTree.parse(build_file).getroot()
        except (OSError, ElementTree.ParseError) as error:
            findings.append(f"{relative}: build input is not valid XML: {error}")
            continue
        elements, compiler_text, conditional_elements = (
            _collect_build_elements(root)
        )
        is_project = build_file.suffix.lower() in {".fsproj", ".csproj"}
        required = dict(common_required)
        if build_file.suffix.lower() == ".csproj":
            required["Nullable"] = "enable"
        if is_project:
            for property_name, expected_value in required.items():
                actual_values = elements.get(property_name.casefold(), [])
                if actual_values != [expected_value]:
                    findings.append(
                        f"{relative}: {property_name} must occur once as "
                        f"{expected_value!r}, found {actual_values!r}"
                    )
                if property_name.casefold() in conditional_elements:
                    findings.append(
                        f"{relative}: {property_name} must be unconditional"
                    )
        else:
            for property_name, expected_value in common_required.items():
                actual_values = elements.get(property_name.casefold(), [])
                if any(value != expected_value for value in actual_values):
                    findings.append(
                        f"{relative}: {property_name} may not override "
                        f"{expected_value!r}, found {actual_values!r}"
                    )
        for suppression in (
            "NoWarn",
            "WarningsNotAsErrors",
            "WarningsAsMessages",
        ):
            if suppression.casefold() in elements:
                findings.append(f"{relative}: {suppression} is forbidden")
        warning_levels = elements.get("warninglevel", [])
        if any(value.strip() == "0" for value in warning_levels):
            findings.append(f"{relative}: WarningLevel=0 is forbidden")
        nullable_values = elements.get("nullable", [])
        if any(value.casefold() != "enable" for value in nullable_values):
            findings.append(f"{relative}: Nullable suppression is forbidden")
        if any(
            re.search(r"(?:--|/)nowarn(?::|\s|$)", value, re.IGNORECASE)
            for value in compiler_text
        ):
            findings.append(f"{relative}: compiler nowarn flag is forbidden")
        if "packagereference" in elements:
            findings.append(f"{relative}: PackageReference is forbidden")
    return sorted(set(findings))


def _collect_build_elements(
    root: ElementTree.Element,
) -> tuple[dict[str, list[str]], list[str], set[str]]:
    elements: dict[str, list[str]] = {}
    compiler_text: list[str] = []
    conditional_elements: set[str] = set()

    def visit(
        element: ElementTree.Element,
        inherited_condition: bool,
    ) -> None:
        name = element.tag.rsplit("}", 1)[-1].casefold()
        has_condition = inherited_condition or any(
            key.rsplit("}", 1)[-1].casefold() == "condition"
            for key in element.attrib
        )
        text = (element.text or "").strip()
        elements.setdefault(name, []).append(text)
        compiler_text.append(text)
        compiler_text.extend(element.attrib.values())
        if has_condition:
            conditional_elements.add(name)
        for child in element:
            visit(child, has_condition)

    visit(root, False)
    return elements, compiler_text, conditional_elements


def _workspace_files(
    workspace: Path,
    suffixes: tuple[str, ...],
) -> list[Path]:
    normalized_suffixes = tuple(suffix.casefold() for suffix in suffixes)
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
            and not (Path(directory) / name).is_symlink()
        )
        for file_name in sorted(file_names):
            if file_name.casefold().endswith(normalized_suffixes):
                paths.append(Path(directory) / file_name)
    return paths


def _discover_build_inputs(
    workspace: Path,
    seeds: Iterable[Path],
) -> tuple[list[Path], list[str]]:
    pending = sorted(
        {_absolute_path(path) for path in seeds},
        key=lambda path: path.as_posix(),
    )
    discovered: set[Path] = set()
    findings: list[str] = []
    while pending:
        build_file = pending.pop(0)
        if build_file in discovered:
            continue
        discovered.add(build_file)
        relative = _relative(build_file, workspace)
        if not build_file.is_relative_to(workspace):
            findings.append(
                f"{relative}: imported build input must remain inside workspace"
            )
            continue
        symlink_component = _first_symlink_component(build_file, workspace)
        if symlink_component is not None:
            findings.append(
                f"{relative}: build input path contains symlink: "
                f"{_relative(symlink_component, workspace)}"
            )
            continue
        try:
            details = build_file.lstat()
        except FileNotFoundError:
            findings.append(f"{relative}: imported build input is missing")
            continue
        except OSError as error:
            findings.append(f"{relative}: could not inspect build input: {error}")
            continue
        if not stat.S_ISREG(details.st_mode):
            findings.append(f"{relative}: build input must be a regular file")
            continue
        try:
            root = ElementTree.parse(build_file).getroot()
        except (OSError, ElementTree.ParseError):
            continue
        for element in root.iter():
            if element.tag.rsplit("}", 1)[-1].casefold() != "import":
                continue
            raw_project = element.attrib.get("Project")
            if raw_project is None:
                raw_project = element.attrib.get("project")
            if raw_project is None or not raw_project.strip():
                findings.append(f"{relative}: Import must name a build input")
                continue
            imported, error = _resolve_imported_build_input(
                workspace,
                build_file,
                raw_project,
            )
            if error is not None:
                findings.append(f"{relative}: {error}")
                continue
            assert imported is not None
            if imported not in discovered and imported not in pending:
                pending.append(imported)
                pending.sort(key=lambda path: path.as_posix())
    return sorted(discovered, key=lambda path: path.as_posix()), sorted(findings)


def _resolve_imported_build_input(
    workspace: Path,
    importing_file: Path,
    raw_project: str,
) -> tuple[Path | None, str | None]:
    project = re.sub(
        re.escape("$(MSBuildThisFileDirectory)"),
        str(importing_file.parent) + os.sep,
        raw_project.strip(),
        flags=re.IGNORECASE,
    ).replace("\\", os.sep)
    if "$(" in project or any(character in project for character in "*?;"):
        return None, (
            "Import uses an uninspectable dynamic or wildcard path: "
            f"{raw_project!r}"
        )
    imported = Path(project)
    if not imported.is_absolute():
        imported = importing_file.parent / imported
    imported = _absolute_path(imported)
    if not imported.is_relative_to(workspace):
        return None, (
            f"Import escapes the workspace: {raw_project!r}"
        )
    return imported, None


def _first_symlink_component(path: Path, root: Path) -> Path | None:
    current = root
    for component in path.relative_to(root).parts:
        current /= component
        try:
            if stat.S_ISLNK(current.lstat().st_mode):
                return current
        except FileNotFoundError:
            return None
    return None


def _protected_file_changes(
    layout: WorkspaceLayout,
) -> tuple[list[str], list[str]]:
    try:
        changes = _compare_protected_tree(
            expected_root=EXPERIMENT_ROOT / "shared" / "public",
            actual_root=layout.public_root,
            display_prefix=Path("shared/public"),
            allow_additions=False,
        )
        changes.extend(
            _compare_protected_tree(
                expected_root=(
                    EXPERIMENT_ROOT
                    / layout.language
                    / "tests"
                    / "Nayamnyam.Spike.Tests"
                ),
                actual_root=(
                    layout.workspace
                    / "tests"
                    / "Nayamnyam.Spike.Tests"
                ),
                display_prefix=Path("tests/Nayamnyam.Spike.Tests"),
                allow_additions=True,
            )
        )
        return sorted(set(changes)), []
    except (OSError, ValueError) as error:
        return [], [
            "could not verify evaluator-owned protected inventory: "
            f"{error}"
        ]


def _compare_protected_tree(
    *,
    expected_root: Path,
    actual_root: Path,
    display_prefix: Path,
    allow_additions: bool,
) -> list[str]:
    expected = _tree_inventory(expected_root)
    actual = _tree_inventory(actual_root)
    paths = set(expected)
    if not allow_additions:
        paths.update(actual)
    return [
        (display_prefix / path).as_posix()
        for path in sorted(paths)
        if expected.get(path) != actual.get(path)
    ]


def _tree_inventory(
    root: Path,
) -> dict[str, tuple[str, int, bytes | str]]:
    root_details = root.lstat()
    if stat.S_ISLNK(root_details.st_mode) or not stat.S_ISDIR(root_details.st_mode):
        return {Path(".").as_posix(): ("invalid-root", root_details.st_mode, b"")}
    entries: dict[str, tuple[str, int, bytes | str]] = {}
    for directory, directory_names, file_names in os.walk(
        root,
        topdown=True,
        followlinks=False,
    ):
        directory_path = Path(directory)
        retained_directories: list[str] = []
        for name in sorted(directory_names):
            if name in _SKIPPED_DIRECTORIES:
                continue
            path = directory_path / name
            details = path.lstat()
            relative = path.relative_to(root).as_posix()
            if stat.S_ISLNK(details.st_mode):
                entries[relative] = (
                    "symlink",
                    details.st_mode & 0o111,
                    os.readlink(path),
                )
            elif stat.S_ISDIR(details.st_mode):
                retained_directories.append(name)
            else:
                entries[relative] = (
                    "non-regular",
                    details.st_mode & 0o111,
                    b"",
                )
        directory_names[:] = retained_directories
        for name in sorted(file_names):
            path = directory_path / name
            details = path.lstat()
            relative = path.relative_to(root).as_posix()
            if stat.S_ISLNK(details.st_mode):
                entries[relative] = (
                    "symlink",
                    details.st_mode & 0o111,
                    os.readlink(path),
                )
            elif stat.S_ISREG(details.st_mode):
                entries[relative] = (
                    "regular",
                    details.st_mode & 0o111,
                    path.read_bytes(),
                )
            else:
                entries[relative] = (
                    "non-regular",
                    details.st_mode & 0o111,
                    b"",
                )
    return entries


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
    print(_absolute_path(output))
    return 0 if bool(result["passed"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
