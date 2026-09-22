# Expression recipe reference live probe — 2026-09-22

**Status:** Completed environment-dependent validation.

## Result

**Primary success.** A fresh external project was authored through the ordinary Codex CLI + local stdio MCP surface after the `nyrwvy_recipe_reference` change at `f95027b6`.

The prompt described a small pure policy program but did not name any expression-recipe kinds or the recipe-reference tool. The model:

1. created a new project-relative snapshot;
2. independently called `nyrwvy_recipe_reference`;
3. read the compact index and five needed recipe shapes;
4. created three types;
5. submitted the nested `decide` function recipe successfully on its first function-authoring attempt;
6. reviewed and explicitly committed all four proposals;
7. closed and reopened the same snapshot;
8. inspected status, declarations, and the committed function.

No Nyrwvy repository documentation or implementation source was consulted, and no snapshot/generated-source direct read or edit was used.

## Task

The external project implemented:

```text
Priority = Normal | Urgent
Request = { enabled: Bool, priority: Priority }
Decision = Skip | Run | Escalate

decide(request):
  enabled = false -> Skip
  enabled = true, priority = Normal -> Run
  enabled = true, priority = Urgent -> Escalate
```

The function therefore exercised the same discovery-sensitive shapes that had caused the earlier external-consumer friction: parameter reference, product field access, conditional, exhaustive Union match, and Union construction.

## Recipe discovery

The model first called the reference with no `kind` and then requested:

- `parameter`
- `fieldAccess`
- `unionConstruct`
- `conditional`
- `matchUnion`

All six reference calls succeeded.

The subsequent `nyrwvy_create_function` call used the returned shapes directly and succeeded on the first function-recipe proposal. There was no unsupported `help` request and no malformed recipe retry.

## Comparison with the motivating consumer result

| Observation | Before #198 | 2026-09-22 follow-up |
| --- | ---: | ---: |
| Unsupported help/discovery request | 1 | 0 |
| Function-recipe ProtocolError | 10 | 0 |
| Repository docs/source needed | No in the independent consumer, but wire shapes were discovered by errors | No |
| On-demand recipe reference used | Not available | 6 successful calls |
| Successful function proposal attempts | After repeated shape errors | 1 |
| Semantic user intervention | None | None |

This is a bounded before/after interaction result, not a repeated-trial success-rate benchmark.

## Publication and persistence

Three type proposals and one function proposal were each returned with `semanticallyReady`, reviewed, and explicitly committed.

The final function had:

- `requiredEffects: []`;
- no callers or callees;
- no open obligations;
- the requested nested conditional / Union-match behavior.

After close/reopen, the persisted snapshot ID was:

```text
01a0c8d0-f8ad-79d3-bd94-5b340cfd6beb
```

The reopened status matched the final proposal/review fingerprints:

```text
snapshotFingerprint: sha256:68fcb7e201d51bca841145b55afcee3537883f49b6e33e3379360be073637574
semanticFingerprint: sha256:0bf285718c1e7d0bc04fe65a63dd8b43b91e96e9080c8136f20e7b872b05304c
```

## Errors and intervention

- recipe `ProtocolError`: **0**
- semantic errors: **0**
- MCP transport errors: **0**
- host approval errors: **0**
- mutation retries: **0**
- semantic user intervention: **none**
- stderr: empty

The model used only Nyrwvy MCP graph tools for authoring and verification.

## Conclusion

The measured recipe-discovery problem that motivated #198 is resolved for this bounded external-consumer task: the model discovered the on-demand reference itself and constructed the previously troublesome recipe shapes without trial-and-error.

Do not generalize this single run into a model reliability rate or rerun it routinely. Reuse this evidence until the ordinary-tool recipe/discovery interaction changes or another real workload exposes a new decision-relevant friction.
