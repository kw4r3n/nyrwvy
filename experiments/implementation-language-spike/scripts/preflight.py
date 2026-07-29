"""Validate experiment prerequisites before any language trial can start."""

import argparse
from collections.abc import Callable
from dataclasses import asdict, dataclass
import errno
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tempfile
import tomllib


sys.dont_write_bytecode = True
EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from evaluator.common import ExperimentConfig

try:
    from . import committed_inventory
except ImportError:
    import committed_inventory


SCHEMA_VERSION = 1
SUPPORTED_LANGUAGES = ("fsharp", "csharp")
REQUIRED_EXECUTABLES = ("dotnet", "git", "node", "npm", "codex")
REQUIRED_CODEX_EXEC_OPTIONS = ("--json", "--model", "--sandbox")
PUBLIC_INPUT_PATHS = (
    "shared/public/AGENTS.md",
    "shared/public/acceptance.md",
    "shared/public/fixtures/initial-snapshot-v1.json",
    "shared/public/maintenance-task.md",
    "shared/public/schema/snapshot-v1.schema.json",
    "shared/public/semantic-contract.md",
)
INPUT_PATHS = ("experiment.toml", *PUBLIC_INPUT_PATHS)
MANIFEST_KEYS = frozenset(
    {
        "schema_version",
        "model",
        "sandbox",
        "python_version",
        "codex_cli_version",
        "dotnet_info",
        "os",
        "input_sha256",
        "baseline_sha256",
    }
)
OS_KEYS = frozenset({"system", "release", "version", "machine"})
_EXCLUDED_DIRECTORIES = frozenset({".git", "bin", "obj", "__pycache__"})
_EXCLUDED_FILE_SUFFIXES = (".pyc", ".jsonl", ".log")
_SMOKE_PROMPT = (
    "This is a harmless preflight smoke check. Do not create, edit, or delete files. "
    "Inspect the empty Git worktree and reply with PREFLIGHT_OK."
)
_TOOL_TIMEOUT_SECONDS = 30
_SMOKE_TIMEOUT_SECONDS = 120
_CONFIG_KEYS = frozenset(
    {
        "schema_version",
        "model",
        "sandbox",
        "timeout_seconds",
        "calibration_runs",
        "scored_runs",
        "languages",
    }
)

ExecutableResolver = Callable[[str], str | None]
CommandRunner = Callable[
    [tuple[str, ...], Path, int],
    subprocess.CompletedProcess[str],
]


class PreflightError(ValueError):
    """Raised when one or more experiment prerequisites are unavailable."""

    def __init__(self, problems: list[str] | tuple[str, ...]) -> None:
        self.problems = tuple(problems)
        super().__init__("preflight failed:\n" + "\n".join(f"- {problem}" for problem in self.problems))


@dataclass(frozen=True, slots=True)
class OsDetails:
    system: str
    release: str
    version: str
    machine: str


@dataclass(frozen=True, slots=True)
class PreflightManifest:
    schema_version: int
    model: str
    sandbox: str
    python_version: str
    codex_cli_version: str
    dotnet_info: str
    os: OsDetails
    input_sha256: dict[str, str]
    baseline_sha256: dict[str, str]


@dataclass(frozen=True, slots=True)
class _TreeEntry:
    path: str
    kind: str
    mode: int
    size: int
    modified_ns: int
    changed_ns: int
    payload: bytes


class _PersistenceFailure(ValueError):
    """Internal precise failure while installing the manifest."""


def run_preflight(
    config_path: Path,
    *,
    executable_resolver: ExecutableResolver | None = None,
    command_runner: CommandRunner | None = None,
    python_version: tuple[int, int, int] | None = None,
    os_details: OsDetails | None = None,
) -> PreflightManifest:
    """Validate prerequisites and write the manifest only after complete success."""
    config_path = Path(os.path.abspath(config_path))
    if config_path.name != "experiment.toml":
        raise PreflightError(
            [
                "config path must name the canonical experiment.toml; "
                f"received {config_path}"
            ]
        )
    experiment_root = config_path.parent
    resolver = executable_resolver if executable_resolver is not None else shutil.which
    runner = command_runner if command_runner is not None else _run_command
    selected_python_version = (
        python_version
        if python_version is not None
        else (sys.version_info.major, sys.version_info.minor, sys.version_info.micro)
    )

    try:
        root_file_descriptor = _open_directory_path_no_symlinks(experiment_root)
    except (OSError, ValueError) as error:
        raise PreflightError(
            [f"experiment root is not a stable real directory: {experiment_root}: {error}"]
        ) from error
    try:
        try:
            config_bytes = _read_regular_file_at(
                root_file_descriptor,
                ("experiment.toml",),
                "experiment.toml",
            )
            config = _load_config_bytes(config_bytes)
        except (OSError, UnicodeDecodeError, ValueError, tomllib.TOMLDecodeError) as error:
            raise PreflightError([f"config snapshot failed: {error}"]) from error

        manifest = _run_preflight_from_root(
            experiment_root,
            root_file_descriptor,
            config,
            config_bytes,
            resolver,
            runner,
            selected_python_version,
            os_details,
        )
        return manifest
    finally:
        os.close(root_file_descriptor)


