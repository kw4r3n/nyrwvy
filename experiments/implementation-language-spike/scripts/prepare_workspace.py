"""Prepare an isolated language-spike workspace without starting a trial."""

import argparse
import ctypes
from dataclasses import dataclass
import errno
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import stat
import subprocess
import sys


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
_RENAME_NOREPLACE = 1


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
class _ParentAnchor:
    path: Path
    file_descriptor: int
    device: int
    inode: int


@dataclass(slots=True)
class _InstallState:
    renamed: bool = False


@dataclass(frozen=True, slots=True)
class _TreeEntry:
    mode: str
    object_type: str
    object_id: str
    path: PurePosixPath


def prepare_workspace(source: Path, language: str, destination: Path) -> Path:
    """Export one committed baseline and public bundle into a fresh Git repository."""
    source = source.resolve()
    requested_destination = Path(os.path.abspath(destination))
    if language not in SUPPORTED_LANGUAGES:
        raise WorkspacePreparationError(
            f"unsupported language: {language!r}; expected one of: {', '.join(SUPPORTED_LANGUAGES)}"
        )
    if not requested_destination.name:
        raise WorkspacePreparationError(f"destination must name a new directory: {requested_destination}")
    destination = requested_destination
    if destination == source or destination.is_relative_to(source):
        raise WorkspacePreparationError(f"destination must be outside source experiment tree: {destination}")

    parent_anchor = _open_parent_anchor(requested_destination.parent, create_missing=True)
    staging_name: str | None = None
    staging_file_descriptor: int | None = None
    install_state = _InstallState()
    try:
        _require_destination_absent(parent_anchor, destination)
        committed_source = _inspect_committed_source(source, language)
        _verify_parent_anchor(parent_anchor)
        _require_destination_absent(parent_anchor, destination)

        staging_name = _create_private_staging(parent_anchor, destination.name)
        staging_file_descriptor = os.open(
            staging_name,
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=parent_anchor.file_descriptor,
        )
        _export_committed_tree(
            committed_source.repository,
            committed_source.baseline_tree,
            staging_file_descriptor,
        )
        _export_committed_tree(
            committed_source.repository,
            committed_source.public_tree,
            staging_file_descriptor,
            prefix="shared/public",
        )
        _initialize_repository(staging_file_descriptor)
        _install_staging(
            parent_anchor,
            staging_name,
            staging_file_descriptor,
            destination,
            install_state,
        )
        return destination
    finally:
        if staging_file_descriptor is not None:
            os.close(staging_file_descriptor)
        if staging_name is not None and not install_state.renamed:
            _remove_tree_at(parent_anchor.file_descriptor, staging_name)
        os.close(parent_anchor.file_descriptor)


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


def _git_ls_tree(repository: Path, tree: str) -> bytes:
    completed = _run_git_process(
        repository,
        "ls-tree",
        "-r",
        "-z",
        "--full-tree",
        tree,
        text=False,
    )
    assert isinstance(completed.stdout, bytes)
    return completed.stdout


def _export_committed_tree(
    repository: Path,
    tree: str,
    staging_file_descriptor: int,
    *,
    prefix: str | None = None,
) -> None:
    prefix_path = PurePosixPath(prefix) if prefix is not None else None
    for entry in _parse_tree_entries(_git_ls_tree(repository, tree)):
        output_path = prefix_path / entry.path if prefix_path is not None else entry.path
        if _is_excluded_committed_path(output_path):
            continue
        blob = _git_cat_blob(repository, entry.object_id)
        _write_blob(
            staging_file_descriptor,
            output_path,
            blob,
            executable=entry.mode == "100755",
        )


def _parse_tree_entries(tree_bytes: bytes) -> tuple[_TreeEntry, ...]:
    entries = []
    for record in tree_bytes.split(b"\0"):
        if not record:
            continue
        try:
            metadata, raw_path = record.split(b"\t", 1)
            raw_mode, raw_object_type, raw_object_id = metadata.split(b" ")
            mode = raw_mode.decode("ascii")
            object_type = raw_object_type.decode("ascii")
            object_id = raw_object_id.decode("ascii")
        except (UnicodeDecodeError, ValueError) as error:
            raise WorkspacePreparationError("committed tree contains malformed ls-tree output") from error
        path = _safe_committed_path(os.fsdecode(raw_path))
        if mode not in {"100644", "100755"} or object_type != "blob":
            raise WorkspacePreparationError(
                f"committed tree contains unsafe mode {mode} and type {object_type} for {path.as_posix()!r}"
            )
        if len(object_id) not in {40, 64} or any(character not in "0123456789abcdef" for character in object_id):
            raise WorkspacePreparationError(
                f"committed tree contains malformed object ID for {path.as_posix()!r}"
            )
        entries.append(
            _TreeEntry(
                mode=mode,
                object_type=object_type,
                object_id=object_id,
                path=path,
            )
        )
    return tuple(entries)


