"""Shared strict configuration loading for the implementation-language experiment."""

from dataclasses import dataclass
from pathlib import Path
import tomllib


_EXPECTED_KEYS = frozenset(
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
_EXPECTED_LANGUAGES = ("fsharp", "csharp")


@dataclass(frozen=True, slots=True)
class ExperimentConfig:
    schema_version: int
    model: str
    sandbox: str
    timeout_seconds: int
    calibration_runs: int
    scored_runs: int
    languages: tuple[str, str]


def load_config(path: Path) -> ExperimentConfig:
    """Load and validate the frozen experiment contract at *path*."""
    with path.open("rb") as config_file:
        values = tomllib.load(config_file)

    _validate_keys(values)
    schema_version = _require_integer(values, "schema_version")
    model = _require_string(values, "model")
    sandbox = _require_string(values, "sandbox")
    timeout_seconds = _require_positive_integer(values, "timeout_seconds")
    calibration_runs = _require_positive_integer(values, "calibration_runs")
    scored_runs = _require_positive_integer(values, "scored_runs")
    languages = _require_languages(values)

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
        languages=languages,
    )


def _validate_keys(values: dict[str, object]) -> None:
    actual_keys = set(values)
    unknown_keys = actual_keys - _EXPECTED_KEYS
    missing_keys = _EXPECTED_KEYS - actual_keys
    if unknown_keys or missing_keys:
        details = []
        if unknown_keys:
            details.append(f"unknown keys: {sorted(unknown_keys)}")
        if missing_keys:
            details.append(f"missing keys: {sorted(missing_keys)}")
        raise ValueError("; ".join(details))


def _require_integer(values: dict[str, object], key: str) -> int:
    value = values[key]
    if type(value) is not int:
        raise ValueError(f"{key} must be an integer")
    return value


def _require_positive_integer(values: dict[str, object], key: str) -> int:
    value = _require_integer(values, key)
    if value <= 0:
        raise ValueError(f"{key} must be positive")
    return value


def _require_string(values: dict[str, object], key: str) -> str:
    value = values[key]
    if type(value) is not str:
        raise ValueError(f"{key} must be a string")
    return value


def _require_languages(values: dict[str, object]) -> tuple[str, str]:
    languages = values["languages"]
    if type(languages) is not list or any(type(language) is not str for language in languages):
        raise ValueError("languages must be a list of strings")

    language_tuple = tuple(languages)
    if language_tuple != _EXPECTED_LANGUAGES:
        raise ValueError("languages must be ['fsharp', 'csharp'] without duplicates")
    return language_tuple