def _run_preflight_from_root(
    experiment_root: Path,
    root_file_descriptor: int,
    config: ExperimentConfig,
    config_bytes: bytes,
    resolver: ExecutableResolver,
    runner: CommandRunner,
    selected_python_version: tuple[int, int, int],
    os_details: OsDetails | None,
) -> PreflightManifest:
    problems: list[str] = []
    if selected_python_version[0] != 3 or selected_python_version < (3, 13, 0):
        version_text = ".".join(str(component) for component in selected_python_version)
        problems.append(
            "Python 3.13 or newer within major version 3 is required; "
            f"running {version_text}"
        )

    executables: dict[str, str] = {}
    for name in REQUIRED_EXECUTABLES:
        executable = resolver(name)
        if executable is None:
            problems.append(f"required executable is unavailable: {name}")
        else:
            executables[name] = executable

    if "dotnet" in executables:
        dotnet_version = _invoke(
            runner,
            (executables["dotnet"], "--version"),
            experiment_root,
            "dotnet --version",
            _TOOL_TIMEOUT_SECONDS,
            problems,
        )
        if dotnet_version is not None and not dotnet_version.stdout.strip().startswith("10."):
            problems.append(
                "dotnet --version must begin with 10.; "
                f"reported {dotnet_version.stdout.strip()!r}"
            )

    if "codex" in executables:
        codex_help = _invoke(
            runner,
            (executables["codex"], "exec", "--help"),
            experiment_root,
            "codex exec --help",
            _TOOL_TIMEOUT_SECONDS,
            problems,
        )
        if codex_help is not None:
            help_text = codex_help.stdout + codex_help.stderr
            for option in REQUIRED_CODEX_EXEC_OPTIONS:
                option_pattern = rf"(?<![\w-]){re.escape(option)}(?![\w-])"
                if re.search(option_pattern, help_text) is None:
                    problems.append(f"codex exec --help is missing required option: {option}")

    baseline_paths = {
        language: experiment_root / language
        for language in SUPPORTED_LANGUAGES
    }
    for language, baseline_path in baseline_paths.items():
        if not _is_real_directory_at(root_file_descriptor, (language,)):
            problems.append(f"missing baseline directory: {baseline_path}")

    input_paths = {
        relative_path: experiment_root / relative_path
        for relative_path in INPUT_PATHS
    }
    for relative_path, input_path in input_paths.items():
        if relative_path == "experiment.toml":
            continue
        if not _is_real_regular_file_at(
            root_file_descriptor,
            tuple(Path(relative_path).parts),
        ):
            problems.append(f"missing required input file: {input_path}")

    if problems:
        raise PreflightError(problems)

    codex_version_result = _invoke(
        runner,
        (executables["codex"], "--version"),
        experiment_root,
        "codex --version",
        _TOOL_TIMEOUT_SECONDS,
        problems,
    )
    dotnet_info_result = _invoke(
        runner,
        (executables["dotnet"], "--info"),
        experiment_root,
        "dotnet --info",
        _TOOL_TIMEOUT_SECONDS,
        problems,
    )
    codex_version = _required_output(codex_version_result, "codex --version", problems)
    dotnet_info = _required_output(dotnet_info_result, "dotnet --info", problems)
    if problems:
        raise PreflightError(problems)

    try:
        input_sha256, baseline_sha256, source_commit = _capture_input_hashes(
            experiment_root,
            root_file_descriptor,
        )
    except (OSError, ValueError) as error:
        raise PreflightError([f"input snapshot failed: {error}"]) from error
    initial_config_digest = hashlib.sha256(config_bytes).hexdigest()
    if input_sha256["experiment.toml"] != initial_config_digest:
        raise PreflightError(
            ["input snapshot failed: experiment.toml changed after it was loaded"]
        )

    smoke_problem = _run_model_smoke(config, executables, runner)
    if smoke_problem is not None:
        raise PreflightError([smoke_problem])

    try:
        (
            final_input_sha256,
            final_baseline_sha256,
            final_source_commit,
        ) = _capture_input_hashes(
            experiment_root,
            root_file_descriptor,
        )
    except (OSError, ValueError) as error:
        raise PreflightError(
            [f"experiment inputs changed during preflight: {error}"]
        ) from error
    if (
        final_input_sha256 != input_sha256
        or final_baseline_sha256 != baseline_sha256
        or final_source_commit != source_commit
    ):
        changed_inputs = sorted(
            key
            for key in set(input_sha256) | set(final_input_sha256)
            if input_sha256.get(key) != final_input_sha256.get(key)
        )
        changed_languages = sorted(
            key
            for key in set(baseline_sha256) | set(final_baseline_sha256)
            if baseline_sha256.get(key) != final_baseline_sha256.get(key)
        )
        details = []
        if changed_inputs:
            details.append(f"files={changed_inputs}")
        if changed_languages:
            details.append(f"baselines={changed_languages}")
        if final_source_commit != source_commit:
            details.append("source_commit")
        raise PreflightError(
            [
                "experiment inputs changed during preflight: "
                + ", ".join(details)
            ]
        )

    selected_os_details = os_details if os_details is not None else _current_os_details()
    manifest = PreflightManifest(
        schema_version=SCHEMA_VERSION,
        model=config.model,
        sandbox=config.sandbox,
        python_version=".".join(str(component) for component in selected_python_version),
        codex_cli_version=codex_version,
        dotnet_info=dotnet_info,
        os=selected_os_details,
        input_sha256=input_sha256,
        baseline_sha256=baseline_sha256,
    )
    _validate_manifest(asdict(manifest))
    _write_manifest(
        root_file_descriptor,
        experiment_root,
        manifest,
    )
    return manifest


