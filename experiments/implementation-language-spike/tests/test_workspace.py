import io
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock


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
                    ".editorconfig",
                    ".gitignore",
                    "README.md",
                    "diagnostic.log",
                    "history.jsonl",
                    "src/core.txt",
                    "shared/public/.editorconfig",
                    "shared/public/AGENTS.md",
                    "shared/public/acceptance.md",
                    "shared/public/audit.jsonl",
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
            self.assertEqual((destination / ".editorconfig").read_text(encoding="utf-8"), "root = true\n")
            self.assertEqual((destination / "history.jsonl").read_text(encoding="utf-8"), '{"tracked":true}\n')
            self.assertEqual((destination / "diagnostic.log").read_text(encoding="utf-8"), "tracked log\n")
            self.assertEqual(
                (destination / "shared/public/.editorconfig").read_text(encoding="utf-8"),
                "indent_size = 4\n",
            )
            self.assertEqual(
                (destination / "shared/public/audit.jsonl").read_text(encoding="utf-8"),
                '{"public":true}\n',
            )
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
            self.assertEqual(
                self._git(
                    destination,
                    "show",
                    "-s",
                    "--format=%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI",
                    "HEAD",
                ).split("\0"),
                [
                    "Nayamnyam Spike Runner",
                    "nayamnyam-spike@example.invalid",
                    "2000-01-01T00:00:00Z",
                    "Nayamnyam Spike Runner",
                    "nayamnyam-spike@example.invalid",
                    "2000-01-01T00:00:00Z",
                ],
            )
            self.assertEqual(self._git(destination, "status", "--porcelain"), "")

    def test_prepares_identical_commits_under_hostile_git_environment(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            first_destination = temporary_root / "first"
            second_destination = temporary_root / "second"
            hostile_home = temporary_root / "hostile-home"
            hostile_template = temporary_root / "hostile-template"
            hostile_home.mkdir()
            hostile_template.mkdir()
            (hostile_home / ".gitconfig").write_text(
                "[user]\n"
                "    name = Hostile Global User\n"
                "    email = hostile@example.com\n"
                "[commit]\n"
                "    gpgSign = true\n"
                "[init]\n"
                f"    templateDir = {hostile_template}\n",
                encoding="utf-8",
            )
            (hostile_template / "unexpected-template-file").write_text("hostile", encoding="utf-8")
            hostile_environment = {
                "HOME": str(hostile_home),
                "GIT_DIR": str(temporary_root / "wrong-git-dir"),
                "GIT_WORK_TREE": str(temporary_root / "wrong-work-tree"),
                "GIT_AUTHOR_NAME": "Hostile Author",
                "GIT_AUTHOR_EMAIL": "hostile-author@example.com",
                "GIT_AUTHOR_DATE": "2037-12-31T23:59:59+00:00",
                "GIT_COMMITTER_NAME": "Hostile Committer",
                "GIT_COMMITTER_EMAIL": "hostile-committer@example.com",
                "GIT_COMMITTER_DATE": "2037-12-31T23:59:59+00:00",
                "GIT_CONFIG_COUNT": "1",
                "GIT_CONFIG_KEY_0": "core.excludesFile",
                "GIT_CONFIG_VALUE_0": str(temporary_root / "hostile-excludes"),
            }

            workspace.prepare_workspace(source, "fsharp", first_destination)
            with mock.patch.dict(os.environ, hostile_environment):
                workspace.prepare_workspace(source, "fsharp", second_destination)

            self.assertEqual(
                self._git(first_destination, "rev-parse", "HEAD"),
                self._git(second_destination, "rev-parse", "HEAD"),
            )
            self.assertEqual(
                self._git(first_destination, "rev-parse", "HEAD^{tree}"),
                self._git(second_destination, "rev-parse", "HEAD^{tree}"),
            )
            self.assertFalse((first_destination / ".git" / "unexpected-template-file").exists())
            self.assertEqual(
                self._git(first_destination, "show", "-s", "--format=%an <%ae> / %cn <%ce>", "HEAD"),
                "Nayamnyam Spike Runner <nayamnyam-spike@example.invalid> / "
                "Nayamnyam Spike Runner <nayamnyam-spike@example.invalid>",
            )

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
            self._commit_source(source, "test: remove fsharp baseline")
            destination = temporary_root / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                rf"^selected baseline is missing: {str(missing_baseline.resolve())}$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())

    def test_rejects_dirty_tracked_source_inputs_without_creating_destination(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            tracked_input = source / "fsharp" / "README.md"
            tracked_input.write_text("dirty worktree contents\n", encoding="utf-8")
            destination = temporary_root / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                r"^tracked source inputs are dirty:",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())

    def test_uses_committed_snapshot_and_ignores_untracked_source_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            (source / "fsharp" / "untracked-source.txt").write_text("untracked", encoding="utf-8")
            (source / "shared" / "public" / "untracked-public.txt").write_text("untracked", encoding="utf-8")
            destination = temporary_root / "workspace"

            workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse((destination / "untracked-source.txt").exists())
            self.assertFalse((destination / "shared/public/untracked-public.txt").exists())
            self.assertEqual((destination / "README.md").read_bytes(), b"fsharp baseline\r\n")

    def test_uses_one_frozen_commit_when_source_head_advances_between_archives(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"
            original_git_archive = workspace._git_archive
            archive_count = 0

            def archive_then_advance_head(
                repository: Path,
                tree: str,
                *,
                prefix: str | None = None,
            ) -> bytes:
                nonlocal archive_count
                archive_bytes = original_git_archive(repository, tree, prefix=prefix)
                archive_count += 1
                if archive_count == 1:
                    (source / "shared/public/acceptance.md").write_text(
                        "Changed after preparation started\n",
                        encoding="utf-8",
                    )
                    self._commit_source(source, "test: advance source during archive")
                return archive_bytes

            with mock.patch.object(workspace, "_git_archive", side_effect=archive_then_advance_head):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertEqual(
                (destination / "shared/public/acceptance.md").read_text(encoding="utf-8"),
                "Visible acceptance\n",
            )

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
            self._git(source.parents[1], "add", "--force", str(unsafe_link))
            self._commit_source(source, "test: add committed symlink")
            destination = temporary_root / "workspace"

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                r"^archive contains unsafe member type: 'unsafe-link'$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())

    def test_rejects_existing_destination_symlink_without_changing_target(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            target = temporary_root / "symlink-target"
            target.mkdir()
            sentinel = target / "keep.txt"
            sentinel.write_text("keep", encoding="utf-8")
            destination = temporary_root / "workspace"
            destination.symlink_to(target, target_is_directory=True)

            with self.assertRaisesRegex(
                workspace.WorkspacePreparationError,
                rf"^destination must not be a symlink: {str(destination.absolute())}$",
            ):
                workspace.prepare_workspace(source, "fsharp", destination)

            self.assertTrue(destination.is_symlink())
            self.assertEqual(sentinel.read_text(encoding="utf-8"), "keep")

    def test_rejects_destination_changed_while_private_staging_is_prepared(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"
            destination.mkdir()
            original_initialize = workspace._initialize_repository

            def initialize_then_change_destination(staging: Path) -> None:
                original_initialize(staging)
                (destination / "intruder.txt").write_text("external change", encoding="utf-8")

            with mock.patch.object(
                workspace,
                "_initialize_repository",
                side_effect=initialize_then_change_destination,
            ):
                with self.assertRaisesRegex(
                    workspace.WorkspacePreparationError,
                    rf"^destination changed during preparation: {str(destination.absolute())}$",
                ):
                    workspace.prepare_workspace(source, "fsharp", destination)

            self.assertEqual((destination / "intruder.txt").read_text(encoding="utf-8"), "external change")
            self._assert_no_staging_directories(destination)

    def test_archive_failure_removes_private_staging_and_absent_reservation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"

            with mock.patch.object(
                workspace,
                "_git_archive",
                side_effect=OSError("archive fault"),
                create=True,
            ):
                with self.assertRaisesRegex(OSError, "^archive fault$"):
                    workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())
            self._assert_no_staging_directories(destination)

    def test_copy_failure_leaves_caller_supplied_empty_destination_empty(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"
            destination.mkdir()

            with mock.patch.object(workspace.shutil, "copyfileobj", side_effect=OSError("copy fault")):
                with self.assertRaisesRegex(OSError, "^copy fault$"):
                    workspace.prepare_workspace(source, "fsharp", destination)

            self.assertTrue(destination.is_dir())
            self.assertEqual(list(destination.iterdir()), [])
            self._assert_no_staging_directories(destination)

    def test_git_failure_removes_private_staging_and_absent_reservation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            temporary_root = Path(directory)
            source = self._create_fake_source(temporary_root)
            destination = temporary_root / "workspace"

            with mock.patch.object(
                workspace,
                "_initialize_repository",
                side_effect=workspace.WorkspacePreparationError("git fault"),
            ):
                with self.assertRaisesRegex(workspace.WorkspacePreparationError, "^git fault$"):
                    workspace.prepare_workspace(source, "fsharp", destination)

            self.assertFalse(destination.exists())
            self._assert_no_staging_directories(destination)

    def test_rejects_unsafe_archive_paths_and_special_members(self) -> None:
        cases = (
            ("path traversal", self._tar_bytes("../escape.txt", contents=b"escape")),
            ("absolute path", self._tar_bytes("/escape.txt", contents=b"escape")),
            ("special member", self._tar_bytes("unsafe-pipe", member_type=tarfile.FIFOTYPE)),
        )
        for label, archive_bytes in cases:
            with self.subTest(label=label), tempfile.TemporaryDirectory() as directory:
                temporary_root = Path(directory)
                source = self._create_fake_source(temporary_root)
                destination = temporary_root / "workspace"

                with mock.patch.object(
                    workspace,
                    "_git_archive",
                    return_value=archive_bytes,
                    create=True,
                ):
                    with self.assertRaisesRegex(
                        workspace.WorkspacePreparationError,
                        "^archive contains unsafe ",
                    ):
                        workspace.prepare_workspace(source, "fsharp", destination)

                self.assertFalse(destination.exists())
                self._assert_no_staging_directories(destination)

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
            self._commit_source(source, "test: remove csharp baseline")
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
        repository = temporary_root / "repository"
        source = repository / "experiments" / "implementation-language-spike"
        tracked_files = {
            "fsharp/.editorconfig": b"root = true\n",
            "fsharp/.gitignore": b"bin/\nobj/\n__pycache__/\n*.pyc\n",
            "fsharp/README.md": b"fsharp baseline\r\n",
            "fsharp/diagnostic.log": b"tracked log\n",
            "fsharp/history.jsonl": b'{"tracked":true}\n',
            "fsharp/src/core.txt": b"selected",
            "fsharp/evaluator/hidden.py": b"hidden",
            "fsharp/results/old.txt": b"old",
            "csharp/README.md": b"csharp baseline\n",
            "csharp/csharp-only.txt": b"selected csharp",
            "shared/public/.editorconfig": b"indent_size = 4\n",
            "shared/public/AGENTS.md": b"Agent rules\r\n\r\n",
            "shared/public/maintenance-task.md": b"Do work\rNext",
            "shared/public/acceptance.md": b"Visible acceptance\n",
            "shared/public/audit.jsonl": b'{"public":true}\n',
            "shared/expected/answer.json": b"hidden expected",
            "evaluator/hidden.py": b"hidden evaluator",
            "results/previous/events.jsonl": b'{"event":"old"}\n',
        }
        for relative_path, contents in tracked_files.items():
            path = source / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(contents)
        WorkspacePreparationTests._git(repository, "init", "--initial-branch=main")
        WorkspacePreparationTests._git(repository, "config", "--local", "user.name", "Test Source")
        WorkspacePreparationTests._git(
            repository,
            "config",
            "--local",
            "user.email",
            "test-source@example.invalid",
        )
        WorkspacePreparationTests._git(repository, "add", "--all")
        WorkspacePreparationTests._git(repository, "commit", "--message", "test: create fake source")

        untracked_files = {
            "fsharp/bin/build.dll": b"build",
            "fsharp/obj/cache": b"cache",
            "fsharp/__pycache__/module.pyc": b"cache",
            "fsharp/loose.pyc": b"cache",
            "fsharp/.secret": b"secret",
            "fsharp/raw.jsonl": b'{"event":"old"}\n',
            "fsharp/stderr.log": b"old log",
            "shared/public/__pycache__/cached.pyc": b"cache",
        }
        for relative_path, contents in untracked_files.items():
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
    def _commit_source(source: Path, message: str) -> None:
        repository = source.parents[1]
        WorkspacePreparationTests._git(repository, "add", "--all")
        WorkspacePreparationTests._git(repository, "commit", "--message", message)

    @staticmethod
    def _assert_no_staging_directories(destination: Path) -> None:
        pattern = f".{destination.name}.prepare-*"
        staging_directories = list(destination.parent.glob(pattern))
        if staging_directories:
            raise AssertionError(f"private staging directories remain: {staging_directories}")

    @staticmethod
    def _tar_bytes(
        name: str,
        *,
        contents: bytes = b"",
        member_type: bytes = tarfile.REGTYPE,
    ) -> bytes:
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w") as archive:
            member = tarfile.TarInfo(name)
            member.type = member_type
            member.size = len(contents) if member_type == tarfile.REGTYPE else 0
            archive.addfile(member, io.BytesIO(contents) if member.size else None)
        return buffer.getvalue()

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

    def test_removes_whitespace_only_prompt_tails_before_fixed_separator(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace_root = Path(directory)
            public = workspace_root / "shared" / "public"
            public.mkdir(parents=True)
            (public / "AGENTS.md").write_bytes(b"First\r\n  \r\n\t\r\n")
            (public / "maintenance-task.md").write_bytes(b"Second\n \t\n\n")

            prompt = workspace.load_prompt(workspace_root)

            self.assertEqual(prompt, b"First\n\nSecond\n")

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
