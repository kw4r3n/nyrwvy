namespace NyamNyam.Core.Tests

open System.Reflection
open Microsoft.FSharp.Reflection
open NyamNyam.Core
open Xunit

module ExpressionAuthoritySurfaceTests =
    [<Fact>]
    let ``expression authority surface rejects nested raw graph payloads`` () =
        Assert.Equal("ExpressionRecipe", typeof<ExpressionRecipe>.Name)
        Assert.Equal(typeof<ExpressionRecipe>, typeof<PureExpressionRecipe>)
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

        let rec containsForbidden visited (candidate: System.Type) =
            if forbiddenPayloadTypes |> List.contains candidate then
                true
            elif visited |> List.contains candidate then
                false
            else
                let visited = candidate :: visited

                if candidate.IsArray then
                    containsForbidden visited (candidate.GetElementType())
                elif FSharpType.IsTuple candidate then
                    FSharpType.GetTupleElements candidate
                    |> Array.exists (containsForbidden visited)
                elif candidate.Assembly = coreAssembly && FSharpType.IsRecord(candidate, bindingFlags) then
                    FSharpType.GetRecordFields(candidate, bindingFlags)
                    |> Array.exists (fun field -> containsForbidden visited field.PropertyType)
                elif candidate.Assembly = coreAssembly && FSharpType.IsUnion(candidate, bindingFlags) then
                    FSharpType.GetUnionCases(candidate, bindingFlags)
                    |> Array.collect (fun case -> case.GetFields())
                    |> Array.exists (fun field -> containsForbidden visited field.PropertyType)
                elif candidate.IsGenericType then
                    candidate.GetGenericArguments()
                    |> Array.exists (containsForbidden visited)
                else
                    false

        let leakedPayloads =
            FSharpType.GetUnionCases(typeof<ExpressionRecipe>)
            |> Array.collect (fun case ->
                case.GetFields()
                |> Array.choose (fun field ->
                    if containsForbidden [] field.PropertyType then
                        Some(case.Name + "." + field.Name, field.PropertyType)
                    else
                        None))
            |> Array.toList

        if not (List.isEmpty leakedPayloads) then
            failwithf "Expression recipe exposes nested raw graph authority: %A" leakedPayloads
