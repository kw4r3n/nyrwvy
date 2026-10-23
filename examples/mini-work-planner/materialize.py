#!/usr/bin/env python3
"""Materialize the successful Mini Work Planner through public AuthoringSession operations."""

from __future__ import annotations

import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent
REPO = ROOT.parent.parent
SNAPSHOT = ROOT / "planner.snapshot.nyrec"
RUNNER = ROOT / "runner"
PROJECT = REPO / "tools" / "Nyrwvy.AuthoringSession" / "Nyrwvy.AuthoringSession.fsproj"


def exchange(proc: subprocess.Popen[str], payload: dict) -> dict:
    assert proc.stdin is not None
    assert proc.stdout is not None
    proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
    proc.stdin.flush()
    line = proc.stdout.readline()
    if not line:
        stderr = proc.stderr.read() if proc.stderr is not None else ""
        raise RuntimeError(f"AuthoringSession ended early: {stderr}")
    response = json.loads(line)
    if not response.get("ok"):
        raise RuntimeError(response)
    return response["result"]


def symbol_maps(symbols: dict) -> dict[str, dict[str, str]]:
    return {
        kind: {item["id"]: item["name"] for item in symbols[kind]}
        for kind in ("types", "fields", "variants", "functions")
    }


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: materialize.py <successful-workspace.json>")

    source_snapshot = pathlib.Path(sys.argv[1]).expanduser().resolve()
    if not source_snapshot.is_file():
        raise SystemExit(f"workspace not found: {source_snapshot}")

    subprocess.run(
        ["dotnet", "build", str(PROJECT), "--configuration", "Release"],
        cwd=REPO,
        check=True,
    )

    SNAPSHOT.unlink(missing_ok=True)
    subprocess.run(
        ["dotnet", "run", "--project", str(PROJECT), "--configuration", "Release", "--no-build", "--",
         "migrate", "--snapshot", str(source_snapshot), "--to", str(SNAPSHOT)],
        cwd=REPO,
        check=True,
        stdout=subprocess.DEVNULL,
    )

    proc = subprocess.Popen(
        [
            "dotnet",
            "run",
            "--project",
            str(PROJECT),
            "--configuration",
            "Release",
            "--no-build",
            "--",
            "open",
            "--snapshot",
            str(SNAPSHOT),
        ],
        cwd=REPO,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    try:
        declarations = exchange(proc, {"id": "d1", "op": "declarations"})
        readiness = exchange(proc, {"id": "r1", "op": "executionReadiness"})
        if readiness.get("kind") != "ready":
            raise RuntimeError(f"executionReadiness was not ready: {readiness}")
        artifact = exchange(
            proc,
            {"id": "e1", "op": "emitCSharp", "profile": {"kind": "empty"}},
        )
    finally:
        if proc.stdin is not None:
            proc.stdin.close()
        return_code = proc.wait(timeout=15)
        if return_code != 0:
            stderr = proc.stderr.read() if proc.stderr is not None else ""
            raise RuntimeError(f"AuthoringSession exited with {return_code}: {stderr}")

    (ROOT / "declarations.json").write_text(
        json.dumps(declarations, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    source = artifact["source"]
    symbols = artifact["symbols"]
    maps = symbol_maps(symbols)

    types = {item["displayName"]: item for item in declarations["types"]}
    functions = {item["displayName"]: item for item in declarations["functions"]}

    required_types = {
        "PrerequisiteState",
        "ExecutionState",
        "ValidationState",
        "WorkItem",
        "NextAction",
    }
    required_functions = {"can_retry", "next_action", "action_label"}
    if not required_types.issubset(types):
        raise RuntimeError(f"missing types: {sorted(required_types - types.keys())}")
    if not required_functions.issubset(functions):
        raise RuntimeError(
            f"missing functions: {sorted(required_functions - functions.keys())}"
        )

    def symbol(kind: str, identity: str) -> str:
        name = maps[kind][identity]
        if name not in source:
            raise RuntimeError(f"symbol missing from generated source: {name}")
        return name

    def type_symbol(type_name: str) -> str:
        return symbol("types", types[type_name]["id"])

    def variant_symbol(type_name: str, variant_name: str) -> str:
        variants = types[type_name]["typeKind"]["variants"]
        variant = next(v for v in variants if v["displayName"] == variant_name)
        return symbol("variants", variant["id"])

    work_fields = {
        field["displayName"]: symbol("fields", field["id"])
        for field in types["WorkItem"]["typeKind"]["fields"]
    }

    replacements = {
        "__NAMESPACE__": symbols["namespace"],
        "__FUNCTION_CONTAINER__": symbols["functionContainer"],
        "__PREREQUISITE__": type_symbol("PrerequisiteState"),
        "__EXECUTION__": type_symbol("ExecutionState"),
        "__VALIDATION__": type_symbol("ValidationState"),
        "__WORK_ITEM__": type_symbol("WorkItem"),
        "__PR_READY__": variant_symbol("PrerequisiteState", "Ready"),
        "__PR_WAITING__": variant_symbol("PrerequisiteState", "Waiting"),
        "__PR_FAILED__": variant_symbol("PrerequisiteState", "Failed"),
        "__EX_NOT_STARTED__": variant_symbol("ExecutionState", "NotStarted"),
        "__EX_RUNNING__": variant_symbol("ExecutionState", "Running"),
        "__EX_FAILED__": variant_symbol("ExecutionState", "Failed"),
        "__EX_TIMED_OUT__": variant_symbol("ExecutionState", "TimedOut"),
        "__EX_SUCCEEDED__": variant_symbol("ExecutionState", "Succeeded"),
        "__VA_NOT_REQUIRED__": variant_symbol("ValidationState", "NotRequired"),
        "__VA_PENDING__": variant_symbol("ValidationState", "Pending"),
        "__VA_PASSED__": variant_symbol("ValidationState", "Passed"),
        "__VA_FAILED__": variant_symbol("ValidationState", "Failed"),
        "__F_PREREQUISITES__": work_fields["prerequisites"],
        "__F_EXECUTION__": work_fields["execution"],
        "__F_VALIDATION__": work_fields["validation"],
        "__F_ATTEMPTS__": work_fields["attempts"],
        "__F_MAX_ATTEMPTS__": work_fields["maxAttempts"],
        "__NEXT_ACTION__": symbol("functions", functions["next_action"]["id"]),
        "__ACTION_LABEL__": symbol("functions", functions["action_label"]["id"]),
    }

    program = r'''using System;
using System.Globalization;
using __NAMESPACE__;

internal static class Program
{
    static __PREREQUISITE__ ParsePrerequisite(string value) =>
        value.ToLowerInvariant() switch
        {
            "ready" => new __PR_READY__(),
            "waiting" => new __PR_WAITING__(),
            "failed" => new __PR_FAILED__(),
            _ => throw new ArgumentException("Unknown prerequisite state: " + value)
        };

    static __EXECUTION__ ParseExecution(string value) =>
        value.ToLowerInvariant() switch
        {
            "notstarted" => new __EX_NOT_STARTED__(),
            "running" => new __EX_RUNNING__(),
            "failed" => new __EX_FAILED__(),
            "timedout" => new __EX_TIMED_OUT__(),
            "succeeded" => new __EX_SUCCEEDED__(),
            _ => throw new ArgumentException("Unknown execution state: " + value)
        };

    static __VALIDATION__ ParseValidation(string value) =>
        value.ToLowerInvariant() switch
        {
            "notrequired" => new __VA_NOT_REQUIRED__(),
            "pending" => new __VA_PENDING__(),
            "passed" => new __VA_PASSED__(),
            "failed" => new __VA_FAILED__(),
            _ => throw new ArgumentException("Unknown validation state: " + value)
        };

    static string Run(
        __PREREQUISITE__ prerequisites,
        __EXECUTION__ execution,
        __VALIDATION__ validation,
        long attempts,
        long maxAttempts)
    {
        var item = new __WORK_ITEM__(
            __F_PREREQUISITES__: prerequisites,
            __F_EXECUTION__: execution,
            __F_VALIDATION__: validation,
            __F_ATTEMPTS__: attempts,
            __F_MAX_ATTEMPTS__: maxAttempts);

        var action = __FUNCTION_CONTAINER__.__NEXT_ACTION__(item);
        return __FUNCTION_CONTAINER__.__ACTION_LABEL__(action);
    }

    static void Check(
        __PREREQUISITE__ prerequisites,
        __EXECUTION__ execution,
        __VALIDATION__ validation,
        long attempts,
        long maxAttempts,
        string expected)
    {
        var actual = Run(prerequisites, execution, validation, attempts, maxAttempts);
        if (actual != expected)
            throw new Exception($"{actual} != {expected}");
    }

    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--test")
        {
            Check(new __PR_FAILED__(), new __EX_NOT_STARTED__(), new __VA_NOT_REQUIRED__(), 0, 1, "needs-human");
            Check(new __PR_WAITING__(), new __EX_NOT_STARTED__(), new __VA_NOT_REQUIRED__(), 0, 1, "wait");
            Check(new __PR_READY__(), new __EX_NOT_STARTED__(), new __VA_NOT_REQUIRED__(), 0, 1, "start");
            Check(new __PR_READY__(), new __EX_RUNNING__(), new __VA_NOT_REQUIRED__(), 0, 1, "wait");
            Check(new __PR_READY__(), new __EX_FAILED__(), new __VA_PENDING__(), 0, 1, "retry");
            Check(new __PR_READY__(), new __EX_FAILED__(), new __VA_PENDING__(), 1, 1, "needs-human");
            Check(new __PR_READY__(), new __EX_TIMED_OUT__(), new __VA_PENDING__(), 0, 1, "retry");
            Check(new __PR_READY__(), new __EX_TIMED_OUT__(), new __VA_PENDING__(), 1, 1, "needs-human");
            Check(new __PR_READY__(), new __EX_SUCCEEDED__(), new __VA_NOT_REQUIRED__(), 0, 1, "complete");
            Check(new __PR_READY__(), new __EX_SUCCEEDED__(), new __VA_PENDING__(), 0, 1, "validate");
            Check(new __PR_READY__(), new __EX_SUCCEEDED__(), new __VA_PASSED__(), 0, 1, "complete");
            Check(new __PR_READY__(), new __EX_SUCCEEDED__(), new __VA_FAILED__(), 0, 1, "retry");
            Check(new __PR_READY__(), new __EX_SUCCEEDED__(), new __VA_FAILED__(), 1, 1, "needs-human");
            Console.WriteLine("PASS 13 policy cases");
            return 0;
        }

        if (args.Length != 5)
        {
            Console.Error.WriteLine(
                "usage: <PrerequisiteState> <ExecutionState> <ValidationState> <attempts> <maxAttempts>");
            return 2;
        }

        try
        {
            var result = Run(
                ParsePrerequisite(args[0]),
                ParseExecution(args[1]),
                ParseValidation(args[2]),
                long.Parse(args[3], CultureInfo.InvariantCulture),
                long.Parse(args[4], CultureInfo.InvariantCulture));
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
}
'''

    for key, value in replacements.items():
        program = program.replace(key, value)

    RUNNER.mkdir(exist_ok=True)
    (RUNNER / "Generated.cs").write_text(source, encoding="utf-8")
    (RUNNER / "Program.cs").write_text(program, encoding="utf-8")
    (RUNNER / "Runner.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
        '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
        '<Nullable>enable</Nullable></PropertyGroup></Project>\n',
        encoding="utf-8",
    )

    subprocess.run(
        ["dotnet", "run", "--project", str(RUNNER / "Runner.csproj"), "--", "--test"],
        cwd=REPO,
        check=True,
    )

    print("PASS materialized Mini Work Planner")
    print(json.dumps(readiness, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