def digest_file(path: Path) -> str:
    """Return the SHA-256 digest of one regular file's exact bytes."""
    absolute_path = Path(os.path.abspath(path))
    parent_file_descriptor = _open_directory_path_no_symlinks(
        absolute_path.parent
    )
    try:
        contents = _read_regular_file_at(
            parent_file_descriptor,
            (absolute_path.name,),
            str(absolute_path),
        )
    finally:
        os.close(parent_file_descriptor)
    return hashlib.sha256(contents).hexdigest()


def digest_tree(root: Path) -> str:
    """Hash included relative paths and bytes in stable lexical order."""
    absolute_root = Path(os.path.abspath(root))
    parent_file_descriptor = _open_directory_path_no_symlinks(
        absolute_root.parent
    )
    tree_file_descriptor: int | None = None
    try:
        tree_file_descriptor = os.open(
            absolute_root.name,
            os.O_RDONLY
            | os.O_DIRECTORY
            | os.O_CLOEXEC
            | os.O_NOFOLLOW,
            dir_fd=parent_file_descriptor,
        )
        entries = _stable_tree_entries(
            tree_file_descriptor,
            exclusions=True,
            include_symlinks=False,
        )
        return _hash_tree_entries(entries)
    except OSError as error:
        raise ValueError(f"cannot hash non-directory tree: {absolute_root}: {error}") from error
    finally:
        if tree_file_descriptor is not None:
            os.close(tree_file_descriptor)
        os.close(parent_file_descriptor)


def _open_directory_path_no_symlinks(path: Path) -> int:
    absolute_path = Path(os.path.abspath(path))
    flags = (
        os.O_RDONLY
        | os.O_DIRECTORY
        | os.O_CLOEXEC
        | os.O_NOFOLLOW
    )
    file_descriptor = os.open(absolute_path.anchor, flags)
    try:
        for component in absolute_path.parts[1:]:
            next_file_descriptor = os.open(
                component,
                flags,
                dir_fd=file_descriptor,
            )
            os.close(file_descriptor)
            file_descriptor = next_file_descriptor
        return file_descriptor
    except BaseException:
        os.close(file_descriptor)
        raise


def _open_relative_directory(
    root_file_descriptor: int,
    parts: tuple[str, ...],
) -> int:
    file_descriptor = os.dup(root_file_descriptor)
    flags = (
        os.O_RDONLY
        | os.O_DIRECTORY
        | os.O_CLOEXEC
        | os.O_NOFOLLOW
    )
    try:
        for part in parts:
            next_file_descriptor = os.open(
                part,
                flags,
                dir_fd=file_descriptor,
            )
            os.close(file_descriptor)
            file_descriptor = next_file_descriptor
        return file_descriptor
    except BaseException:
        os.close(file_descriptor)
        raise


def _is_real_directory_at(
    root_file_descriptor: int,
    parts: tuple[str, ...],
) -> bool:
    try:
        file_descriptor = _open_relative_directory(
            root_file_descriptor,
            parts,
        )
    except OSError:
        return False
    os.close(file_descriptor)
    return True


def _is_real_regular_file_at(
    root_file_descriptor: int,
    parts: tuple[str, ...],
) -> bool:
    if not parts:
        return False
    try:
        parent_file_descriptor = _open_relative_directory(
            root_file_descriptor,
            parts[:-1],
        )
        try:
            details = os.stat(
                parts[-1],
                dir_fd=parent_file_descriptor,
                follow_symlinks=False,
            )
        finally:
            os.close(parent_file_descriptor)
    except OSError:
        return False
    return stat.S_ISREG(details.st_mode)


def _read_regular_file_at(
    root_file_descriptor: int,
    parts: tuple[str, ...],
    label: str,
) -> bytes:
    if not parts:
        raise ValueError(f"not a stable regular file: {label}")
    parent_file_descriptor = _open_relative_directory(
        root_file_descriptor,
        parts[:-1],
    )
    file_descriptor: int | None = None
    try:
        initial = os.stat(
            parts[-1],
            dir_fd=parent_file_descriptor,
            follow_symlinks=False,
        )
        if not stat.S_ISREG(initial.st_mode):
            raise ValueError(f"not a stable regular file: {label}")
        file_descriptor = os.open(
            parts[-1],
            os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=parent_file_descriptor,
        )
        opened = os.fstat(file_descriptor)
        if not _same_file_state(initial, opened):
            raise ValueError(f"file changed while hashing: {label}")
        contents = _read_all(file_descriptor)
        after_read = os.fstat(file_descriptor)
        final = os.stat(
            parts[-1],
            dir_fd=parent_file_descriptor,
            follow_symlinks=False,
        )
        if (
            not _same_file_state(opened, after_read)
            or not _same_file_state(after_read, final)
        ):
            raise ValueError(f"file changed while hashing: {label}")
        return contents
    except OSError as error:
        raise ValueError(f"not a stable regular file: {label}: {error}") from error
    finally:
        if file_descriptor is not None:
            os.close(file_descriptor)
        os.close(parent_file_descriptor)


def _read_regular_entry_at(
    directory_file_descriptor: int,
    name: str,
    initial: os.stat_result,
    label: str,
) -> bytes:
    file_descriptor: int | None = None
    try:
        file_descriptor = os.open(
            name,
            os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW,
            dir_fd=directory_file_descriptor,
        )
        opened = os.fstat(file_descriptor)
        if not _same_file_state(initial, opened):
            raise ValueError(f"file changed while hashing: {label}")
        contents = _read_all(file_descriptor)
        after_read = os.fstat(file_descriptor)
        final = os.stat(
            name,
            dir_fd=directory_file_descriptor,
            follow_symlinks=False,
        )
        if (
            not _same_file_state(opened, after_read)
            or not _same_file_state(after_read, final)
        ):
            raise ValueError(f"file changed while hashing: {label}")
        return contents
    except OSError as error:
        raise ValueError(f"file changed while hashing: {label}: {error}") from error
    finally:
        if file_descriptor is not None:
            os.close(file_descriptor)


