"""Prepare an isolated language-spike workspace without starting a trial."""

import argparse
from dataclasses import dataclass
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from evaluator.common import load_config


SUPPORTED_LANGUAGES = ("fsharp", "csharp")
BASELINE_COMMIT_MESSAGE = "chore(spike): initialize trial baseline"
GIT_AUTHOR_NAME = "Nayamnyam Spike Runner"
GIT_AUTHOR_EMAIL = "nayamnyam-spike@example.invalid"
_EXCLUDED_DIRECTORIES = frozenset({".git", "bin", "obj", "__pycache__", "evaluator", "results"})
_EXCLUDED_SUFFIXES = (".pyc", ".jsonl", ".log")
_SAFE_RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]*\Z")


class WorkspacePreparationError(ValueError):
    """Raised when an isolated workspace cannot be prepared safely."""


@dataclass(frozen=True, slots=True)
class LogPaths:
    stdout: Path
    stderr: Path


def prepare_workspace(source: Path, language: str, destination: Path) -> Path:
    """Copy one baseline and the public bundle into a fresh Git repository."""
    source = source.resolve()
    destination = destination.resolve()
    if language not in SUPPORTED_LANGUAGES:
        raise WorkspacePreparationError(
            f"unsupported language: {language!r}; expected one of: {', '.join(SUPPORTED_LANGUAGES)}"
        )

    baseline = source / language
    public_bundle = source / "shared" / "public"
    if not baseline.is_dir() or baseline.is_symlink():
        raise WorkspacePreparationError(f"selected baseline is missing: {baseline}")
    if not public_bundle.is_dir() or public_bundle.is_symlink():
        raise WorkspacePreparationError(f"shared public bundle is missing: {public_bundle}")
    if destination == source or destination.is_relative_to(source):
        raise WorkspacePreparationError(f"destination must be outside source experiment tree: {destination}")
    if destination.exists() and (not destination.is_dir() or any(destination.iterdir())):
        raise WorkspacePreparationError(f"destination must be new or empty: {destination}")

    _validate_source_tree(baseline)
    _validate_source_tree(public_bundle)

    destination.mkdir(parents=True, exist_ok=True)
    _copy_tree_contents(baseline, destination)
    _copy_tree_contents(public_bundle, destination / "shared" / "public")
    _initialize_repository(destination)
    return destination


def load_prompt(workspace: Path) -> bytes:
    """Load the two public prompt documents in their fixed order."""
    public_bundle = workspace / "shared" / "public"
    agents = _normalize_document((public_bundle / "AGENTS.md").read_bytes())
    maintenance_task = _normalize_document((public_bundle / "maintenance-task.md").read_bytes())
    return agents + b"\n\n" + maintenance_task + b"\n"


def build_codex_command(config_path: Path, prompt: bytes) -> tuple[str, ...]:
    """Build the frozen, single-invocation Codex command."""
    config = load_config(config_path)
    try:
        prompt_text = prompt.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ValueError("prompt must be UTF-8") from error
    return (
        "codex",
        "exec",
        "--json",
        "--model",
        config.model,
        "--sandbox",
        config.sandbox,
        prompt_text,
    )


def configured_timeout(config_path: Path) -> int:
    """Return the timeout from the strict frozen experiment configuration."""
    return load_config(config_path).timeout_seconds


def derive_log_paths(workspace: Path, results_directory: Path, run_id: str) -> LogPaths:
    """Derive non-mutating paths for a trial's stdout and stderr logs."""
    if _SAFE_RUN_ID.fullmatch(run_id) is None:
        raise ValueError(f"unsafe run ID: {run_id!r}")

    workspace = workspace.resolve()
    results_directory = results_directory.resolve()
    if (
        results_directory == workspace
        or results_directory.is_relative_to(workspace)
        or workspace.is_relative_to(results_directory)
    ):
        raise ValueError(f"results directory must be outside workspace: {results_directory}")

    run_directory = results_directory / run_id
    return LogPaths(
        stdout=run_directory / "events.jsonl",
        stderr=run_directory / "stderr.log",
    )


def _normalize_document(contents: bytes) -> bytes:
    return contents.replace(b"\r\n", b"\n").replace(b"\r", b"\n").rstrip(b"\n")


def _validate_source_tree(root: Path) -> None:
    for current_directory, directory_names, file_names in os.walk(root, topdown=True, followlinks=False):
        current_path = Path(current_directory)
        directory_names.sort()
        file_names.sort()

        included_directories = []
        for name in directory_names:
            path = current_path / name
            if path.is_symlink():
                raise WorkspacePreparationError(f"source tree contains forbidden symlink: {path}")
            if not _is_excluded_directory(name):
                included_directories.append(name)
        directory_names[:] = included_directories

        for name in file_names:
            path = current_path / name
            if path.is_symlink():
                raise WorkspacePreparationError(f"source tree contains forbidden symlink: {path}")
            if not path.is_file():
                raise WorkspacePreparationError(f"source tree contains unsupported entry: {path}")


def _copy_tree_contents(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    for source_path in sorted(source.iterdir(), key=lambda path: path.name):
        if source_path.is_symlink():
            raise WorkspacePreparationError(f"source tree contains forbidden symlink: {source_path}")
        if source_path.is_dir():
            if _is_excluded_directory(source_path.name):
                continue
            _copy_tree_contents(source_path, destination / source_path.name)
            continue
        if not source_path.is_file():
            raise WorkspacePreparationError(f"source tree contains unsupported entry: {source_path}")
        if _is_excluded_file(source_path.name):
            continue
        shutil.copy2(source_path, destination / source_path.name, follow_symlinks=False)


def _is_excluded_directory(name: str) -> bool:
    return name.startswith(".") or name in _EXCLUDED_DIRECTORIES


def _is_excluded_file(name: str) -> bool:
    return name.startswith(".") or name.endswith(_EXCLUDED_SUFFIXES)


def _initialize_repository(destination: Path) -> None:
    _run_git(destination, "init", "--initial-branch=main")
    _run_git(destination, "config", "--local", "user.name", GIT_AUTHOR_NAME)
    _run_git(destination, "config", "--local", "user.email", GIT_AUTHOR_EMAIL)
    _run_git(destination, "config", "--local", "core.autocrlf", "false")
    _run_git(destination, "add", "--all")
    environment = os.environ.copy()
    environment.update(
        {
            "GIT_AUTHOR_DATE": "2000-01-01T00:00:00+00:00",
            "GIT_COMMITTER_DATE": "2000-01-01T00:00:00+00:00",
        }
    )
    _run_git(
        destination,
        "-c",
        "commit.gpgSign=false",
        "-c",
        "core.hooksPath=/dev/null",
        "commit",
        "--allow-empty",
        "--message",
        BASELINE_COMMIT_MESSAGE,
        environment=environment,
    )


def _run_git(
    repository: Path,
    *arguments: str,
    environment: dict[str, str] | None = None,
) -> None:
    command = ("git", *arguments)
    try:
        subprocess.run(
            command,
            cwd=repository,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=environment,
        )
    except FileNotFoundError as error:
        raise WorkspacePreparationError("git executable was not found") from error
    except subprocess.CalledProcessError as error:
        detail = error.stderr.strip() or error.stdout.strip() or f"exit code {error.returncode}"
        raise WorkspacePreparationError(f"git command failed ({' '.join(command)}): {detail}") from error


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--language", required=True, choices=SUPPORTED_LANGUAGES)
    parser.add_argument("--destination", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        prepared = prepare_workspace(arguments.source, arguments.language, arguments.destination)
    except (WorkspacePreparationError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    print(prepared)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
