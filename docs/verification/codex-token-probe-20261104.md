# Codex token probe, 2026-11-04

A live `codex exec --json` check of the tool-definition trim (the `recipe_reference` kind enum becomes a string, and the review paging text repeated in five tools moves to `request` help topic `review`). The shared tool definitions shrank from 27,768 to 20,632 characters (description plus JSON schema).

- **Task.** In an empty folder: create Product `Order` and Union `Discount`, a function `total` over them, review and commit each, check three cases with `runPure`, close. The prompt was Japanese and identical in every run.
- **Setup.** `codex-cli 0.166.0`, model `gpt-6.1-sol`, `--ignore-user-config --ephemeral`, `workspace-write`, the MCP server from this repository with tool approval `approve`. "Before" ran a worktree of main `6aece10b`; "after" ran the trimmed branch.
- **Tokens** are the summed `turn.completed` usage (cached input is part of input).

| Run | Tool calls | Input | Cached | Output | Errors |
| --- | ---: | ---: | ---: | ---: | ---: |
| before-1 | 14 | 307,039 | 269,056 | 1,314 | 1 |
| before-2 | 15 | 438,676 | 382,720 | 1,377 | 1 |
| after-1 | 15 | 358,303 | 281,472 | 1,247 | 1 |
| after-2 | 15 | 443,493 | 396,800 | 1,464 | 1 |
| after-3 (help fix) | 16 | 251,908 | 208,128 | 1,177 | 0 |

**Tokens.** The trim saves about 1,800 tokens per model request, but run-to-run variance (307k to 443k for the same code) is larger than that, so two runs per side show no measurable difference. Comparing a change of this size needs many more runs, or a count of model requests per run.

**Friction found.** All four runs first sent `runPureByName` arguments as `[{parameter, value}]` and were rejected (`Run values are typed objects with a kind ...`), although each had read `request` help topic `runPure` first: that topic describes `{parameter, value}`-style case arguments for fake capabilities. The topic now opens with positional arguments and a plain-JSON example; the one run after that change made no error. One run is not enough to call it fixed.

Every run finished the task, and all three `runPure` checks matched.

**Follow-up: repeatable runs.** The probe is now `evaluation/agent-eval/run.mjs`: it runs every task in `evaluation/agent-eval/tasks/` N times with `codex exec`, then reopens each committed snapshot read-only and runs the task's checks itself (one check the prompt does not mention), so success does not rest on the agent's own report. It reports pass rate, tool calls, rejected calls by error code, and median input tokens. The `{parameter, value}` rejection now names the function's parameter order and shows the plain-JSON form. Three runs after both changes: 3/3 passed, 15.0 tool calls on average, no rejected calls, median input 372,815 tokens.
