#!/usr/bin/env bash
# Waits for a check that ci.yml started in the background, prints its log and exits with its status.
set -u
name=$1
while [ ! -f "$RUNNER_TEMP/$name.status" ]; do sleep 1; done
cat "$RUNNER_TEMP/$name.log"
exit "$(cat "$RUNNER_TEMP/$name.status")"
