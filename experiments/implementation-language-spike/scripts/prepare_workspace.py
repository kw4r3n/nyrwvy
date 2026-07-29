"""Prepare an isolated language-spike workspace without starting a trial."""

import argparse
from dataclasses import dataclass
import io
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile


EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from evaluator.common import load_config


SUPPORTED_LANGUAGES = ("fsharp", "csharp")
BASELINE_COMMIT_MESSAGE = "chore(spike): initialize trial baseline"
GIT_AUTHOR_NAME = "Nayamnyam Spike Runner"
GIT_AUTHOR_EMAIL = "nayamnyam-spike@example.invalid"
GIT_COMMIT_TIMESTAMP = "2000-01-01T00:00:00+00:00"
_EXCLUDED_PATH_PARTS = frozenset({".git", "bin", "obj", "__pycache__", "evaluator", "results"})
_EXCLUDED_SUFFIXES = (".pyc",)
_SAFE_RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]*\Z")


class WorkspacePreparationError(ValueError):
    """Raised when an isolated workspace cannot be prepared safely."""


@dataclass(frozen=True, slots=True)
class LogPaths:
    stdout: Path
    stderr: Path


@dataclass(frozen=True, slots=True)
class _CommittedSource:
    repository: Path
    baseline_tree: str
    public_tree: str


@dataclass(frozen=True, slots=True)
class _DestinationReservation:
    path: Path
    device: int
    inode: int
    created: bool


def prepare_workspace(source: Path, language: str, destination: Path) -> Path:
    """Export one committed baseline and public bundle into a fresh Git repository."""
    source = source.resolve()
    destination = Path(os.path.abspath(destination))
    if language not in SUPPORTED_LANGUAGES:
        raise WorkspacePreparationError(
            f"unsupported language: {language!r}; expected one of: {', '.join(SUPPORTED_LANGUAGES)}"
        )
    if destination.is_symlink():
        raise WorkspacePreparationError(f"destination must not be a symlink: {destination}")
    resolved_destination = destination.resolve(strict=False)
    if resolved_destination == source or resolved_destination.is_relative_to(source):
        raise WorkspacePreparationError(f"destination must be outside source experiment tree: {destination}")

    committed_source = _inspect_committed_source(source, language)
    reservation = _reserve_destination(destination)
    staging: Path | None = None
    try:
        staging = Path(
            tempfile.mkdtemp(
                prefix=f".{destination.name}.prepare-",
                dir=destination.parent,
            )
        )
        _extract_archive(
            _git_archive(committed_source.repository, committed_source.baseline_tree),
            staging,
        )
        _extract_archive(
            _git_archive(
                committed_source.repository,
                committed_source.public_tree,
                prefix="shared/public/",
            ),
            staging,
        )
        _initialize_repository(staging)
        _install_staging(staging, reservation)
        staging = None
        return destination
    finally:
        if staging is not None and staging.exists():
            shutil.rmtree(staging)
        _restore_absent_destination(reservation)


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
    normalized = contents.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    lines = normalized.split(b"\n")
    while lines and not lines[-1].strip(b" \t"):
        lines.pop()
    return b"\n".join(lines)


def _inspect_committed_source(source: Path, language: str) -> _CommittedSource:
    if not source.is_dir():
        raise WorkspacePreparationError(f"selected baseline is missing: {source / language}")
    repository_text = _git_text(source, "rev-parse", "--show-toplevel")
    repository = Path(repository_text).resolve()
    try:
        source_relative = source.relative_to(repository)
    except ValueError as error:
        raise WorkspacePreparationError(f"source experiment tree is outside its Git repository: {source}") from error

    baseline_relative = source_relative / language
    public_relative = source_relative / "shared" / "public"
    baseline_tree = baseline_relative.as_posix()
    public_tree = public_relative.as_posix()
    commit = _git_text(repository, "rev-parse", "--verify", "HEAD^{commit}")
    if _git_object_type(repository, f"{commit}:{baseline_tree}") != "tree":
        raise WorkspacePreparationError(f"selected baseline is missing: {source / language}")
    if _git_object_type(repository, f"{commit}:{public_tree}") != "tree":
        raise WorkspacePreparationError(f"shared public bundle is missing: {source / 'shared' / 'public'}")

    dirty = _git_text(
        repository,
        "status",
        "--porcelain=v1",
        "--untracked-files=no",
        "--",
        baseline_tree,
        public_tree,
    )
    if dirty:
        raise WorkspacePreparationError(f"tracked source inputs are dirty:\n{dirty}")
    return _CommittedSource(
        repository=repository,
        baseline_tree=f"{commit}:{baseline_tree}",
        public_tree=f"{commit}:{public_tree}",
    )