def _read_all(file_descriptor: int) -> bytes:
    chunks: list[bytes] = []
    while True:
        try:
            chunk = os.read(file_descriptor, 1024 * 1024)
        except InterruptedError:
            continue
        if not chunk:
            return b"".join(chunks)
        chunks.append(chunk)


def _same_file_state(
    first: os.stat_result,
    second: os.stat_result,
) -> bool:
    return (
        first.st_dev,
        first.st_ino,
        first.st_mode,
        first.st_nlink,
        first.st_size,
        first.st_mtime_ns,
        first.st_ctime_ns,
    ) == (
        second.st_dev,
        second.st_ino,
        second.st_mode,
        second.st_nlink,
        second.st_size,
        second.st_mtime_ns,
        second.st_ctime_ns,
    )


def _directory_names(directory_file_descriptor: int) -> tuple[str, ...]:
    with os.scandir(directory_file_descriptor) as entries:
        return tuple(sorted(entry.name for entry in entries))


def _stable_tree_entries(
    directory_file_descriptor: int,
    *,
    exclusions: bool,
    include_symlinks: bool,
) -> tuple[_TreeEntry, ...]:
    first = _capture_tree_entries_once(
        directory_file_descriptor,
        prefix="",
        exclusions=exclusions,
        include_symlinks=include_symlinks,
        include_root=True,
    )
    second = _capture_tree_entries_once(
        directory_file_descriptor,
        prefix="",
        exclusions=exclusions,
        include_symlinks=include_symlinks,
        include_root=True,
    )
    if first != second:
        raise ValueError("tree changed while hashing")
    return first


def _capture_tree_entries_once(
    directory_file_descriptor: int,
    *,
    prefix: str,
    exclusions: bool,
    include_symlinks: bool,
    include_root: bool,
) -> tuple[_TreeEntry, ...]:
    initial_directory = os.fstat(directory_file_descriptor)
    before_names = _directory_names(directory_file_descriptor)
    captured: list[_TreeEntry] = []
    if include_root:
        captured.append(
            _tree_entry(
                "." if not prefix else prefix,
                "directory",
                initial_directory,
                b"",
            )
        )

    for name in before_names:
        relative_path = name if not prefix else f"{prefix}/{name}"
        initial = os.stat(
            name,
            dir_fd=directory_file_descriptor,
            follow_symlinks=False,
        )
        if stat.S_ISLNK(initial.st_mode):
            if include_symlinks:
                target = os.fsencode(
                    os.readlink(
                        name,
                        dir_fd=directory_file_descriptor,
                    )
                )
                final = os.stat(
                    name,
                    dir_fd=directory_file_descriptor,
                    follow_symlinks=False,
                )
                if not _same_file_state(initial, final):
                    raise ValueError(
                        f"symlink changed while hashing: {relative_path}"
                    )
                captured.append(
                    _tree_entry(
                        relative_path,
                        "symlink",
                        initial,
                        target,
                    )
                )
            else:
                final = os.stat(
                    name,
                    dir_fd=directory_file_descriptor,
                    follow_symlinks=False,
                )
                if not _same_file_state(initial, final):
                    raise ValueError(
                        f"symlink changed while hashing: {relative_path}"
                    )
            continue

        if stat.S_ISDIR(initial.st_mode):
            if exclusions and name in _EXCLUDED_DIRECTORIES:
                final = os.stat(
                    name,
                    dir_fd=directory_file_descriptor,
                    follow_symlinks=False,
                )
                if not _same_file_state(initial, final):
                    raise ValueError(
                        f"excluded directory changed while hashing: {relative_path}"
                    )
                continue
            child_file_descriptor: int | None = None
            try:
                child_file_descriptor = os.open(
                    name,
                    os.O_RDONLY
                    | os.O_DIRECTORY
                    | os.O_CLOEXEC
                    | os.O_NOFOLLOW,
                    dir_fd=directory_file_descriptor,
                )
                opened = os.fstat(child_file_descriptor)
                if not _same_file_state(initial, opened):
                    raise ValueError(
                        f"directory changed while hashing: {relative_path}"
                    )
                captured.append(
                    _tree_entry(
                        relative_path,
                        "directory",
                        initial,
                        b"",
                    )
                )
                captured.extend(
                    _capture_tree_entries_once(
                        child_file_descriptor,
                        prefix=relative_path,
                        exclusions=exclusions,
                        include_symlinks=include_symlinks,
                        include_root=False,
                    )
                )
                after_scan = os.fstat(child_file_descriptor)
                final = os.stat(
                    name,
                    dir_fd=directory_file_descriptor,
                    follow_symlinks=False,
                )
                if (
                    not _same_file_state(opened, after_scan)
                    or not _same_file_state(after_scan, final)
                ):
                    raise ValueError(
                        f"directory changed while hashing: {relative_path}"
                    )
            except OSError as error:
                raise ValueError(
                    f"directory changed while hashing: {relative_path}: {error}"
                ) from error
            finally:
                if child_file_descriptor is not None:
                    os.close(child_file_descriptor)
            continue

        if stat.S_ISREG(initial.st_mode):
            if exclusions and name.endswith(_EXCLUDED_FILE_SUFFIXES):
                final = os.stat(
                    name,
                    dir_fd=directory_file_descriptor,
                    follow_symlinks=False,
                )
                if not _same_file_state(initial, final):
                    raise ValueError(
                        f"excluded file changed while hashing: {relative_path}"
                    )
                continue
            contents = _read_regular_entry_at(
                directory_file_descriptor,
                name,
                initial,
                relative_path,
            )
            final = os.stat(
                name,
                dir_fd=directory_file_descriptor,
                follow_symlinks=False,
            )
            captured.append(
                _tree_entry(
                    relative_path,
                    "file",
                    final,
                    contents,
                )
            )
            continue

        raise ValueError(f"unsupported path type while hashing: {relative_path}")

    after_names = _directory_names(directory_file_descriptor)
    final_directory = os.fstat(directory_file_descriptor)
    if before_names != after_names:
        label = prefix or "."
        raise ValueError(f"directory changed while hashing: {label}")
    if not _same_file_state(initial_directory, final_directory):
        label = prefix or "."
        raise ValueError(f"directory changed while hashing: {label}")
    return tuple(sorted(captured, key=lambda entry: entry.path))