def _safe_committed_path(name: str) -> PurePosixPath:
    parts = name.split("/")
    if (
        not name
        or name.startswith("/")
        or "\\" in name
        or any(part in {"", ".", ".."} for part in parts)
    ):
        raise WorkspacePreparationError(f"committed tree contains unsafe path: {name!r}")
    return PurePosixPath(*parts)


def _is_excluded_committed_path(path: PurePosixPath) -> bool:
    return any(part in _EXCLUDED_PATH_PARTS for part in path.parts) or path.name.endswith(_EXCLUDED_SUFFIXES)


def _git_cat_blob(repository: Path, object_id: str) -> bytes:
    completed = _run_git_process(
        repository,
        "cat-file",
        "blob",
        object_id,
        text=False,
    )
    assert isinstance(completed.stdout, bytes)
    return completed.stdout


def _write_blob(
    staging_file_descriptor: int,
    path: PurePosixPath,
    contents: bytes,
    *,
    executable: bool,
) -> None:
    parent_file_descriptor = _open_or_create_directories(
        staging_file_descriptor,
        path.parts[:-1],
    )
    try:
        try:
            file_descriptor = os.open(
                path.name,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
                0o755 if executable else 0o644,
                dir_fd=parent_file_descriptor,
            )
        except FileExistsError as error:
            raise WorkspacePreparationError(
                f"committed tree path collides during export: {path.as_posix()!r}"
            ) from error
        try:
            remaining = memoryview(contents)
            while remaining:
                written = os.write(file_descriptor, remaining)
                remaining = remaining[written:]
            os.fchmod(file_descriptor, 0o755 if executable else 0o644)
        finally:
            os.close(file_descriptor)
    finally:
        os.close(parent_file_descriptor)


def _open_or_create_directories(root_file_descriptor: int, parts: tuple[str, ...]) -> int:
    current_file_descriptor = os.dup(root_file_descriptor)
    try:
        for part in parts:
            try:
                os.mkdir(part, mode=0o755, dir_fd=current_file_descriptor)
            except FileExistsError:
                pass
            next_file_descriptor = os.open(
                part,
                os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                dir_fd=current_file_descriptor,
            )
            os.close(current_file_descriptor)
            current_file_descriptor = next_file_descriptor
        return current_file_descriptor
    except BaseException:
        os.close(current_file_descriptor)
        raise


def _open_parent_anchor(parent: Path, *, create_missing: bool) -> _ParentAnchor:
    if not parent.is_absolute():
        raise WorkspacePreparationError(f"destination parent must be absolute: {parent}")
    open_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    file_descriptor = os.open(parent.anchor, open_flags)
    try:
        root_metadata = os.stat(parent.anchor, follow_symlinks=False)
        descriptor_metadata = os.fstat(file_descriptor)
        if (
            not stat.S_ISDIR(root_metadata.st_mode)
            or root_metadata.st_dev != descriptor_metadata.st_dev
            or root_metadata.st_ino != descriptor_metadata.st_ino
        ):
            raise WorkspacePreparationError(
                f"destination parent changed while opening: {Path(parent.anchor)}"
            )

        display_path = Path(parent.anchor)
        for component in parent.parts[1:]:
            display_path /= component
            try:
                expected_metadata = os.stat(
                    component,
                    dir_fd=file_descriptor,
                    follow_symlinks=False,
                )
            except FileNotFoundError:
                if not create_missing:
                    raise WorkspacePreparationError(
                        f"destination parent component is missing: {display_path}"
                    ) from None
                try:
                    os.mkdir(component, mode=0o755, dir_fd=file_descriptor)
                except FileExistsError:
                    pass
                try:
                    expected_metadata = os.stat(
                        component,
                        dir_fd=file_descriptor,
                        follow_symlinks=False,
                    )
                except FileNotFoundError as error:
                    raise WorkspacePreparationError(
                        f"destination parent changed while opening: {display_path}"
                    ) from error

            if stat.S_ISLNK(expected_metadata.st_mode):
                raise WorkspacePreparationError(
                    f"destination parent path must not contain symlinks: {display_path}"
                )
            if not stat.S_ISDIR(expected_metadata.st_mode):
                raise WorkspacePreparationError(
                    f"destination parent component is not a directory: {display_path}"
                )

            next_file_descriptor = _open_verified_directory_component(
                file_descriptor,
                component,
                expected_metadata,
                display_path,
            )
            os.close(file_descriptor)
            file_descriptor = next_file_descriptor

        metadata = os.fstat(file_descriptor)
    except BaseException:
        os.close(file_descriptor)
        raise
    return _ParentAnchor(
        path=parent,
        file_descriptor=file_descriptor,
        device=metadata.st_dev,
        inode=metadata.st_ino,
    )


