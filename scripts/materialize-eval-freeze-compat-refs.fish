#!/usr/bin/env fish

# Historical frozen evaluation operators were committed before freeze refs moved
# from remote branches to immutable tags. They intentionally remain unchanged,
# so exact replay can materialize the old refs/remotes/origin/eval/... names
# locally from the corresponding tags without recreating remote branches.

set -l repo_root (git rev-parse --show-toplevel 2>/dev/null)
if test -z "$repo_root"
    echo "ERROR: run from the nyamnyam-lang repository." >&2
    exit 1
end
cd "$repo_root"
or exit 1

set -l freeze_names \
    eval/m1-agent-corpus-v1-20260829 \
    eval/m2-agent-corpus-v1-20260830 \
    eval/m2-structural-condition-v1-20260830 \
    eval/m2-structural-condition-v1.1-20260830 \
    eval/m2-listmap-agent-corpus-v1-20260831 \
    eval/m2-listmap-condition-v1-20260831 \
    eval/m2-listmap-orchestrator-v1-20260831

set -l freeze_shas \
    a40e839b46cee90c28858b5b80cd05ecf2aa462d \
    efce0cac15565941c11c8af8d1a52e7ecafaa5c6 \
    6cbf13695fa90b1311ae7ece7da403f7cbeafe05 \
    178f38274890a28ae61a1c70c9d598bc01492256 \
    a2ffb5615d37d44bbd910c4f47c36136c31cdce4 \
    23bf09d87c03b9c15dd4640b662a05b24aa5e330 \
    f7d6d07cd47367dde2272386b6e430fddef6d03a

if test (count $freeze_names) -ne (count $freeze_shas)
    echo "ERROR: internal freeze-ref table is inconsistent." >&2
    exit 1
end

git fetch origin --tags
or begin
    echo "ERROR: could not fetch freeze tags from origin." >&2
    exit 1
end

for index in (seq (count $freeze_names))
    set -l name $freeze_names[$index]
    set -l expected $freeze_shas[$index]
    set -l actual (git rev-parse "refs/tags/$name^{commit}" 2>/dev/null)

    if test -z "$actual"
        echo "ERROR: required freeze tag $name is unavailable." >&2
        exit 1
    end

    if test "$actual" != "$expected"
        echo "ERROR: freeze tag $name moved: expected $expected, got $actual." >&2
        exit 1
    end

    git update-ref "refs/remotes/origin/$name" "$expected"
    or begin
        echo "ERROR: could not materialize compatibility ref origin/$name." >&2
        exit 1
    end
end

echo "Materialized "(count $freeze_names)" local evaluation freeze compatibility refs from immutable tags."
echo "These refs are local-only and may be removed by a later git fetch --prune; rerun this helper before replaying a historical frozen operator if needed."
