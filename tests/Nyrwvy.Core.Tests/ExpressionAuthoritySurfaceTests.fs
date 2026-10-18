namespace Nyrwvy.Core.Tests

open System.Reflection
open Microsoft.FSharp.Reflection
open Nyrwvy.Core
open Xunit

module ExpressionAuthoritySurfaceTests =
    [<Fact>]
    let ``expression authority surface rejects nested raw graph payloads`` () =
        Assert.Equal("ExpressionRecipe", typeof<ExpressionRecipe>.Name)
        let requestFields =
            FSharpType.GetRecordFields(typeof<AgentReplaceFunctionBodyWithExpressionRecipe>)
            |> Array.map (fun field -> field.Name, field.PropertyType)
            |> Array.toList

        let expectedRequestFields =
            [ "TargetFunctionId", typeof<FunctionId>
              "ExpectedBaseSnapshotId", typeof<SnapshotId>
              "ExpectedCurrentResultNodeId", typeof<GraphNodeId>
              "Replacement", typeof<ExpressionRecipe> ]

        if requestFields <> expectedRequestFields then
            failwithf "Expression request authority surface changed: %A" requestFields

        let forbiddenPayloadTypes =
            [ typeof<GraphNodeId>
              typeof<Snapshot>
              typeof<ValueGraph>
              typeof<SemanticNode>
              typeof<SemanticNodeKind> ]
        let bindingFlags = BindingFlags.Public ||| BindingFlags.NonPublic
        let coreAssembly = typeof<ExpressionRecipe>.Assembly

        let children (candidate: System.Type) =
            if candidate.IsArray then
                [| candidate.GetElementType() |]
            elif FSharpType.IsTuple candidate then
                FSharpType.GetTupleElements candidate
            elif candidate.Assembly = coreAssembly && FSharpType.IsRecord(candidate, bindingFlags) then
                FSharpType.GetRecordFields(candidate, bindingFlags) |> Array.map (fun field -> field.PropertyType)
            elif candidate.Assembly = coreAssembly && FSharpType.IsUnion(candidate, bindingFlags) then
                FSharpType.GetUnionCases(candidate, bindingFlags)
                |> Array.collect (fun case -> case.GetFields())
                |> Array.map (fun field -> field.PropertyType)
            elif candidate.IsGenericType then
                candidate.GetGenericArguments()
            else
                [||]

        // Reflect each reachable type once, then mark every type that reaches a forbidden one.
        // Walking per path instead re-reflects shared types once per path, which grows with every
        // recipe case.
        let fieldTypes =
            FSharpType.GetUnionCases(typeof<ExpressionRecipe>)
            |> Array.collect (fun case -> case.GetFields())
            |> Array.map (fun field -> field.PropertyType)

        let edges = System.Collections.Generic.Dictionary<System.Type, System.Type array>()
        let pending = System.Collections.Generic.Stack<System.Type>(fieldTypes)

        while pending.Count > 0 do
            let candidate = pending.Pop()

            if not (edges.ContainsKey candidate) then
                let next =
                    if forbiddenPayloadTypes |> List.contains candidate then [||] else children candidate

                edges[candidate] <- next
                next |> Array.iter pending.Push

        let forbidden = System.Collections.Generic.HashSet<System.Type>(forbiddenPayloadTypes)
        let mutable changed = true

        while changed do
            changed <- false

            for KeyValue(candidate, next) in edges do
                if not (forbidden.Contains candidate) && next |> Array.exists forbidden.Contains then
                    forbidden.Add candidate |> ignore
                    changed <- true

        let containsForbidden (candidate: System.Type) = forbidden.Contains candidate

        let leakedPayloads =
            FSharpType.GetUnionCases(typeof<ExpressionRecipe>)
            |> Array.collect (fun case ->
                case.GetFields()
                |> Array.choose (fun field ->
                    if containsForbidden field.PropertyType then
                        Some(case.Name + "." + field.Name, field.PropertyType)
                    else
                        None))
            |> Array.toList

        if not (List.isEmpty leakedPayloads) then
            failwithf "Expression recipe exposes nested raw graph authority: %A" leakedPayloads