def _open_verified_directory_component(
    parent_file_descriptor: int,
    component: str,
    expected_metadata: os.stat_result,
    display_path: Path,
) -> int:
    try:
        file_descriptor = os.open(
            component,
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=parent_file_descriptor,
        )
    except OSError as error:
        raise WorkspacePreparationError(
            f"destination parent changed while opening: {display_path}"
        ) from error
    try:
        actual_metadata = os.fstat(file_descriptor)
        if (
            not stat.S_ISDIR(actual_metadata.st_mode)
            or actual_metadata.st_dev != expected_metadata.st_dev
            or actual_metadata.st_ino != expected_metadata.st_ino
        ):
            raise WorkspacePreparationError(
                f"destination parent changed while opening: {display_path}"
            )
        return file_descriptor
    except BaseException:
        os.close(file_descriptor)
        raise


def _verify_parent_anchor(parent_anchor: _ParentAnchor) -> None:
    try:
        reopened_anchor = _open_parent_anchor(parent_anchor.path, create_missing=False)
    except (OSError, WorkspacePreparationError) as error:
        raise WorkspacePreparationError(
            f"destination parent changed during preparation: {parent_anchor.path}"
        ) from error
    try:
        if (
            reopened_anchor.device != parent_anchor.device
            or reopened_anchor.inode != parent_anchor.inode
        ):
            raise WorkspacePreparationError(
                f"destination parent changed during preparation: {parent_anchor.path}"
            )
    finally:
        os.close(reopened_anchor.file_descriptor)


def _require_destination_absent(parent_anchor: _ParentAnchor, destination: Path) -> None:
    try:
        os.stat(
            destination.name,
            dir_fd=parent_anchor.file_descriptor,
            follow_symlinks=False,
        )
    except FileNotFoundError:
        return
    raise WorkspacePreparationError(f"destination must not exist: {destination}")


def _create_private_staging(parent_anchor: _ParentAnchor, destination_name: str) -> str:
    prefix = f".{destination_name[:64]}.prepare-"
    for _ in range(128):
        staging_name = prefix + secrets.token_hex(8)
        try:
            os.mkdir(
                staging_name,
                mode=0o700,
                dir_fd=parent_anchor.file_descriptor,
            )
            return staging_name
        except FileExistsError:
            continue
    raise WorkspacePreparationError("could not allocate a private staging directory")


def _install_staging(
    parent_anchor: _ParentAnchor,
    staging_name: str,
    staging_file_descriptor: int,
    destination: Path,
    install_state: _InstallState,
) -> None:
    _verify_parent_anchor(parent_anchor)
    try:
        _rename_noreplace(
            parent_anchor.file_descriptor,
            staging_name,
            destination.name,
        )
    except FileExistsError as error:
        raise WorkspacePreparationError(
            f"destination appeared during preparation: {destination}"
        ) from error
    install_state.renamed = True
    _verify_installed_destination(
        parent_anchor.file_descriptor,
        destination.name,
        staging_file_descriptor,
        destination,
    )
    _verify_parent_anchor(parent_anchor)
    _verify_installed_destination(
        parent_anchor.file_descriptor,
        destination.name,
        staging_file_descriptor,
        destination,
    )


def _verify_installed_destination(
    parent_file_descriptor: int,
    destination_name: str,
    staging_file_descriptor: int,
    destination: Path,
) -> None:
    installed_metadata = os.fstat(staging_file_descriptor)
    try:
        destination_metadata = os.stat(
            destination_name,
            dir_fd=parent_file_descriptor,
            follow_symlinks=False,
        )
    except OSError as error:
        raise WorkspacePreparationError(
            f"installed destination identity changed during verification: {destination}"
        ) from error
    if (
        not stat.S_ISDIR(destination_metadata.st_mode)
        or destination_metadata.st_dev != installed_metadata.st_dev
        or destination_metadata.st_ino != installed_metadata.st_ino
    ):
        raise WorkspacePreparationError(
            f"installed destination identity changed during verification: {destination}"
        )


