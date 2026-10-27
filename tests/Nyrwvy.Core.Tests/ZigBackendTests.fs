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
            // Created last: a snapshot the backend cannot emit whole builds one runner per entry.
            create session "toBits" [ "n", int64Type ] """{"kind":"primitive","primitive":"bits32"}""" ($"""{{"kind":"bits32FromInt64","value":{p "n"}}}""")
            let unsupported = run session "zig" "toBits" [ int64 3L ]
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

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs Lists, list callbacks and generic functions and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let listOf item = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{item}]}}"""
            let ints = listOf int64Type
            let e = """{"kind":"typeParameter","name":"E"}"""
            let lit (n: int) = $"""{{"kind":"int64Literal","value":{n}}}"""
            create session "firstOr" [ "items", listOf e; "fallback", e ] e
                ($"""{{"kind":"matchOption","input":{{"kind":"listItemAt","list":{p "items"},"index":{lit 0}}},"someBody":{{"kind":"patternReference","scopeDepth":0,"slot":"value"}},"noneBody":{p "fallback"}}}""")
            create session "firstNum" [ "xs", ints ] int64Type ($"""{{"kind":"directCall","function":"firstOr","arguments":[{{"parameter":"items","value":{p "xs"}}},{{"parameter":"fallback","value":{lit -1}}}]}}""")
            create session "firstName" [ "names", listOf stringType ] stringType ($"""{{"kind":"directCall","function":"firstOr","arguments":[{{"parameter":"items","value":{p "names"}}},{{"parameter":"fallback","value":{{"kind":"stringLiteral","value":"none"}}}}]}}""")
            create session "add" [ "acc", int64Type; "item", int64Type ] int64Type ($"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{p "item"}}}""")
            create session "sum" [ "xs", ints ] int64Type ($"""{{"kind":"listFold","source":{p "xs"},"seed":{lit 0},"function":"add"}}""")
            create session "scale" [ "factor", int64Type; "x", int64Type ] int64Type ($"""{{"kind":"int64WrappingMultiply","left":{p "x"},"right":{p "factor"}}}""")
            create session "scaled" [ "xs", ints; "k", int64Type ] ints ($"""{{"kind":"listMap","source":{p "xs"},"function":"scale","context":[{{"parameter":"factor","value":{p "k"}}}]}}""")
            create session "isEven" [ "x", int64Type ] boolType ($"""{{"kind":"valueEqual","left":{{"kind":"int64FloorModulo","left":{p "x"},"right":{lit 2}}},"right":{lit 0}}}""")
            create session "evens" [ "xs", ints ] ints ($"""{{"kind":"listFilter","source":{p "xs"},"function":"isEven"}}""")
            create session "edit" [ "xs", ints ] ints
                ($"""{{"kind":"listReverse","value":{{"kind":"listAppendItem","list":{{"kind":"listInsertAt","list":{{"kind":"listRemoveAt","list":{{"kind":"listSetAt","list":{{"kind":"listSlice","list":{p "xs"},"start":{lit 1},"end":{lit 5}}},"index":{lit 1},"item":{lit 99}}},"index":{lit 0}}},"index":{lit 1},"item":{lit 7}}},"item":{lit 42}}}}}""")
            create session "upTo" [ "n", int64Type ] ints ($"""{{"kind":"listRange","start":{lit 0},"end":{p "n"}}}""")
            for xs in [ "[]"; "[5]"; "[1,2,3,4,5,6,-7]" ] do
                for name in [ "firstNum"; "sum"; "evens"; "edit" ] do agree session name [ xs ]
                agree session "scaled" [ xs; "3" ]
            for names in [ "[]"; "[\"ä\",\"b\"]" ] do agree session "firstName" [ names ]
            for n in [ "0"; "5" ] do agree session "upTo" [ n ]
            // An allocation the runtime cannot make is fatal on both backends.
            let huge = run session "zig" "upTo" [ "9223372036854775807" ]
            Assert.False(huge.GetProperty("ok").GetBoolean())
            Assert.Equal("fatal", huge.GetProperty("error").GetProperty("failureKind").GetString())
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs Float64, Maps and Sets and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let float = """{"kind":"primitive","primitive":"float64"}"""
            let generic name (arguments: string list) = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.Join(",", arguments)}]}}"""
            let xy = [ "a", float; "b", float ]
            for kind in [ "float64Add"; "float64Subtract"; "float64Multiply"; "float64Divide"; "float64Min"; "float64Max"; "float64FloorModulo" ] do
                create session kind xy float (binary kind)
            for kind in [ "float64Equal"; "float64LessThan" ] do create session kind xy boolType (binary kind)
            for kind in [ "float64Sqrt"; "float64Abs"; "float64Negate"; "float64Floor"; "float64Ceiling"; "float64Truncate" ] do
                create session kind [ "a", float ] float ($"""{{"kind":"{kind}","value":{p "a"}}}""")
            for kind in [ "float64IsNaN"; "float64IsFinite"; "float64IsInfinite" ] do
                create session kind [ "a", float ] boolType ($"""{{"kind":"{kind}","value":{p "a"}}}""")
            for kind in [ "float64RoundToInt64"; "float64FloorToInt64" ] do
                create session kind [ "a", float ] (generic "option" [ int64Type ]) ($"""{{"kind":"{kind}","value":{p "a"}}}""")
            create session "toFloat" [ "n", int64Type ] float ($"""{{"kind":"int64ToFloat64","value":{p "n"}}}""")
            let counts = generic "map" [ stringType; int64Type ]
            create session "bump" [ "acc", counts; "word", stringType ] counts
                ($"""{{"kind":"mapInsert","map":{p "acc"},"key":{p "word"},"value":{{"kind":"int64WrappingAdd","left":{{"kind":"mapGetOrDefault","map":{p "acc"},"key":{p "word"},"default":{{"kind":"int64Literal","value":0}}}},"right":{{"kind":"int64Literal","value":1}}}}}}""")
            create session "count" [ "words", generic "list" [ stringType ] ] counts
                ($"""{{"kind":"listFold","source":{p "words"},"seed":{{"kind":"mapEmpty","keyType":{stringType},"valueType":{int64Type}}},"function":"bump"}}""")
            create session "merge" [ "a", counts; "b", counts ] counts (binary "mapUnion")
            create session "dropKey" [ "m", counts; "k", stringType ] counts ($"""{{"kind":"mapRemove","map":{p "m"},"key":{p "k"}}}""")
            create session "keysOf" [ "m", counts ] (generic "list" [ stringType ]) ($"""{{"kind":"mapKeys","value":{p "m"}}}""")
            create session "sameMap" [ "a", counts; "b", counts ] boolType (binary "valueEqual")
            let ints = generic "set" [ int64Type ]
            create session "unique" [ "xs", generic "list" [ int64Type ] ] ints ($"""{{"kind":"setFromList","value":{p "xs"}}}""")
            for kind in [ "setUnion"; "setIntersection"; "setDifference" ] do create session kind [ "a", ints; "b", ints ] ints (binary kind)
            create session "subset" [ "a", ints; "b", ints ] boolType (binary "setIsSubset")
            create session "sameSet" [ "a", ints; "b", ints ] boolType (binary "valueEqual")
            let floats = [ "0.5"; "-2.5"; "3"; "\"-0\""; "1e308"; "\"NaN\""; "\"Infinity\""; "-7.25" ]
            for a in floats do
                for kind in [ "float64Sqrt"; "float64Abs"; "float64Negate"; "float64Floor"; "float64Ceiling"; "float64Truncate"; "float64IsNaN"; "float64IsFinite"; "float64IsInfinite"; "float64RoundToInt64"; "float64FloorToInt64" ] do
                    agree session kind [ a ]
                // Pairs over a smaller set keep the run short; every value still meets every operation.
                for b in [ "-2.5"; "\"-0\""; "\"NaN\""; "\"Infinity\""; "3" ] do
                    for kind in [ "float64Add"; "float64Subtract"; "float64Multiply"; "float64Divide"; "float64Min"; "float64Max"; "float64FloorModulo"; "float64Equal"; "float64LessThan" ] do
                        agree session kind [ a; b ]
            for n in [ "-9007199254740993"; "0"; "9223372036854775807" ] do agree session "toFloat" [ n ]
            agree session "count" [ """["a","b","a","c","b","a"]""" ]
            let m1 = """[["a",1],["b",2]]"""
            let m2 = """[["b",20],["c",3]]"""
            for a, b in [ m1, m2; m2, m1; m1, "[]" ] do
                agree session "merge" [ a; b ]
                agree session "sameMap" [ a; b ]
            agree session "sameMap" [ m1; """[["b",2],["a",1]]""" ]
            agree session "dropKey" [ m1; "\"a\"" ]
            agree session "keysOf" [ m2 ]
            agree session "unique" [ "[3,1,3,2,1]" ]
            for a, b in [ "[1,2,3]", "[3,4]"; "[]", "[1]"; "[2,1]", "[1,2,3]" ] do
                for kind in [ "setUnion"; "setIntersection"; "setDifference"; "subset"; "sameSet" ] do agree session kind [ a; b ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs Bytes and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let bytesType = """{"kind":"primitive","primitive":"bytes"}"""
            let from name = $"""{{"kind":"bytesFromLatin1","value":{p name}}}"""
            create session "fill" [ "n", int64Type; "v", int64Type ] bytesType ($"""{{"kind":"bytesFill","length":{p "n"},"value":{p "v"}}}""")
            create session "edit" [ "s", stringType; "i", int64Type; "v", int64Type ] stringType
                ($"""{{"kind":"bytesToLatin1","value":{{"kind":"bytesAppend","bytes":{{"kind":"bytesSet","bytes":{from "s"},"index":{p "i"},"value":{p "v"}}},"value":{p "v"}}}}}""")
            create session "at" [ "s", stringType; "i", int64Type ] (sprintf """{"kind":"builtinGeneric","generic":"option","arguments":[%s]}""" int64Type) ($"""{{"kind":"bytesGet","bytes":{from "s"},"index":{p "i"}}}""")
            create session "cut" [ "s", stringType; "i", int64Type; "j", int64Type ] bytesType ($"""{{"kind":"bytesSlice","bytes":{from "s"},"start":{p "i"},"end":{p "j"}}}""")
            create session "join" [ "s", stringType; "t", stringType ] bytesType ($"""{{"kind":"bytesConcat","left":{from "s"},"right":{from "t"}}}""")
            create session "size" [ "s", stringType ] int64Type ($"""{{"kind":"bytesLength","value":{from "s"}}}""")
            create session "sameBytes" [ "s", stringType; "t", stringType ] boolType ($"""{{"kind":"valueEqual","left":{from "s"},"right":{from "t"}}}""")
            for n, v in [ "-1", "5"; "0", "1"; "3", "-1"; "4", "256" ] do agree session "fill" [ n; v ]
            let texts = [ "\"\""; "\"abc\""; "\"\\u00ff\\u0000x\""; "\"\\u20ac\"" ]
            for s in texts do
                agree session "size" [ s ]
                for i in [ "-1"; "0"; "2"; "9" ] do
                    agree session "edit" [ s; i; "300" ]
                    agree session "at" [ s; i ]
                    agree session "cut" [ s; i; "2" ]
                for t in texts do
                    agree session "join" [ s; t ]
                    agree session "sameBytes" [ s; t ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs scripted fake capabilities and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let effect = execute session """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}"""
            let effectProposal = effect.GetProperty("result").GetProperty("proposalId").GetString()
            Assert.True((execute session ($"""{{"id":"m","op":"commit","proposalId":"{effectProposal}"}}""")).GetProperty("ok").GetBoolean())
            let optionText = $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{stringType}]}}"""
            let capability = execute session ($"""{{"id":"c","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Store","operations":[{{"displayName":"get","parameters":[{{"displayName":"key","type":{stringType}}},{{"displayName":"slot","type":{int64Type}}}],"resultType":{optionText},"effect":"io"}},{{"displayName":"count","parameters":[],"resultType":{int64Type},"effect":"io"}}]}}""")
            Assert.True(capability.GetProperty("ok").GetBoolean(), capability.ToString())
            let capabilityProposal = capability.GetProperty("result").GetProperty("proposalId").GetString()
            Assert.True((execute session ($"""{{"id":"m","op":"commit","proposalId":"{capabilityProposal}"}}""")).GetProperty("ok").GetBoolean())
            let storeType = """{"kind":"capability","name":"Store"}"""
            let invokeGet key = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "store"},"capability":"Store","operation":"get","arguments":[{{"parameter":"key","value":{key}}},{{"parameter":"slot","value":{{"kind":"int64Literal","value":1}}}}]}}"""
            let count = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "store"},"capability":"Store","operation":"count","arguments":[]}}"""
            let proposal = execute session ($"""{{"id":"f","op":"simulateCreateFunctionByName","exported":true,"displayName":"look","parameters":[{{"displayName":"store","type":{storeType}}},{{"displayName":"name","type":{stringType}}}],"resultType":{stringType},"requiredEffects":["io"],"body":{{"kind":"bind","value":{count},"body":{{"kind":"stringConcat","left":{{"kind":"int64ToString","value":{{"kind":"patternReference","scopeDepth":0,"slot":"value"}}}},"right":{{"kind":"matchOption","input":{invokeGet (p "name")},"someBody":{{"kind":"patternReference","scopeDepth":0,"slot":"value"}},"noneBody":{{"kind":"stringLiteral","value":"-"}}}}}}}}}}""")
            Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
            Assert.True((execute session ($"""{{"id":"m","op":"commit","proposalId":"{proposal.GetProperty("result").GetProperty("proposalId").GetString()}"}}""")).GetProperty("ok").GetBoolean())
            let ordered responses = $"""{{"kind":"fakeCapability","capability":"Store","operations":[{{"operation":"count","responses":[{{"kind":"return","value":{{"kind":"int64","value":"3"}}}}]}},{{"operation":"get","responses":[{responses}]}}]}}"""
            let some text = $"""{{"kind":"return","value":{{"kind":"option","itemKind":"string","hasValue":true,"value":"{text}"}}}}"""
            let keyed = $"""{{"kind":"fakeCapability","capability":"Store","operations":[{{"operation":"count","responses":[{{"kind":"return","value":{{"kind":"int64","value":"0"}}}}]}},{{"operation":"get","cases":[{{"arguments":[{{"parameter":"key","value":{{"kind":"string","value":"ä 😀"}}}},{{"parameter":"slot","value":{{"kind":"int64","value":"1"}}}}],"responses":[{some "found"}]}}]}}]}}"""
            let compare (arguments: string list) =
                let csharp = run session "csharp" "look" arguments
                let zig = run session "zig" "look" arguments
                Assert.True(csharp.GetProperty("ok").GetBoolean() = zig.GetProperty("ok").GetBoolean(), csharp.ToString() + "\n" + zig.ToString())
                if not (csharp.GetProperty("ok").GetBoolean()) && not (csharp.GetProperty("error").TryGetProperty("failureKind") |> fst) then failwith (csharp.ToString() + "\n" + zig.ToString())
                if csharp.GetProperty("ok").GetBoolean() then
                    Assert.Equal(csharp.GetProperty("result").GetProperty("actual").ToString(), zig.GetProperty("result").GetProperty("actual").ToString())
                    Assert.Equal(csharp.GetProperty("result").GetProperty("calls").ToString(), zig.GetProperty("result").GetProperty("calls").ToString())
                else
                    for property in [ "code"; "message"; "failureKind"; "exceptionType"; "calls" ] do
                        Assert.Equal(csharp.GetProperty("error").GetProperty(property).ToString(), zig.GetProperty("error").GetProperty(property).ToString())
            compare [ ordered (some "x"); "\"k\"" ]
            compare [ ordered """{"kind":"throw","message":"down ✗"}"""; "\"k\"" ]
            compare [ ordered ""; "\"k\"" ]
            compare [ keyed; "\"ä 😀\"" ]
            compare [ keyed; "\"other\"" ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs sorting, min and max by, and function values and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let ints = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{int64Type}]}}"""
            let optionInt = $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{int64Type}]}}"""
            let modulo name = $"""{{"kind":"int64FloorModulo","left":{p name},"right":{{"kind":"int64Literal","value":10}}}}"""
            // Orders by the last digit only, so equal keys show stability; `descending` comes from context.
            create session "before" [ "descending", boolType; "left", int64Type; "right", int64Type ] boolType
                ($"""{{"kind":"conditional","condition":{p "descending"},"whenTrue":{{"kind":"int64LessThan","left":{modulo "right"},"right":{modulo "left"}}},"whenFalse":{{"kind":"int64LessThan","left":{modulo "left"},"right":{modulo "right"}}}}}""")
            create session "sorted" [ "xs", ints; "down", boolType ] ints ($"""{{"kind":"listSortBy","source":{p "xs"},"function":"before","context":[{{"parameter":"descending","value":{p "down"}}}]}}""")
            create session "smallest" [ "xs", ints ] optionInt ($"""{{"kind":"listMinBy","source":{p "xs"},"function":"before","context":[{{"parameter":"descending","value":{{"kind":"boolLiteral","value":false}}}}]}}""")
            create session "largest" [ "xs", ints ] optionInt ($"""{{"kind":"listMaxBy","source":{p "xs"},"function":"before","context":[{{"parameter":"descending","value":{{"kind":"boolLiteral","value":false}}}}]}}""")
            let unary = $"""{{"kind":"function","parameters":[{int64Type}],"resultType":{int64Type}}}"""
            create session "twice" [ "x", int64Type ] int64Type ($"""{{"kind":"int64WrappingMultiply","left":{p "x"},"right":{{"kind":"int64Literal","value":2}}}}""")
            create session "apply" [ "f", unary; "x", int64Type ] int64Type ($"""{{"kind":"functionInvoke","functionValue":{p "f"},"arguments":[{{"kind":"functionInvoke","functionValue":{p "f"},"arguments":[{p "x"}]}}]}}""")
            create session "quadruple" [ "x", int64Type ] int64Type ($"""{{"kind":"directCall","function":"apply","arguments":[{{"parameter":"f","value":{{"kind":"functionReference","function":"twice"}}}},{{"parameter":"x","value":{p "x"}}}]}}""")
            for xs in [ "[]"; "[7]"; "[13,2,23,5,42,3,12,33]" ] do
                for down in [ "true"; "false" ] do agree session "sorted" [ xs; down ]
                agree session "smallest" [ xs ]
                agree session "largest" [ xs ]
            for x in [ "3"; "-9223372036854775808" ] do agree session "quadruple" [ x ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs exception boundaries and resources and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let commitResult (response: JsonElement) =
                Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
                let proposalId = response.GetProperty("result").GetProperty("proposalId").GetString()
                Assert.True((execute session ($"""{{"id":"m","op":"commit","proposalId":"{proposalId}"}}""")).GetProperty("ok").GetBoolean())
            commitResult (execute session """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}""")
            let unitType = """{"kind":"primitive","primitive":"unit"}"""
            let resultOf ok = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{ok},{stringType}]}}"""
            commitResult (execute session ($"""{{"id":"c","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Files","operations":[{{"displayName":"open","parameters":[],"resultType":{resultOf int64Type},"effect":"io"}},{{"displayName":"read","parameters":[{{"displayName":"handle","type":{int64Type}}}],"resultType":{resultOf stringType},"effect":"io"}},{{"displayName":"close","parameters":[{{"displayName":"handle","type":{int64Type}}}],"resultType":{resultOf unitType},"effect":"io"}}]}}"""))
            let filesType = """{"kind":"capability","name":"Files"}"""
            let exceptionType = """{"kind":"builtinGeneric","generic":"exception","arguments":[]}"""
            let effectful name (parameters: (string * string) list) result body =
                let parameters = parameters |> List.map (fun (name, kind) -> $"""{{"displayName":"{name}","type":{kind}}}""") |> String.concat ","
                commitResult (execute session ($"""{{"id":"f","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":["io"],"body":{body}}}"""))
            let invoke operation (arguments: string) = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "files"},"capability":"Files","operation":"{operation}","arguments":[{arguments}]}}"""
            effectful "readBody" [ "files", filesType; "handle", int64Type ] (resultOf stringType) (invoke "read" ($"""{{"parameter":"handle","value":{p "handle"}}}"""))
            effectful "withFile" [ "files", filesType ] (resultOf stringType)
                ($"""{{"kind":"useResource","acquire":{invoke "open" ""},"releaseCapability":{p "files"},"capability":"Files","releaseOperation":"close","body":"readBody","context":[{{"parameter":"files","value":{p "files"}}}]}}""")
            effectful "attempt" [ "files", filesType; "start", unitType ] (resultOf stringType) ($"""{{"kind":"directCall","function":"withFile","arguments":[{{"parameter":"files","value":{p "files"}}}]}}""")
            create session "recover" [ "failure", exceptionType ] (resultOf stringType) ($"""{{"kind":"resultErr","okType":{stringType},"error":{{"kind":"stringConcat","left":{{"kind":"stringLiteral","value":"caught: "}},"right":{{"kind":"exceptionMessage","value":{p "failure"}}}}}}}""")
            effectful "rethrowCancellation" [ "failure", exceptionType ] (resultOf stringType)
                ($"""{{"kind":"conditional","condition":{{"kind":"exceptionIsCancellation","value":{p "failure"}}},"whenTrue":{{"kind":"exceptionRethrow","value":{p "failure"},"resultType":{resultOf stringType}}},"whenFalse":{{"kind":"resultErr","okType":{stringType},"error":{{"kind":"exceptionMessage","value":{p "failure"}}}}}}}""")
            effectful "rethrowAll" [ "failure", exceptionType ] (resultOf stringType) ($"""{{"kind":"exceptionRethrow","value":{p "failure"},"resultType":{resultOf stringType}}}""")
            for handler in [ "recover"; "rethrowCancellation"; "rethrowAll" ] do
                effectful ("guarded_" + handler) [ "files", filesType ] (resultOf stringType)
                    ($"""{{"kind":"exceptionCatch","body":"attempt","bodyContext":[{{"parameter":"files","value":{p "files"}}}],"handler":"{handler}","handlerContext":[]}}""")
            let typed kind (value: string) = if kind = "unit" then """{"kind":"unit"}""" else $"""{{"kind":"{kind}","value":{value}}}"""
            let primitive kind = $"""{{"kind":"primitive","primitive":"{kind}"}}"""
            let ok kind (value: string) = $"""{{"kind":"return","value":{{"kind":"result","okType":{primitive kind},"errorType":{stringType},"isOk":true,"value":{typed kind value}}}}}"""
            let err kind = $"""{{"kind":"return","value":{{"kind":"result","okType":{primitive kind},"errorType":{stringType},"isOk":false,"value":{{"kind":"string","value":"no"}}}}}}"""
            let throw = """{"kind":"throw","message":"disk ✗"}"""
            let files openResponse readResponses closeResponses =
                $"""{{"kind":"fakeCapability","capability":"Files","operations":[{{"operation":"open","responses":[{openResponse}]}},{{"operation":"read","responses":[{readResponses}]}},{{"operation":"close","responses":[{closeResponses}]}}]}}"""
            let scripts =
                [ files (ok "int64" "\"7\"") (ok "string" "\"text\"") (ok "unit" "\"unit\"")
                  files (err "int64") "" ""
                  files (ok "int64" "\"7\"") throw (ok "unit" "\"unit\"")
                  files (ok "int64" "\"7\"") throw throw
                  files (ok "int64" "\"7\"") (ok "string" "\"text\"") (err "unit")
                  files (ok "int64" "\"7\"") (err "string") (err "unit")
                  files throw "" "" ]
            let compare name (arguments: string list) =
                let csharp = run session "csharp" name arguments
                let zig = run session "zig" name arguments
                Assert.True(csharp.GetProperty("ok").GetBoolean() = zig.GetProperty("ok").GetBoolean(), csharp.ToString() + "\n" + zig.ToString())
                if csharp.GetProperty("ok").GetBoolean() then
                    Assert.Equal(csharp.GetProperty("result").GetProperty("actual").ToString(), zig.GetProperty("result").GetProperty("actual").ToString())
                    Assert.Equal(csharp.GetProperty("result").GetProperty("calls").ToString(), zig.GetProperty("result").GetProperty("calls").ToString())
                else
                    if not (csharp.GetProperty("error").TryGetProperty("failureKind") |> fst) then failwith (csharp.ToString() + "\n" + zig.ToString())
                    for property in [ "code"; "message"; "failureKind"; "exceptionType"; "calls" ] do
                        Assert.Equal(csharp.GetProperty("error").GetProperty(property).ToString(), zig.GetProperty("error").GetProperty(property).ToString())
            for script in scripts do
                for name in [ "withFile"; "guarded_recover"; "guarded_rethrowCancellation"; "guarded_rethrowAll" ] do compare name [ script ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs tasks under the virtual clock and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let commitResult (response: JsonElement) =
                Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
                let proposalId = response.GetProperty("result").GetProperty("proposalId").GetString()
                Assert.True((execute session ($"""{{"id":"m","op":"commit","proposalId":"{proposalId}"}}""")).GetProperty("ok").GetBoolean())
            commitResult (execute session """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}""")
            commitResult (execute session ($"""{{"id":"c","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Svc","operations":[{{"displayName":"work","parameters":[{{"displayName":"n","type":{int64Type}}}],"resultType":{int64Type},"effect":"io"}},{{"displayName":"hold","parameters":[],"resultType":{int64Type},"effect":"io"}}]}}"""))
            let svcType = """{"kind":"capability","name":"Svc"}"""
            let generic name (arguments: string list) = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.Join(",", arguments)}]}}"""
            let outcome = generic "result" [ int64Type; stringType ]
            let taskType = generic "task" [ outcome ]
            let scopeType = generic "taskScope" []
            let waited = generic "list" [ generic "option" [ outcome ] ]
            let effectful name (parameters: (string * string) list) result body =
                let parameters = parameters |> List.map (fun (name, kind) -> $"""{{"displayName":"{name}","type":{kind}}}""") |> String.concat ","
                commitResult (execute session ($"""{{"id":"f","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":["io"],"body":{body}}}"""))
            let value depth = $"""{{"kind":"patternReference","scopeDepth":{depth},"slot":"value"}}"""
            let svc operation (arguments: string) = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "svc"},"capability":"Svc","operation":"{operation}","arguments":[{arguments}]}}"""
            let ok x = $"""{{"kind":"resultOk","errorType":{stringType},"value":{x}}}"""
            let bind x body = $"""{{"kind":"bind","value":{x},"body":{body}}}"""
            let ctx = $"""[{{"parameter":"svc","value":{p "svc"}}}]"""
            effectful "job" [ "svc", svcType; "n", int64Type ] outcome (bind (svc "work" ($"""{{"parameter":"n","value":{p "n"}}}""")) (ok (value 0)))
            effectful "holder" [ "svc", svcType; "n", int64Type ] outcome (bind (svc "hold" "") (ok ($"""{{"kind":"int64WrappingAdd","left":{value 0},"right":{p "n"}}}""")))
            effectful "fanOut" [ "svc", svcType; "xs", generic "list" [ int64Type ] ] (generic "result" [ generic "list" [ int64Type ]; stringType ])
                ($"""{{"kind":"parallelAll","source":{p "xs"},"worker":"job","context":{ctx}}}""")
            let start worker item = $"""{{"kind":"taskStart","scope":{p "scope"},"worker":"{worker}","item":{{"kind":"int64Literal","value":{item}}},"context":{ctx}}}"""
            let tasks depths = $"""{{"kind":"listConstruct","itemType":{taskType},"items":[{String.Join(",", depths |> List.map value)}]}}"""
            let some x = $"""{{"kind":"optionSome","value":{x}}}"""
            effectful "raceBody" [ "svc", svcType; "scope", scopeType ] (generic "result" [ waited; stringType ])
                (bind (start "job" 1) (bind (start "job" 2) (bind ($"""{{"kind":"taskWait","tasks":{tasks [ 1; 0 ]},"mode":"any","limit":{some "{\"kind\":\"int64Literal\",\"value\":25}"}}}""") (ok (value 0)))))
            effectful "race" [ "svc", svcType ] (generic "result" [ waited; stringType ]) ($"""{{"kind":"taskScope","body":"raceBody","context":{ctx}}}""")
            effectful "cancelBody" [ "svc", svcType; "scope", scopeType ] (generic "result" [ waited; stringType ])
                (bind (start "holder" 7) (bind ($"""{{"kind":"taskCancel","task":{value 0}}}""") (bind ($"""{{"kind":"taskWait","tasks":{tasks [ 1 ]},"mode":"all"}}""") (ok (value 0)))))
            effectful "cancelled" [ "svc", svcType ] (generic "result" [ waited; stringType ]) ($"""{{"kind":"taskScope","body":"cancelBody","context":{ctx}}}""")
            effectful "shieldBody" [ "svc", svcType; "start", """{"kind":"primitive","primitive":"unit"}""" ] outcome
                (bind (svc "work" ($"""{{"parameter":"n","value":{{"kind":"int64Literal","value":5}}}}""")) (ok ($"""{{"kind":"conditional","condition":{{"kind":"cancellationRequested"}},"whenTrue":{{"kind":"int64Literal","value":-1}},"whenFalse":{value 0}}}""")))
            effectful "shielded" [ "svc", svcType ] outcome ($"""{{"kind":"cancellationShield","body":"shieldBody","context":{ctx}}}""")
            effectful "addWork" [ "svc", svcType; "acc", int64Type; "n", int64Type ] int64Type (bind (svc "work" ($"""{{"parameter":"n","value":{p "n"}}}""")) ($"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{value 0}}}"""))
            effectful "sumWork" [ "svc", svcType; "xs", generic "list" [ int64Type ] ] int64Type ($"""{{"kind":"listEffectFold","source":{p "xs"},"seed":{{"kind":"int64Literal","value":0}},"function":"addWork","context":{ctx}}}""")
            let response (n: int) (delay: int option) =
                let delayed = delay |> Option.map (fun d -> $""","delayMillis":"{d}" """) |> Option.defaultValue ""
                $"""{{"kind":"return","value":{{"kind":"int64","value":"{n}"}}{delayed}}}"""
            let fake (work: string list) (hold: string list) =
                $"""{{"kind":"fakeCapability","capability":"Svc","operations":[{{"operation":"work","responses":[{String.Join(",", work)}]}},{{"operation":"hold","responses":[{String.Join(",", hold)}]}}]}}"""
            let awaiting = """{"kind":"awaitCancellation","value":{"kind":"int64","value":100}}""".Replace("\"value\":100", "\"value\":\"100\"")
            let compare name (arguments: string list) =
                let csharp = run session "csharp" name arguments
                let zig = run session "zig" name arguments
                Assert.True(csharp.GetProperty("ok").GetBoolean() = zig.GetProperty("ok").GetBoolean(), csharp.ToString() + "\n" + zig.ToString())
                if csharp.GetProperty("ok").GetBoolean() then
                    Assert.Equal(csharp.GetProperty("result").GetProperty("actual").ToString(), zig.GetProperty("result").GetProperty("actual").ToString())
                    Assert.Equal(csharp.GetProperty("result").GetProperty("calls").ToString(), zig.GetProperty("result").GetProperty("calls").ToString())
                else
                    if not (csharp.GetProperty("error").TryGetProperty("failureKind") |> fst) then failwith (csharp.ToString() + "\n" + zig.ToString())
                    for property in [ "code"; "message"; "failureKind"; "exceptionType"; "calls" ] do
                        Assert.Equal(csharp.GetProperty("error").GetProperty(property).ToString(), zig.GetProperty("error").GetProperty(property).ToString())
            compare "fanOut" [ fake [ response 10 (Some 30); response 20 (Some 10); response 30 None ] []; "[1,2,3]" ]
            compare "fanOut" [ fake [ response 10 (Some 5) ] []; "[1,2]" ]
            compare "race" [ fake [ response 10 (Some 40); response 20 (Some 20) ] [] ]
            compare "race" [ fake [ response 10 (Some 40); response 20 (Some 30) ] [] ]
            compare "cancelled" [ fake [] [ awaiting ] ]
            compare "cancelled" [ fake [] [] ]
            compare "shielded" [ fake [ response 4 (Some 3) ] [] ]
            compare "sumWork" [ fake [ response 1 None; response 2 None; response 3 None ] []; "[1,2,3]" ]
            // One-shot hand-offs, failures surfacing from tasks, a Result-returning effectful fold, and a deadlock.
            let optionInt = generic "option" [ int64Type ]
            let handed = generic "result" [ optionInt; stringType ]
            let create = $"""{{"kind":"oneShotCreate","scope":{p "scope"},"valueType":{int64Type}}}"""
            let waitCell limit = $"""{{"kind":"oneShotWait","cell":{value 0}{limit}}}"""
            effectful "handoffBody" [ "svc", svcType; "scope", scopeType ] handed
                (bind create (bind ($"""{{"kind":"oneShotComplete","cell":{value 0},"value":{{"kind":"int64Literal","value":5}}}}""") (bind ($"""{{"kind":"oneShotWait","cell":{value 1}}}""") (ok (value 0)))))
            effectful "handoff" [ "svc", svcType ] handed ($"""{{"kind":"taskScope","body":"handoffBody","context":{ctx}}}""")
            let limited = some "{\"kind\":\"int64Literal\",\"value\":10}"
            effectful "timeoutBody" [ "svc", svcType; "scope", scopeType ] handed (bind create (bind (waitCell (",\"limit\":" + limited)) (ok (value 0))))
            effectful "timeout" [ "svc", svcType ] handed ($"""{{"kind":"taskScope","body":"timeoutBody","context":{ctx}}}""")
            effectful "stuckBody" [ "svc", svcType; "scope", scopeType ] handed (bind create (bind (waitCell "") (ok (value 0))))
            effectful "stuck" [ "svc", svcType ] handed ($"""{{"kind":"taskScope","body":"stuckBody","context":{ctx}}}""")
            effectful "failWaitBody" [ "svc", svcType; "scope", scopeType ] (generic "result" [ waited; stringType ])
                (bind (start "job" 1) (bind ($"""{{"kind":"taskWait","tasks":{tasks [ 0 ]},"mode":"all"}}""") (ok (value 0))))
            effectful "failWait" [ "svc", svcType ] (generic "result" [ waited; stringType ]) ($"""{{"kind":"taskScope","body":"failWaitBody","context":{ctx}}}""")
            effectful "failCloseBody" [ "svc", svcType; "scope", scopeType ] outcome (bind (start "job" 1) (bind (start "job" 2) (ok "{\"kind\":\"int64Literal\",\"value\":0}")))
            effectful "failClose" [ "svc", svcType ] outcome ($"""{{"kind":"taskScope","body":"failCloseBody","context":{ctx}}}""")
            let added = ok ($"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{value 0}}}""")
            effectful "addChecked" [ "svc", svcType; "acc", int64Type; "n", int64Type ] outcome
                (bind (svc "work" ($"""{{"parameter":"n","value":{p "n"}}}""")) ($"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{value 0},"right":{{"kind":"int64Literal","value":0}}}},"whenTrue":{{"kind":"resultErr","okType":{int64Type},"error":{{"kind":"stringLiteral","value":"negative"}}}},"whenFalse":{added}}}"""))
            effectful "sumChecked" [ "svc", svcType; "xs", generic "list" [ int64Type ] ] outcome ($"""{{"kind":"listEffectFold","source":{p "xs"},"seed":{{"kind":"int64Literal","value":0}},"function":"addChecked","context":{ctx}}}""")
            let throws = """{"kind":"throw","message":"task ✗"}"""
            compare "handoff" [ fake [] [] ]
            compare "timeout" [ fake [] [] ]
            compare "stuck" [ fake [] [] ]
            compare "failWait" [ fake [ throws ] [] ]
            compare "failClose" [ fake [ response 1 (Some 5); throws ] [] ]
            compare "failClose" [ fake [ throws; response 2 None ] [] ]
            compare "sumChecked" [ fake [ response 1 None; response -2 None; response 3 None ] []; "[1,2,3]" ]
            compare "sumChecked" [ fake [ response 1 None; response 2 None ] []; "[1,2]" ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs String search, split and join and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let st = [ "s", stringType; "t", stringType ]
            let sti = st @ [ "i", int64Type ]
            let optionInt = $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{int64Type}]}}"""
            let texts = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{stringType}]}}"""
            create session "slice" [ "s", stringType; "i", int64Type; "j", int64Type ] stringType ($"""{{"kind":"stringSlice","value":{p "s"},"start":{p "i"},"end":{p "j"}}}""")
            create session "indexOf" sti optionInt ($"""{{"kind":"stringIndexOf","value":{p "s"},"substring":{p "t"},"from":{p "i"}}}""")
            create session "lastBefore" sti optionInt ($"""{{"kind":"stringLastIndexOfBefore","value":{p "s"},"substring":{p "t"},"end":{p "i"}}}""")
            create session "span" sti int64Type ($"""{{"kind":"stringSpan","value":{p "s"},"set":{p "t"},"from":{p "i"}}}""")
            create session "spanBack" sti int64Type ($"""{{"kind":"stringSpanBack","value":{p "s"},"set":{p "t"},"end":{p "i"}}}""")
            create session "lastIndexOf" st optionInt ($"""{{"kind":"stringLastIndexOf","value":{p "s"},"substring":{p "t"}}}""")
            create session "contains" st boolType ($"""{{"kind":"stringContains","value":{p "s"},"substring":{p "t"}}}""")
            create session "startsWith" st boolType ($"""{{"kind":"stringStartsWith","value":{p "s"},"prefix":{p "t"}}}""")
            create session "endsWith" st boolType ($"""{{"kind":"stringEndsWith","value":{p "s"},"suffix":{p "t"}}}""")
            create session "less" st boolType ($"""{{"kind":"stringLessThan","left":{p "s"},"right":{p "t"}}}""")
            create session "split" st texts ($"""{{"kind":"stringSplit","value":{p "s"},"separator":{p "t"}}}""")
            create session "lines" st texts ($"""{{"kind":"stringSplitLines","value":{p "s"},"breaks":{p "t"}}}""")
            create session "replace" st stringType ($"""{{"kind":"stringReplace","value":{p "s"},"pattern":{p "t"},"replacement":{{"kind":"stringLiteral","value":"<😀>"}}}}""")
            create session "join" [ "xs", texts; "t", stringType ] stringType ($"""{{"kind":"stringJoin","list":{p "xs"},"separator":{p "t"}}}""")
            let strings = [ "\"\""; "\"aXbXXc\""; "\"héllo wörld\""; "\"a😀b😀\""; "\"x\\r\\ny\\n\\u2028z\\r\""; "\"€uro€\"" ]
            let patterns = [ "\"\""; "\"X\""; "\"😀\""; "\"ö\""; "\"\\r\\n\\u2028\""; "\"€\""; "\"lo wö\"" ]
            for s in strings do
                for i in [ "-1"; "0"; "2"; "100" ] do
                    for j in [ "-1"; "1"; "3"; "100" ] do agree session "slice" [ s; i; j ]
                for t in patterns @ strings do
                    for name in [ "lastIndexOf"; "contains"; "startsWith"; "endsWith"; "less"; "split"; "lines"; "replace" ] do agree session name [ s; t ]
                for t in patterns do
                    for i in [ "-1"; "0"; "2"; "100" ] do
                        for name in [ "indexOf"; "lastBefore"; "span"; "spanBack" ] do agree session name [ s; t; i ]
            for xs in [ "[]"; "[\"\"]"; "[\"a\",\"\",\"😀\"]" ] do
                for t in [ "\"\""; "\", \"" ] do agree session "join" [ xs; t ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs case mapping, trimming, code points and UTF-8 and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let unary kind = $"""{{"kind":"{kind}","value":{p "s"}}}"""
            for kind in [ "stringToLower"; "stringToUpper"; "stringTrim"; "stringTrimStart"; "stringTrimEnd"; "stringEncodeUtf8"; "stringDecodeUtf8Lossy" ] do
                create session kind [ "s", stringType ] stringType (unary kind)
            create session "codePoints" [ "s", stringType ] ($"""{{"kind":"builtinGeneric","generic":"list","arguments":[{stringType}]}}""") (unary "stringCodePoints")
            create session "byteCount" [ "s", stringType ] int64Type (unary "stringUtf8ByteCount")
            create session "ord" [ "s", stringType ] ($"""{{"kind":"builtinGeneric","generic":"option","arguments":[{int64Type}]}}""") (unary "stringToCodePoint")
            create session "repeat" [ "s", stringType; "n", int64Type ] stringType ($"""{{"kind":"stringRepeat","text":{p "s"},"count":{p "n"}}}""")
            let chr value = $"""{{"kind":"optionGetOrDefault","option":{{"kind":"stringFromCodePoint","value":{value}}},"default":{{"kind":"stringLiteral","value":"-"}}}}"""
            create session "chr" [ "n", int64Type ] stringType (chr (p "n"))
            // Every code point in [a, b), lowered and uppered; the chunks below cover every cased one.
            let all = $"""{{"kind":"stringJoin","list":{{"kind":"listMap","source":{{"kind":"listRange","start":{p "a"},"end":{p "b"}}},"function":"chr"}},"separator":{{"kind":"stringLiteral","value":""}}}}"""
            create session "cases" [ "a", int64Type; "b", int64Type ] stringType
                ($"""{{"kind":"stringConcat","left":{{"kind":"stringToLower","value":{all}}},"right":{{"kind":"stringToUpper","value":{all}}}}}""")
            let strings =
                [ "\"\""; "\"  \\t a b \\u00a0\\u3000\\n\""; "\"\\u200bx\\u200b\""; "\"Straße İstanbul ıi ΣΑΣ σς \\u212a k ǅ\""; "\"😀𐐷𐐏\""
                  "\"\\u00c0\\u00ff\\u0080\\u00e2\\u0082\\u00ac\""; "\"\\u00ed\\u00a0\\u0080A\""; "\"\\u00f0\\u009f\\u0098\""; "\"\\u0100\\u00e2\\u0082\"" ]
            for s in strings do
                for kind in [ "stringToLower"; "stringToUpper"; "stringTrim"; "stringTrimStart"; "stringTrimEnd"; "stringEncodeUtf8"; "stringDecodeUtf8Lossy"; "codePoints"; "byteCount"; "ord" ] do
                    agree session kind [ s ]
                for n in [ "-1"; "0"; "3" ] do agree session "repeat" [ s; n ]
            for s in [ "\"a\""; "\"😀\""; "\"é\"" ] do agree session "ord" [ s ]
            for n in [ "-1"; "0"; "65"; "55296"; "57343"; "128512"; "1114111"; "1114112" ] do agree session "chr" [ n ]
            // A result holds at most 65536 code units, and no code point above U+1EFFF has a case mapping.
            for start in 0 .. 16000 .. 0x1F000 do agree session "cases" [ string start; string (start + 16000) ]
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Zig runs number text, Float64 rounding and checked Int64 and agrees with the C# backend`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> unwrap
            let float = """{"kind":"primitive","primitive":"float64"}"""
            let option item = $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{item}]}}"""
            let unary kind name = $"""{{"kind":"{kind}","value":{p name}}}"""
            create session "hex" [ "n", int64Type ] stringType (unary "int64ToHexString" "n")
            create session "parse" [ "s", stringType ] (option int64Type) (unary "stringParseInt64" "s")
            create session "parseHex" [ "s", stringType ] (option int64Type) (unary "stringParseInt64Hex" "s")
            create session "parseFloat" [ "s", stringType ] (option float) (unary "stringParseFloat64" "s")
            create session "show" [ "x", float ] stringType (unary "float64ToString" "x")
            create session "round" [ "x", float; "n", int64Type ] float ($"""{{"kind":"float64Round","value":{p "x"},"digits":{p "n"}}}""")
            for kind in [ "int64CheckedAdd"; "int64CheckedSubtract"; "int64CheckedMultiply" ] do
                create session kind [ "a", int64Type; "b", int64Type ] (option int64Type) (binary kind)
            let edges = [ Int64.MinValue; -255L; -1L; 0L; 2L; 255L; 3037000500L; Int64.MaxValue ]
            for a in edges do
                agree session "hex" [ int64 a ]
                for b in edges do
                    for kind in [ "int64CheckedAdd"; "int64CheckedSubtract"; "int64CheckedMultiply" ] do agree session kind [ int64 a; int64 b ]
            let numbers =
                [ ""; "-"; "0"; "-0"; "007"; "+1"; " 1"; "1_0"; "12a"; "١"; "FFff"; "0x10"; "7fffffffffffffff"; "-8000000000000000"; "8000000000000000"
                  "10000000000000000"; "9223372036854775807"; "9223372036854775808"; "-9223372036854775808"; "-9223372036854775809"
                  "0.1"; "1e400"; "-1e-400"; "1E+2"; ".5"; "5."; "1e"; "00.5"; "1e99999999999999999999"; "NaN"; "inf"; "1.0e-5"; "2.675" ]
            for text in numbers do
                for name in [ "parse"; "parseHex"; "parseFloat" ] do agree session name [ JsonSerializer.Serialize text ]
            let floats =
                [ "0.1"; "1e16"; "1e17"; "1e-5"; "0.0001"; "\"-0\""; "\"NaN\""; "\"Infinity\""; "\"-Infinity\""; "5e-324"; "1.7976931348623157e308"
                  "2.675"; "0.5"; "1.5"; "-2.5"; "123.456"; "100"; "1250"; "-0.004"; "1e22"; "1e23"; "123456789012345680"; "0.30000000000000004" ]
            for x in floats do
                agree session "show" [ x ]
                for n in [ "-1001"; "-2"; "0"; "1"; "2"; "15"; "400" ] do agree session "round" [ x; n ]
        finally Directory.Delete(dir, true)