def _tree_entry(
    path: str,
    kind: str,
    details: os.stat_result,
    payload: bytes,
) -> _TreeEntry:
    return _TreeEntry(
        path=path,
        kind=kind,
        mode=stat.S_IMODE(details.st_mode),
        size=details.st_size,
        modified_ns=details.st_mtime_ns,
        changed_ns=details.st_ctime_ns,
        payload=payload,
    )


def _hash_tree_entries(entries: tuple[_TreeEntry, ...]) -> str:
    digest = hashlib.sha256()
    for entry in entries:
        if entry.kind != "file":
            continue
        relative_bytes = entry.path.encode("utf-8")
        digest.update(len(relative_bytes).to_bytes(8, "big"))
        digest.update(relative_bytes)
        digest.update(len(entry.payload).to_bytes(8, "big"))
        digest.update(entry.payload)
    return digest.hexdigest()


def _capture_input_hashes(
    experiment_root: Path,
    root_file_descriptor: int,
) -> tuple[dict[str, str], dict[str, str], str]:
    first = _capture_input_hashes_once(
        experiment_root,
        root_file_descriptor,
    )
    second = _capture_input_hashes_once(
        experiment_root,
        root_file_descriptor,
    )
    if first != second:
        raise ValueError("experiment inputs changed while hashing")
    return first


def _capture_input_hashes_once(
    experiment_root: Path,
    root_file_descriptor: int,
) -> tuple[dict[str, str], dict[str, str], str]:
    input_sha256: dict[str, str] = {}
    for relative_path in INPUT_PATHS:
        contents = _read_regular_file_at(
            root_file_descriptor,
            tuple(Path(relative_path).parts),
            relative_path,
        )
        input_sha256[relative_path] = hashlib.sha256(contents).hexdigest()

    source_commit, inventories = (
        committed_inventory.capture_selected_inventories(
            experiment_root,
            SUPPORTED_LANGUAGES,
        )
    )
    baseline_sha256 = {
        language: committed_inventory.digest_inventory(
            inventories[language]
        )
        for language in SUPPORTED_LANGUAGES
    }
    return input_sha256, baseline_sha256, source_commit


def _load_config_bytes(config_bytes: bytes) -> ExperimentConfig:
    values = tomllib.loads(config_bytes.decode("utf-8"))
    actual_keys = set(values)
    unknown_keys = actual_keys - _CONFIG_KEYS
    missing_keys = _CONFIG_KEYS - actual_keys
    if unknown_keys or missing_keys:
        details: list[str] = []
        if unknown_keys:
            details.append(f"unknown keys: {sorted(unknown_keys)}")
        if missing_keys:
            details.append(f"missing keys: {sorted(missing_keys)}")
        raise ValueError("; ".join(details))

    schema_version = _config_integer(values, "schema_version")
    model = _config_string(values, "model")
    sandbox = _config_string(values, "sandbox")
    timeout_seconds = _config_positive_integer(values, "timeout_seconds")
    calibration_runs = _config_positive_integer(values, "calibration_runs")
    scored_runs = _config_positive_integer(values, "scored_runs")
    languages = values["languages"]
    if (
        type(languages) is not list
        or any(type(language) is not str for language in languages)
        or tuple(languages) != SUPPORTED_LANGUAGES
    ):
        raise ValueError(
            "languages must be ['fsharp', 'csharp'] without duplicates"
        )
    if schema_version != 1:
        raise ValueError("schema_version must be 1")
    if not model:
        raise ValueError("model must not be empty")
    if sandbox != "workspace-write":
        raise ValueError("sandbox must be workspace-write")
    return ExperimentConfig(
        schema_version=schema_version,
        model=model,
        sandbox=sandbox,
        timeout_seconds=timeout_seconds,
        calibration_runs=calibration_runs,
        scored_runs=scored_runs,
        languages=tuple(languages),
    )


def _config_integer(values: dict[str, object], key: str) -> int:
    value = values[key]
    if type(value) is not int:
        raise ValueError(f"{key} must be an integer")
    return value


def _config_positive_integer(
    values: dict[str, object],
    key: str,
) -> int:
    value = _config_integer(values, key)
    if value <= 0:
        raise ValueError(f"{key} must be positive")
    return value


