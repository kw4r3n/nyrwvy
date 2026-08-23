# Shared scenario

Both implementations must model the same scenario:

1. A `validate_user` function has one `raw_input: String` parameter.
2. `main` calls it by parameter ID.
3. Rename the function while preserving its stable ID and call edge.
4. Add a `policy: String` parameter and create a blocking `missing_argument`
   obligation at the existing call site.
5. Rename the function to its current name and report `no_change` without a new
   snapshot.
6. Rename the `String` type to `Text` while preserving its stable ID.

The executable output is a canonical JSON summary of the baseline and these four
operations. F# and C# must emit exactly the same bytes.
