// Build first: dotnet build src/Nyrwvy.Semantics/Nyrwvy.Semantics.fsproj -c Release
// Run: dotnet fsi scripts/generate-recipe-signatures.fsx
// Writes the recipe reference signatures of the fixed-signature operations from the F# table.
#r "../src/Nyrwvy.Semantics/bin/Release/net10.0/Nyrwvy.Semantics.dll"

open System.IO
open Nyrwvy.Core

let path = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "tools", "nyrwvy-agent", "recipe-signatures.generated.mjs"))
File.WriteAllText(path, OperationSignatures.referenceModule ())
printfn $"Wrote {List.length OperationSignatures.table - OperationSignatures.retired.Count} signatures to {path}"
let zigPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "tools", "zig", "host", "recipe_signatures.txt"))
File.WriteAllText(zigPath, OperationSignatures.zigTable ())
printfn $"Wrote {List.length OperationSignatures.table} signatures to {zigPath}"