def _config_string(values: dict[str, object], key: str) -> str:
    value = values[key]
    if type(value) is not str:
        raise ValueError(f"{key} must be a string")
    return value


def load_manifest(path: Path) -> PreflightManifest:
    """Load and strictly validate a preflight manifest."""
    try:
        values = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError("manifest must be valid UTF-8 JSON") from error
    _validate_manifest(values)
    return PreflightManifest(
        schema_version=values["schema_version"],
        model=values["model"],
        sandbox=values["sandbox"],
        python_version=values["python_version"],
        codex_cli_version=values["codex_cli_version"],
        dotnet_info=values["dotnet_info"],
        os=OsDetails(**values["os"]),
        input_sha256=dict(values["input_sha256"]),
        baseline_sha256=dict(values["baseline_sha256"]),
    )


def _invoke(
    runner: CommandRunner,
    command: tuple[str, ...],
    cwd: Path,
    label: str,
    timeout_seconds: int,
    problems: list[str],
) -> subprocess.CompletedProcess[str] | None:
    try:
        completed = runner(command, cwd, timeout_seconds)
    except subprocess.TimeoutExpired as error:
        problems.append(
            f"{label} timed out after {_format_timeout(error.timeout)} seconds"
        )
        return None
    except OSError as error:
        problems.append(f"{label} could not run: {error}")
        return None
    except subprocess.SubprocessError as error:
        problems.append(f"{label} failed to execute: {error}")
        return None
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "no diagnostic output"
        problems.append(f"{label} failed (exit {completed.returncode}): {detail}")
        return None
    return completed


def _format_timeout(timeout: float | None) -> str:
    if timeout is None:
        return "unknown"
    return f"{timeout:g}"


def _required_output(
    completed: subprocess.CompletedProcess[str] | None,
    label: str,
    problems: list[str],
) -> str:
    if completed is None:
        return ""
    output = completed.stdout.strip()
    if not output:
        problems.append(f"{label} returned empty output")
    return output


def _run_model_smoke(
    config: ExperimentConfig,
    executables: dict[str, str],
    runner: CommandRunner,
) -> str | None:
    with tempfile.TemporaryDirectory(prefix="nayamnyam-preflight-") as temporary:
        repository = Path(temporary)
        problems: list[str] = []
        initialized = _invoke(
            runner,
            (executables["git"], "init", "--quiet"),
            repository,
            "temporary Git initialization",
            _TOOL_TIMEOUT_SECONDS,
            problems,
        )
        if initialized is None:
            return problems[0]

        try:
            before_smoke = _snapshot_complete_tree(repository)
        except (OSError, ValueError) as error:
            return f"temporary repository snapshot failed before model smoke: {error}"

        smoke = _invoke(
            runner,
            (
                executables["codex"],
                "exec",
                "--json",
                "--model",
                config.model,
                "--sandbox",
                "read-only",
                _SMOKE_PROMPT,
            ),
            repository,
            "model smoke",
            _SMOKE_TIMEOUT_SECONDS,
            problems,
        )
        if smoke is None:
            return problems[0]

        try:
            after_smoke = _snapshot_complete_tree(repository)
        except (OSError, ValueError) as error:
            return f"temporary repository snapshot failed after model smoke: {error}"
        if before_smoke != after_smoke:
            return (
                "model smoke modified temporary repository: "
                + _describe_tree_change(before_smoke, after_smoke)
            )

        token_problem = _validate_smoke_output(smoke.stdout)
        if token_problem is not None:
            return token_problem

        status = _invoke(
            runner,
            (
                executables["git"],
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
            ),
            repository,
            "temporary Git status",
            _TOOL_TIMEOUT_SECONDS,
            problems,
        )
        if status is None:
            return problems[0]
        if status.stdout.strip():
            return f"model smoke modified temporary repository: {status.stdout.strip()}"
    return None


def _snapshot_complete_tree(root: Path) -> tuple[_TreeEntry, ...]:
    directory_file_descriptor = _open_directory_path_no_symlinks(root)
    try:
        return _stable_tree_entries(
            directory_file_descriptor,
            exclusions=False,
            include_symlinks=True,
        )
    finally:
        os.close(directory_file_descriptor)


def _describe_tree_change(
    before: tuple[_TreeEntry, ...],
    after: tuple[_TreeEntry, ...],
) -> str:
    before_by_path = {entry.path: entry for entry in before}
    after_by_path = {entry.path: entry for entry in after}
    added = sorted(set(after_by_path) - set(before_by_path))
    if added:
        return f"added {added[0]}"
    removed = sorted(set(before_by_path) - set(after_by_path))
    if removed:
        return f"removed {removed[0]}"
    changed = sorted(
        path
        for path in set(before_by_path) & set(after_by_path)
        if before_by_path[path] != after_by_path[path]
    )
    if changed:
        first_non_root = next(
            (path for path in changed if path != "."),
            changed[0],
        )
        return f"changed {first_non_root}"
    return "inventory differs"


def _validate_smoke_output(stdout: str) -> str | None:
    last_agent_message: str | None = None
    for line_number, line in enumerate(stdout.splitlines(), start=1):
        if not line.strip():
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as error:
            return (
                "model smoke returned malformed JSONL "
                f"at line {line_number}: {error.msg}"
            )
        if type(event) is not dict or event.get("type") != "item.completed":
            continue
        item = event.get("item")
        if type(item) is not dict or item.get("type") != "agent_message":
            continue
        text = item.get("text")
        if type(text) is not str:
            last_agent_message = None
            continue
        last_agent_message = text
    if (
        last_agent_message is not None
        and last_agent_message.strip() == "PREFLIGHT_OK"
    ):
        return None
    return (
        "model smoke final completed agent message was not exact "
        "PREFLIGHT_OK"
    )


