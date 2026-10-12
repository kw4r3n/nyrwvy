"""Use the pinned, unmodified Team methods; replace external effects only.

No Team subclass and no replacement spawn/wait/inspect/stop/discard/close/execute.
The controller comparison normalizes IDs, paths, clocks and selected evidence.
"""
import argparse
import asyncio
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from unittest.mock import patch


async def run_case(c, team_module):
    from rrawrlet.backend import AttemptOutcome
    root = Path(tempfile.mkdtemp(prefix="member-oracle-"))
    output = root / "output"
    output.mkdir()
    team = team_module.Team(root / "source", output, max_members=2,
                            attempt_seconds=5, reuse_member_threads=False)
    team.catalog = [{"model": "fake", "isDefault": True,
                     "defaultReasoningEffort": "low",
                     "supportedReasoningEfforts": [{"reasoningEffort": "low"}]}]
    team.member_checks = [{"argv": ["fake"], "required": True}]
    team.git_dir = str(root / "fake-git")
    ids, workspaces, replies, effects, agents = {}, {}, {}, [], {}
    entered, snap_entered, snap_release, provider_release = {}, {}, {}, {}
    both = asyncio.Event()
    check_entered, check_release = asyncio.Event(), asyncio.Event()
    active = 0
    max_active = 0

    def event(table, key):
        return table.setdefault(key, asyncio.Event())

    class GitEffects:
        tampered = []
        def add_worktree(self, workspace, git_dir):
            pass
        async def discard_worktree(self, workspace):
            effects.append(["workspaceDiscard", workspaces[workspace]])
        def restore(self):
            pass
        def unguarded_git_dirs(self):
            return []
        def release(self):
            effects.append(["gitRelease"])

    team.git_host = GitEffects()

    async def clean(*args, **kwargs):
        pass

    async def git_effect(cwd, *args, **kwargs):
        path = str(cwd)
        if args[:2] == ("worktree", "add"):
            workspaces[args[3]] = f"w{len(ids) + 1}"
            effects.append(["workspaceCreate", workspaces[args[3]]])
        if args[:2] == ("rev-parse", "--absolute-git-dir"):
            return str(root / "metadata").encode()
        if args[:2] == ("add", "-A"):
            i = next(i for raw, i in ids.items() if team.get(raw).workspace == path
                     and not any(a.previous == raw for a in team.attempts.values()))
            effects.append(["snapshot", i])
            event(snap_entered, i).set()
            if c["id"] == "snapshot-timeout" and i == 1:
                await event(snap_release, i).wait()
            if c["id"] == "snapshot-error" and i == 1:
                raise RuntimeError("snapshot failed")
        if args[:3] == ("diff", "--cached", "--name-only"):
            return b""
        if args[:2] == ("rev-parse", "HEAD"):
            if path == str(team.repo):
                return b"base"
            i = max(ids[a.id] for a in team.attempts.values() if a.workspace == path)
            return f"commit{i}".encode()
        if args[:2] == ("rev-parse", "--verify"):
            return b"base"
        return b""

    team.host_git = git_effect

    class Agent:
        reusable = False
        cleanup_errors = []
        def __init__(self, attempt):
            self.attempt = attempt
            self.thread_id = f"fresh-{ids[attempt.id]}"
            self.turn_id = "first"
            self.closed = 0
        async def start(self):
            effects.append(["providerStart", ids[self.attempt.id], self.thread_id])
        async def close(self):
            self.closed += 1
            assert self.closed == 1
            effects.append(["memberClose", ids[self.attempt.id]])
            if c["id"] == "cleanup-fault" and ids[self.attempt.id] == 1:
                raise RuntimeError("cleanup fault")
        def final_fields(self, attempt):
            return {}

    def build(team_arg, attempt, **kwargs):
        agent = Agent(attempt)
        agents[attempt.id] = agent
        async def provider(assignment):
            nonlocal active, max_active
            i = ids[attempt.id]
            kind = c["providers"].get(str(i), "hold")
            active += 1
            max_active = max(max_active, active)
            event(entered, i).set()
            if len(entered) >= 2:
                both.set()
            try:
                if i <= 2:
                    await both.wait()
                await event(provider_release, i).wait()
                if i == 1 and c["id"] == "outside-provider-fault":
                    raise RuntimeError("outside provider run")
                if i == 1 and c["id"] == "foreign-cancellation":
                    raise asyncio.CancelledError("foreign cancellation")
                if kind == "expected-error":
                    raise RuntimeError("deadline")  # Error text must not impersonate the attempt deadline.
                if kind == "inner-timeout":
                    raise TimeoutError("inner timeout")
                if kind == "unexpected-fault":
                    raise RuntimeError("unexpected host fault")
                return AttemptOutcome("completed")
            finally:
                active -= 1
        return agent, provider

    async def checks(specs, workspace, commit, *args, **kwargs):
        i = int(commit.removeprefix("commit"))
        effects.append(["checks", i])
        if c["id"] == "checks-gated" and i == 1:
            check_entered.set()
            await check_release.wait()
        status = ("failed" if c["id"] == "checks-failed" and i == 1 else
                  "unavailable" if c["id"] == "checks-unavailable" and i == 1 else "passed")
        return {"status": status, "commit": commit, "checks": []}

    def render(records):
        parts = []
        for a in records:
            i = ids[a["id"]]
            if "workspace" not in a:
                parts.append(f"{i},running;")
            else:
                workspace = workspaces[a["workspace"]]
                parts.append(f"{i},{a['status']},{workspace},base,{ids.get(a['previous'], 0)},"
                             f"{str(a['discarded']).lower()},{a['commit'] or ''},"
                             f"{(a.get('checks') or {}).get('status') or ''};")
        return "".join(parts)

    def reject(exc):
        text = str(exc)
        for key, value in [("Member limit", "capacity"), ("latest", "superseded"),
                           ("discarded", "discarded"), ("terminal", "running"),
                           ("Stop the Member", "running"), ("closing", "closing"),
                           ("Unknown", "unknown")]:
            if key in text:
                return "reject:" + value
        raise AssertionError(text)

    with patch.object(team_module, "clean", clean), \
         patch.object(team_module, "build_attempt", build), \
         patch.object(team_module, "check_submission", checks):
        if c["id"] == "caller-cancellation-gap":
            record = await team.spawn("fresh assignment", "fake", effort="low")
            ids[record[0]["id"]] = 1
            await event(entered, 1).wait()
            waiting = asyncio.create_task(team.wait(list(ids), seconds=None))
            await asyncio.sleep(0)
            waiting.cancel()
            try:
                await waiting
            except asyncio.CancelledError:
                replies["caller-cancellation"] = "caller:cancelled"
            assert not team.tasks[next(iter(ids))].cancelling()
            assert not team.tasks[next(iter(ids))].done()
            replies["member-alive"] = "member:running"
            await team.close()
        else:
            for r in c["script"]:
                op, label = r["op"], r["label"]
                raw = next((raw for raw, i in ids.items() if i == r["target"]), None)
                previous = next((raw for raw, i in ids.items() if i == r["previous"]), None)
                try:
                    if op == "spawn":
                        team.attempt_seconds = r["limit"] / 1000
                        record = await team.spawn("fresh assignment", "fake", effort="low", previous=previous)
                        ids[record[0]["id"]] = len(ids) + 1
                        replies[label] = f"spawn:{len(ids)}"
                    elif op == "wait":
                        result = await team.wait([raw] if raw else list(ids), mode=r["mode"], seconds=r["limit"] / 1000)
                        replies[label] = render(result)
                        if label == "observed" and replies.get("caller") != "caller:cancelled":
                            replies["observer-payload"] = "".join(f"{ids[a['id']]},{a['status']};" for a in result)
                    elif op == "inspect":
                        replies[label] = render(team.inspect(raw))
                    elif op == "inspect-capacity":
                        replies[label] = str(sum(not task.done() for task in team.tasks.values()))
                    elif op == "mark-caller":
                        replies.setdefault("caller", "caller:observed")
                    elif op in ("caller-cancel", "complete-then-cancel", "published-then-cancel", "global-cancel"):
                        saw_result = asyncio.Event()
                        hold_result = asyncio.Event()
                        async def caller():
                            result = await team.wait(list(ids), mode="all", seconds=None)
                            saw_result.set()
                            if op == "published-then-cancel":
                                await hold_result.wait()
                            return result
                        waiting = asyncio.create_task(caller())
                        if op == "complete-then-cancel":
                            result = await waiting
                            replies["observed"] = render(result)
                            replies["observer-payload"] = "".join(f"{ids[a['id']]},{a['status']};" for a in result)
                            assert not waiting.cancel()
                            replies["caller"] = "caller:observed"
                        else:
                            if op == "published-then-cancel":
                                await saw_result.wait()
                            else:
                                await asyncio.sleep(0)
                            waiting.cancel()
                            try:
                                await waiting
                            except asyncio.CancelledError:
                                replies["caller"] = "caller:cancelled"
                            else:
                                raise AssertionError("caller cancellation was swallowed")
                            assert all(not task.cancelling() for task in team.tasks.values())
                            if op == "global-cancel":
                                await team.close()
                            else:
                                replies["observed"] = render(await team.wait(list(ids), seconds=0))
                    elif op == "stop":
                        if c["id"] in ("stop-snapshot", "checks-gated") and r["target"] == 1:
                            stopping = asyncio.create_task(team.stop(raw))
                            await asyncio.sleep(0)
                            assert not team.tasks[raw].cancelling()
                            assert not stopping.done()
                            if c["id"] == "checks-gated":
                                check_release.set()
                            else:
                                event(snap_release, 1).set()
                            replies[label] = render(await stopping)
                        else:
                            replies[label] = render(await team.stop(raw))
                    elif op == "discard":
                        await team.discard(raw)
                        replies[label] = "discarded"
                    elif op == "finish":
                        if c["id"] == "finish-snapshot":
                            closing = asyncio.create_task(team.close())
                            await asyncio.sleep(0)
                            first = next(raw for raw, i in ids.items() if i == 1)
                            assert not team.tasks[first].cancelling()
                            assert not closing.done()
                            event(snap_release, 1).set()
                            await closing
                        else:
                            await team.close()
                        replies[label] = "finished"
                    elif op == "gate":
                        if label == "overlap":
                            await both.wait()
                        elif label == "release-first":
                            event(provider_release, 1).set()
                        elif label == "release-peer":
                            event(provider_release, 2).set()
                        elif label == "release-all":
                            event(provider_release, 1).set()
                            event(provider_release, 2).set()
                        elif label == "release-snapshot":
                            event(snap_release, 1).set()
                        elif label.startswith("provider"):
                            await event(entered, int(label[8:])).wait()
                        elif label.startswith("snapshot"):
                            await event(snap_entered, int(label[8:])).wait()
                        elif label.startswith("release"):
                            event(snap_release, int(label[7:])).set()
                        elif label == "checks1":
                            await check_entered.wait()
                except ValueError as exc:
                    replies[label] = reject(exc)
            await team.close()  # Idempotent task/agent cleanup; external Git release is out of scope.
        assert active == 0
        assert all(a.closed == 1 for a in agents.values())
        assert max_active == 2
        return {"id": c["id"], "replies": replies, "effects": effects,
                "maxActive": max_active, "freshContexts": [a.thread_id for a in agents.values()],
                "records": [{"id": ids[a.id], "status": a.status, "error": a.error,
                             "cleanup_errors": a.cleanup_errors, "discarded": a.discarded}
                            for a in team.attempts.values()]}


