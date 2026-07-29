from contextlib import redirect_stderr
from dataclasses import asdict
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


EXPERIMENT_ROOT = Path(__file__).parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from scripts import preflight


PUBLIC_INPUTS = {
    "shared/public/AGENTS.md": b"agent instructions\n",
    "shared/public/acceptance.md": b"acceptance\n",
    "shared/public/fixtures/initial-snapshot-v1.json": b'{"snapshot":1}\n',
    "shared/public/maintenance-task.md": b"maintenance\n",
    "shared/public/schema/snapshot-v1.schema.json": b'{"schema":1}\n',
    "shared/public/semantic-contract.md": b"semantic contract\n",
}


class FakeCommands:
    def __init__(self) -> None:
        self.executables = {
            name: f"/test-tools/{name}"
            for name in ("codex", "dotnet", "git", "node", "npm")
        }
        self.calls: list[tuple[tuple[str, ...], Path]] = []
        self.overrides: dict[tuple[str, ...], subprocess.CompletedProcess[str]] = {}

    def resolve(self, name: str) -> str | None:
        return self.executables.get(name)

    def run(
        self,
        command: tuple[str, ...],
        cwd: Path,
    ) -> subprocess.CompletedProcess[str]:
        self.calls.append((command, cwd))
        if command in self.overrides:
            return self.overrides[command]
        if command == ("/test-tools/dotnet", "--version"):
            return self._completed(command, stdout="10.0.100\n")
        if command == ("/test-tools/codex", "exec", "--help"):
            return self._completed(command, stdout="usage: codex exec --json --model MODEL --sandbox MODE\n")
        if command == ("/test-tools/codex", "--version"):
            return self._completed(command, stdout="codex-cli 1.2.3\n")
        if command == ("/test-tools/dotnet", "--info"):
            return self._completed(command, stdout=".NET SDK:\n Version: 10.0.100\n")
        if command == ("/test-tools/git", "init", "--quiet"):
            return self._completed(command)
        if command[:2] == ("/test-tools/codex", "exec"):
            return self._completed(command, stdout='{"type":"result","status":"ok"}\n')
        if command == (
            "/test-tools/git",
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
        ):
            return self._completed(command)
        raise AssertionError(f"unexpected command: {command!r}")

    @staticmethod
    def _completed(
        command: tuple[str, ...],
        *,
        returncode: int = 0,
        stdout: str = "",
        stderr: str = "",
    ) -> subprocess.CompletedProcess[str]:
        return subprocess.CompletedProcess(command, returncode, stdout, stderr)


