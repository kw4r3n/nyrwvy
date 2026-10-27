namespace Nyrwvy.Core

/// The table text, `tools/zig/snapshot-reader/record_kinds.txt` (shared with the Zig snapshot reader),
/// is GENERATED from the strict JSON writer's output on the structural all-kinds model; do not edit by
/// hand. `RecordKindsTests` (tests/Nyrwvy.Core.Tests/RecordKindsTests.fs) derives the table again and
/// fails with the regenerated text when it no longer matches the writer: paste that text in. (The
/// derivation came from the retired harness at `eval/snapshot-record-format-20261023`.) Grammar: one record
/// kind per line, `record-kind[=jsonKind]: id|kind|typeParameterId|jsonKey/record-key:shape[?][~]`, keys
/// in the writer's property order; `?` marks a key the writer sometimes omits, `~` a collection it
/// omits when empty (every other collection is written even when empty).
module internal RecordKinds =
    let text =
        use stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream "record_kinds.txt"
        use reader = new System.IO.StreamReader(stream)
        reader.ReadToEnd()

