# Build and suite review follow-ups (#212, #213)

**Status:** Observed result

Baseline: `ec7d682` after the runtime review repairs. SDK: 10.0.112, Linux x64.

## #213 — Reproducible build configuration

`global.json` selects SDK 10.0.112 with roll-forward disabled and no prereleases.
CI consumes that file. `Directory.Build.props` centralizes the F# target framework
and warnings-as-errors setting; C# consumers and project-local test/CLI flags are
not changed.

Compared evaluated Release properties before/after in all 13 projects:
TargetFramework, TreatWarningsAsErrors, OutputType, IsTestProject, IsPackable,
LangVersion, GenerateDocumentationFile and EnableDefaultCompileItems. All were
identical. Full solution build passed without warnings/errors. The maintained
fast fish entry point passed 369 xUnit and 28 Node cases.

Every tracked fish file passed `fish --no-execute`; the offline ListMap summary
script test passed. CI now runs those checks and the actual fast fish entry point
without separately duplicating its .NET/Node suites. Extended/live/preflight/
ref-materialization runners are syntax-checked, not executed by routine CI.

## #212 — Trait-based suite ownership

Extended test modules have `Trait("Suite", "Extended")`; the six mixed-feature
execution methods have the method-level trait. The two runsettings now select
only `Suite!=Extended` and `Suite=Extended`, independent of method/module names.

Captured VSTest discovery before/after migration and compared exact case sets:

| Suite | Cases | SHA-256 of sorted identities joined by LF |
| --- | ---: | --- |
| Fast | 369 | `58ebfff4e1c86639bc7036594908896ba2ff5ba185930e5c1cee522a0f2d61af` |
| Extended | 36 | `257042c216f29c784787fb8ae462860eb6fc8d14ca5d6e54754de6074b6b3298` |

Both inventories were identical, disjoint, and together exhaustive of unfiltered
solution discovery. These counts/hashes are evidence, not ongoing CI gates.
The opt-in [partition audit](../../scripts/check-test-suite-partition.mjs) checks
coverage and overlap from current discovery without a historical name inventory.
A rename cannot affect a filter that only reads the inherited/explicit trait.

Verification: fast 369/369 and extended 36/36 passed, including all six mixed
feature execution cases and all 30 extended-project cases. The unchanged fish
extended entry point was used. No live models were invoked.
