"""Render and audit one canonical setup prompt without starting a Codex session."""

import argparse
from collections.abc import Callable
from dataclasses import asdict, dataclass
import hashlib
import json
from pathlib import Path
import subprocess
import sys


EXPERIMENT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from evaluator.common import load_config


SCHEMA_VERSION = 1
PROMPT_DIRECTORY = Path("docs/superpowers/prompts/implementation-language-spike")
SESSIONS = {
    "01-shared-infrastructure": PROMPT_DIRECTORY / "01-shared-infrastructure.md",
    "02-fsharp-baseline": PROMPT_DIRECTORY / "02-fsharp-baseline.md",
    "03-csharp-baseline": PROMPT_DIRECTORY / "03-csharp-baseline.md",
    "04-integration-automation": PROMPT_DIRECTORY / "04-integration-automation.md",
}
REQUIRED_PLAN_REFERENCES = (
    b"docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike.md",
    b"docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md",
)
MANIFEST_KEYS = frozenset(
    {
        "schema_version",
        "session",
        "prompt_path",
        "prompt_sha256",
        "start_commit",
        "codex_cli_version",
        "model",
    }
)

CommandRunner = Callable[[tuple[str, ...], Path], str]


@dataclass(frozen=True, slots=True)
class SetupPromptManifest:
    schema_version: int
    session: str
    prompt_path: str
    prompt_sha256: str
    start_commit: str
    codex_cli_version: str
    model: str


def render_setup_prompt(
    repository_root: Path,
    session: str,
    output_directory: Path,
    *,
    command_runner: CommandRunner | None = None,
) -> SetupPromptManifest:
    """Render *session* into *output_directory* and return its strict manifest."""
    prompt_relative_path = _prompt_path_for_session(session)
    prompt_bytes = _normalize_prompt((repository_root / prompt_relative_path).read_bytes())
    _validate_prompt_references(prompt_bytes)

    runner = command_runner if command_runner is not None else _run_command
    manifest = SetupPromptManifest(
        schema_version=SCHEMA_VERSION,
        session=session,
        prompt_path=prompt_relative_path.as_posix(),
        prompt_sha256=hashlib.sha256(prompt_bytes).hexdigest(),
        start_commit=runner(("git", "rev-parse", "HEAD"), repository_root).strip(),
        codex_cli_version=runner(("codex", "--version"), repository_root).strip(),
        model=_load_model(repository_root),
    )
    _validate_manifest(asdict(manifest))
    _write_output(output_directory, prompt_bytes, manifest)
    return manifest


def load_manifest(path: Path) -> SetupPromptManifest:
    """Load and strictly validate a setup prompt manifest."""
    try:
        values = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise ValueError("manifest must be valid JSON") from error
    _validate_manifest(values)
    return SetupPromptManifest(**values)


def _prompt_path_for_session(session: str) -> Path:
    try:
        return SESSIONS[session]
    except KeyError as error:
        raise ValueError(f"unknown setup session: {session}") from error


def _normalize_prompt(prompt_bytes: bytes) -> bytes:
    return prompt_bytes.replace(b"\r\n", b"\n").replace(b"\r", b"\n").rstrip(b"\n") + b"\n"


def _validate_prompt_references(prompt_bytes: bytes) -> None:
    missing_references = [reference.decode("ascii") for reference in REQUIRED_PLAN_REFERENCES if reference not in prompt_bytes]
    if missing_references:
        raise ValueError(f"rendered prompt is missing required plan references: {missing_references}")


def _load_model(repository_root: Path) -> str:
    config_path = repository_root / "experiments/implementation-language-spike/experiment.toml"
    return load_config(config_path).model


def _run_command(command: tuple[str, ...], repository_root: Path) -> str:
    completed = subprocess.run(
        command,
        cwd=repository_root,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return completed.stdout


def _write_output(output_directory: Path, prompt_bytes: bytes, manifest: SetupPromptManifest) -> None:
    if output_directory.exists() and (not output_directory.is_dir() or any(output_directory.iterdir())):
        raise ValueError(f"output directory must be new or empty: {output_directory}")
    output_directory.mkdir(parents=True, exist_ok=True)
    (output_directory / "prompt.md").write_bytes(prompt_bytes)
    manifest_bytes = (json.dumps(asdict(manifest), indent=2, sort_keys=True) + "\n").encode("utf-8")
    (output_directory / "manifest.json").write_bytes(manifest_bytes)


def _validate_manifest(values: object) -> None:
    if type(values) is not dict:
        raise ValueError("manifest must be a JSON object")
    actual_keys = set(values)
    unknown_keys = actual_keys - MANIFEST_KEYS
    missing_keys = MANIFEST_KEYS - actual_keys
    if unknown_keys or missing_keys:
        details = []
        if unknown_keys:
            details.append(f"unknown keys: {sorted(unknown_keys)}")
        if missing_keys:
            details.append(f"missing keys: {sorted(missing_keys)}")
        raise ValueError("; ".join(details))

    if type(values["schema_version"]) is not int:
        raise ValueError("schema_version must be an integer")
    if values["schema_version"] != SCHEMA_VERSION:
        raise ValueError(f"schema_version must be {SCHEMA_VERSION}")
    for key in MANIFEST_KEYS - {"schema_version"}:
        if type(values[key]) is not str:
            raise ValueError(f"{key} must be a string")
        if not values[key]:
            raise ValueError(f"{key} must not be empty")

    expected_prompt_path = _prompt_path_for_session(values["session"]).as_posix()
    if values["prompt_path"] != expected_prompt_path:
        raise ValueError("prompt_path does not match session")
    prompt_sha256 = values["prompt_sha256"]
    if len(prompt_sha256) != 64 or any(character not in "0123456789abcdef" for character in prompt_sha256):
        raise ValueError("prompt_sha256 must be a lowercase SHA-256 hex digest")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, choices=tuple(SESSIONS))
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    repository_root = Path(__file__).resolve().parents[3]
    render_setup_prompt(repository_root, arguments.session, arguments.output)


if __name__ == "__main__":
    main()
