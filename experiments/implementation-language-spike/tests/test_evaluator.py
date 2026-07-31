import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import textwrap
import unittest


EXPERIMENT_ROOT = Path(__file__).parents[1]
PUBLIC_CHECKS_SCRIPT = EXPERIMENT_ROOT / "scripts" / "run_public_checks.py"
EVALUATE_SCRIPT = EXPERIMENT_ROOT / "evaluator" / "evaluate.py"


class EvaluatorTests(unittest.TestCase):
    def test_public_checks_reject_unsupported_language(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            completed = self._run_script(
                PUBLIC_CHECKS_SCRIPT,
                "--language",
                "rust",
                "--workspace",
                directory,
            )

        self.assertEqual(completed.returncode, 2)
        self.assertEqual(completed.stdout, "")
        self.assertIn("unsupported language", completed.stderr)

    def test_public_checks_reject_missing_workspace(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "missing"
            completed = self._run_script(
                PUBLIC_CHECKS_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                missing,
            )

        self.assertEqual(completed.returncode, 2)
        self.assertEqual(completed.stdout, "")
        self.assertIn(f"workspace does not exist: {missing.resolve()}", completed.stderr)

    def test_public_checks_run_required_checks_in_source_and_isolated_layouts(self) -> None:
        for layout in ("source", "isolated"):
            with self.subTest(layout=layout), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                workspace, environment = self._create_workspace(root, layout=layout)

                completed = self._run_script(
                    PUBLIC_CHECKS_SCRIPT,
                    "--language",
                    "fsharp",
                    "--workspace",
                    workspace,
                    environment=environment,
                )

                self.assertEqual(completed.returncode, 0, completed.stderr)
                result = json.loads((workspace / "public-checks.json").read_text(encoding="utf-8"))
                self.assertTrue(result["passed"])
                self.assertEqual(result["mode"], "untouched_baseline")
                names = [check["name"] for check in result["checks"]]
                self.assertEqual(names, sorted(names))
                self.assertEqual(
                    names,
                    [
                        "baseline_tests",
                        "build",
                        "canonical_contract",
                        "canonical_fixture",
                        "forbidden_patterns",
                        "project_settings",
                        "protected_files",
                        "summary_contract",
                        "summary_fixture",
                    ],
                )
                for check in result["checks"]:
                    self.assertIsInstance(check["command"], list)
                    self.assertIsInstance(check["exit_code"], int)
                    self.assertIsInstance(check["duration_ms"], int)
                    self.assertGreaterEqual(check["duration_ms"], 0)
                    self.assertTrue((workspace / check["stdout_path"]).is_file())
                    self.assertTrue((workspace / check["stderr_path"]).is_file())

    def test_public_checks_use_schema_aware_post_maintenance_mode(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            environment["FAKE_SCHEMA_VERSION"] = "2"

            completed = self._run_script(
                PUBLIC_CHECKS_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 0, completed.stderr)
            result = json.loads((workspace / "public-checks.json").read_text(encoding="utf-8"))
            self.assertTrue(result["passed"])
            self.assertEqual(result["mode"], "post_maintenance")

    def test_public_checks_report_forbidden_protected_and_project_findings(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            (workspace / "src" / "Nayamnyam.Spike.Core" / "Forbidden.fs").write_text(
                "module Forbidden\nlet value = Unchecked.defaultof<string>\n",
                encoding="utf-8",
            )
            (workspace / "Directory.Build.props").write_text(
                '<Project><ItemGroup><PackageReference Include="Escape" '
                'Version="1.0.0" /></ItemGroup></Project>\n',
                encoding="utf-8",
            )
            protected = workspace / "shared" / "public" / "AGENTS.md"
            protected.write_text("weakened\n", encoding="utf-8")

            completed = self._run_script(
                PUBLIC_CHECKS_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 1, completed.stderr)
            result = json.loads((workspace / "public-checks.json").read_text(encoding="utf-8"))
            self.assertFalse(result["passed"])
            self.assertIn("forbidden_patterns", result["failures"])
            self.assertIn("project_settings", result["failures"])
            self.assertIn("protected_files", result["failures"])
            self.assertEqual(
                [finding["rule"] for finding in result["forbidden_findings"]],
                ["unchecked_default"],
            )
            self.assertEqual(result["modified_protected_files"], ["shared/public/AGENTS.md"])

    def test_public_checks_reject_agent_controlled_artifact_paths_before_execution(
        self,
    ) -> None:
        cases = (
            "result_symlink",
            "artifact_directory_symlink",
            "stream_symlink",
            "non_regular_result",
        )
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                workspace, environment = self._create_workspace(
                    root,
                    layout="isolated",
                )
                sentinel = root / "sentinel"
                sentinel.write_text("do not overwrite\n", encoding="utf-8")
                external_artifacts = root / "external-artifacts"
                if case == "result_symlink":
                    (workspace / "public-checks.json").symlink_to(sentinel)
                elif case == "artifact_directory_symlink":
                    external_artifacts.mkdir()
                    (workspace / ".nayamnyam-evaluation").symlink_to(
                        external_artifacts,
                        target_is_directory=True,
                    )
                elif case == "stream_symlink":
                    artifact_root = workspace / ".nayamnyam-evaluation" / "public"
                    artifact_root.mkdir(parents=True)
                    (artifact_root / "build.stdout").symlink_to(sentinel)
                else:
                    (workspace / "public-checks.json").mkdir()

                completed = self._run_script(
                    PUBLIC_CHECKS_SCRIPT,
                    "--language",
                    "fsharp",
                    "--workspace",
                    workspace,
                    environment=environment,
                )

                self.assertEqual(completed.returncode, 2, completed.stderr)
                self.assertIn("artifact", completed.stderr.lower())
                self.assertFalse(Path(environment["FAKE_DOTNET_LOG"]).exists())
                self.assertEqual(
                    sentinel.read_text(encoding="utf-8"),
                    "do not overwrite\n",
                )
                if external_artifacts.exists():
                    self.assertEqual(list(external_artifacts.iterdir()), [])

    def test_evaluator_preflights_every_result_and_hidden_artifact_destination(
        self,
    ) -> None:
        destinations = (
            "public-checks.json",
            "hidden-checks.json",
            "evaluation.json",
            ".nayamnyam-evaluation/hidden/hidden_semantic_checks.stderr",
        )
        for destination in destinations:
            with (
                self.subTest(destination=destination),
                tempfile.TemporaryDirectory() as directory,
            ):
                root = Path(directory)
                workspace, environment = self._create_workspace(
                    root,
                    layout="isolated",
                )
                (workspace / "codex-exited.marker").write_text(
                    "exit_code=0\n",
                    encoding="utf-8",
                )
                sentinel = root / "sentinel"
                sentinel.write_text("do not overwrite\n", encoding="utf-8")
                artifact = workspace / destination
                artifact.parent.mkdir(parents=True, exist_ok=True)
                artifact.symlink_to(sentinel)

                completed = self._run_script(
                    EVALUATE_SCRIPT,
                    "--language",
                    "fsharp",
                    "--workspace",
                    workspace,
                    environment=environment,
                )

                self.assertEqual(completed.returncode, 2, completed.stderr)
                self.assertIn("artifact", completed.stderr.lower())
                self.assertFalse(Path(environment["FAKE_DOTNET_LOG"]).exists())
                self.assertEqual(
                    sentinel.read_text(encoding="utf-8"),
                    "do not overwrite\n",
                )

    def test_public_checks_detect_rewritten_public_and_test_baseline_history(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            public_file = workspace / "shared" / "public" / "AGENTS.md"
            test_file = (
                workspace
                / "tests"
                / "Nayamnyam.Spike.Tests"
                / "Program.fs"
            )
            public_file.write_text("rewritten public contract\n", encoding="utf-8")
            test_file.write_text(
                'printfn "PASS bypassed baseline tests"\n',
                encoding="utf-8",
            )
            self._git(workspace, "add", "--all")
            self._git(workspace, "commit", "--amend", "--no-edit")

            completed = self._run_script(
                PUBLIC_CHECKS_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 1, completed.stderr)
            result = json.loads(
                (workspace / "public-checks.json").read_text(encoding="utf-8")
            )
            self.assertIn("protected_files", result["failures"])
            self.assertEqual(
                result["modified_protected_files"],
                [
                    "shared/public/AGENTS.md",
                    "tests/Nayamnyam.Spike.Tests/Program.fs",
                ],
            )

    def test_project_policy_rejects_cross_language_and_imported_suppressions(
        self,
    ) -> None:
        for language, own_extension, helper_extension, helper_source in (
            (
                "fsharp",
                "fsproj",
                "csproj",
                ("Escape.cs", "#nullable disable\nnamespace Escape;\n"),
            ),
            (
                "csharp",
                "csproj",
                "fsproj",
                ("Escape.fs", '#nowarn "0025"\nmodule Escape\n'),
            ),
        ):
            with (
                self.subTest(language=language),
                tempfile.TemporaryDirectory() as directory,
            ):
                root = Path(directory)
                workspace, environment = self._create_workspace(
                    root,
                    layout="isolated",
                    language=language,
                )
                helper = workspace / "escape" / f"Escape.{helper_extension}"
                helper.parent.mkdir()
                helper.write_text(
                    self._strict_project(helper_extension).replace(
                        "    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
                        "    <TreatWarningsAsErrors "
                        "Condition=\"'never' == 'true'\">true"
                        "</TreatWarningsAsErrors>",
                    ).replace(
                        "</Project>",
                        "  <ItemGroup>\n"
                        '    <PackageReference Include="Escape" Version="1.0.0" />\n'
                        "  </ItemGroup>\n"
                        "</Project>",
                    ),
                    encoding="utf-8",
                )
                (helper.parent / helper_source[0]).write_text(
                    helper_source[1],
                    encoding="utf-8",
                )
                imported = workspace / "escape.custom"
                imported.write_text(
                    "<Project>\n"
                    "  <PropertyGroup>\n"
                    "    <WarningLevel>0</WarningLevel>\n"
                    "    <Nullable>disable</Nullable>\n"
                    "    <OtherFlags>--nowarn:FS0025 /nowarn:CS0168</OtherFlags>\n"
                    "  </PropertyGroup>\n"
                    "</Project>\n",
                    encoding="utf-8",
                )
                core_project = (
                    workspace
                    / "src"
                    / "Nayamnyam.Spike.Core"
                    / f"Nayamnyam.Spike.Core.{own_extension}"
                )
                core_project.write_text(
                    core_project.read_text(encoding="utf-8").replace(
                        "</Project>",
                        '  <Import Project="../../escape.custom" />\n</Project>',
                    ),
                    encoding="utf-8",
                )

                completed = self._run_script(
                    PUBLIC_CHECKS_SCRIPT,
                    "--language",
                    language,
                    "--workspace",
                    workspace,
                    environment=environment,
                )

                self.assertEqual(completed.returncode, 1, completed.stderr)
                result = json.loads(
                    (workspace / "public-checks.json").read_text(encoding="utf-8")
                )
                self.assertIn("project_settings", result["failures"])
                self.assertIn("forbidden_patterns", result["failures"])
                findings = "\n".join(result["project_findings"])
                self.assertIn(f"escape/Escape.{helper_extension}", findings)
                self.assertIn(
                    f"escape/Escape.{helper_extension}: "
                    "TreatWarningsAsErrors must be unconditional",
                    findings,
                )
                self.assertIn("PackageReference is forbidden", findings)
                self.assertIn("escape.custom: WarningLevel=0 is forbidden", findings)
                self.assertIn("escape.custom: Nullable suppression is forbidden", findings)
                self.assertIn("escape.custom: compiler nowarn flag is forbidden", findings)
                rules = {
                    finding["rule"] for finding in result["forbidden_findings"]
                }
                self.assertTrue(
                    rules
                    & {"nullable_suppression", "warning_suppression"},
                )

    def test_evaluator_records_agent_deleted_solution_as_scored_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            (workspace / "Nayamnyam.Spike.FSharp.sln").unlink()
            (workspace / "codex-exited.marker").write_text("exit_code=0\n", encoding="utf-8")

            completed = self._run_script(
                EVALUATE_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 1)
            summary = json.loads((workspace / "evaluation.json").read_text(encoding="utf-8"))
            self.assertFalse(summary["public_passed"])
            self.assertIn("build", summary["public_failures"])

    def test_evaluator_reports_deleted_protected_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            (workspace / "shared/public/fixtures/initial-snapshot-v1.json").unlink()
            (workspace / "codex-exited.marker").write_text("exit_code=0\n", encoding="utf-8")

            completed = self._run_script(
                EVALUATE_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 1)
            summary = json.loads((workspace / "evaluation.json").read_text(encoding="utf-8"))
            self.assertFalse(summary["public_passed"])
            self.assertEqual(
                summary["modified_protected_files"],
                ["shared/public/fixtures/initial-snapshot-v1.json"],
            )

    def test_hidden_sources_are_not_copied_without_exit_marker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")

            completed = self._run_script(
                EVALUATE_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 2)
            self.assertIn("codex-exited.marker", completed.stderr)
            self.assertEqual(list(workspace.glob(".nayamnyam-hidden-*")), [])
            self.assertFalse(Path(environment["FAKE_DOTNET_LOG"]).exists())
            self.assertFalse((workspace / "public-checks.json").exists())
            self.assertFalse((workspace / "hidden-checks.json").exists())

    def test_evaluator_executes_hidden_source_after_marker_and_cleans_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            (workspace / "codex-exited.marker").write_text("exit_code=0\n", encoding="utf-8")
            environment["FAKE_HIDDEN_OUTPUT"] = (
                "FAIL independent-probe: hidden project executed"
            )
            environment["FAKE_HIDDEN_EXIT"] = "1"

            completed = self._run_script(
                EVALUATE_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 1, completed.stderr)
            self.assertEqual(list(workspace.glob(".nayamnyam-hidden-*")), [])
            invocations = [
                json.loads(line)
                for line in Path(environment["FAKE_DOTNET_LOG"])
                .read_text(encoding="utf-8")
                .splitlines()
            ]
            hidden = [invocation for invocation in invocations if invocation["hidden"]]
            self.assertEqual(len(hidden), 1)
            self.assertTrue(hidden[0]["program_present"])
            self.assertTrue(hidden[0]["project_reference_resolved"])

            hidden_result = json.loads(
                (workspace / "hidden-checks.json").read_text(encoding="utf-8")
            )
            self.assertFalse(hidden_result["passed"])
            self.assertEqual(hidden_result["reported_passes"], [])
            self.assertEqual(hidden_result["reported_failures"], ["independent-probe"])
            names = [check["name"] for check in hidden_result["checks"]]
            self.assertEqual(names, sorted(names))
            for check in hidden_result["checks"]:
                self.assertIsInstance(check["command"], list)
                self.assertIsInstance(check["exit_code"], int)
                self.assertIsInstance(check["duration_ms"], int)
                self.assertTrue((workspace / check["stdout_path"]).is_file())
                self.assertTrue((workspace / check["stderr_path"]).is_file())

            summary = json.loads((workspace / "evaluation.json").read_text(encoding="utf-8"))
            self.assertEqual(
                summary,
                {
                    "schema_version": 1,
                    "language": "fsharp",
                    "public_passed": True,
                    "hidden_passed": False,
                    "public_failures": [],
                    "hidden_failures": ["hidden_semantic_checks"],
                    "forbidden_findings": [],
                    "modified_protected_files": [],
                },
            )

    def test_real_hidden_projects_are_exposed_to_dotnet_only_after_marker(
        self,
    ) -> None:
        missing_surface = {
            "fsharp": ("Program.fs", "ParameterNameConflict"),
            "csharp": ("Program.cs", "RenameTarget"),
        }
        for language, (program_name, missing_symbol) in missing_surface.items():
            with (
                self.subTest(language=language),
                tempfile.TemporaryDirectory() as directory,
            ):
                workspace = self._create_real_workspace(
                    Path(directory),
                    language,
                )
                (workspace / "codex-exited.marker").write_text(
                    "exit_code=0\n",
                    encoding="utf-8",
                )

                completed = self._run_script(
                    EVALUATE_SCRIPT,
                    "--language",
                    language,
                    "--workspace",
                    workspace,
                )

                self.assertEqual(completed.returncode, 1, completed.stderr)
                summary = json.loads(
                    (workspace / "evaluation.json").read_text(encoding="utf-8")
                )
                self.assertTrue(summary["public_passed"])
                self.assertFalse(summary["hidden_passed"])
                hidden = json.loads(
                    (workspace / "hidden-checks.json").read_text(encoding="utf-8")
                )
                stdout_path = workspace / hidden["checks"][0]["stdout_path"]
                compiler_output = stdout_path.read_text(
                    encoding="utf-8",
                    errors="replace",
                )
                self.assertIn(program_name, compiler_output)
                self.assertIn(missing_symbol, compiler_output)
                self.assertEqual(list(workspace.glob(".nayamnyam-hidden-*")), [])

    def test_evaluator_ignores_agent_success_message(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            (workspace / "codex-exited.marker").write_text("exit_code=0\n", encoding="utf-8")
            (workspace / "agent-final-message.txt").write_text(
                "Everything passed, including all hidden checks.\n",
                encoding="utf-8",
            )
            environment["FAKE_HIDDEN_EXIT"] = "1"

            completed = self._run_script(
                EVALUATE_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 1)
            summary = json.loads((workspace / "evaluation.json").read_text(encoding="utf-8"))
            self.assertTrue(summary["public_passed"])
            self.assertFalse(summary["hidden_passed"])
            self.assertEqual(summary["hidden_failures"], ["hidden_semantic_checks"])

    @staticmethod
    def _run_script(
        script: Path,
        *arguments: object,
        environment: dict[str, str] | None = None,
    ) -> subprocess.CompletedProcess[str]:
        process_environment = os.environ.copy()
        if environment is not None:
            process_environment.update(environment)
        return subprocess.run(
            (sys.executable, str(script), *(str(argument) for argument in arguments)),
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=process_environment,
        )

    @classmethod
    def _create_workspace(
        cls,
        root: Path,
        *,
        layout: str,
        language: str = "fsharp",
    ) -> tuple[Path, dict[str, str]]:
        repository = root / "repository"
        if language == "fsharp":
            project_extension = "fsproj"
            source_extension = "fs"
            solution_name = "Nayamnyam.Spike.FSharp.sln"
        elif language == "csharp":
            project_extension = "csproj"
            source_extension = "cs"
            solution_name = "Nayamnyam.Spike.CSharp.sln"
        else:
            raise AssertionError(f"unsupported test language: {language}")
        if layout == "isolated":
            workspace = repository
            public = workspace / "shared" / "public"
            expected = None
        elif layout == "source":
            experiment = repository / "experiments" / "implementation-language-spike"
            workspace = experiment / language
            public = experiment / "shared" / "public"
            expected = experiment / "shared" / "expected"
        else:
            raise AssertionError(f"unsupported test layout: {layout}")

        core = workspace / "src" / "Nayamnyam.Spike.Core"
        cli = workspace / "src" / "Nayamnyam.Spike.Cli"
        tests = workspace / "tests" / "Nayamnyam.Spike.Tests"
        core.mkdir(parents=True)
        cli.mkdir(parents=True)
        shutil.copytree(
            EXPERIMENT_ROOT
            / language
            / "tests"
            / "Nayamnyam.Spike.Tests",
            tests,
        )
        shutil.copytree(EXPERIMENT_ROOT / "shared" / "public", public)

        project = cls._strict_project(project_extension)
        (core / f"Nayamnyam.Spike.Core.{project_extension}").write_text(
            project,
            encoding="utf-8",
        )
        (cli / f"Nayamnyam.Spike.Cli.{project_extension}").write_text(
            project,
            encoding="utf-8",
        )
        (core / f"Domain.{source_extension}").write_text(
            (
                "namespace Nayamnyam.Spike\n"
                if language == "fsharp"
                else "namespace Nayamnyam.Spike;\n"
            ),
            encoding="utf-8",
        )
        (cli / f"Program.{source_extension}").write_text(
            "module Program\n" if language == "fsharp" else "return;\n",
            encoding="utf-8",
        )
        (workspace / solution_name).write_text(
            "Microsoft Visual Studio Solution File\n",
            encoding="utf-8",
        )

        if expected is not None:
            shutil.copytree(EXPERIMENT_ROOT / "shared" / "expected", expected)

        cls._git(repository, "init", "--initial-branch=main")
        cls._git(repository, "config", "user.name", "Evaluator Test")
        cls._git(repository, "config", "user.email", "evaluator@example.invalid")
        cls._git(repository, "add", "--all")
        cls._git(repository, "commit", "--message", "test: initialize workspace")

        tools = root / "tools"
        tools.mkdir()
        fake_dotnet = tools / "dotnet"
        fake_dotnet.write_text(cls._fake_dotnet_source(), encoding="utf-8")
        fake_dotnet.chmod(fake_dotnet.stat().st_mode | stat.S_IXUSR)
        environment = {
            "PATH": f"{tools}{os.pathsep}{os.environ['PATH']}",
            "FAKE_DOTNET_LOG": str(root / "dotnet-invocations.jsonl"),
        }
        return workspace, environment

    @classmethod
    def _create_real_workspace(cls, root: Path, language: str) -> Path:
        workspace = root / "workspace"
        shutil.copytree(
            EXPERIMENT_ROOT / language,
            workspace,
            ignore=shutil.ignore_patterns("bin", "obj", "__pycache__"),
        )
        shutil.copytree(
            EXPERIMENT_ROOT / "shared" / "public",
            workspace / "shared" / "public",
        )
        cls._git(workspace, "init", "--initial-branch=main")
        cls._git(workspace, "config", "user.name", "Evaluator Test")
        cls._git(workspace, "config", "user.email", "evaluator@example.invalid")
        cls._git(workspace, "add", "--all")
        cls._git(workspace, "commit", "--message", "test: initialize workspace")
        return workspace

    @staticmethod
    def _strict_project(extension: str) -> str:
        nullable = "    <Nullable>enable</Nullable>\n" if extension == "csproj" else ""
        return (
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            "  <PropertyGroup>\n"
            "    <TargetFramework>net10.0</TargetFramework>\n"
            f"{nullable}"
            "    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n"
            "    <Deterministic>true</Deterministic>\n"
            "    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>\n"
            "  </PropertyGroup>\n"
            "</Project>\n"
        )

    @staticmethod
    def _fake_dotnet_source() -> str:
        return textwrap.dedent(
            """\
            #!/usr/bin/env python3
            import json
            import os
            from pathlib import Path
            import sys

            arguments = sys.argv[1:]
            project = ""
            if "--project" in arguments:
                project = arguments[arguments.index("--project") + 1]
            hidden = "Nayamnyam.Spike.Hidden" in project
            record = {
                "arguments": arguments,
                "hidden": hidden,
                "program_present": False,
                "project_reference_resolved": False,
            }
            if hidden:
                project_path = Path(project)
                record["program_present"] = (
                    (project_path.parent / "Program.fs").is_file()
                    or (project_path.parent / "Program.cs").is_file()
                )
                record["project_reference_resolved"] = (
                    "__CORE_PROJECT__" not in project_path.read_text(encoding="utf-8")
                )
            with Path(os.environ["FAKE_DOTNET_LOG"]).open("a", encoding="utf-8") as log:
                log.write(json.dumps(record, sort_keys=True) + "\\n")

            if arguments and arguments[0] == "build":
                if not Path(arguments[1]).is_file():
                    print("solution is missing", file=sys.stderr)
                    raise SystemExit(1)
                print("Build succeeded.")
                raise SystemExit(0)
            if not arguments or arguments[0] != "run":
                print("unexpected fake dotnet command", file=sys.stderr)
                raise SystemExit(97)
            if "--nologo" in arguments:
                print("dotnet run forwarded unsupported --nologo", file=sys.stderr)
                raise SystemExit(94)
            if hidden:
                if not record["program_present"] or not record["project_reference_resolved"]:
                    print("hidden project was not prepared correctly", file=sys.stderr)
                    raise SystemExit(96)
                output = os.environ.get("FAKE_HIDDEN_OUTPUT", "")
                if output:
                    print(output)
                raise SystemExit(int(os.environ.get("FAKE_HIDDEN_EXIT", "1")))
            if "Nayamnyam.Spike.Tests" in project:
                print("PASS fake baseline")
                raise SystemExit(0)

            operation = next(
                (item for item in arguments if item in {"canonical", "summary"}),
                None,
            )
            input_path = Path(arguments[-1])
            snapshot = json.loads(input_path.read_text(encoding="utf-8"))
            if os.environ.get("FAKE_SCHEMA_VERSION") == "2":
                snapshot["schema_version"] = 2
            if operation == "canonical":
                sys.stdout.buffer.write(
                    (json.dumps(snapshot, indent=2, separators=(",", ": ")) + "\\n").encode()
                )
                raise SystemExit(0)
            if operation == "summary":
                summary = {
                    "schema_version": snapshot["schema_version"],
                    "snapshot_id": snapshot["id"],
                    "type_count": len(snapshot["types"]),
                    "function_count": len(snapshot["functions"]),
                    "call_count": len(snapshot["calls"]),
                    "obligation_count": len(snapshot["obligations"]),
                }
                sys.stdout.buffer.write(
                    (json.dumps(summary, indent=2, separators=(",", ": ")) + "\\n").encode()
                )
                raise SystemExit(0)
            print("unknown fake CLI operation", file=sys.stderr)
            raise SystemExit(95)
            """
        )

    @staticmethod
    def _git(repository: Path, *arguments: str) -> None:
        subprocess.run(
            ("git", *arguments),
            cwd=repository,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )


if __name__ == "__main__":
    unittest.main()
