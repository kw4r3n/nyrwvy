"""Validate experiment prerequisites before any language trial can start."""

import argparse
from collections.abc import Callable
from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile


EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from evaluator.common import ExperimentConfig, load_config


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

ExecutableResolver = Callable[[str], str | None]
CommandRunner = Callable[[tuple[str, ...], Path], subprocess.CompletedProcess[str]]


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


def run_preflight(
    config_path: Path,
    *,
    executable_resolver: ExecutableResolver | None = None,
    command_runner: CommandRunner | None = None,
    python_version: tuple[int, int, int] | None = None,
    os_details: OsDetails | None = None,
) -> PreflightManifest:
    """Validate prerequisites and write the manifest only after complete success."""
    config_path = config_path.resolve()
    experiment_root = config_path.parent
    config = load_config(config_path)
    resolver = executable_resolver if executable_resolver is not None else shutil.which
    runner = command_runner if command_runner is not None else _run_command
    selected_python_version = (
        python_version
        if python_version is not None
        else (sys.version_info.major, sys.version_info.minor, sys.version_info.micro)
    )

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
    for baseline_path in baseline_paths.values():
        if not baseline_path.is_dir() or baseline_path.is_symlink():
            problems.append(f"missing baseline directory: {baseline_path}")

    input_paths = {
        relative_path: experiment_root / relative_path
        for relative_path in INPUT_PATHS
    }
    for input_path in input_paths.values():
        if not input_path.is_file() or input_path.is_symlink():
            problems.append(f"missing required input file: {input_path}")

    if problems:
        raise PreflightError(problems)

    codex_version_result = _invoke(
        runner,
        (executables["codex"], "--version"),
        experiment_root,
        "codex --version",
        problems,
    )
    dotnet_info_result = _invoke(
        runner,
        (executables["dotnet"], "--info"),
        experiment_root,
        "dotnet --info",
        problems,
    )
    codex_version = _required_output(codex_version_result, "codex --version", problems)
    dotnet_info = _required_output(dotnet_info_result, "dotnet --info", problems)
    if problems:
        raise PreflightError(problems)

    smoke_problem = _run_model_smoke(config, executables, runner)
    if smoke_problem is not None:
        raise PreflightError([smoke_problem])

    selected_os_details = os_details if os_details is not None else _current_os_details()
    manifest = PreflightManifest(
        schema_version=SCHEMA_VERSION,
        model=config.model,
        sandbox=config.sandbox,
        python_version=".".join(str(component) for component in selected_python_version),
        codex_cli_version=codex_version,
        dotnet_info=dotnet_info,
        os=selected_os_details,
        input_sha256={
            relative_path: digest_file(path)
            for relative_path, path in input_paths.items()
        },
        baseline_sha256={
            language: digest_tree(path)
            for language, path in baseline_paths.items()
        },
    )
    _validate_manifest(asdict(manifest))
    _write_manifest(experiment_root / "results/preflight/manifest.json", manifest)
    return manifest


def digest_file(path: Path) -> str:
    """Return the SHA-256 digest of one regular file's exact bytes."""
    if not path.is_file() or path.is_symlink():
        raise ValueError(f"cannot hash non-regular file: {path}")
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def digest_tree(root: Path) -> str:
    """Hash included relative paths and bytes in stable lexical order."""
    if not root.is_dir() or root.is_symlink():
        raise ValueError(f"cannot hash non-directory tree: {root}")

    included_files: list[Path] = []
    for directory, directory_names, file_names in os.walk(root, topdown=True, followlinks=False):
        directory_path = Path(directory)
        directory_names[:] = sorted(
            name
            for name in directory_names
            if name not in _EXCLUDED_DIRECTORIES
            and not (directory_path / name).is_symlink()
        )
        for file_name in sorted(file_names):
            path = directory_path / file_name
            if path.is_symlink() or file_name.endswith(_EXCLUDED_FILE_SUFFIXES):
                continue
            included_files.append(path)

    digest = hashlib.sha256()
    for path in sorted(included_files, key=lambda candidate: candidate.relative_to(root).as_posix()):
        relative_bytes = path.relative_to(root).as_posix().encode("utf-8")
        contents = path.read_bytes()
        digest.update(len(relative_bytes).to_bytes(8, "big"))
        digest.update(relative_bytes)
        digest.update(len(contents).to_bytes(8, "big"))
        digest.update(contents)
    return digest.hexdigest()


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
    problems: list[str],
) -> subprocess.CompletedProcess[str] | None:
    try:
        completed = runner(command, cwd)
    except (OSError, subprocess.SubprocessError) as error:
        problems.append(f"{label} could not run: {error}")
        return None
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "no diagnostic output"
        problems.append(f"{label} failed (exit {completed.returncode}): {detail}")
        return None
    return completed


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
            problems,
        )
        if initialized is None:
            return problems[0]

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
            problems,
        )
        if smoke is None:
            return problems[0]

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
            problems,
        )
        if status is None:
            return problems[0]
        if status.stdout.strip():
            return f"model smoke modified temporary repository: {status.stdout.strip()}"
    return None


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
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=cwd,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=120,
    )


def _write_manifest(path: Path, manifest: PreflightManifest) -> None:
    manifest_bytes = (json.dumps(asdict(manifest), indent=2, sort_keys=True) + "\n").encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(manifest_bytes)


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