def _rename_noreplace(
    parent_file_descriptor: int,
    source_name: str,
    destination_name: str,
) -> None:
    try:
        renameat2 = ctypes.CDLL(None, use_errno=True).renameat2
    except AttributeError as error:
        raise WorkspacePreparationError("atomic no-replace install is unavailable") from error
    renameat2.argtypes = (
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_uint,
    )
    renameat2.restype = ctypes.c_int
    result = renameat2(
        parent_file_descriptor,
        os.fsencode(source_name),
        parent_file_descriptor,
        os.fsencode(destination_name),
        _RENAME_NOREPLACE,
    )
    if result == 0:
        return
    error_number = ctypes.get_errno()
    if error_number in {errno.ENOSYS, errno.EINVAL, errno.EOPNOTSUPP}:
        raise WorkspacePreparationError("atomic no-replace install is unavailable")
    raise OSError(error_number, os.strerror(error_number), destination_name)


def _remove_tree_at(parent_file_descriptor: int, name: str) -> None:
    try:
        directory_file_descriptor = os.open(
            name,
            os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=parent_file_descriptor,
        )
    except FileNotFoundError:
        return
    except NotADirectoryError:
        os.unlink(name, dir_fd=parent_file_descriptor)
        return
    try:
        for child_name in os.listdir(directory_file_descriptor):
            child_metadata = os.stat(
                child_name,
                dir_fd=directory_file_descriptor,
                follow_symlinks=False,
            )
            if stat.S_ISDIR(child_metadata.st_mode):
                _remove_tree_at(directory_file_descriptor, child_name)
            else:
                os.unlink(child_name, dir_fd=directory_file_descriptor)
    finally:
        os.close(directory_file_descriptor)
    os.rmdir(name, dir_fd=parent_file_descriptor)


def _initialize_repository(staging_file_descriptor: int) -> None:
    _git_no_output(staging_file_descriptor, "init", "--initial-branch=main", "--template=")
    _git_no_output(staging_file_descriptor, "config", "--local", "user.name", GIT_AUTHOR_NAME)
    _git_no_output(staging_file_descriptor, "config", "--local", "user.email", GIT_AUTHOR_EMAIL)
    _git_no_output(staging_file_descriptor, "config", "--local", "core.autocrlf", "false")
    _git_no_output(staging_file_descriptor, "config", "--local", "core.hooksPath", os.devnull)
    _git_no_output(staging_file_descriptor, "config", "--local", "core.excludesFile", os.devnull)
    _git_no_output(staging_file_descriptor, "add", "--force", "--all")
    _git_no_output(
        staging_file_descriptor,
        "-c",
        "commit.gpgSign=false",
        "commit",
        "--allow-empty",
        "--message",
        BASELINE_COMMIT_MESSAGE,
        commit_identity=True,
    )
    if _git_text(staging_file_descriptor, "branch", "--show-current") != "main":
        raise WorkspacePreparationError("prepared repository branch is not main")
    if _git_text(staging_file_descriptor, "rev-list", "--all", "--count") != "1":
        raise WorkspacePreparationError("prepared repository does not contain exactly one commit")
    status = _git_text(staging_file_descriptor, "status", "--porcelain")
    if status:
        raise WorkspacePreparationError(f"prepared repository is dirty:\n{status}")


def _git_text(repository: Path | int, *arguments: str) -> str:
    completed = _run_git_process(repository, *arguments, text=True)
    assert isinstance(completed.stdout, str)
    return completed.stdout.strip()


def _git_no_output(
    repository: Path | int,
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
    repository: Path | int,
    *arguments: str,
    check: bool = True,
    text: bool,
    commit_identity: bool = False,
) -> subprocess.CompletedProcess[str] | subprocess.CompletedProcess[bytes]:
    command = ("git", *arguments)
    if isinstance(repository, int):
        working_directory = f"/proc/self/fd/{repository}"
        if not Path(working_directory).exists():
            raise WorkspacePreparationError("fd-anchored Git working directories are unavailable")
        pass_file_descriptors = (repository,)
    else:
        working_directory = repository
        pass_file_descriptors = ()
    try:
        completed = subprocess.run(
            command,
            cwd=working_directory,
            check=check,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=text,
            env=_git_environment(commit_identity=commit_identity),
            pass_fds=pass_file_descriptors,
        )
    except FileNotFoundError as error:
        if isinstance(repository, int):
            raise WorkspacePreparationError("fd-anchored Git working directory became unavailable") from error
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
