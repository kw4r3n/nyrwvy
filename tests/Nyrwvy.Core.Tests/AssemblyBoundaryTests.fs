namespace Nyrwvy.Core.Tests

open System.Reflection
open Nyrwvy.Core
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
            [ "Nyrwvy.Semantics"
              "Nyrwvy.Execution"
              "Nyrwvy.Backend.CSharp"
              "Nyrwvy.Viewer" ]

        if actual <> expected then
            failwithf "Unexpected architecture assemblies. Expected %A, got %A" expected actual

    [<Fact>]
    let ``semantics does not depend on downstream assemblies`` () =
        typeof<Snapshot>.Assembly
        |> forbidReferences
            [ "Nyrwvy.Execution"
              "Nyrwvy.Backend.CSharp"
              "Nyrwvy.Viewer"
              "Nyrwvy.AgentEval.Structural"
              "Nyrwvy.AgentEval.ListMapStructural" ]

    [<Fact>]
    let ``evaluation compatibility stays downstream while shared structural contracts stay in semantics`` () =
        let semantics = typeof<Snapshot>.Assembly
        Assert.Equal("Nyrwvy.Semantics", assemblyName typeof<FunctionBodyReplacementDiff>.Assembly)
        Assert.Equal("Nyrwvy.Semantics", assemblyName typeof<AgentStructuralWorkflowError>.Assembly)
        Assert.Equal("Nyrwvy.Semantics", assemblyName typeof<ExpressionRecipe>.Assembly)
        Assert.Equal("Nyrwvy.AgentEval.Structural", assemblyName typeof<Float64ExpressionRecipe>.Assembly)
        Assert.Equal("Nyrwvy.AgentEval.ListMapStructural", assemblyName typeof<AgentReplaceFunctionBodyWithListMap>.Assembly)
        Assert.Null(semantics.GetType("Nyrwvy.Core.AgentStructuralWorkflow"))
        Assert.Null(semantics.GetType("Nyrwvy.Core.AgentListMapStructuralWorkflow"))

    [<Fact>]
    let ``execution depends on semantics but not backend or viewer`` () =
        let assembly = typeof<ExecutionProgram>.Assembly
        requireReference "Nyrwvy.Semantics" assembly
        forbidReferences [ "Nyrwvy.Backend.CSharp"; "Nyrwvy.Viewer" ] assembly

    [<Fact>]
    let ``C# backend depends on execution without depending on viewer`` () =
        let assembly = typeof<CSharpArtifact>.Assembly
        requireReference "Nyrwvy.Execution" assembly
        forbidReferences [ "Nyrwvy.Viewer" ] assembly

    [<Fact>]
    let ``viewer depends on semantics but not execution or backend`` () =
        let assembly = typeof<ViewerArtifact>.Assembly
        requireReference "Nyrwvy.Semantics" assembly
        forbidReferences [ "Nyrwvy.Execution"; "Nyrwvy.Backend.CSharp" ] assembly
