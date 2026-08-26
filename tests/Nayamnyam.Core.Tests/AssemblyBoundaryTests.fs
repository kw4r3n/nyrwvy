namespace Nayamnyam.Core.Tests

open System.Reflection
open Nayamnyam.Core
open Xunit

module AssemblyBoundaryTests =
    let private assemblyName (assembly: Assembly) = assembly.GetName().Name

    let private references (assembly: Assembly) =
        assembly.GetReferencedAssemblies()
        |> Array.map (fun reference -> reference.Name)
        |> Set.ofArray

    let private requireReference expected assembly =
        let actual = references assembly
        if not (Set.contains expected actual) then
            failwithf "Assembly '%s' does not reference required assembly '%s'. References: %A" (assemblyName assembly) expected actual

    let private forbidReferences forbidden assembly =
        let actual = references assembly
        let violations = Set.intersect actual (Set.ofList forbidden)
        if not violations.IsEmpty then
            failwithf "Assembly '%s' has forbidden architecture references: %A" (assemblyName assembly) violations

    [<Fact>]
    let ``architecture layers compile into distinct assemblies`` () =
        let semanticAssembly = typeof<Snapshot>.Assembly
        let executionAssembly = typeof<TypedSemanticProgram>.Assembly
        let backendAssembly = typeof<CSharpArtifact>.Assembly
        let viewerAssembly = typeof<ViewerArtifact>.Assembly

        let actual =
            [ assemblyName semanticAssembly
              assemblyName executionAssembly
              assemblyName backendAssembly
              assemblyName viewerAssembly ]

        let expected =
            [ "Nayamnyam.Semantics"
              "Nayamnyam.Execution"
              "Nayamnyam.Backend.CSharp"
              "Nayamnyam.Viewer" ]

        if actual <> expected then
            failwithf "Unexpected architecture assemblies. Expected %A, got %A" expected actual

    [<Fact>]
    let ``semantics does not depend on downstream assemblies`` () =
        typeof<Snapshot>.Assembly
        |> forbidReferences
            [ "Nayamnyam.Execution"
              "Nayamnyam.Backend.CSharp"
              "Nayamnyam.Viewer" ]

    [<Fact>]
    let ``execution depends on semantics but not backend or viewer`` () =
        let assembly = typeof<ExecutionProgram>.Assembly
        requireReference "Nayamnyam.Semantics" assembly
        forbidReferences [ "Nayamnyam.Backend.CSharp"; "Nayamnyam.Viewer" ] assembly

    [<Fact>]
    let ``C# backend depends on execution without depending on viewer`` () =
        let assembly = typeof<CSharpArtifact>.Assembly
        requireReference "Nayamnyam.Execution" assembly
        forbidReferences [ "Nayamnyam.Viewer" ] assembly

    [<Fact>]
    let ``viewer depends on semantics but not execution or backend`` () =
        let assembly = typeof<ViewerArtifact>.Assembly
        requireReference "Nayamnyam.Semantics" assembly
        forbidReferences [ "Nayamnyam.Execution"; "Nayamnyam.Backend.CSharp" ] assembly
