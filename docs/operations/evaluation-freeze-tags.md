# Evaluation freeze tags

**Status:** Procedure

Nyrwvy uses immutable Git tags as the canonical names for frozen evaluation revisions. Historical verification records may still describe the original remote branches because those records document how the freezes were created at the time.

Canonical freeze tags:

| Tag | Commit |
| --- | --- |
| `eval/m1-agent-corpus-v1-20260829` | `a40e839b46cee90c28858b5b80cd05ecf2aa462d` |
| `eval/m2-agent-corpus-v1-20260830` | `efce0cac15565941c11c8af8d1a52e7ecafaa5c6` |
| `eval/m2-structural-condition-v1-20260830` | `6cbf13695fa90b1311ae7ece7da403f7cbeafe05` |
| `eval/m2-structural-condition-v1.1-20260830` | `178f38274890a28ae61a1c70c9d598bc01492256` |
| `eval/m2-listmap-agent-corpus-v1-20260831` | `a2ffb5615d37d44bbd910c4f47c36136c31cdce4` |
| `eval/m2-listmap-condition-v1-20260831` | `23bf09d87c03b9c15dd4640b662a05b24aa5e330` |
| `eval/m2-listmap-orchestrator-v1-20260831` | `f7d6d07cd47367dde2272386b6e430fddef6d03a` |

## Historical operator replay

Some frozen operator commits predate this migration and intentionally resolve `refs/remotes/origin/eval/...` names. Those commits must not be rewritten just to change ref naming.

After fetching the canonical tags, run:

```fish
fish scripts/materialize-eval-freeze-compat-refs.fish
```

The helper verifies every tag against its fixed commit SHA and materializes matching local-only `refs/remotes/origin/eval/...` compatibility refs. This preserves exact replay without recreating remote branches.

A later `git fetch --prune` may remove those local compatibility refs because no corresponding remote branch exists. Rerun the helper before replaying a historical frozen operator when necessary.
