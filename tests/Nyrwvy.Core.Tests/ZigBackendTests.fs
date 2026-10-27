namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The Zig backend runs through runPure with `"backend": "zig"` and agrees with the C# backend:
/// Int64, Bool and Unit, direct calls and tail self calls; Strings, Option, Result, Products and
/// Unions. Needs `zig` (mise.toml).
module ZigBackendTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e
    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private boolType = """{"kind":"primitive","primitive":"bool"}"""
    let private stringType = """{"kind":"primitive","primitive":"string"}"""
    let private execute session (request: string) =
        use doc = JsonDocument.Parse(AuthoringProtocol.executeLine session request)
        doc.RootElement.Clone()
    let private create session name (parameters: (string * string) list) result body =
        let parameters = parameters |> List.map (fun (name, kind) -> $"""{{"displayName":"{name}","type":{kind}}}""") |> String.concat ","
        let proposal = execute session ($"""{{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":[],"body":{body}}}""")
        Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
        let proposalId = proposal.GetProperty("result").GetProperty("proposalId").GetString()
        let committed = execute session ($"""{{"id":"m","op":"commit","proposalId":"{proposalId}"}}""")
        Assert.True(committed.GetProperty("ok").GetBoolean(), committed.ToString())
    let private createType session name (typeKind: string) =
        let proposal = execute session ($"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"{name}","typeKind":{typeKind}}}""")
        Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
        let proposalId = proposal.GetProperty("result").GetProperty("proposalId").GetString()
        let committed = execute session ($"""{{"id":"m","op":"commit","proposalId":"{proposalId}"}}""")
        Assert.True(committed.GetProperty("ok").GetBoolean(), committed.ToString())
    let private run session backend name (arguments: string list) =
        execute session ($"""{{"id":"r","op":"runPureByName","function":"{name}","backend":"{backend}","arguments":[{String.Join(",", arguments)}]}}""")
    let private agree session name (arguments: string list) =
        let csharp = run session "csharp" name arguments
        let zig = run session "zig" name arguments
        Assert.True(csharp.GetProperty("ok").GetBoolean(), csharp.ToString())
        Assert.True(zig.GetProperty("ok").GetBoolean(), zig.ToString())
        Assert.Equal(csharp.GetProperty("result").GetProperty("actual").ToString(), zig.GetProperty("result").GetProperty("actual").ToString())
    let private int64 (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private p name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private binary kind = $"""{{"kind":"{kind}","left":{p "a"},"right":{p "b"}}}"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs Int64 and Bool functions and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let ab = [ "a", int64Type; "b", int64Type ]
            let operations =
                [ "int64WrappingAdd"; "int64WrappingSubtract"; "int64WrappingMultiply"; "int64FloorDivide"; "int64FloorModulo"
                  "int64Min"; "int64Max"; "int64BitAnd"; "int64BitOr"; "int64BitXor" ]
            for kind in operations do create session kind ab int64Type (binary kind)
            for kind in [ "int64ShiftLeft"; "int64ShiftRight" ] do
                create session kind ab int64Type ($"""{{"kind":"{kind}","value":{p "a"},"count":{p "b"}}}""")
            create session "less" ab boolType (binary "int64LessThan")
            create session "absNot" [ "a", int64Type ] int64Type ($"""{{"kind":"int64BitNot","value":{{"kind":"int64WrappingAbs","value":{p "a"}}}}}""")
            // A tail self call: sum(n, acc) = n < 1 ? acc : sum(n - 1, acc + n), a loop on both backends.
            let sumBody =
                $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{{"kind":"int64Literal","value":1}}}},"whenTrue":{p "acc"},"whenFalse":{{"kind":"directCall","function":"sum","arguments":[{{"parameter":"n","value":{{"kind":"int64WrappingSubtract","left":{p "n"},"right":{{"kind":"int64Literal","value":1}}}}}},{{"parameter":"acc","value":{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{p "n"}}}}}]}}}}"""
            create session "sum" [ "n", int64Type; "acc", int64Type ] int64Type sumBody
            create session "sumTo" [ "n", int64Type ] int64Type ($"""{{"kind":"directCall","function":"sum","arguments":[{{"parameter":"n","value":{p "n"}}},{{"parameter":"acc","value":{{"kind":"int64Literal","value":0}}}}]}}""")
            let listType = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{int64Type}]}}"""
            create session "range" [ "n", int64Type ] listType ($"""{{"kind":"listRange","start":{{"kind":"int64Literal","value":0}},"end":{p "n"}}}""")
            let edges = [ Int64.MinValue; -7L; -1L; 0L; 1L; 3L; 64L; Int64.MaxValue ]
            let cases =
                [ for kind in operations @ [ "int64ShiftLeft"; "int64ShiftRight"; "less" ] do
                    for a in edges do
                        for b in edges -> kind, [ int64 a; int64 b ] ]
                @ [ for a in edges -> "absNot", [ int64 a ] ]
                @ [ "sumTo", [ int64 1000000L ]; "sumTo", [ int64 0L ] ]
            for name, arguments in cases do
                let csharp = run session "csharp" name arguments
                let zig = run session "zig" name arguments
                Assert.True(csharp.GetProperty("ok").GetBoolean(), csharp.ToString())
                Assert.True(zig.GetProperty("ok").GetBoolean(), zig.ToString())
                Assert.Equal(csharp.GetProperty("result").GetProperty("actual").ToString(), zig.GetProperty("result").GetProperty("actual").ToString())
            Assert.Equal("500000500000", (run session "zig" "sumTo" [ int64 1000000L ]).GetProperty("result").GetProperty("actual").GetProperty("value").ToString())
            let unsupported = run session "zig" "range" [ int64 3L ]
            Assert.False(unsupported.GetProperty("ok").GetBoolean())
            Assert.Equal("RunBackendUnsupported", unsupported.GetProperty("error").GetProperty("code").GetString())
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs Strings, Options, Results, Products and Unions and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let field name kind = $"""{{"displayName":"{name}","type":{kind}}}"""
            createType session "Point" ($"""{{"kind":"product","fields":[{field "x" int64Type},{field "y" int64Type}]}}""")
            createType session "Shape" ($"""{{"kind":"union","variants":[{{"displayName":"Circle","fields":[{field "r" int64Type}]}},{{"displayName":"Rect","fields":[{field "w" int64Type},{field "h" int64Type}]}},{{"displayName":"Empty","fields":[]}}]}}""")
            let pointType = """{"kind":"named","name":"Point"}"""
            let shapeType = """{"kind":"named","name":"Shape"}"""
            let optionInt = $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{int64Type}]}}"""
            let resultIntText = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{int64Type},{stringType}]}}"""
            let slot name = $"""{{"kind":"patternReference","scopeDepth":0,"slot":"{name}"}}"""
            let shapeField variant name = $"""{{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"{variant}","field":"{name}"}}"""
            let mul a b = $"""{{"kind":"int64WrappingMultiply","left":{a},"right":{b}}}"""
            let three = """{"kind":"int64Literal","value":3}"""
            create session "area" [ "s", shapeType ] int64Type
                ($"""{{"kind":"matchUnion","type":"Shape","input":{p "s"},"arms":[{{"variant":"Circle","body":{mul (mul (shapeField "Circle" "r") (shapeField "Circle" "r")) three}}},{{"variant":"Rect","body":{mul (shapeField "Rect" "w") (shapeField "Rect" "h")}}},{{"variant":"Empty","body":{{"kind":"int64Literal","value":0}}}}]}}""")
            create session "rect" [ "w", int64Type; "h", int64Type ] shapeType
                ($"""{{"kind":"unionConstruct","type":"Shape","variant":"Rect","fields":[{{"field":"w","value":{p "w"}}},{{"field":"h","value":{p "h"}}}]}}""")
            create session "shift" [ "pt", pointType; "dx", int64Type ] pointType
                ($"""{{"kind":"productUpdate","source":{p "pt"},"type":"Point","fields":[{{"field":"x","value":{{"kind":"int64WrappingAdd","left":{{"kind":"fieldAccess","source":{p "pt"},"type":"Point","field":"x"}},"right":{p "dx"}}}}}]}}""")
            create session "origin" [] pointType """{"kind":"productConstruct","type":"Point","fields":[{"field":"x","value":{"kind":"int64Literal","value":0}},{"field":"y","value":{"kind":"int64Literal","value":0}}]}"""
            create session "same" [ "a", pointType; "b", pointType ] boolType (binary "valueEqual")
            create session "greet" [ "name", stringType ] stringType ($"""{{"kind":"stringConcat","left":{{"kind":"stringLiteral","value":"héllo, "}},"right":{p "name"}}}""")
            create session "size" [ "name", stringType ] int64Type ($"""{{"kind":"stringLength","value":{p "name"}}}""")
            create session "describe" [ "n", int64Type ] stringType ($"""{{"kind":"int64ToString","value":{p "n"}}}""")
            create session "positive" [ "n", int64Type ] optionInt
                ($"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{{"kind":"int64Literal","value":0}}}},"whenTrue":{{"kind":"optionNone","itemType":{int64Type}}},"whenFalse":{{"kind":"optionSome","value":{p "n"}}}}}""")
            create session "orZero" [ "o", optionInt ] int64Type
                ($"""{{"kind":"matchOption","input":{p "o"},"someBody":{slot "value"},"noneBody":{{"kind":"int64Literal","value":0}}}}""")
            create session "checked" [ "n", int64Type ] resultIntText
                ($"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{{"kind":"int64Literal","value":0}}}},"whenTrue":{{"kind":"resultErr","okType":{int64Type},"error":{{"kind":"stringLiteral","value":"negative ✗"}}}},"whenFalse":{{"kind":"resultOk","errorType":{stringType},"value":{p "n"}}}}}""")
            create session "settle" [ "r", resultIntText ] stringType
                ($"""{{"kind":"matchResult","input":{p "r"},"okBody":{{"kind":"int64ToString","value":{slot "value"}}},"errBody":{slot "error"}}}""")
            for shape in [ """{"Circle":{"r":2}}"""; """{"Rect":{"w":3,"h":4}}"""; """{"Empty":{}}""" ] do agree session "area" [ shape ]
            agree session "rect" [ "5"; "6" ]
            agree session "shift" [ """{"x":1,"y":2}"""; "10" ]
            agree session "origin" []
            agree session "same" [ """{"x":1,"y":2}"""; """{"x":1,"y":2}""" ]
            agree session "same" [ """{"x":1,"y":2}"""; """{"x":1,"y":3}""" ]
            for name in [ "\"\""; "\"wörld 😀\""; "\"a\\\"b\\nc\"" ] do
                agree session "greet" [ name ]
                agree session "size" [ name ]
            for n in [ "-9223372036854775808"; "-1"; "0"; "42" ] do
                agree session "describe" [ n ]
                agree session "positive" [ n ]
                agree session "checked" [ n ]
            for o in [ "null"; "7" ] do agree session "orZero" [ o ]
        finally Directory.Delete(dir, true)