def _current_os_details() -> OsDetails:
    details = platform.uname()
    return OsDetails(
        system=details.system,
        release=details.release,
        version=details.version,
        machine=details.machine,
    )


def _run_command(
    command: tuple[str, ...],
    cwd: Path,
    timeout_seconds: int,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=cwd,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=timeout_seconds,
    )


def _write_manifest(
    root_file_descriptor: int,
    experiment_root: Path,
    manifest: PreflightManifest,
) -> None:
    manifest_bytes = (json.dumps(asdict(manifest), indent=2, sort_keys=True) + "\n").encode("utf-8")
    results_file_descriptor: int | None = None
    preflight_file_descriptor: int | None = None
    temporary_file_descriptor: int | None = None
    temporary_name: str | None = None
    created_results = False
    created_preflight = False
    replaced = False
    try:
        results_file_descriptor, created_results = _open_or_create_manifest_directory(
            root_file_descriptor,
            "results",
            experiment_root / "results",
        )
        preflight_file_descriptor, created_preflight = _open_or_create_manifest_directory(
            results_file_descriptor,
            "preflight",
            experiment_root / "results/preflight",
        )
        destination = experiment_root / "results/preflight/manifest.json"
        _validate_manifest_destination(
            preflight_file_descriptor,
            destination,
        )
        temporary_name, temporary_file_descriptor = _create_manifest_temporary(
            preflight_file_descriptor
        )
        _write_all(temporary_file_descriptor, manifest_bytes)
        os.fsync(temporary_file_descriptor)
        os.close(temporary_file_descriptor)
        temporary_file_descriptor = None
        _validate_manifest_destination(
            preflight_file_descriptor,
            destination,
        )
        os.replace(
            temporary_name,
            "manifest.json",
            src_dir_fd=preflight_file_descriptor,
            dst_dir_fd=preflight_file_descriptor,
        )
        replaced = True
        temporary_name = None
        os.fsync(preflight_file_descriptor)
    except _PersistenceFailure as error:
        _cleanup_manifest_temporary(
            preflight_file_descriptor,
            temporary_name,
            temporary_file_descriptor,
        )
        temporary_file_descriptor = None
        temporary_name = None
        _close_and_cleanup_manifest_directories(
            root_file_descriptor,
            results_file_descriptor,
            preflight_file_descriptor,
            created_results,
            created_preflight,
            replaced,
        )
        results_file_descriptor = None
        preflight_file_descriptor = None
        raise PreflightError([str(error)]) from error
    except OSError as error:
        _cleanup_manifest_temporary(
            preflight_file_descriptor,
            temporary_name,
            temporary_file_descriptor,
        )
        temporary_file_descriptor = None
        temporary_name = None
        _close_and_cleanup_manifest_directories(
            root_file_descriptor,
            results_file_descriptor,
            preflight_file_descriptor,
            created_results,
            created_preflight,
            replaced,
        )
        results_file_descriptor = None
        preflight_file_descriptor = None
        raise PreflightError(
            [f"could not persist preflight manifest: {error}"]
        ) from error
    finally:
        if temporary_file_descriptor is not None:
            os.close(temporary_file_descriptor)
        if preflight_file_descriptor is not None:
            os.close(preflight_file_descriptor)
        if results_file_descriptor is not None:
            os.close(results_file_descriptor)


def _open_or_create_manifest_directory(
    parent_file_descriptor: int,
    name: str,
    display_path: Path,
) -> tuple[int, bool]:
    created = False
    try:
        os.mkdir(name, mode=0o755, dir_fd=parent_file_descriptor)
        created = True
    except FileExistsError:
        pass
    try:
        file_descriptor = os.open(
            name,
            os.O_RDONLY
            | os.O_DIRECTORY
            | os.O_CLOEXEC
            | os.O_NOFOLLOW,
            dir_fd=parent_file_descriptor,
        )
    except OSError as error:
        if created:
            try:
                os.rmdir(name, dir_fd=parent_file_descriptor)
            except OSError:
                pass
        raise _PersistenceFailure(
            "manifest directory component is not a real directory: "
            f"{display_path}"
        ) from error
    return file_descriptor, created


def _validate_manifest_destination(
    preflight_file_descriptor: int,
    destination: Path,
) -> None:
    try:
        details = os.stat(
            "manifest.json",
            dir_fd=preflight_file_descriptor,
            follow_symlinks=False,
        )
    except FileNotFoundError:
        return
    if stat.S_ISLNK(details.st_mode):
        raise _PersistenceFailure(
            f"manifest destination is a symlink: {destination}"
        )
    if not stat.S_ISREG(details.st_mode):
        raise _PersistenceFailure(
            f"manifest destination is not a regular file: {destination}"
        )


def _create_manifest_temporary(
    preflight_file_descriptor: int,
) -> tuple[str, int]:
    for _ in range(128):
        name = f".manifest.json.{secrets.token_hex(12)}.tmp"
        try:
            file_descriptor = os.open(
                name,
                os.O_WRONLY
                | os.O_CREAT
                | os.O_EXCL
                | os.O_CLOEXEC
                | os.O_NOFOLLOW,
                0o600,
                dir_fd=preflight_file_descriptor,
            )
            return name, file_descriptor
        except FileExistsError:
            continue
    raise OSError(
        errno.EEXIST,
        "could not allocate exclusive manifest temporary",
    )