def _git_object_type(repository: Path, object_name: str) -> str | None:
    completed = _run_git_process(
        repository,
        "cat-file",
        "-t",
        object_name,
        check=False,
        text=True,
    )
    if completed.returncode != 0:
        return None
    assert isinstance(completed.stdout, str)
    return completed.stdout.strip()


def _git_archive(repository: Path, tree: str, *, prefix: str | None = None) -> bytes:
    arguments = ["archive", "--format=tar"]
    if prefix is not None:
        arguments.append(f"--prefix={prefix}")
    arguments.append(tree)
    completed = _run_git_process(repository, *arguments, text=False)
    assert isinstance(completed.stdout, bytes)
    return completed.stdout


def _extract_archive(archive_bytes: bytes, destination: Path) -> None:
    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:") as archive:
        members = archive.getmembers()
        for member in members:
            archive_path = _safe_archive_path(member)
            if _is_excluded_archive_path(archive_path):
                continue
            output_path = destination.joinpath(*archive_path.parts)
            if member.isdir():
                output_path.mkdir(parents=True, exist_ok=True)
                continue
            output_path.parent.mkdir(parents=True, exist_ok=True)
            try:
                source_file = archive.extractfile(member)
                if source_file is None:
                    raise WorkspacePreparationError(f"archive file has no contents: {member.name!r}")
                with source_file, output_path.open("xb") as output_file:
                    shutil.copyfileobj(source_file, output_file)
            except FileExistsError as error:
                raise WorkspacePreparationError(f"archive member collides with existing path: {member.name!r}") from error
            output_path.chmod(0o755 if member.mode & 0o111 else 0o644)


def _safe_archive_path(member: tarfile.TarInfo) -> PurePosixPath:
    name = member.name.rstrip("/")
    parts = name.split("/")
    if (
        not name
        or member.name.startswith("/")
        or "\\" in member.name
        or any(part in {"", ".", ".."} for part in parts)
    ):
        raise WorkspacePreparationError(f"archive contains unsafe path: {member.name!r}")
    if not member.isdir() and not member.isfile():
        raise WorkspacePreparationError(f"archive contains unsafe member type: {member.name!r}")
    return PurePosixPath(*parts)


def _is_excluded_archive_path(path: PurePosixPath) -> bool:
    return any(part in _EXCLUDED_PATH_PARTS for part in path.parts) or path.name.endswith(_EXCLUDED_SUFFIXES)


def _reserve_destination(destination: Path) -> _DestinationReservation:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.is_symlink():
        raise WorkspacePreparationError(f"destination must not be a symlink: {destination}")

    created = False
    if destination.exists():
        if not destination.is_dir() or any(destination.iterdir()):
            raise WorkspacePreparationError(f"destination must be new or empty: {destination}")
    else:
        try:
            destination.mkdir(mode=0o700)
            created = True
        except FileExistsError as error:
            raise WorkspacePreparationError(f"destination changed before preparation: {destination}") from error

    metadata = os.lstat(destination)
    if not stat.S_ISDIR(metadata.st_mode):
        raise WorkspacePreparationError(f"destination must be new or empty: {destination}")
    return _DestinationReservation(
        path=destination,
        device=metadata.st_dev,
        inode=metadata.st_ino,
        created=created,
    )


def _reservation_is_unchanged_empty(reservation: _DestinationReservation) -> bool:
    try:
        metadata = os.lstat(reservation.path)
    except FileNotFoundError:
        return False
    return (
        stat.S_ISDIR(metadata.st_mode)
        and metadata.st_dev == reservation.device
        and metadata.st_ino == reservation.inode
        and not any(reservation.path.iterdir())
    )


