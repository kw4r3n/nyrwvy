import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest


EXPERIMENT_ROOT = Path(__file__).parents[1]
REPOSITORY_ROOT = EXPERIMENT_ROOT.parents[1]
SCRIPTS_DIRECTORY = EXPERIMENT_ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS_DIRECTORY))

import render_setup_prompt as renderer


SESSIONS = (
    "01-shared-infrastructure",
    "02-fsharp-baseline",
    "03-csharp-baseline",
    "04-integration-automation",
)
EXPECTED_MANIFEST_KEYS = {
    "codex_cli_version",
    "model",
    "prompt_path",
    "prompt_sha256",
    "schema_version",
    "session",
    "start_commit",
}
MAIN_PLAN = "docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike.md"
AUTOMATION_ADDENDUM = "docs/superpowers/plans/2026-07-29-fsharp-csharp-implementation-language-spike-automation-addendum.md"


class SetupPromptRendererTests(unittest.TestCase):
    def test_renders_normalized_prompt_and_deterministic_manifest(self) -> None:
        with self._temporary_repository(line_endings="\r\n") as repository_root, tempfile.TemporaryDirectory() as directory:
            output_directory = Path(directory) / "session"
            command_runner = _FakeCommandRunner()
            original_prompt = self._prompt_path(repository_root, SESSIONS[0]).read_bytes()

            manifest = renderer.render_setup_prompt(
                repository_root,
                SESSIONS[0],
                output_directory,
                command_runner=command_runner,
            )

            prompt_bytes = (output_directory / "prompt.md").read_bytes()
            manifest_bytes = (output_directory / "manifest.json").read_bytes()
            manifest_values = json.loads(manifest_bytes)
            self.assertEqual({path.name for path in output_directory.iterdir()}, {"manifest.json", "prompt.md"})
            self.assertEqual(prompt_bytes, original_prompt.replace(b"\r\n", b"\n").rstrip(b"\n") + b"\n")
            self.assertNotIn(b"\r", prompt_bytes)
            self.assertTrue(prompt_bytes.endswith(b"\n"))
            self.assertFalse(prompt_bytes.endswith(b"\n\n"))
            self.assertIn(MAIN_PLAN.encode(), prompt_bytes)
            self.assertIn(AUTOMATION_ADDENDUM.encode(), prompt_bytes)
            self.assertEqual(manifest.prompt_sha256, hashlib.sha256(prompt_bytes).hexdigest())
            self.assertEqual(manifest_values["prompt_sha256"], hashlib.sha256(prompt_bytes).hexdigest())
            self.assertEqual(manifest_values, json.loads(json.dumps(manifest_values, indent=2, sort_keys=True) + "\n"))
            self.assertEqual(manifest_bytes, (json.dumps(manifest_values, indent=2, sort_keys=True) + "\n").encode())
            self.assertEqual(renderer.load_manifest(output_directory / "manifest.json"), manifest)
            self.assertEqual(self._prompt_path(repository_root, SESSIONS[0]).read_bytes(), original_prompt)
            self.assertEqual(command_runner.commands, [("git", "rev-parse", "HEAD"), ("codex", "--version")])
            self.assertFalse(any("exec" in command for invocation in command_runner.commands for command in invocation))

    def test_rejects_unknown_session_before_creating_output(self) -> None:
        with self._temporary_repository() as repository_root, tempfile.TemporaryDirectory() as directory:
            output_directory = Path(directory) / "session"
            with self.assertRaises(ValueError):
                renderer.render_setup_prompt(
                    repository_root,
                    "not-a-session",
                    output_directory,
                    command_runner=_FakeCommandRunner(),
                )
            self.assertFalse(output_directory.exists())

    def test_rejects_prompt_without_required_plan_references(self) -> None:
        with self._temporary_repository() as repository_root, tempfile.TemporaryDirectory() as directory:
            prompt_path = self._prompt_path(repository_root, SESSIONS[0])
            prompt_path.write_text("# Missing required references\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                renderer.render_setup_prompt(
                    repository_root,
                    SESSIONS[0],
                    Path(directory) / "session",
                    command_runner=_FakeCommandRunner(),
                )

    def test_all_canonical_sessions_have_distinct_prompt_hashes(self) -> None:
        with self._temporary_repository() as repository_root, tempfile.TemporaryDirectory() as directory:
            manifests = [
                renderer.render_setup_prompt(
                    repository_root,
                    session,
                    Path(directory) / session,
                    command_runner=_FakeCommandRunner(),
                )
                for session in SESSIONS
            ]
            self.assertEqual({manifest.session for manifest in manifests}, set(SESSIONS))
            self.assertEqual(len({manifest.prompt_sha256 for manifest in manifests}), len(SESSIONS))

    def test_manifest_loader_rejects_unknown_and_missing_fields(self) -> None:
        with self._temporary_repository() as repository_root, tempfile.TemporaryDirectory() as directory:
            output_directory = Path(directory) / "session"
            renderer.render_setup_prompt(
                repository_root,
                SESSIONS[0],
                output_directory,
                command_runner=_FakeCommandRunner(),
            )
            values = json.loads((output_directory / "manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(set(values), EXPECTED_MANIFEST_KEYS)

            invalid_values = (
                {**values, "unexpected": "value"},
                {key: value for key, value in values.items() if key != "model"},
            )
            for invalid_value in invalid_values:
                with self.subTest(invalid_value=invalid_value):
                    path = Path(directory) / f"invalid-{len(invalid_value)}.json"
                    path.write_text(json.dumps(invalid_value), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        renderer.load_manifest(path)

    def test_manifest_loader_rejects_unsupported_schema_and_wrong_types(self) -> None:
        with self._temporary_repository() as repository_root, tempfile.TemporaryDirectory() as directory:
            output_directory = Path(directory) / "session"
            renderer.render_setup_prompt(
                repository_root,
                SESSIONS[0],
                output_directory,
                command_runner=_FakeCommandRunner(),
            )
            values = json.loads((output_directory / "manifest.json").read_text(encoding="utf-8"))
            invalid_values = [{**values, "schema_version": 2}, {**values, "schema_version": True}]
            invalid_values.extend(
                {**values, key: 1}
                for key in EXPECTED_MANIFEST_KEYS - {"schema_version"}
            )
            for index, invalid_value in enumerate(invalid_values):
                with self.subTest(invalid_value=invalid_value):
                    path = Path(directory) / f"invalid-type-{index}.json"
                    path.write_text(json.dumps(invalid_value), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        renderer.load_manifest(path)

    def _temporary_repository(self, line_endings: str = "\n") -> "_TemporaryRepository":
        return _TemporaryRepository(line_endings)

    @staticmethod
    def _prompt_path(repository_root: Path, session: str) -> Path:
        return repository_root / "docs" / "superpowers" / "prompts" / "implementation-language-spike" / f"{session}.md"


class _FakeCommandRunner:
    def __init__(self) -> None:
        self.commands: list[tuple[str, ...]] = []

    def __call__(self, command: tuple[str, ...], _: Path) -> str:
        self.commands.append(command)
        responses = {
            ("git", "rev-parse", "HEAD"): "0123456789abcdef0123456789abcdef01234567\n",
            ("codex", "--version"): "codex-cli 0.0.0-test\n",
        }
        return responses[command]


class _TemporaryRepository:
    def __init__(self, line_endings: str) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name)
        self._line_endings = line_endings

    def __enter__(self) -> Path:
        source_prompts = REPOSITORY_ROOT / "docs" / "superpowers" / "prompts" / "implementation-language-spike"
        target_prompts = self.root / "docs" / "superpowers" / "prompts" / "implementation-language-spike"
        target_prompts.parent.mkdir(parents=True)
        shutil.copytree(source_prompts, target_prompts)
        for source_prompt in target_prompts.glob("[0-9][0-9]-*.md"):
            source_prompt.write_bytes(source_prompt.read_bytes().replace(b"\n", self._line_endings.encode()))

        config_path = self.root / "experiments" / "implementation-language-spike" / "experiment.toml"
        config_path.parent.mkdir(parents=True)
        shutil.copyfile(EXPERIMENT_ROOT / "experiment.toml", config_path)
        return self.root

    def __exit__(self, exception_type: object, exception: object, traceback: object) -> None:
        self._directory.cleanup()


if __name__ == "__main__":
    unittest.main()
