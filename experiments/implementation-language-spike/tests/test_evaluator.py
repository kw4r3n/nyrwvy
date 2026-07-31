import json
import os
from pathlib import Path
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

    def test_evaluator_copies_hidden_source_after_marker_and_cleans_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace, environment = self._create_workspace(root, layout="isolated")
            (workspace / "codex-exited.marker").write_text("exit_code=0\n", encoding="utf-8")

            completed = self._run_script(
                EVALUATE_SCRIPT,
                "--language",
                "fsharp",
                "--workspace",
                workspace,
                environment=environment,
            )

            self.assertEqual(completed.returncode, 0, completed.stderr)
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
            self.assertTrue(hidden_result["passed"])
            self.assertEqual(
                hidden_result["reported_passes"],
                [
                    "duplicate-name parameter obligations",
                    "same-name rename is no change",
                    "schema v2 migration and canonical stability",
                    "type rename preserves identity and references",
                ],
            )
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
                    "hidden_passed": True,
                    "public_failures": [],
                    "hidden_failures": [],
                    "forbidden_findings": [],
                    "modified_protected_files": [],
                },
            )

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
    ) -> tuple[Path, dict[str, str]]:
        repository = root / "repository"
        if layout == "isolated":
            workspace = repository
            public = workspace / "shared" / "public"
            expected = None
        elif layout == "source":
            experiment = repository / "experiments" / "implementation-language-spike"
            workspace = experiment / "fsharp"
            public = experiment / "shared" / "public"
            expected = experiment / "shared" / "expected"
        else:
            raise AssertionError(f"unsupported test layout: {layout}")

        core = workspace / "src" / "Nayamnyam.Spike.Core"
        cli = workspace / "src" / "Nayamnyam.Spike.Cli"
        tests = workspace / "tests" / "Nayamnyam.Spike.Tests"
        core.mkdir(parents=True)
        cli.mkdir(parents=True)
        tests.mkdir(parents=True)
        public.mkdir(parents=True)
        (public / "fixtures").mkdir()
        (public / "schema").mkdir()

        project = cls._strict_project("fsproj")
        (core / "Nayamnyam.Spike.Core.fsproj").write_text(project, encoding="utf-8")
        (cli / "Nayamnyam.Spike.Cli.fsproj").write_text(project, encoding="utf-8")
        (tests / "Nayamnyam.Spike.Tests.fsproj").write_text(project, encoding="utf-8")
        (core / "Domain.fs").write_text("namespace Nayamnyam.Spike\n", encoding="utf-8")
        (cli / "Program.fs").write_text("module Program\n", encoding="utf-8")
        (tests / "Program.fs").write_text("module Tests\n", encoding="utf-8")
        (workspace / "Nayamnyam.Spike.FSharp.sln").write_text(
            "Microsoft Visual Studio Solution File\n",
            encoding="utf-8",
        )

        fixture = cls._fixture_bytes()
        (public / "fixtures" / "initial-snapshot-v1.json").write_bytes(fixture)
        (public / "AGENTS.md").write_text("agent rules\n", encoding="utf-8")
        (public / "acceptance.md").write_text("acceptance\n", encoding="utf-8")
        (public / "maintenance-task.md").write_text("maintenance\n", encoding="utf-8")
        (public / "semantic-contract.md").write_text("contract\n", encoding="utf-8")
        (public / "schema" / "snapshot-v1.schema.json").write_text(
            '{"schema_version":1}\n',
            encoding="utf-8",
        )
        if expected is not None:
            expected.mkdir(parents=True)
            (expected / "initial-canonical.json").write_bytes(fixture)
            (expected / "initial-summary.json").write_bytes(cls._summary_bytes())

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
    def _fixture_bytes() -> bytes:
        fixture = {
            "schema_version": 1,
            "id": "snapshot:initial",
            "types": [
                {"id": "type:raw-user-input", "name": "raw_user_input"},
                {"id": "type:string", "name": "string"},
                {"id": "type:valid-user", "name": "valid_user"},
            ],
            "functions": [
                {"id": "function:main", "name": "main", "parameters": []},
                {
                    "id": "function:validate-user",
                    "name": "validate_user",
                    "parameters": [
                        {
                            "id": "parameter:input",
                            "name": "input",
                            "type_id": "type:raw-user-input",
                        }
                    ],
                },
            ],
            "calls": [
                {
                    "id": "call:main-validate",
                    "callee_id": "function:validate-user",
                    "arguments": [
                        {
                            "parameter_id": "parameter:input",
                            "value_ref": "local:raw-input",
                        }
                    ],
                }
            ],
            "obligations": [],
        }
        return (json.dumps(fixture, indent=2, separators=(",", ": ")) + "\n").encode()

    @staticmethod
    def _summary_bytes() -> bytes:
        summary = {
            "schema_version": 1,
            "snapshot_id": "snapshot:initial",
            "type_count": 3,
            "function_count": 2,
            "call_count": 1,
            "obligation_count": 0,
        }
        return (json.dumps(summary, indent=2, separators=(",", ": ")) + "\n").encode()

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
                print("PASS duplicate-name parameter obligations")
                print("PASS same-name rename is no change")
                print("PASS type rename preserves identity and references")
                print("PASS schema v2 migration and canonical stability")
                raise SystemExit(int(os.environ.get("FAKE_HIDDEN_EXIT", "0")))
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
