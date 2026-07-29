from pathlib import Path
import sys
import tempfile
import unittest


EXPERIMENT_ROOT = Path(__file__).parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from evaluator.common import load_config


class ConfigTests(unittest.TestCase):
    def test_loads_canonical_configuration(self) -> None:
        config = load_config(EXPERIMENT_ROOT / "experiment.toml")
        self.assertEqual(config.schema_version, 1)
        self.assertEqual(config.model, "gpt-5.6-sol")
        self.assertEqual(config.sandbox, "workspace-write")
        self.assertEqual(config.timeout_seconds, 3600)
        self.assertEqual(config.calibration_runs, 1)
        self.assertEqual(config.scored_runs, 3)
        self.assertEqual(config.languages, ("fsharp", "csharp"))

    def test_rejects_unknown_keys(self) -> None:
        with self._temporary_config("extra = true\n") as path:
            with self.assertRaises(ValueError):
                load_config(path)

    def test_rejects_missing_keys(self) -> None:
        with self._temporary_config(contents=self._canonical_config().replace("scored_runs = 3\n", "")) as path:
            with self.assertRaises(ValueError):
                load_config(path)

    def test_rejects_invalid_contract_values(self) -> None:
        invalid_values = (
            ("schema_version = 1", "schema_version = 2"),
            ('sandbox = "workspace-write"', 'sandbox = "read-only"'),
            ('languages = ["fsharp", "csharp"]', 'languages = ["fsharp", "fsharp"]'),
            ("timeout_seconds = 3600", "timeout_seconds = 0"),
            ("calibration_runs = 1", "calibration_runs = 0"),
            ("scored_runs = 3", "scored_runs = 0"),
        )
        for expected, replacement in invalid_values:
            with self.subTest(replacement=replacement):
                with self._temporary_config(contents=self._canonical_config().replace(expected, replacement)) as path:
                    with self.assertRaises(ValueError):
                        load_config(path)

    def test_rejects_boolean_where_integer_is_required(self) -> None:
        with self._temporary_config(contents=self._canonical_config().replace("timeout_seconds = 3600", "timeout_seconds = true")) as path:
            with self.assertRaises(ValueError):
                load_config(path)

    def test_rejects_wrong_language_shape(self) -> None:
        with self._temporary_config(contents=self._canonical_config().replace('languages = ["fsharp", "csharp"]', 'languages = ["csharp", "fsharp"]')) as path:
            with self.assertRaises(ValueError):
                load_config(path)

    @staticmethod
    def _canonical_config() -> str:
        return (
            "schema_version = 1\n"
            'model = "gpt-5.6-sol"\n'
            'sandbox = "workspace-write"\n'
            "timeout_seconds = 3600\n"
            "calibration_runs = 1\n"
            "scored_runs = 3\n"
            'languages = ["fsharp", "csharp"]\n'
        )

    @staticmethod
    def _temporary_config(suffix: str = "", contents: str | None = None):
        directory = tempfile.TemporaryDirectory()
        path = Path(directory.name) / "experiment.toml"
        path.write_text(contents if contents is not None else ConfigTests._canonical_config() + suffix, encoding="utf-8")
        return _TemporaryConfig(directory, path)


class _TemporaryConfig:
    def __init__(self, directory: tempfile.TemporaryDirectory[str], path: Path) -> None:
        self._directory = directory
        self.path = path

    def __enter__(self) -> Path:
        return self.path

    def __exit__(self, exception_type: object, exception: object, traceback: object) -> None:
        self._directory.cleanup()


if __name__ == "__main__":
    unittest.main()
