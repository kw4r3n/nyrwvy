from contextlib import redirect_stderr
from dataclasses import asdict
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


EXPERIMENT_ROOT = Path(__file__).parents[1]
sys.path.insert(0, str(EXPERIMENT_ROOT))

from scripts import preflight
from scripts import prepare_workspace


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
        self.calls: list[tuple[tuple[str, ...], Path, int | None]] = []
        self.overrides: dict[tuple[str, ...], subprocess.CompletedProcess[str]] = {}
        self.before_command = None
        self.on_smoke = None
        self.failure_for: str | None = None
        self.failure: BaseException | None = None
        self.smoke_stdout = (
            '{"type":"item.completed","item":{"type":"agent_message",'
            '"text":" PREFLIGHT_OK "}}\n'
        )

    def resolve(self, name: str) -> str | None:
        return self.executables.get(name)

    def run(
        self,
        command: tuple[str, ...],
        cwd: Path,
        timeout_seconds: int | None = None,
    ) -> subprocess.CompletedProcess[str]:
        self.calls.append((command, cwd, timeout_seconds))
        if self.before_command is not None:
            self.before_command(command, cwd)
        command_kind = self._command_kind(command)
        if command_kind == self.failure_for:
            assert self.failure is not None
            raise self.failure
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
            (cwd / ".git/info").mkdir(parents=True)
            (cwd / ".git/config").write_text("[core]\n", encoding="utf-8")
            (cwd / ".git/info/exclude").write_text("ignored.tmp\n", encoding="utf-8")
            return self._completed(command)
        if command[:2] == ("/test-tools/codex", "exec"):
            if self.on_smoke is not None:
                self.on_smoke(cwd)
            return self._completed(command, stdout=self.smoke_stdout)
        if command == (
            "/test-tools/git",
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
        ):
            return self._completed(command)
        raise AssertionError(f"unexpected command: {command!r}")

    @staticmethod
    def _command_kind(command: tuple[str, ...]) -> str:
        if command == ("/test-tools/dotnet", "--version"):
            return "dotnet-version"
        if command == ("/test-tools/codex", "exec", "--help"):
            return "codex-help"
        if command == ("/test-tools/codex", "--version"):
            return "codex-version"
        if command == ("/test-tools/dotnet", "--info"):
            return "dotnet-info"
        if command == ("/test-tools/git", "init", "--quiet"):
            return "git-init"
        if command[:2] == ("/test-tools/codex", "exec"):
            return "codex-smoke"
        if command == (
            "/test-tools/git",
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
        ):
            return "git-status"
        return "unknown"

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
                    "csharp": "e44913efd7261ea42a4df91763860681afb9eace83cec51ae79cea45509d7897",
                    "fsharp": "6966ec370ffbc5cd842236723741b21362c0eefb908456fca7d28b4e86c943cf",
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
                for command, _, _ in commands.calls
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
            for _, _, timeout_seconds in commands.calls:
                self.assertIs(type(timeout_seconds), int)
                self.assertGreater(timeout_seconds, 0)

    def test_baseline_digest_matches_the_exact_prepared_workspace_inventory(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            manifest = self._run(experiment, commands)
            destination = experiment.parents[2] / "prepared-fsharp"

            prepare_workspace.prepare_workspace(
                experiment,
                "fsharp",
                destination,
            )

            self.assertEqual(
                manifest.baseline_sha256["fsharp"],
                _digest_exported_inventory(destination),
            )

    def test_committed_log_jsonl_and_mode_changes_affect_inventory_but_build_outputs_do_not(
        self,
    ) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            initial = self._run(experiment, commands).baseline_sha256["fsharp"]

            history = experiment / "fsharp/history.jsonl"
            history.write_bytes(b'{"tracked":true}\n')
            _commit_paths(experiment, "test: add tracked JSONL", history)
            with_jsonl = self._run(
                experiment,
                FakeCommands(),
            ).baseline_sha256["fsharp"]
            self.assertNotEqual(initial, with_jsonl)

            diagnostic = experiment / "fsharp/diagnostic.log"
            diagnostic.write_bytes(b"tracked diagnostic\n")
            _commit_paths(experiment, "test: add tracked log", diagnostic)
            with_log = self._run(
                experiment,
                FakeCommands(),
            ).baseline_sha256["fsharp"]
            self.assertNotEqual(with_jsonl, with_log)

            source = experiment / "fsharp/source.txt"
            source.chmod(source.stat().st_mode | stat.S_IXUSR)
            _commit_paths(experiment, "test: make source executable", source)
            executable = self._run(
                experiment,
                FakeCommands(),
            ).baseline_sha256["fsharp"]
            self.assertNotEqual(with_log, executable)

            build_output = experiment / "fsharp/bin/generated.dll"
            build_output.parent.mkdir()
            build_output.write_bytes(b"tracked generated output\n")
            _commit_paths(
                experiment,
                "test: add excluded build output",
                build_output,
                force=True,
            )
            with_build_output = self._run(
                experiment,
                FakeCommands(),
            ).baseline_sha256["fsharp"]
            self.assertEqual(executable, with_build_output)

    def test_rejects_dirty_tracked_baseline_instead_of_hashing_worktree_bytes(self) -> None:
        with _synthetic_experiment() as experiment:
            (experiment / "fsharp/source.txt").write_text(
                "dirty worktree bytes\n",
                encoding="utf-8",
            )
            commands = FakeCommands()

            with self.assertRaisesRegex(
                preflight.PreflightError,
                "tracked source inputs are dirty",
            ):
                self._run(experiment, commands)

            self.assertFalse(self._smoke_was_called(commands))
            self.assertFalse(
                (experiment / "results/preflight/manifest.json").exists()
            )

    def test_rejects_unsafe_committed_symlink_before_smoke(self) -> None:
        with _synthetic_experiment() as experiment:
            link = experiment / "fsharp/unsafe-link"
            link.symlink_to("source.txt")
            _commit_paths(experiment, "test: add unsafe symlink", link)
            commands = FakeCommands()

            with self.assertRaisesRegex(
                preflight.PreflightError,
                "committed tree contains unsafe mode 120000",
            ):
                self._run(experiment, commands)

            self.assertFalse(self._smoke_was_called(commands))
            self.assertFalse(
                (experiment / "results/preflight/manifest.json").exists()
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

    def test_smoke_requires_exact_success_token_in_valid_jsonl(self) -> None:
        cases = (
            (
                "empty",
                "",
                "final completed agent message was not exact PREFLIGHT_OK",
            ),
            (
                "blank",
                "\n\n",
                "final completed agent message was not exact PREFLIGHT_OK",
            ),
            ("malformed", "{not json}\n", "returned malformed JSONL"),
            (
                "token plus malformed",
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"PREFLIGHT_OK"}}\nnot-json\n',
                "returned malformed JSONL",
            ),
            (
                "refusal",
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"I cannot perform that task"}}\n',
                "final completed agent message was not exact PREFLIGHT_OK",
            ),
            (
                "near token",
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"PREFLIGHT_OK!"}}\n',
                "final completed agent message was not exact PREFLIGHT_OK",
            ),
        )
        for name, stdout, message in cases:
            with self.subTest(name=name), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                commands.smoke_stdout = stdout
                with self.assertRaisesRegex(preflight.PreflightError, message):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight").exists())

    def test_smoke_token_in_wrong_or_nonfinal_event_never_proves_success(self) -> None:
        cases = (
            (
                "command token then refusal",
                '{"type":"item.completed","item":{"type":"command_execution",'
                '"text":"PREFLIGHT_OK"}}\n'
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"I refuse"}}\n',
            ),
            (
                "earlier assistant token then refusal",
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"PREFLIGHT_OK"}}\n'
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"I refuse"}}\n',
            ),
            (
                "unknown event token",
                '{"type":"future.event","payload":{"text":"PREFLIGHT_OK"}}\n',
            ),
            (
                "missing item type",
                '{"type":"item.completed","item":{"text":"PREFLIGHT_OK"}}\n',
            ),
            (
                "non-string assistant text",
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":{"nested":"PREFLIGHT_OK"}}}\n',
            ),
        )
        for name, stdout in cases:
            with self.subTest(name=name), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                commands.smoke_stdout = stdout
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    "final completed agent message was not exact PREFLIGHT_OK",
                ):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight").exists())

    def test_smoke_uses_last_completed_agent_message_and_ignores_later_unknown_events(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            commands.smoke_stdout = (
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":"not yet"}}\n'
                '{"type":"item.completed","item":{"type":"agent_message",'
                '"text":" PREFLIGHT_OK "}}\n'
                '{"type":"future.event","payload":{"text":"ignored"}}\n'
            )
            manifest = self._run(experiment, commands)
            self.assertEqual(manifest.model, "gpt-5.6-sol")
            self.assertTrue(
                (experiment / "results/preflight/manifest.json").is_file()
            )

    def test_smoke_detects_ignored_worktree_write_even_when_git_reports_clean(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            commands.on_smoke = lambda repository: (
                repository / "ignored.tmp"
            ).write_text("changed\n", encoding="utf-8")
            with self.assertRaisesRegex(
                preflight.PreflightError,
                r"model smoke modified temporary repository:.*ignored\.tmp",
            ):
                self._run(experiment, commands)
            self.assertFalse((experiment / "results/preflight").exists())

    def test_smoke_detects_git_metadata_mutation(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            commands.on_smoke = lambda repository: (
                repository / ".git/config"
            ).write_text("[core]\nchanged = true\n", encoding="utf-8")
            with self.assertRaisesRegex(
                preflight.PreflightError,
                r"model smoke modified temporary repository:.*\.git/config",
            ):
                self._run(experiment, commands)
            self.assertFalse((experiment / "results/preflight").exists())

    def test_manifest_replace_interruption_preserves_prior_manifest_and_cleans_temp(self) -> None:
        with _synthetic_experiment() as experiment:
            destination = experiment / "results/preflight/manifest.json"
            destination.parent.mkdir(parents=True)
            prior_bytes = b'{"prior":"complete"}\n'
            destination.write_bytes(prior_bytes)
            commands = FakeCommands()
            with mock.patch.object(
                preflight.os,
                "replace",
                side_effect=OSError("simulated replace interruption"),
            ):
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    "could not persist preflight manifest: simulated replace interruption",
                ):
                    self._run(experiment, commands)
            self.assertEqual(destination.read_bytes(), prior_bytes)
            self.assertEqual(
                {path.name for path in destination.parent.iterdir()},
                {"manifest.json"},
            )

    def test_manifest_replace_interruption_removes_new_empty_result_directories(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            with mock.patch.object(
                preflight.os,
                "replace",
                side_effect=OSError("simulated replace interruption"),
            ):
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    "could not persist preflight manifest: simulated replace interruption",
                ):
                    self._run(experiment, commands)
            self.assertFalse((experiment / "results").exists())

    def test_manifest_directory_open_interruption_removes_just_created_directories(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            original_open = os.open
            failed = False

            def failing_open(
                path,
                flags,
                mode=0o777,
                *,
                dir_fd=None,
            ):
                nonlocal failed
                if (
                    path == "preflight"
                    and flags & os.O_DIRECTORY
                    and not failed
                ):
                    failed = True
                    raise OSError("simulated directory open interruption")
                return original_open(
                    path,
                    flags,
                    mode,
                    dir_fd=dir_fd,
                )

            with mock.patch.object(
                preflight.os,
                "open",
                side_effect=failing_open,
            ):
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    "manifest directory component is not a real directory",
                ):
                    self._run(experiment, commands)
            self.assertFalse((experiment / "results").exists())

    def test_rejects_symlinked_results_directory_without_writing_target(self) -> None:
        with _synthetic_experiment() as experiment, tempfile.TemporaryDirectory() as outside_temp:
            outside = Path(outside_temp)
            (experiment / "results").symlink_to(outside, target_is_directory=True)
            commands = FakeCommands()
            with self.assertRaisesRegex(
                preflight.PreflightError,
                "manifest directory component is not a real directory",
            ):
                self._run(experiment, commands)
            self.assertEqual(list(outside.iterdir()), [])

    def test_rejects_symlinked_preflight_directory_without_writing_target(self) -> None:
        with _synthetic_experiment() as experiment, tempfile.TemporaryDirectory() as outside_temp:
            outside = Path(outside_temp)
            (experiment / "results").mkdir()
            (experiment / "results/preflight").symlink_to(
                outside,
                target_is_directory=True,
            )
            commands = FakeCommands()
            with self.assertRaisesRegex(
                preflight.PreflightError,
                "manifest directory component is not a real directory",
            ):
                self._run(experiment, commands)
            self.assertEqual(list(outside.iterdir()), [])

    def test_rejects_symlinked_manifest_without_overwriting_target(self) -> None:
        with _synthetic_experiment() as experiment, tempfile.TemporaryDirectory() as outside_temp:
            outside_target = Path(outside_temp) / "outside.json"
            outside_target.write_bytes(b"outside sentinel\n")
            destination = experiment / "results/preflight/manifest.json"
            destination.parent.mkdir(parents=True)
            destination.symlink_to(outside_target)
            commands = FakeCommands()
            with self.assertRaisesRegex(
                preflight.PreflightError,
                "manifest destination is a symlink",
            ):
                self._run(experiment, commands)
            self.assertEqual(outside_target.read_bytes(), b"outside sentinel\n")

    def test_rejects_noncanonical_config_instead_of_hashing_a_different_file(self) -> None:
        with _synthetic_experiment() as experiment:
            alternate = experiment / "alternate.toml"
            alternate.write_text(
                (experiment / "experiment.toml")
                .read_text(encoding="utf-8")
                .replace("gpt-5.6-sol", "different-model"),
                encoding="utf-8",
            )
            commands = FakeCommands()
            with self.assertRaisesRegex(
                preflight.PreflightError,
                r"config path must name the canonical experiment\.toml",
            ):
                preflight.run_preflight(
                    alternate,
                    executable_resolver=commands.resolve,
                    command_runner=commands.run,
                    python_version=(3, 13, 7),
                    os_details=preflight.OsDetails(
                        "TestOS",
                        "release",
                        "version",
                        "machine",
                    ),
                )
            self.assertFalse(self._smoke_was_called(commands))
            self.assertFalse((experiment / "results/preflight").exists())

    def test_symlink_swap_before_hashing_fails_before_smoke(self) -> None:
        with _synthetic_experiment() as experiment, tempfile.TemporaryDirectory() as outside_temp:
            outside = Path(outside_temp) / "outside.md"
            outside.write_text("outside\n", encoding="utf-8")
            input_path = experiment / "shared/public/AGENTS.md"
            swapped = False

            def swap_before_metadata(
                command: tuple[str, ...],
                cwd: Path,
            ) -> None:
                nonlocal swapped
                if command == ("/test-tools/codex", "--version") and not swapped:
                    input_path.unlink()
                    input_path.symlink_to(outside)
                    swapped = True

            commands = FakeCommands()
            commands.before_command = swap_before_metadata
            with self.assertRaises(ValueError) as caught:
                self._run(experiment, commands)
            self.assertIsInstance(caught.exception, preflight.PreflightError)
            self.assertIn("input snapshot failed", str(caught.exception))
            self.assertFalse(self._smoke_was_called(commands))
            self.assertFalse((experiment / "results/preflight").exists())

    def test_input_drift_during_smoke_refuses_manifest(self) -> None:
        def change(path: Path) -> None:
            path.write_text("changed\n", encoding="utf-8")

        def remove(path: Path) -> None:
            path.unlink()

        for name, mutation in (("change", change), ("remove", remove)):
            with self.subTest(name=name), _synthetic_experiment() as experiment:
                commands = FakeCommands()
                source = experiment / "fsharp/source.txt"
                commands.on_smoke = lambda _, source=source, mutation=mutation: mutation(source)
                with self.assertRaisesRegex(
                    preflight.PreflightError,
                    "experiment inputs changed during preflight",
                ):
                    self._run(experiment, commands)
                self.assertFalse((experiment / "results/preflight").exists())

    def test_untracked_baseline_artifact_during_smoke_does_not_change_committed_inventory(
        self,
    ) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            commands.on_smoke = lambda _: (
                experiment / "fsharp/untracked.txt"
            ).write_text("untracked\n", encoding="utf-8")

            manifest = self._run(experiment, commands)

            self.assertEqual(set(manifest.baseline_sha256), {"fsharp", "csharp"})
            self.assertTrue(
                (experiment / "results/preflight/manifest.json").is_file()
            )

    def test_config_drift_during_smoke_refuses_manifest(self) -> None:
        with _synthetic_experiment() as experiment:
            commands = FakeCommands()
            config_path = experiment / "experiment.toml"
            commands.on_smoke = lambda _: config_path.write_text(
                config_path.read_text(encoding="utf-8").replace(
                    "gpt-5.6-sol",
                    "changed-model",
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(
                preflight.PreflightError,
                "experiment inputs changed during preflight",
            ):
                self._run(experiment, commands)
            self.assertFalse((experiment / "results/preflight").exists())

    def test_tree_digest_rejects_concurrent_directory_addition_on_rescan(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "source.txt").write_text("source\n", encoding="utf-8")
            original_scandir = os.scandir
            scan_count = 0

            def changing_scandir(path):
                nonlocal scan_count
                scan_count += 1
                if scan_count == 2:
                    (root / "late.txt").write_text("late\n", encoding="utf-8")
                return original_scandir(path)

            with mock.patch.object(
                preflight.os,
                "scandir",
                side_effect=changing_scandir,
            ):
                with self.assertRaisesRegex(
                    ValueError,
                    "directory changed while hashing",
                ):
                    preflight.digest_tree(root)

    def test_command_timeouts_and_os_errors_are_precise_and_never_write(self) -> None:
        labels = {
            "dotnet-version": "dotnet --version",
            "codex-help": "codex exec --help",
            "codex-version": "codex --version",
            "dotnet-info": "dotnet --info",
            "git-init": "temporary Git initialization",
            "codex-smoke": "model smoke",
            "git-status": "temporary Git status",
        }
        for kind, label in labels.items():
            for failure_name, failure, expected in (
                (
                    "timeout",
                    subprocess.TimeoutExpired(("fake",), 7),
                    f"{label} timed out after 7 seconds",
                ),
                (
                    "os error",
                    OSError("blocked"),
                    f"{label} could not run: blocked",
                ),
            ):
                with (
                    self.subTest(kind=kind, failure=failure_name),
                    _synthetic_experiment() as experiment,
                ):
                    commands = FakeCommands()
                    commands.failure_for = kind
                    commands.failure = failure
                    with self.assertRaises(preflight.PreflightError) as caught:
                        self._run(experiment, commands)
                    self.assertIn(expected, str(caught.exception))
                    self.assertFalse(
                        (experiment / "results/preflight/manifest.json").exists()
                    )

    def test_missing_baseline_cli_subprocess_creates_no_bytecode(self) -> None:
        with _synthetic_experiment(baselines=False) as experiment:
            scripts = experiment / "scripts"
            evaluator = experiment / "evaluator"
            fake_bin = experiment / "fake-bin"
            scripts.mkdir()
            evaluator.mkdir()
            fake_bin.mkdir()
            shutil.copyfile(preflight.__file__, scripts / "preflight.py")
            shutil.copyfile(
                EXPERIMENT_ROOT / "scripts/committed_inventory.py",
                scripts / "committed_inventory.py",
            )
            shutil.copyfile(
                EXPERIMENT_ROOT / "evaluator/common.py",
                evaluator / "common.py",
            )
            _write_fake_tool(
                fake_bin / "dotnet",
                'if [ "$1" = "--version" ]; then printf "10.0.100\\n"; fi\n',
            )
            _write_fake_tool(
                fake_bin / "codex",
                'if [ "$1" = "exec" ] && [ "$2" = "--help" ]; then '
                'printf "%s\\n" "usage: codex exec --json --model --sandbox"; fi\n',
            )
            for name in ("git", "node", "npm"):
                _write_fake_tool(fake_bin / name, "exit 0\n")
            environment = os.environ.copy()
            environment.pop("PYTHONDONTWRITEBYTECODE", None)
            environment.pop("PYTHONPYCACHEPREFIX", None)
            environment["PATH"] = str(fake_bin)
            completed = subprocess.run(
                (
                    sys.executable,
                    str(scripts / "preflight.py"),
                    "--config",
                    str(experiment / "experiment.toml"),
                ),
                cwd=experiment,
                env=environment,
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=30,
            )
            self.assertEqual(completed.returncode, 1)
            self.assertIn(str(experiment / "fsharp"), completed.stderr)
            self.assertIn(str(experiment / "csharp"), completed.stderr)
            self.assertEqual(list(experiment.rglob("__pycache__")), [])
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
            for command, _, _ in commands.calls
        )


def _failing_smoke_runner(
    commands: FakeCommands,
    returncode: int,
    stdout: str,
    stderr: str,
):
    default_run = commands.run

    def run(
        command: tuple[str, ...],
        cwd: Path,
        timeout_seconds: int | None = None,
    ) -> subprocess.CompletedProcess[str]:
        if command[:2] == ("/test-tools/codex", "exec") and command[-1] != "--help":
            commands.calls.append((command, cwd, timeout_seconds))
            return subprocess.CompletedProcess(command, returncode, stdout, stderr)
        return default_run(command, cwd, timeout_seconds)

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


def _write_fake_tool(path: Path, body: str) -> None:
    path.write_text("#!/bin/sh\n" + body, encoding="utf-8")
    path.chmod(0o755)


def _synthetic_experiment(*, baselines: bool = True):
    directory = tempfile.TemporaryDirectory()
    temporary_root = Path(directory.name).resolve()
    repository = temporary_root / "repository"
    root = repository / "experiments/implementation-language-spike"
    root.mkdir(parents=True)
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
    _git(repository, "init", "--quiet", "--initial-branch=main")
    _git(repository, "config", "--local", "user.name", "Preflight Test")
    _git(
        repository,
        "config",
        "--local",
        "user.email",
        "preflight-test@example.invalid",
    )
    _git(repository, "add", "--all")
    _git(repository, "commit", "--quiet", "--message", "test: create experiment")
    return _TemporaryDirectory(directory, root)


def _commit_paths(
    experiment: Path,
    message: str,
    *paths: Path,
    force: bool = False,
) -> None:
    repository = experiment.parents[1]
    arguments = ["add"]
    if force:
        arguments.append("--force")
    arguments.extend(str(path) for path in paths)
    _git(repository, *arguments)
    _git(repository, "commit", "--quiet", "--message", message)


def _digest_exported_inventory(workspace: Path) -> str:
    digest = hashlib.sha256()
    digest.update(b"nayamnyam-spike-committed-inventory-v1\0")
    files = sorted(
        (
            path.relative_to(workspace).as_posix(),
            path,
        )
        for path in workspace.rglob("*")
        if path.is_file()
        and ".git" not in path.relative_to(workspace).parts
    )
    for relative_path, path in files:
        mode = b"100755" if path.stat().st_mode & 0o111 else b"100644"
        relative_bytes = relative_path.encode("utf-8")
        contents = path.read_bytes()
        for field in (mode, relative_bytes, contents):
            digest.update(len(field).to_bytes(8, "big"))
            digest.update(field)
    return digest.hexdigest()


def _git(repository: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ("git", *arguments),
        cwd=repository,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return completed.stdout.strip()


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