def oracle_case(c):
    def q(op, label, target=0, **extra):
        return {"op": op, "label": label, "target": target, "previous": 0,
                "mode": "all", "limit": 5000, **extra}
    script = [q("spawn", "a"), q("spawn", "b"), q("gate", "overlap")]
    mode = c["mode"]
    if c.get("global"):
        script += [q("global-cancel", "global"), q("inspect", "final")]
    else:
        if mode in ("any", "tagged-isolation", "tagged-expected"):
            script += [q("gate", "release-first"), q("wait", "observed", mode="any")]
        elif mode == "all":
            script += [q("gate", "release-all"), q("wait", "observed")]
        elif mode == "timeout":
            script += [q("wait", "observed", limit=c["limit"])]
        elif mode == "snapshot-timeout":
            script += [q("gate", "release-first"), q("gate", "snapshot1"), q("wait", "observed", limit=c["limit"])]
        elif mode in ("caller-cancel", "race-cancel-first"):
            script += [q("caller-cancel", "cancel")]
            if mode == "race-cancel-first":
                script += [q("gate", "release-all"), q("wait", "observed")]
        elif mode == "race-complete-first":
            script += [q("gate", "release-all"), q("complete-then-cancel", "cancel")]
        elif mode == "race-published-cancel":
            script += [q("gate", "release-all"), q("published-then-cancel", "cancel")]
        script += [q("mark-caller", "caller"), q("inspect-capacity", "occupied")]
        if mode.startswith("tagged-"):
            script += [q("gate", "release-peer"), q("wait", "after-peer")]
        if mode == "snapshot-timeout":
            script += [q("gate", "release-snapshot")]
        script += [q("stop", "stop1", 1), q("stop", "stop2", 2), q("inspect", "final"), q("finish", "finish")]
    kinds = {"unexpected": "unexpected-fault", "expected": "expected-error"}
    return {**c, "providers": {str(i + 1): kinds.get(kind, kind) for i, kind in enumerate(c["providers"])}, "script": script}


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rrawrlet")
    parser.add_argument("bundle")
    args = parser.parse_args()
    repo, bundle = Path(args.rrawrlet).resolve(), Path(args.bundle).resolve()
    head = subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()
    assert head == "4bab9e030744a0887c1ee13bdd8fc8db3b98334f"
    sys.path.insert(0, str(repo))
    import rrawrlet.team as team_module
    cases = json.loads((bundle / "cases.json").read_text())
    virtual = {r["checks"]["id"]: r["checks"] for r in json.loads((bundle / "evidence.json").read_text())}
    runtime = {r["id"]: r["checks"] for r in json.loads((bundle / "runtime-evidence.json").read_text())["results"]}
    rows = []
    for c in cases:
        row = await asyncio.wait_for(run_case(oracle_case(c), team_module), 10)
        if c.get("fault"):
            assert virtual[c["id"]]["scopeFaultPreserved"]
            assert runtime[c["id"]]["scopeFaultPreserved"]
            if c["id"] == "foreign-cancellation":
                assert row["records"][0]["status"] == "stopped"
            else:
                assert row["records"][0]["status"] == "failed"
            if c["id"] == "cleanup-fault":
                assert row["records"][0]["cleanup_errors"]
            row["comparison"] = "intentional raw-fault control; Team isolates, graph propagates"
        else:
            for label in (["final"] if c.get("global") else ["caller", "observed", "occupied", "final"]):
                assert row["replies"][label] == virtual[c["id"]]["replies"][label], (c["id"], label, row["replies"], virtual[c["id"]])
                assert row["replies"][label] == runtime[c["id"]]["replies"][label], (c["id"], label)
            if not c.get("global"):
                assert row["replies"].get("observer-payload") == virtual[c["id"]]["replies"].get("observer-payload")
                assert row["replies"].get("observer-payload") == runtime[c["id"]]["replies"].get("observer-payload")
            if c["id"].startswith("tagged-"):
                for probe in (virtual[c["id"]], runtime[c["id"]]):
                    assert probe["replies"]["provider-evidence:1"].split("|", 1)[1] == row["records"][0]["error"]
                assert row["records"][1]["status"] == "completed", "peer must complete, not merely survive until stop"
                assert all(not r["discarded"] for r in row["records"])
            row["comparison"] = "matched normalized Team observations"
        rows.append(row)
        print(f"PASS: follow-up actual Team {c['id']}; {row['comparison']}")
    controls = []
    for case, label, wrong in [("caller-cancel", "caller", "caller:pending"),
                               ("caller-cancel", "observed", "1,stopped;2,stopped;"),
                               ("tagged-isolation", "final", "1,failed;2,stopped;"),
                               ("snapshot-timeout", "observed", "1,completed;2,running;"),
                               ("snapshot-timeout", "observer-payload", "1,completed;2,running;")]:
        correct = next(r["replies"][label] for r in rows if r["id"] == case)
        assert correct != wrong
        controls.append({"case": case, "label": label, "mutantRejected": True})
    evidence = {"oracleHead": head, "python": sys.version,
                "teamSha256": hashlib.sha256((repo / "rrawrlet/team.py").read_bytes()).hexdigest(),
                "methodsUnmodified": ["spawn", "wait", "inspect", "stop", "discard", "close", "execute", "run_member_checks"],
                "effectStubs": ["clean", "host_git", "git_host", "build_attempt", "check_submission"],
                "matchedCases": sum(r["comparison"] == "matched normalized Team observations" for r in rows),
                "rawFaultControls": sum("intentional" in r["comparison"] for r in rows),
                "results": rows, "negativeControls": controls}
    (bundle / "oracle-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    asyncio.run(main())
