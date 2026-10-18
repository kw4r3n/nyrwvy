# Generic Product and Union types

- **Status:** Accepted
- **Date:** 2026-10-18
- **Roadmap:** G2 of the [general-purpose programming roadmap](../plans/general-purpose-programming-roadmap.md) ("Product/Union with type parameters"); a warranted language feature under the [selection rule](../plans/post-m2-roadmap.md#selection-rule-from-here)

Functions could be generic, but declared types could not: "declared types stay monomorphic" ([expression authoring](../specs/expression-authoring.md), generic functions). A program that needed a `Pair`, a `Tree` or a result-like Union for several item types declared one copy per item type, and the per-type operations each copy needed. The owner chose on 2026-10-18 to add type parameters to Products and Unions, without constraints.

## Decision

- **Declarations.** A Product or Union may declare type parameters, in order (`SemanticType.TypeParameters`). Its field types may use them and no other type parameter (`TypeParameterNotDeclared`). Identities are system-owned, as for functions: creation requests name them (`"typeParameters": ["T"]` and `{"kind":"typeParameter","name":"T"}` in field types), and simulation gives each a fresh `TypeParameterId`. A type parameter belongs to exactly one function or type (`TypeParameterSharedAcrossFunctions`).
- **References.** `TypeRef.Named(typeId, arguments)` carries one type argument per declared type parameter (`NamedTypeArityMismatch`); a monomorphic type has none. Field types are read through the arguments: the field `value: T` of `Box<Int64>` has type Int64. Wire forms are `{"kind":"named","name":"Box","arguments":[...]}` and `{"kind":"named","typeId":...,"arguments":[...]}`; `arguments` is omitted for a monomorphic type.
- **Construction.** `productConstruct` and `unionConstruct` take optional `typeArguments`. Without them, the type arguments are derived by matching the declared field types against the field values, the same structural matching as a generic call. A type parameter that no field value determines, such as `T` in a fieldless `Nothing` variant, must be given, or the recipe fails with `PureExpressionTypeArgumentsRequired`. The persisted `ProductConstruct` and `UnionConstruct` nodes always store the resulting arguments, so the graph never depends on inference.
- **Field access, update and match.** `FieldAccess`, `ProductUpdate` and `MatchUnion` read the fields at the source's or input's type arguments; pattern bindings record the instantiated field types.
- **Equality.** A generic type is equatable at given arguments when its instantiated fields are: `Box<Int64>` is, `Box<Float64>` is not. A type parameter is still never equatable, so a generic function cannot compare values of its own type parameters.
- **Recursion is regular.** A field that refers back into its type's own recursive group passes plain type parameters as arguments (`Tree<T>` may hold `List<Tree<T>>`, but not `Tree<List<T>>`; `IrregularRecursiveType`). The set of instances a value can reach therefore stays finite, which equality, the task-type checks and `runPure` rely on. This matches the rule that a generic function has no polymorphic recursion.
- **No constraints.** There is no `T : equality` or numeric constraint; collapsing the per-type operations into constrained generics stays out of scope, as "generic numeric operator bags" are in the [language subset](../reference/language-subset.md#explicitly-outside-the-current-subset).
- **No type-parameter migrations.** Adding, removing or reordering an existing type's type parameters is not an operation; create a new type instead. A Product field type migration on a generic type may use the type's own parameters in the new field type.
- **Backend.** The C# backend emits a generic Product as a generic record and a generic Union as a generic abstract record whose variant records share its type parameters; construction, variant tests and field extraction name the instance (`new V<long>(...)`, `is V<long>`).
- **`runPure`.** A Product or Union run value of a generic type names its arguments in `typeArguments`; the runner generates one encoder, decoder and key writer per instance.

## Consequences

- Snapshot JSON 18 / Nyason `nayamnyam.snapshot.v14` hold the new constructs: a type with `typeParameters`, a named type with `arguments`, and a construct node with `typeArguments`. All three are omitted when empty, so a monomorphic snapshot keeps its version, bytes and fingerprint ([schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions)). The typed semantic IR and Execution IR write schema version 13 when a type has type parameters.
- `GenericTypeTests` covers declaration, derived and explicit type arguments, rejection of missing, conflicting and wrong-arity arguments, irregular recursion, equality, persistence in both codecs, lowering, the C# backend and `runPure` results.

## Revisit conditions

Revisit constraints when a program shows that taking an equality or ordering callback costs more than a constraint would; revisit type-parameter migrations when an existing generic type needs a new parameter in place.