def _install_staging(staging: Path, reservation: _DestinationReservation) -> None:
    if not _reservation_is_unchanged_empty(reservation):
        raise WorkspacePreparationError(f"destination changed during preparation: {reservation.path}")
    try:
        os.replace(staging, reservation.path)
    except OSError as error:
        raise WorkspacePreparationError(
            f"could not atomically install prepared workspace at {reservation.path}: {error}"
        ) from error


def _restore_absent_destination(reservation: _DestinationReservation) -> None:
    if reservation.created and _reservation_is_unchanged_empty(reservation):
        reservation.path.rmdir()


def _initialize_repository(destination: Path) -> None:
    _git_no_output(destination, "init", "--initial-branch=main", "--template=")
    _git_no_output(destination, "config", "--local", "user.name", GIT_AUTHOR_NAME)
    _git_no_output(destination, "config", "--local", "user.email", GIT_AUTHOR_EMAIL)
    _git_no_output(destination, "config", "--local", "core.autocrlf", "false")
    _git_no_output(destination, "config", "--local", "core.hooksPath", os.devnull)
    _git_no_output(destination, "config", "--local", "core.excludesFile", os.devnull)
    _git_no_output(destination, "add", "--force", "--all")
    _git_no_output(
        destination,
        "-c",
        "commit.gpgSign=false",
        "commit",
        "--allow-empty",
        "--message",
        BASELINE_COMMIT_MESSAGE,
        commit_identity=True,
    )
    if _git_text(destination, "branch", "--show-current") != "main":
        raise WorkspacePreparationError("prepared repository branch is not main")
    if _git_text(destination, "rev-list", "--all", "--count") != "1":
        raise WorkspacePreparationError("prepared repository does not contain exactly one commit")
    status = _git_text(destination, "status", "--porcelain")
    if status:
        raise WorkspacePreparationError(f"prepared repository is dirty:\n{status}")


def _git_text(repository: Path, *arguments: str) -> str:
    completed = _run_git_process(repository, *arguments, text=True)
    assert isinstance(completed.stdout, str)
    return completed.stdout.strip()


def _git_no_output(
    repository: Path,
    *arguments: str,
    commit_identity: bool = False,
) -> None:
    _run_git_process(
        repository,
        *arguments,
        text=True,
        commit_identity=commit_identity,
    )


def _run_git_process(
    repository: Path,
    *arguments: str,
    check: bool = True,
    text: bool,
    commit_identity: bool = False,
) -> subprocess.CompletedProcess[str] | subprocess.CompletedProcess[bytes]:
    command = ("git", *arguments)
    try:
        completed = subprocess.run(
            command,
            cwd=repository,
            check=check,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=text,
            env=_git_environment(commit_identity=commit_identity),
        )
    except FileNotFoundError as error:
        raise WorkspacePreparationError("git executable was not found") from error
    except subprocess.CalledProcessError as error:
        stderr = error.stderr.decode("utf-8", errors="replace") if isinstance(error.stderr, bytes) else error.stderr
        stdout = error.stdout.decode("utf-8", errors="replace") if isinstance(error.stdout, bytes) else error.stdout
        detail = (stderr or "").strip() or (stdout or "").strip() or f"exit code {error.returncode}"
        raise WorkspacePreparationError(f"git command failed ({' '.join(command)}): {detail}") from error
    return completed


def _git_environment(*, commit_identity: bool = False) -> dict[str, str]:
    environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    environment.update(
        {
            "GIT_ATTR_NOSYSTEM": "1",
            "GIT_CONFIG_COUNT": "0",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": os.devnull,
            "LC_ALL": "C",
        }
    )
    if commit_identity:
        environment.update(
            {
                "GIT_AUTHOR_NAME": GIT_AUTHOR_NAME,
                "GIT_AUTHOR_EMAIL": GIT_AUTHOR_EMAIL,
                "GIT_AUTHOR_DATE": GIT_COMMIT_TIMESTAMP,
                "GIT_COMMITTER_NAME": GIT_AUTHOR_NAME,
                "GIT_COMMITTER_EMAIL": GIT_AUTHOR_EMAIL,
                "GIT_COMMITTER_DATE": GIT_COMMIT_TIMESTAMP,
            }
        )
    return environment


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