def _write_all(file_descriptor: int, contents: bytes) -> None:
    remaining = memoryview(contents)
    while remaining:
        try:
            written = os.write(file_descriptor, remaining)
        except InterruptedError:
            continue
        if written <= 0:
            raise OSError(errno.EIO, "manifest temporary write made no progress")
        remaining = remaining[written:]


def _cleanup_manifest_temporary(
    preflight_file_descriptor: int | None,
    temporary_name: str | None,
    temporary_file_descriptor: int | None,
) -> None:
    if temporary_file_descriptor is not None:
        try:
            os.close(temporary_file_descriptor)
        except OSError:
            pass
    if (
        preflight_file_descriptor is not None
        and temporary_name is not None
    ):
        try:
            os.unlink(
                temporary_name,
                dir_fd=preflight_file_descriptor,
            )
        except FileNotFoundError:
            pass
        except OSError:
            pass


def _close_and_cleanup_manifest_directories(
    root_file_descriptor: int,
    results_file_descriptor: int | None,
    preflight_file_descriptor: int | None,
    created_results: bool,
    created_preflight: bool,
    replaced: bool,
) -> None:
    if preflight_file_descriptor is not None:
        os.close(preflight_file_descriptor)
    if (
        created_preflight
        and not replaced
        and results_file_descriptor is not None
    ):
        try:
            os.rmdir(
                "preflight",
                dir_fd=results_file_descriptor,
            )
        except OSError:
            pass
    if results_file_descriptor is not None:
        os.close(results_file_descriptor)
    if created_results and not replaced:
        try:
            os.rmdir("results", dir_fd=root_file_descriptor)
        except OSError:
            pass


def _validate_manifest(values: object) -> None:
    if type(values) is not dict:
        raise ValueError("manifest must be a JSON object")
    _validate_object_keys(values, MANIFEST_KEYS, "manifest")

    if type(values["schema_version"]) is not int:
        raise ValueError("schema_version must be an integer")
    if values["schema_version"] != SCHEMA_VERSION:
        raise ValueError(f"schema_version must be {SCHEMA_VERSION}")
    for key in (
        "model",
        "sandbox",
        "python_version",
        "codex_cli_version",
        "dotnet_info",
    ):
        _require_nonempty_string(values, key)

    os_values = values["os"]
    if type(os_values) is not dict:
        raise ValueError("os must be an object")
    _validate_object_keys(os_values, OS_KEYS, "os")
    for key in OS_KEYS:
        _require_nonempty_string(os_values, key, prefix="os.")

    _validate_digest_map(
        values["input_sha256"],
        "input_sha256",
        frozenset(INPUT_PATHS),
        key_kind="input",
    )
    _validate_digest_map(
        values["baseline_sha256"],
        "baseline_sha256",
        frozenset(SUPPORTED_LANGUAGES),
        key_kind="language",
    )


def _validate_object_keys(
    values: dict[str, object],
    expected_keys: frozenset[str],
    label: str,
) -> None:
    actual_keys = set(values)
    unknown_keys = actual_keys - expected_keys
    missing_keys = expected_keys - actual_keys
    if unknown_keys or missing_keys:
        details: list[str] = []
        if unknown_keys:
            details.append(f"{label} unknown keys: {sorted(unknown_keys)}")
        if missing_keys:
            details.append(f"{label} missing keys: {sorted(missing_keys)}")
        raise ValueError("; ".join(details))


def _require_nonempty_string(
    values: dict[str, object],
    key: str,
    *,
    prefix: str = "",
) -> None:
    value = values[key]
    label = f"{prefix}{key}"
    if type(value) is not str:
        raise ValueError(f"{label} must be a string")
    if not value:
        raise ValueError(f"{label} must not be empty")


def _validate_digest_map(
    values: object,
    label: str,
    expected_keys: frozenset[str],
    *,
    key_kind: str,
) -> None:
    if type(values) is not dict:
        raise ValueError(f"{label} must be an object")
    actual_keys = set(values)
    unknown_keys = actual_keys - expected_keys
    missing_keys = expected_keys - actual_keys
    if unknown_keys or missing_keys:
        details: list[str] = []
        if unknown_keys:
            details.append(f"unknown {key_kind} keys: {sorted(unknown_keys)}")
        if missing_keys:
            details.append(f"missing {key_kind} keys: {sorted(missing_keys)}")
        raise ValueError("; ".join(details))
    for key, value in values.items():
        if (
            type(value) is not str
            or len(value) != 64
            or any(character not in "0123456789abcdef" for character in value)
        ):
            raise ValueError(
                f"{label}[{key!r}] must be a lowercase SHA-256 hex digest"
            )


def main(
    argv: list[str] | None = None,
    *,
    executable_resolver: ExecutableResolver | None = None,
    command_runner: CommandRunner | None = None,
    python_version: tuple[int, int, int] | None = None,
    os_details: OsDetails | None = None,
) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    arguments = parser.parse_args(argv)
    try:
        manifest = run_preflight(
            arguments.config,
            executable_resolver=executable_resolver,
            command_runner=command_runner,
            python_version=python_version,
            os_details=os_details,
        )
    except (OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    manifest_path = arguments.config.resolve().parent / "results/preflight/manifest.json"
    print(f"preflight passed: {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
