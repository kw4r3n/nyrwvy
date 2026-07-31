#!/usr/bin/env python3
"""Run public and marker-gated hidden evaluation for one trial workspace."""

from __future__ import annotations

import argparse
from html import escape
from pathlib import Path
import shutil
import sys
import tempfile


EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from scripts.run_public_checks import (  # noqa: E402
    PUBLIC_CHECK_NAMES,
    PublicCheckError,
    atomic_write_bytes,
    capture_command,
    check_artifact_paths,
    read_artifact_bytes,
    resolve_workspace,
    run_public_checks,
    validate_artifact_paths,
    write_json,
)


_EXPECTED_HIDDEN_PASSES = (
    "duplicate-name parameter obligations",
    "same-name function and type renames are no change",
    "schema v2 migration and canonical stability",
    "type rename preserves identity and references",
)


def evaluate(
    language: str,
    workspace: Path,
    *,
    output_path: Path | None = None,
) -> dict[str, object]:
    """Evaluate one exited trial without consulting any agent-authored claim."""
    layout = resolve_workspace(language, workspace)
    marker = layout.workspace / "codex-exited.marker"
    if marker.is_symlink() or not marker.is_file():
        raise PublicCheckError(
            f"hidden evaluation requires regular marker file: {marker}"
        )

    public_output = layout.workspace / "public-checks.json"
    hidden_output = layout.workspace / "hidden-checks.json"
    evaluation_output = output_path or layout.workspace / "evaluation.json"
    public_artifact_root = (
        layout.workspace / ".nayamnyam-evaluation" / "public"
    )
    hidden_artifact_root = (
        layout.workspace / ".nayamnyam-evaluation" / "hidden"
    )
    validate_artifact_paths(
        (
            public_output,
            hidden_output,
            evaluation_output,
            *check_artifact_paths(public_artifact_root, PUBLIC_CHECK_NAMES),
            *check_artifact_paths(
                hidden_artifact_root,
                ("hidden_semantic_checks",),
            ),
        )
    )
    public_result = run_public_checks(
        language,
        layout.workspace,
        output_path=public_output,
    )
    hidden_result = _run_hidden_checks(language, layout.workspace, layout.core_project)
    write_json(hidden_output, hidden_result)

    result: dict[str, object] = {
        "schema_version": 1,
        "language": language,
        "public_passed": bool(public_result["passed"]),
        "hidden_passed": bool(hidden_result["passed"]),
        "public_failures": list(public_result["failures"]),
        "hidden_failures": list(hidden_result["failures"]),
        "forbidden_findings": list(public_result["forbidden_findings"]),
        "modified_protected_files": list(
            public_result["modified_protected_files"]
        ),
    }
    write_json(evaluation_output, result)
    return result


def _run_hidden_checks(
    language: str,
    workspace: Path,
    core_project: Path,
) -> dict[str, object]:
    source = Path(__file__).resolve().parent / f"{language}-hidden"
    if not source.is_dir():
        raise PublicCheckError(f"hidden evaluator source is missing: {source}")
    extension = "fsproj" if language == "fsharp" else "csproj"
    project_name = f"Nayamnyam.Spike.Hidden.{extension}"
    source_project = source / project_name
    if not source_project.is_file():
        raise PublicCheckError(f"hidden evaluator project is missing: {source_project}")

    artifact_root = workspace / ".nayamnyam-evaluation" / "hidden"
    with tempfile.TemporaryDirectory(
        prefix=".nayamnyam-hidden-",
        dir=workspace,
    ) as temporary_directory:
        hidden_root = Path(temporary_directory) / "project"
        shutil.copytree(source, hidden_root)
        hidden_project = hidden_root / project_name
        project_text = hidden_project.read_text(encoding="utf-8")
        if project_text.count("__CORE_PROJECT__") != 1:
            raise PublicCheckError(
                f"hidden project must contain one core-project placeholder: {source_project}"
            )
        project_text = project_text.replace(
            "__CORE_PROJECT__",
            escape(str(core_project.resolve()), quote=True),
        )
        hidden_project.write_text(
            project_text,
            encoding="utf-8",
            newline="\n",
        )
        command = (
            "dotnet",
            "run",
            "--project",
            hidden_project.relative_to(workspace).as_posix(),
            "--configuration",
            "Release",
        )
        check = capture_command(
            name="hidden_semantic_checks",
            command=command,
            display_command=(
                "dotnet",
                "run",
                "--project",
                f"<hidden-project>/{project_name}",
                "--configuration",
                "Release",
            ),
            cwd=workspace,
            workspace=workspace,
            artifact_root=artifact_root,
        )

    stdout_path = workspace / str(check["stdout_path"])
    stderr_path = workspace / str(check["stderr_path"])
    stdout_lines = read_artifact_bytes(stdout_path).decode(
        "utf-8",
        errors="replace",
    ).splitlines()
    stderr_bytes = read_artifact_bytes(stderr_path)
    stderr_lines = stderr_bytes.decode(
        "utf-8",
        errors="replace",
    ).splitlines()
    reported_passes = sorted(
        line.removeprefix("PASS ")
        for line in stdout_lines
        if line.startswith("PASS ")
    )
    reported_failures = sorted(
        _failure_name(line)
        for line in (*stdout_lines, *stderr_lines)
        if line.startswith("FAIL ")
    )
    expected = list(_EXPECTED_HIDDEN_PASSES)
    if reported_passes != expected:
        missing = sorted(set(expected) - set(reported_passes))
        unexpected = sorted(set(reported_passes) - set(expected))
        details = []
        if missing:
            details.append("missing PASS lines: " + ", ".join(missing))
        if unexpected:
            details.append("unexpected PASS lines: " + ", ".join(unexpected))
        atomic_write_bytes(
            stderr_path,
            stderr_bytes + ("; ".join(details) + "\n").encode("utf-8"),
        )
        check["passed"] = False
    checks = [check]
    checks.sort(key=lambda item: str(item["name"]))
    failures = [
        str(item["name"])
        for item in checks
        if not bool(item["passed"])
    ]
    return {
        "schema_version": 1,
        "language": language,
        "passed": not failures,
        "checks": checks,
        "failures": failures,
        "reported_passes": reported_passes,
        "reported_failures": reported_failures,
    }


def _failure_name(line: str) -> str:
    failure = line.removeprefix("FAIL ")
    return failure.split(":", 1)[0]


def _parse_arguments(arguments: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--language", required=True)
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    return parser.parse_args(arguments)


def main(arguments: list[str] | None = None) -> int:
    parsed = _parse_arguments(arguments)
    try:
        result = evaluate(
            parsed.language,
            parsed.workspace,
            output_path=parsed.output,
        )
        output = parsed.output or parsed.workspace.resolve() / "evaluation.json"
        sys.stdout.write(
            read_artifact_bytes(output).decode("utf-8")
        )
    except PublicCheckError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return (
        0
        if bool(result["public_passed"]) and bool(result["hidden_passed"])
        else 1
    )


if __name__ == "__main__":
    raise SystemExit(main())
