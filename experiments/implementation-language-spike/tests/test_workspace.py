from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


EXPERIMENT_ROOT = Path(__file__).parents[1]
SCRIPTS_DIRECTORY = EXPERIMENT_ROOT / "scripts"
PREPARE_WORKSPACE_SCRIPT = SCRIPTS_DIRECTORY / "prepare_workspace.py"
sys.path.insert(0, str(SCRIPTS_DIRECTORY))

import prepare_workspace as workspace


class WorkspacePreparationTests(unittest.TestCase):
    def test_prepares_only_selected_baseline_and_public_bundle(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspaces" / "fsharp-1"

            prepared = workspace.prepare_workspace(source, "fsharp", destination)

            self.assertEqual(prepared, destination.resolve())
            self.assertEqual(
                self._workspace_files(destination),
                {
                    "README.md",
                    "src/core.txt",
                    "shared/public/AGENTS.md",
                    "shared/public/acceptance.md",
                    "shared/public/maintenance-task.md",
                },
            )
            self.assertEqual((destination / "README.md").read_bytes(), b"fsharp baseline\r\n")
            self.assertEqual((destination / "src/core.txt").read_text(encoding="utf-8"), "selected")
            self.assertFalse((destination / "csharp-only.txt").exists())
            self.assertFalse((destination / "shared/expected").exists())
            self.assertFalse((destination / "evaluator").exists())
            self.assertFalse((destination / "results").exists())
            self.assertFalse((destination / "bin").exists())
            self.assertFalse((destination / "obj").exists())
            self.assertFalse((destination / "__pycache__").exists())
            self.assertFalse((destination / ".secret").exists())
            self.assertFalse((destination / "raw.jsonl").exists())
            self.assertFalse((destination / "stderr.log").exists())
            self.assertFalse(any(path.is_symlink() for path in destination.rglob("*")))

    def test_initializes_one_clean_main_branch_baseline_commit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"

            workspace.prepare_workspace(source, "csharp", destination)

            self.assertTrue((destination / ".git").is_dir())
            self.assertEqual(self._git(destination, "branch", "--show-current"), "main")
            self.assertEqual(self._git(destination, "rev-list", "--all", "--count"), "1")
            self.assertEqual(
                self._git(destination, "log", "-1", "--format=%s"),
                "chore(spike): initialize trial baseline",
            )
            self.assertEqual(self._git(destination, "config", "--local", "--get", "user.name"), "Nayamnyam Spike Runner")
            self.assertEqual(
                self._git(destination, "config", "--local", "--get", "user.email"),
                "nayamnyam-spike@example.invalid",
            )
            self.assertEqual(self._git(destination, "status", "--porcelain"), "")

    def test_rejects_unsupported_language_before_creating_destination(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                r"^unsupported language: 'python'; expected one of: fsharp, csharp$",
            ):
                workspace.prepare_workspace(source, "python", destination)

            self.assertFalse(destination.exists())

    def test_reports_exact_missing_selected_baseline_path(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            missing_baseline = source / "fsharp"
            self._remove_empty_tree_contents(missing_baseline)
            missing_baseline.rmdir()
            destination = temporary_root / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                rf"^selected baseline is missing: {str(missing_baseline.resolve())}$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())

    def test_rejects_destination_inside_source_experiment_tree(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = self._create_fake_source(Path(directory))
            destination = source / "results" / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                rf"^destination must be outside source experiment tree: {str(destination.resolve())}$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())

    def test_rejects_nonempty_destination_without_changing_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"
            destination.mkdir()
            sentinel = destination / "keep.txt"
            sentinel.write_text("keep", encoding="utf-8")

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                rf"^destination must be new or empty: {str(destination.resolve())}$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertEqual(sentinel.read_text(encoding="utf-8"), "keep")
            self.assertEqual(list(destination.iterdir()), [sentinel])

    def test_rejects_source_symlink_instead_of_following_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            target = temporary_root / "outside-secret.txt"
            target.write_text("do not copy", encoding="utf-8")
            unsafe_link = source / "fsharp" / "unsafe-link"
            unsafe_link.symlink_to(target)
            destination = temporary_root / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                rf"^source tree contains forbidden symlink: {str(unsafe_link)}$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())

    def test_cli_prints_prepared_path_on_success(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"

            completed = subprocess.run(
                (
                    sys.executable,
                    str(PREPARE_WORKSPACE_SCRIPT),
                    "--source",
                    str(source),
                    "--language",
                    "fsharp",
                    "--destination",
                    str(destination),
                ),
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )

            self.assertEqual(completed.returncode, 0)
            self.assertEqual(completed.stdout, f"{destination.resolve()}\n")
            self.assertEqual(completed.stderr, "")
            self.assertEqual(self._git(destination, "status", "--porcelain"), "")

    def test_cli_reports_precise_nonzero_error(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            missing_baseline = source / "csharp"
            self._remove_empty_tree_contents(missing_baseline)
            missing_baseline.rmdir()
            destination = temporary_root / "workspace"

            completed = subprocess.run(
                (
                    sys.executable,
                    str(PREPARE_WORKSPACE_SCRIPT),
                    "--source",
                    str(source),
                    "--language",
                    "csharp",
                    "--destination",
                    str(destination),
                ),
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )

            self.assertEqual(completed.returncode, 2)
            self.assertEqual(completed.stdout, "")
            self.assertEqual(
                completed.stderr,
                f"error: selected baseline is missing: {missing_baseline.resolve()}\n",
            )
            self.assertFalse(destination.exists())

    @staticmethod
    def _create_fake_source(temporary_root: Path) -> Path:
        source = temporary_root / "source-experiment"
        files = {
            "fsharp/README.md": b"fsharp baseline\r\n",
            "fsharp/src/core.txt": b"selected",
            "fsharp/bin/build.dll": b"build",
            "fsharp/obj/cache": b"cache",
            "fsharp/__pycache__/module.pyc": b"cache",
            "fsharp/loose.pyc": b"cache",
            "fsharp/.secret": b"secret",
            "fsharp/raw.jsonl": b'{"event":"old"}\n',
            "fsharp/stderr.log": b"old log",
            "fsharp/evaluator/hidden.py": b"hidden",
            "fsharp/results/old.txt": b"old",
            "csharp/README.md": b"csharp baseline\n",
            "csharp/csharp-only.txt": b"selected csharp",
            "shared/public/AGENTS.md": b"Agent rules\r\n\r\n",
            "shared/public/maintenance-task.md": b"Do work\rNext",
            "shared/public/acceptance.md": b"Visible acceptance\n",
            "shared/public/.hidden": b"hidden",
            "shared/public/__pycache__/cached.pyc": b"cache",
            "shared/expected/answer.json": b"hidden expected",
            "evaluator/hidden.py": b"hidden evaluator",
            "results/previous/events.jsonl": b'{"event":"old"}\n',
            ".git/HEAD": b"ref: refs/heads/main\n",
        }
        for relative_path, contents in files.items():
            path = source / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(contents)
        return source

    @staticmethod
    def _workspace_files(destination: Path) -> set[str]:
        return {
            path.relative_to(destination).as_posix()
            for path in destination.rglob("*")
            if path.is_file() and ".git" not in path.relative_to(destination).parts
        }

    @staticmethod
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

    @staticmethod
    def _remove_empty_tree_contents(root: Path) -> None:
        for path in sorted(root.rglob("*"), key=lambda candidate: len(candidate.parts), reverse=True):
            if path.is_dir():
                path.rmdir()
            else:
                path.unlink()


class TrialInvocationHelperTests(unittest.TestCase):
    def test_loads_normalized_prompt_in_fixed_file_order(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace_root = Path(directory)
            public = workspace_root / "shared" / "public"
            public.mkdir(parents=True)
            (public / "AGENTS.md").write_bytes(b"First\r\n\r\n")
            (public / "maintenance-task.md").write_bytes(b"Second\rThird")

            prompt = workspace.load_prompt(workspace_root)

            self.assertEqual(prompt, b"First\n\nSecond\nThird\n")
            self.assertNotIn(b"\r", prompt)

    def test_builds_exact_codex_command_from_strict_config(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config_path = Path(directory) / "experiment.toml"
            self._write_config(config_path)
            prompt = b"First\n\nSecond\n"

            command = workspace.build_codex_command(config_path, prompt)

            self.assertEqual(
                command,
                (
                    "codex",
                    "exec",
                    "--json",
                    "--model",
                    "gpt-5.6-sol",
                    "--sandbox",
                    "workspace-write",
                    "First\n\nSecond\n",
                ),
            )

    def test_exposes_configured_timeout_and_preserves_config_strictness(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config_path = Path(directory) / "experiment.toml"
            self._write_config(config_path)
            self.assertEqual(workspace.configured_timeout(config_path), 3600)

            config_path.write_text(
                config_path.read_text(encoding="utf-8") + "unexpected = true\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, r"^unknown keys: \['unexpected'\]$"):
                workspace.configured_timeout(config_path)

    def test_derives_external_stdout_and_stderr_paths(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            workspace_root = temporary_root / "workspace"
            results_root = temporary_root / "results"
            workspace_root.mkdir()

            paths = workspace.derive_log_paths(workspace_root, results_root, "fsharp-1")

            self.assertEqual(paths.stdout, results_root.resolve() / "fsharp-1" / "events.jsonl")
            self.assertEqual(paths.stderr, results_root.resolve() / "fsharp-1" / "stderr.log")
            self.assertFalse(paths.stdout.parent.exists())
            self.assertFalse(paths.stdout.is_relative_to(workspace_root.resolve()))
            self.assertFalse(paths.stderr.is_relative_to(workspace_root.resolve()))

    def test_rejects_unsafe_run_ids(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            workspace_root = temporary_root / "workspace"
            results_root = temporary_root / "results"
            workspace_root.mkdir()

            for run_id in ("", ".", "..", "../escape", "nested/run", "white space", "é"):
                with self.subTest(run_id=run_id):
                    with self.assertRaisesRegex(ValueError, "^unsafe run ID:"):
                        workspace.derive_log_paths(workspace_root, results_root, run_id)

    def test_rejects_output_root_inside_workspace(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace_root = Path(directory) / "workspace"
            workspace_root.mkdir()
            results_root = workspace_root / "results"

            with self.assertRaisesRegex(
                ValueError,
                rf"^results directory must be outside workspace: {str(results_root.resolve())}$",
            ):
                workspace.derive_log_paths(workspace_root, results_root, "fsharp-1")

    @staticmethod
    def _write_config(path: Path) -> None:
        path.write_text(
            "schema_version = 1\n"
            'model = "gpt-5.6-sol"\n'
            'sandbox = "workspace-write"\n'
            "timeout_seconds = 3600\n"
            "calibration_runs = 1\n"
            "scored_runs = 3\n"
            'languages = ["fsharp", "csharp"]\n',
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
