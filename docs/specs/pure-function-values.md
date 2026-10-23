# Pure function values

**Status:** Current

The initial contract adds `Function(parameterTypes, resultType)`,
`FunctionReference(functionId)`, and `FunctionInvoke(functionValue, arguments)`.
Parameter order is part of a function type; parameter names and IDs are not.
Types match exactly, with the existing structural matching of function type
parameters also descending into function types. There is no subtyping, implicit
conversion, function equality, or effect-polymorphic function type.

A reference points to an existing, monomorphic, pure function by stable identity.
It has no capture fields and does not execute the referenced body. Pure means no
declared effects, capability operations, resource/parallel execution, or ambient
cancellation reads in its transitive call graph. Recursive pure functions are
allowed; purity does not promise termination. Referencing a generic function as a
value requires a concrete wrapper in this first slice. Generic functions can
receive, pass, return, and invoke function values whose types mention their own
type parameters.

Invocation checks positional arity and every argument type, and returns the
declared result type. It retains normal graph ordering and `Bind` semantics;
independent effectful input expressions still require explicit sequencing.
Function values can be forwarded through parameters, branches, and the existing
immutable containers. They never acquire captures or implicit authority.

For example, `find<T>(items: List<T>, predicate: Function([T], Bool)) -> Option<T>`
uses `ListFold` with an `Option<T>` seed. Its pure folder accepts the predicate
through the existing callback `context`, keeps the first `Some`, and invokes the
predicate only while the accumulator is `None`. Different named predicates are
passed as references; no dedicated find node or duplicated find body is needed.

Function references are declaration dependencies for deletion, inspection,
fingerprinting, dependency composition, and entry-point reachability. Editing a
referenced function's signature or purity must preserve all uses or reject the
candidate before publication. Function values have no observable allocation
identity and are not serialized host objects: saved graphs persist reference
nodes and types. `runPure` exercises them inside a concrete entry function; raw
function-valued runner inputs/outputs and capability boundary values are outside
the initial runner value protocol.

Both exact-ID and ordinary name-resolved authoring expose `functionReference` and
`functionInvoke`. A function TypeRef is `{ "kind": "function", "parameters":
[<TypeRef>, ...], "resultType": <TypeRef> }`. The recipe for a reference has
`functionId` (exact-ID) or `function` (name-resolved); invocation has `functionValue`
and an ordered `arguments` array of recipes. No capture or effect fields are
accepted. C# delegates are downstream representations, not the semantic source.

Function types at generated host interfaces retain the same contract: a host
implementation supplying a delegate asserts that it is pure and capture-free.
As with other external values, the compiler cannot prove host conformance. The
common runner does not accept or fabricate delegate values across this boundary.

Persistence is additive: existing graphs retain their canonical JSON bytes
and semantic fingerprints. New function types and nodes require a reader that
supports this slice; unknown fields, including capture fields, remain errors.

The maintained example in `FunctionValueTests.fs` creates `findStep<T>` and
`find<T>` with ordinary name-resolved recipes, commits and reopens the workspace,
then calls the same search body with String and Int64 predicates. The common
runner checks first-match, empty/missing input, and a reviewed predicate edit.
