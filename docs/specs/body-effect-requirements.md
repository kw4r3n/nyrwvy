# Body effect requirements

**Status:** Current

`RequiredEffects` is a function's explicit public contract. A focus projection's
`bodyEffectRequirements` is derived information and never edits that contract.
It is absent from snapshots, fingerprints, and receipts' authoritative content.

The report scans every node, including nested branches and matches. It collects
the declared contracts of direct callees and List/resource/parallel callbacks,
and the effect of each capability invocation and resource release. A callee's
public contract is used even when its implementation needs fewer effects. No
recursive inference, path feasibility, or automatic contract minimization occurs.
Unused stored nodes are included, matching snapshot validation's scope.

The deterministic report contains:

- `effectIds`: distinct required EffectIds;
- `evidence`: each EffectId, requiring node ID, and a source identifying either a
  `functionContract` with FunctionId or a `capabilityOperation` with operation ID;
- `unresolvedFunctionIds` and `unresolvedOperationIds`: unavailable contracts in
  an incomplete/invalid graph. An empty resolved effect set is not proof of purity
  if either unresolved list is nonempty.

`CancellationRequested` has ordering semantics but no nominal EffectId, so it does
not invent a nominal effect in this report. Existing validation still decides
validity and requires derived nominal effects to be covered by the declaration.

`focusFunction`, structured `readFunction`/ordinary `nyrwvy_inspect`, and focus
payloads in change review expose the report. Creation and whole-body replacement
reviews include the resulting body's report; targeted replacement includes base
and proposed focus. Parameter/result/Product-field type migration reviews include
`functionEffects` for every rewritten function, pairing its declared effects with
the resulting body's report. HTML focus and change review show declarations and
derived evidence separately. The usual simulation/review/commit workflow remains
the only way to change the declared contract.
