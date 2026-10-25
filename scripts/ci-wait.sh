#!/usr/bin/env bash
# Waits for a check that ci.yml started in the background, prints its log and exits with its status.
set -u
name=$1
iterations=0
while [ ! -f "$RUNNER_TEMP/$name.status" ]; do
    if [ "$iterations" -ge 600 ]; then
        echo "ci-wait: timed out waiting for $name" >&2
        if [ -f "$RUNNER_TEMP/$name.log" ]; then cat "$RUNNER_TEMP/$name.log"; fi
        exit 1
    fi
    sleep 1
    iterations=$((iterations + 1))
done
cat "$RUNNER_TEMP/$name.log"
exit "$(cat "$RUNNER_TEMP/$name.status")"
