# M4 capability and operation declaration authoring verification — 2026-09-15

**Status:** Observed result

This record captures deterministic verification for issue #161, the Capability/Operation stage of staged declaration authoring.

## Verified implementation

- Repository: `kw4r3n/nyamnyam-lang`
- Implementation issue: #161
- Implementation PR: #163
- Exact verified implementation head: `d5a000089a653e8db36a08c38cc09275a46d388b`
- Branch: `feat/161-capability-authoring`
- GitHub Actions CI: run #1166, run ID `34985024955`
- .NET SDK: `10.0.401`

## Result

The repository's normal fast CI path completed successfully at the exact implementation head:

```text
Release build:
  warnings: 0
  errors: 0

Fast suite:
  passed: 271
  failed: 0
  skipped: 0
```

The fast suite increased from 266 to 271 tests for this slice: four focused Core workflow tests and one maintained-session persistence check.

## Completion evidence

The focused tests establish that:

- complete capability intent with multiple operations and parameters allocates fresh system-owned `CapabilityId`, `CapabilityOperationId`, and `CapabilityParameterId` values;
- the exact reviewed capability publishes and remains discoverable while the prerequisite effect remains unchanged;
- blank capability/operation/parameter metadata, unknown effects, and invalid type references reject without publication;
- receipt binding and stale-base rejection preserve the established reviewed publication contract;
- the published IDs compose with existing atomic function creation and `ExpressionRecipe.CapabilityInvoke` while the function explicitly declares the operation effect;
- the maintained JSONL session can create the prerequisite effect, create and commit a capability, close/reopen, and rediscover the same capability/operation/parameter identities.

## Scope of this evidence

This slice completes the accepted Function -> Product/Union -> Effect -> Capability/Operation declaration-creation sequence. It does not register or execute runtime providers and does not change persistence, lowering, backend, or provider contracts.

No extended or live validation was run because #161 introduces a typed declaration-authoring adapter over established publication/persistence and existing capability-expression semantics; its completion criteria are covered by the focused fast suite.