class PreflightTests(unittest.TestCase):
    def test_full_success_writes_deterministic_manifest_and_loader_round_trips(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            manifest = preflight.run_preflight(
                experiment / "experiment.toml",
                executable_resolver=commands.resolve,
                command_runner=commands.run,
                python_version=(3, 13, 7),
                os_details=preflight.OsDetails(
                    system="TestOS",
                    release="test-release",
                    version="test-version",
                    machine="test-machine",
                ),
            )

            manifest_path = experiment / "results/preflight/manifest.json"
            manifest_bytes = manifest_path.read_bytes()
            expected_values = {
                "baseline_sha256": {
                    "csharp": "b13b26f732b88047ea85013216463050118fb3e78a9020b02d34b252562afae4",
                    "fsharp": "bd5f65f64eb35cfc6a631944c358607bf82be05e12181149af0711e0490f358d",
                },
                "codex_cli_version": "codex-cli 1.2.3",
                "dotnet_info": ".NET SDK:\n Version: 10.0.100",
                "input_sha256": {
                    "experiment.toml": "1fad3b04d6acbbd242fd3727ed416ba7d2c4801fe671f859ff17ebfb9d77f40d",
                    "shared/public/AGENTS.md": "1b935c7b032f64428cebfabda3acd84d27353fe15153cfdfc20cca4de7913e36",
                    "shared/public/acceptance.md": "93682060bf3e8203a105bcda6ef7ee727d90fbe3db4f8848fa2f97e5e7c2bf9f",
                    "shared/public/fixtures/initial-snapshot-v1.json": "7f300fafa632dcb0b60ff7dd091f651b40b5985a7e705b4fc01e18eaff4d255f",
                    "shared/public/maintenance-task.md": "6ed92e770aaa702a9020a7db027ab1193c27d7896fb124ce4321f1a57d9571fb",
                    "shared/public/schema/snapshot-v1.schema.json": "6b823fa123b900a4139de2101277275af8329f3a3d34c00ef3bf4fc6bf60287e",
                    "shared/public/semantic-contract.md": "baade4fc7d03cfcfa6e61be1f6a13cbe7fcda7be5aa2f8295e59273e4e728240",
                },
                "model": "gpt-5.6-sol",
                "os": {
                    "machine": "test-machine",
                    "release": "test-release",
                    "system": "TestOS",
                    "version": "test-version",
                },
                "python_version": "3.13.7",
                "sandbox": "workspace-write",
                "schema_version": 1,
            }
            self.assertTrue(manifest_bytes.endswith(b"\n"))
            self.assertFalse(manifest_bytes.endswith(b"\n\n"))
            self.assertNotIn(b"\r", manifest_bytes)
            self.assertEqual(asdict(manifest), expected_values)
            self.assertEqual(
                manifest_bytes,
                (json.dumps(expected_values, indent=2, sort_keys=True) + "\n").encode("utf-8"),
            )
            self.assertEqual(preflight.load_manifest(manifest_path), manifest)
            self.assertEqual(manifest.schema_version, 1)
            self.assertEqual(manifest.model, "gpt-5.6-sol")
            self.assertEqual(manifest.sandbox, "workspace-write")
            self.assertEqual(manifest.python_version, "3.13.7")
            self.assertEqual(manifest.codex_cli_version, "codex-cli 1.2.3")
            self.assertEqual(manifest.dotnet_info, ".NET SDK:\n Version: 10.0.100")
            self.assertEqual(
                set(manifest.input_sha256),
                {"experiment.toml", *PUBLIC_INPUTS},
            )
            self.assertEqual(set(manifest.baseline_sha256), {"fsharp", "csharp"})
            smoke_commands = [
                command
                for command, _ in commands.calls
                if command[:2] == ("/test-tools/codex", "exec")
                and command[-1] != "--help"
            ]
            self.assertEqual(len(smoke_commands), 1)
            self.assertEqual(
                smoke_commands[0][2:8],
                (
                    "--json",
                    "--model",
                    "gpt-5.6-sol",
                    "--sandbox",
                    "read-only",
                    smoke_commands[0][-1],
                ),
            )

    def test_rejects_unsupported_python_versions_without_writing(self) -> None:
        for version in ((3, 12, 9), (4, 0, 0)):
            with self.subTest(version=version), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    r"Python 3\.13 or newer within major version 3 is required",
                ):
                    self._run(experiment, commands, python_version=version)
                self.assertFalse((experiment / "results/preflight/manifest.json").exists())
                self.assertFalse(self._smoke_was_called(commands))

    def test_rejects_each_missing_executable(self) -> None:
        for missing in ("dotnet", "git", "node", "npm", "codex"):
            with self.subTest(missing=missing), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                del commands.executables[missing]
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    rf"required executable is unavailable: {missing}",
                ):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight/manifest.json").exists())
                self.assertFalse(self._smoke_was_called(commands))

    def test_rejects_dotnet_wrong_major_and_version_command_failure(self) -> None:
        cases = (
            (
                subprocess.CompletedProcess((), 0, "9.0.999\n", ""),
                r"dotnet --version must begin with 10\.",
            ),
            (
                subprocess.CompletedProcess((), 7, "", "broken SDK\n"),
                r"dotnet --version failed \(exit 7\): broken SDK",
            ),
        )
        for result, message in cases:
            with self.subTest(message=message), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                commands.overrides[("/test-tools/dotnet", "--version")] = result
                with self.assertRaisesRegex(preflight.PreflightError, message):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight/manifest.json").exists())
                self.assertFalse(self._smoke_was_called(commands))

    def test_rejects_each_missing_codex_exec_flag(self) -> None:
        complete_help = "usage: codex exec --json --model MODEL --sandbox MODE\n"
        for flag in ("--json", "--model", "--sandbox"):
            with self.subTest(flag=flag), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                command = ("/test-tools/codex", "exec", "--help")
                commands.overrides[command] = subprocess.CompletedProcess(
                    command,
                    0,
                    complete_help.replace(flag, ""),
                    "",
                )
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    rf"codex exec --help is missing required option: {flag}",
                ):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight/manifest.json").exists())
                self.assertFalse(self._smoke_was_called(commands))

    def test_rejects_longer_codex_options_that_only_contain_required_names(self) -> None:
        lookalikes = {
            "--json": "--json-schema",
            "--model": "--model-provider",
            "--sandbox": "--sandbox-policy",
        }
        complete_help = "usage: codex exec --json --model MODEL --sandbox MODE\n"
        for required, lookalike in lookalikes.items():
            with self.subTest(required=required), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                command = ("/test-tools/codex", "exec", "--help")
                commands.overrides[command] = subprocess.CompletedProcess(
                    command,
                    0,
                    complete_help.replace(required, lookalike),
                    "",
                )
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    rf"codex exec --help is missing required option: {required}",
                ):
                    self._run(experiment, commands)

    def test_rejects_codex_help_command_failure(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            command = ("/test-tools/codex", "exec", "--help")
            commands.overrides[command] = subprocess.CompletedProcess(command, 2, "", "bad help\n")
            with self.assertRaisesRegex(
                preflight.PreflightError,
                r"codex exec --help failed \(exit 2\): bad help",
            ):
                self._run(experiment, commands)
            self.assertFalse((experiment / "results/preflight/manifest.json").exists())
            self.assertFalse(self._smoke_was_called(commands))

    def test_rejects_metadata_command_failures_before_smoke(self) -> None:
        cases = (
            (
                ("/test-tools/codex", "--version"),
                "codex --version failed",
            ),
            (
                ("/test-tools/dotnet", "--info"),
                "dotnet --info failed",
            ),
        )
        for command, message in cases:
            with self.subTest(command=command), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                commands.overrides[command] = subprocess.CompletedProcess(command, 3, "", "metadata error\n")
                with self.assertRaisesRegex(preflight.PreflightError, message):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight/manifest.json").exists())
                self.assertFalse(self._smoke_was_called(commands))

    def test_missing_baselines_reports_both_exact_paths_without_write_or_smoke(self) -> None:
        with _synthetic_experiment(baselines=False) as experiment:
            commands = FakeCommands()
            with self.assertRaises(preflight.PreflightError) as caught:
                self._run(experiment, commands)

            message = str(caught.exception)
            self.assertIn(f"missing baseline directory: {experiment / 'fsharp'}", message)
            self.assertIn(f"missing baseline directory: {experiment / 'csharp'}", message)
            self.assertFalse((experiment / "results/preflight").exists())
            self.assertFalse(self._smoke_was_called(commands))

    def test_smoke_failure_and_dirty_smoke_repository_do_not_write(self) -> None:
        cases = (
            ("codex", 9, "", "authentication failed\n", r"model smoke failed \(exit 9\): authentication failed"),
            ("status", 0, "untracked.txt\n", "", r"model smoke modified temporary repository: untracked\.txt"),
        )
        for mode, returncode, stdout, stderr, message in cases:
            with self.subTest(mode=mode), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                if mode == "codex":
                    commands.run = _failing_smoke_runner(commands, returncode, stdout, stderr)
                else:
                    status = (
                        "/test-tools/git",
                        "status",
                        "--porcelain=v1",
                        "--untracked-files=all",
                    )
                    commands.overrides[status] = subprocess.CompletedProcess(status, returncode, stdout, stderr)
                with self.assertRaisesRegex(preflight.PreflightError, message):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight").exists())

    def test_tree_digest_is_order_independent_and_sensitive_to_paths_and_content(self) -> None:
        with tempfile.TemporaryDirectory() as first_temp, tempfile.TemporaryDirectory() as second_temp:
            first = Path(first_temp)
            second = Path(second_temp)
            (first / "nested").mkdir()
            (first / "z.txt").write_bytes(b"z")
            (first / "nested/a.txt").write_bytes(b"a")
            (second / "nested").mkdir()
            (second / "nested/a.txt").write_bytes(b"a")
            (second / "z.txt").write_bytes(b"z")
            original = preflight.digest_tree(first)
            self.assertEqual(original, preflight.digest_tree(second))

            (second / "nested/a.txt").write_bytes(b"changed")
            self.assertNotEqual(original, preflight.digest_tree(second))
            (second / "nested/a.txt").write_bytes(b"a")
            (second / "nested/a.txt").rename(second / "nested/renamed.txt")
            self.assertNotEqual(original, preflight.digest_tree(second))

    def test_tree_digest_excludes_generated_logs_caches_and_symlinks(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "source.txt").write_bytes(b"source")
            initial = preflight.digest_tree(root)
            for directory in (".git", "bin", "obj", "__pycache__"):
                path = root / directory
                path.mkdir()
                (path / "ignored.txt").write_bytes(b"ignored")
            for name in ("cache.pyc", "events.jsonl", "stderr.log"):
                (root / name).write_bytes(b"ignored")
            (root / "link.txt").symlink_to(root / "source.txt")
            (root / "linked-directory").symlink_to(root, target_is_directory=True)
            self.assertEqual(initial, preflight.digest_tree(root))

    def test_strict_loader_rejects_malformed_manifests(self) -> None:
        valid = _valid_manifest_values()
        cases: tuple[tuple[str, object, str], ...] = (
            ("non-object", [], "manifest must be a JSON object"),
            ("unknown field", {**valid, "extra": "x"}, "unknown keys"),
            (
                "missing field",
                {key: value for key, value in valid.items() if key != "model"},
                "missing keys",
            ),
            ("boolean schema", {**valid, "schema_version": True}, "schema_version must be an integer"),
            ("unsupported schema", {**valid, "schema_version": 2}, "schema_version must be 1"),
            ("empty model", {**valid, "model": ""}, "model must not be empty"),
            (
                "malformed input digest",
                {**valid, "input_sha256": {**valid["input_sha256"], "experiment.toml": "ABC"}},
                "input_sha256\\['experiment.toml'\\] must be a lowercase SHA-256 hex digest",
            ),
            (
                "unsupported language",
                {**valid, "baseline_sha256": {**valid["baseline_sha256"], "rust": "a" * 64}},
                "unknown language keys",
            ),
            (
                "missing language",
                {**valid, "baseline_sha256": {"fsharp": "a" * 64}},
                "missing language keys",
            ),
            (
                "unknown OS field",
                {**valid, "os": {**valid["os"], "extra": "x"}},
                "os unknown keys",
            ),
            (
                "wrong digest map type",
                {**valid, "baseline_sha256": []},
                "baseline_sha256 must be an object",
            ),
        )
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "manifest.json"
            for name, values, message in cases:
                with self.subTest(name=name):
                    path.write_text(json.dumps(values), encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, message):
                        preflight.load_manifest(path)

    def test_cli_error_lists_missing_baselines(self) -> None:
        with _synthetic_experiment(baselines=False) as experiment:
            commands = FakeCommands()
            stderr = io.StringIO()
            with redirect_stderr(stderr):
                exit_code = preflight.main(
                    ["--config", str(experiment / "experiment.toml")],
                    executable_resolver=commands.resolve,
                    command_runner=commands.run,
                    python_version=(3, 13, 7),
                    os_details=preflight.OsDetails("TestOS", "release", "version", "machine"),
                )
            self.assertEqual(exit_code, 1)
            self.assertIn("preflight failed:", stderr.getvalue())
            self.assertIn(str(experiment / "fsharp"), stderr.getvalue())
            self.assertIn(str(experiment / "csharp"), stderr.getvalue())

    @staticmethod
    def _run(
        experiment: Path,
        commands: FakeCommands,
        *,
        python_version: tuple[int, int, int] = (3, 13, 7),
    ) -> preflight.PreflightManifest:
        return preflight.run_preflight(
            experiment / "experiment.toml",
            executable_resolver=commands.resolve,
            command_runner=commands.run,
            python_version=python_version,
            os_details=preflight.OsDetails("TestOS", "release", "version", "machine"),
        )

    @staticmethod
    def _smoke_was_called(commands: FakeCommands) -> bool:
        return any(
            command[:2] == ("/test-tools/codex", "exec") and command[-1] != "--help"
            for command, _ in commands.calls
        )


def _failing_smoke_runner(
    commands: FakeCommands,
    returncode: int,
    stdout: str,
    stderr: str,
):
    default_run = commands.run

    def run(command: tuple[str, ...], cwd: Path) -> subprocess.CompletedProcess[str]:
        if command[:2] == ("/test-tools/codex", "exec") and command[-1] != "--help":
            commands.calls.append((command, cwd))
            return subprocess.CompletedProcess(command, returncode, stdout, stderr)
        return default_run(command, cwd)

    return run


def _valid_manifest_values() -> dict[str, object]:
    return {
        "schema_version": 1,
        "model": "gpt-5.6-sol",
        "sandbox": "workspace-write",
        "python_version": "3.13.7",
        "codex_cli_version": "codex-cli 1.2.3",
        "dotnet_info": ".NET SDK 10.0.100",
        "os": {
            "system": "TestOS",
            "release": "release",
            "version": "version",
            "machine": "machine",
        },
        "input_sha256": {
            "experiment.toml": "a" * 64,
            **{path: "b" * 64 for path in PUBLIC_INPUTS},
        },
        "baseline_sha256": {
            "fsharp": "c" * 64,
            "csharp": "d" * 64,
        },
    }


def _synthetic_experiment(*, baselines: bool = True):
    directory = tempfile.TemporaryDirectory()
    root = Path(directory.name).resolve()
    (root / "experiment.toml").write_text(
        "schema_version = 1\n"
        'model = "gpt-5.6-sol"\n'
        'sandbox = "workspace-write"\n'
        "timeout_seconds = 3600\n"
        "calibration_runs = 1\n"
        "scored_runs = 3\n"
        'languages = ["fsharp", "csharp"]\n',
        encoding="utf-8",
    )
    for relative_path, contents in PUBLIC_INPUTS.items():
        path = root / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(contents)
    if baselines:
        for language in ("fsharp", "csharp"):
            baseline = root / language
            baseline.mkdir()
            (baseline / "source.txt").write_text(f"{language}\n", encoding="utf-8")
    return _TemporaryDirectory(directory, root)


class _TemporaryDirectory:
    def __init__(self, directory: tempfile.TemporaryDirectory[str], root: Path) -> None:
        self._directory = directory
        self.root = root

    def __enter__(self) -> Path:
        return self.root

    def __exit__(self, exception_type: object, exception: object, traceback: object) -> None:
        self._directory.cleanup()


if __name__ == "__main__":
    unittest.main()
