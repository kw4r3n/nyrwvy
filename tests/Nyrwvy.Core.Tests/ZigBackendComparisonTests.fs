namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit

/// The C# and Zig backends agree: each case runs once per backend on the Zig host, on one snapshot
/// (so one runner build per backend), and the replies are compared without `frames`, which name
/// generated symbols (docs/decisions/backend-targets-and-parity.md). Ported from the comparisons the
/// F# host ran (ab5d0db6:tests/Nyrwvy.Core.Tests/ZigBackendTests.fs), plus an abort inside a resource.
module ZigBackendComparisonTests =
    open ZigHostParityTests

    let private int64Type = """{"kind":"primitive","primitive":"int64"}"""
    let private boolType = """{"kind":"primitive","primitive":"bool"}"""
    let private stringType = """{"kind":"primitive","primitive":"string"}"""
    let private float = """{"kind":"primitive","primitive":"float64"}"""
    let private bytesType = """{"kind":"primitive","primitive":"bytes"}"""
    let private bits32Type = """{"kind":"primitive","primitive":"bits32"}"""
    let private generic name (arguments: string list) = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.Join(",", arguments)}]}}"""
    let private listOf item = generic "list" [ item ]
    let private ints = listOf int64Type
    let private optionInt = generic "option" [ int64Type ]
    let private int64 (value: int64) = $"""{{"kind":"int64","value":"{value}"}}"""
    let private p name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private lit (n: int) = $"""{{"kind":"int64Literal","value":{n}}}"""
    let private binary kind = $"""{{"kind":"{kind}","left":{p "a"},"right":{p "b"}}}"""
    let private unary kind name = $"""{{"kind":"{kind}","value":{p name}}}"""
    let private slot name = $"""{{"kind":"patternReference","scopeDepth":0,"slot":"{name}"}}"""
    let private call name (arguments: (string * string) list) =
        let arguments = arguments |> List.map (fun (parameter, value) -> $"""{{"parameter":"{parameter}","value":{value}}}""")
        $"""{{"kind":"directCall","function":"{name}","arguments":[{String.Join(",", arguments)}]}}"""

    let private declare (parameters: (string * string) list) =
        String.Join(",", parameters |> List.map (fun (name, kind) -> $"""{{"displayName":"{name}","type":{kind}}}"""))
    let private fn name parameters result body =
        $"""{{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{declare parameters}],"resultType":{result},"requiredEffects":[],"body":{body}}}"""
    let private createType name (typeKind: string) =
        $"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"{name}","typeKind":{typeKind}}}"""

    let private int64Operations =
        [ "int64WrappingAdd"; "int64WrappingSubtract"; "int64WrappingMultiply"; "int64FloorDivide"; "int64FloorModulo"
          "int64Min"; "int64Max"; "int64BitAnd"; "int64BitOr"; "int64BitXor" ]
    let private floatBinary = [ "float64Add"; "float64Subtract"; "float64Multiply"; "float64Divide"; "float64Min"; "float64Max"; "float64FloorModulo" ]
    let private floatUnary = [ "float64Sqrt"; "float64Abs"; "float64Negate"; "float64Floor"; "float64Ceiling"; "float64Truncate" ]
    let private floatTests = [ "float64IsNaN"; "float64IsFinite"; "float64IsInfinite" ]
    let private floatToInt = [ "float64RoundToInt64"; "float64FloorToInt64" ]
    let private stringUnary = [ "stringToLower"; "stringToUpper"; "stringTrim"; "stringTrimStart"; "stringTrimEnd"; "stringEncodeUtf8"; "stringDecodeUtf8Lossy" ]
    let private checkedOperations = [ "int64CheckedAdd"; "int64CheckedSubtract"; "int64CheckedMultiply" ]
    let private setOperations = [ "setUnion"; "setIntersection"; "setDifference" ]
    let private bits32Binary = [ "bits32Xor"; "bits32And"; "bits32Or"; "bits32Add" ]
    let private bits32Shifts = [ "bits32RotateRight"; "bits32ShiftRight"; "bits32ShiftLeft" ]

    // The `xs: List<Int64>` functions of the old "remaining list, map and set operations" test.
    let private listCases =
        let xs = p "xs"
        let toSet = $"""{{"kind":"setFromList","value":{xs}}}"""
        let toMap = $"""{{"kind":"mapFromList","source":{xs},"function":"half"}}"""
        let map = generic "map" [ int64Type; int64Type ]
        [ "all", boolType, $"""{{"kind":"listAll","source":{xs},"function":"isPositive"}}"""
          "findIndex", optionInt, $"""{{"kind":"listFindIndex","source":{xs},"function":"isTwo"}}"""
          "flatMap", ints, $"""{{"kind":"listFlatMap","source":{xs},"function":"twiceList"}}"""
          "scan", ints, $"""{{"kind":"listScan","source":{xs},"seed":{lit 0},"function":"add"}}"""
          "zip", ints, $"""{{"kind":"listZipWith","left":{xs},"right":{{"kind":"listDropLast","value":{xs}}},"function":"mul"}}"""
          "last", optionInt, $"""{{"kind":"listLast","value":{xs}}}"""
          "length", int64Type, $"""{{"kind":"listLength","value":{xs}}}"""
          "listContains", boolType, $"""{{"kind":"listContains","list":{xs},"item":{lit 3}}}"""
          "listIndexOf", optionInt, $"""{{"kind":"listIndexOf","list":{xs},"item":{lit 3}}}"""
          "listLastIndexOf", optionInt, $"""{{"kind":"listLastIndexOf","list":{xs},"item":{lit 3}}}"""
          "mapFold", int64Type, $"""{{"kind":"mapFold","map":{toMap},"seed":{lit 0},"function":"keyAdd"}}"""
          "mapFilter", map, $"""{{"kind":"mapFilter","map":{toMap},"function":"keyBig"}}"""
          "mapValues", ints, $"""{{"kind":"mapValues","value":{{"kind":"mapMapValues","map":{toMap},"function":"keyLe"}}}}"""
          "mapGet", optionInt, $"""{{"kind":"mapGet","map":{toMap},"key":{lit 1}}}"""
          "mapHas", boolType, $"""{{"kind":"mapContainsKey","map":{toMap},"key":{lit 1}}}"""
          "mapSize", int64Type, $"""{{"kind":"mapSize","value":{toMap}}}"""
          "setFold", int64Type, $"""{{"kind":"setFold","set":{toSet},"seed":{lit 0},"function":"add"}}"""
          "setFilter", ints, $"""{{"kind":"setToList","value":{{"kind":"setFilter","set":{toSet},"function":"small"}}}}"""
          "setMap", ints, $"""{{"kind":"setToList","value":{{"kind":"setMap","set":{toSet},"function":"half"}}}}"""
          "setEdit", int64Type, $"""{{"kind":"setSize","value":{{"kind":"setRemove","set":{{"kind":"setInsert","set":{{"kind":"setEmpty","itemType":{int64Type}}},"item":{lit 4}}},"item":{lit 5}}}}}"""
          "setHas", boolType, $"""{{"kind":"setContains","set":{toSet},"item":{lit 3}}}"""
          "someLast", boolType, $"""{{"kind":"optionIsSome","value":{{"kind":"listLast","value":{xs}}}}}"""
          "bytes", int64Type, $"""{{"kind":"bytesLength","value":{{"kind":"bytesEmpty"}}}}"""
          "float", boolType, $"""{{"kind":"float64LessThan","left":{{"kind":"float64Literal","bits":"3ff8000000000000"}},"right":{{"kind":"int64ToFloat64","value":{{"kind":"listLength","value":{xs}}}}}}}"""
          "text", boolType, $"""{{"kind":"stringEqual","left":{{"kind":"int64ToString","value":{{"kind":"listLength","value":{xs}}}}},"right":{{"kind":"stringLiteral","value":"3"}}}}"""
          "empty", boolType, $"""{{"kind":"stringIsEmpty","value":{{"kind":"int64ToString","value":{lit 0}}}}}""" ]

    let private effectful name parameters result body = (fn name parameters result body).Replace("\"requiredEffects\":[]", "\"requiredEffects\":[\"io\"]")
    let private unitType = """{"kind":"primitive","primitive":"unit"}"""
    let private resultOf ok = generic "result" [ ok; stringType ]
    let private outcome = resultOf int64Type
    let private waited = resultOf (listOf (generic "option" [ outcome ]))
    let private bind value body = $"""{{"kind":"bind","value":{value},"body":{body}}}"""
    let private value depth = $"""{{"kind":"patternReference","scopeDepth":{depth},"slot":"value"}}"""
    let private ok x = $"""{{"kind":"resultOk","errorType":{stringType},"value":{x}}}"""

    /// Exception boundaries, resources, abort, tasks and scopes (the old tests' last two groups, after
    /// the `io` effect the Store group creates); every one is effectful, so none is batched.
    let private effectfulDeclarations =
        let filesType = """{"kind":"capability","name":"Files"}"""
        let files operation (arguments: string) = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "files"},"capability":"Files","operation":"{operation}","arguments":[{arguments}]}}"""
        let readHandle = files "read" $"""{{"parameter":"handle","value":{p "handle"}}}"""
        let withResource body = $"""{{"kind":"useResource","acquire":{files "open" ""},"releaseCapability":{p "files"},"capability":"Files","releaseOperation":"close","body":"{body}","context":[{{"parameter":"files","value":{p "files"}}}]}}"""
        let exceptionType = generic "exception" []
        let svcType = """{"kind":"capability","name":"Svc"}"""
        let scopeType = generic "taskScope" []
        let svc operation (arguments: string) = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "svc"},"capability":"Svc","operation":"{operation}","arguments":[{arguments}]}}"""
        let work n = svc "work" $"""{{"parameter":"n","value":{n}}}"""
        let ctx = $"""[{{"parameter":"svc","value":{p "svc"}}}]"""
        let scoped name result body = effectful name [ "svc", svcType ] result $"""{{"kind":"taskScope","body":"{body}","context":{ctx}}}"""
        let inScope name result body = effectful name [ "svc", svcType; "scope", scopeType ] result body
        let start worker item = $"""{{"kind":"taskStart","scope":{p "scope"},"worker":"{worker}","item":{lit item},"context":{ctx}}}"""
        let tasks depths = $"""{{"kind":"listConstruct","itemType":{generic "task" [ outcome ]},"items":[{String.Join(",", depths |> List.map value)}]}}"""
        let some x = $"""{{"kind":"optionSome","value":{x}}}"""
        let handed = resultOf optionInt
        let create = $"""{{"kind":"oneShotCreate","scope":{p "scope"},"valueType":{int64Type}}}"""
        let waitCell limit = $"""{{"kind":"oneShotWait","cell":{value 0}{limit}}}"""
        let added = ok $"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{value 0}}}"""
        [ // Exception boundaries, resources and abort.
          $"""{{"id":"k","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Files","operations":[{{"displayName":"open","parameters":[],"resultType":{resultOf int64Type},"effect":"io"}},{{"displayName":"read","parameters":[{declare [ "handle", int64Type ]}],"resultType":{resultOf stringType},"effect":"io"}},{{"displayName":"close","parameters":[{declare [ "handle", int64Type ]}],"resultType":{resultOf unitType},"effect":"io"}}]}}"""
          effectful "readBody" [ "files", filesType; "handle", int64Type ] (resultOf stringType) readHandle
          effectful "withFile" [ "files", filesType ] (resultOf stringType) (withResource "readBody")
          // A read failure aborts the body, which still closes the file.
          let got = ok $"""{{"kind":"stringConcat","left":{{"kind":"stringLiteral","value":"got "}},"right":{slot "value"}}}"""
          effectful "abortBody" [ "files", filesType; "handle", int64Type ] (resultOf stringType)
              $"""{{"kind":"matchResult","input":{readHandle},"okBody":{got},"errBody":{{"kind":"abort","error":{slot "error"}}}}}"""
          effectful "abortFile" [ "files", filesType ] (resultOf stringType) (withResource "abortBody")
          effectful "attempt" [ "files", filesType; "start", unitType ] (resultOf stringType) (call "withFile" [ "files", p "files" ])
          fn "recover" [ "failure", exceptionType ] (resultOf stringType)
              $"""{{"kind":"resultErr","okType":{stringType},"error":{{"kind":"stringConcat","left":{{"kind":"stringLiteral","value":"caught: "}},"right":{{"kind":"exceptionMessage","value":{p "failure"}}}}}}}"""
          effectful "rethrowCancellation" [ "failure", exceptionType ] (resultOf stringType)
              $"""{{"kind":"conditional","condition":{{"kind":"exceptionIsCancellation","value":{p "failure"}}},"whenTrue":{{"kind":"exceptionRethrow","value":{p "failure"},"resultType":{resultOf stringType}}},"whenFalse":{{"kind":"resultErr","okType":{stringType},"error":{{"kind":"exceptionMessage","value":{p "failure"}}}}}}}"""
          effectful "rethrowAll" [ "failure", exceptionType ] (resultOf stringType) $"""{{"kind":"exceptionRethrow","value":{p "failure"},"resultType":{resultOf stringType}}}"""
          for handler in [ "recover"; "rethrowCancellation"; "rethrowAll" ] do
              effectful ("guarded_" + handler) [ "files", filesType ] (resultOf stringType)
                  $"""{{"kind":"exceptionCatch","body":"attempt","bodyContext":[{{"parameter":"files","value":{p "files"}}}],"handler":"{handler}","handlerContext":[]}}"""
          // Program capability implementations, chosen at run time, beside a scripted fake of the same capability.
          $"""{{"id":"k","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Calc","operations":[{{"displayName":"scale","parameters":[{declare [ "n", int64Type ]}],"resultType":{int64Type},"effect":"io"}},{{"displayName":"label","parameters":[{declare [ "text", stringType ]}],"resultType":{stringType},"effect":"io"}}]}}"""
          fn "calcTwice" [ "n", int64Type ] int64Type $"""{{"kind":"int64WrappingAdd","left":{p "n"},"right":{p "n"}}}"""
          fn "calcThrice" [ "n", int64Type ] int64Type $"""{{"kind":"int64WrappingMultiply","left":{p "n"},"right":{lit 3}}}"""
          fn "calcLabel" [ "text", stringType ] stringType $"""{{"kind":"stringConcat","left":{p "text"},"right":{p "text"}}}"""
          let calcType = """{"kind":"capability","name":"Calc"}"""
          let calcOf scale = $"""{{"kind":"capabilityConstruct","capability":"Calc","implementations":[{{"operation":"scale","function":"{scale}"}},{{"operation":"label","function":"calcLabel"}}]}}"""
          let calc operation parameter value = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "calc"},"capability":"Calc","operation":"{operation}","arguments":[{{"parameter":"{parameter}","value":{value}}}]}}"""
          fn "pickCalc" [ "double", boolType ] calcType $"""{{"kind":"conditional","condition":{p "double"},"whenTrue":{calcOf "calcTwice"},"whenFalse":{calcOf "calcThrice"}}}"""
          effectful "useCalc" [ "calc", calcType; "n", int64Type ] int64Type (calc "scale" "n" (p "n"))
          effectful "useLabel" [ "calc", calcType; "text", stringType ] stringType (bind (calc "label" "text" (p "text")) (calc "label" "text" (value 0)))
          effectful "programCalc" [ "double", boolType; "n", int64Type ] int64Type (call "useCalc" [ "calc", call "pickCalc" [ "double", p "double" ]; "n", p "n" ])
          effectful "programLabel" [ "text", stringType ] stringType (call "useLabel" [ "calc", call "pickCalc" [ "double", """{"kind":"boolLiteral","value":true}""" ]; "text", p "text" ])
          // Tasks under the virtual clock.
          $"""{{"id":"k","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Svc","operations":[{{"displayName":"work","parameters":[{declare [ "n", int64Type ]}],"resultType":{int64Type},"effect":"io"}},{{"displayName":"hold","parameters":[],"resultType":{int64Type},"effect":"io"}}]}}"""
          effectful "job" [ "svc", svcType; "n", int64Type ] outcome (bind (work (p "n")) (ok (value 0)))
          effectful "holder" [ "svc", svcType; "n", int64Type ] outcome (bind (svc "hold" "") (ok $"""{{"kind":"int64WrappingAdd","left":{value 0},"right":{p "n"}}}"""))
          effectful "fanOut" [ "svc", svcType; "xs", ints ] (resultOf ints) $"""{{"kind":"parallelAll","source":{p "xs"},"worker":"job","context":{ctx}}}"""
          inScope "raceBody" waited (bind (start "job" 1) (bind (start "job" 2) (bind $"""{{"kind":"taskWait","tasks":{tasks [ 1; 0 ]},"mode":"any","limit":{some (lit 25)}}}""" (ok (value 0)))))
          scoped "race" waited "raceBody"
          inScope "cancelBody" waited (bind (start "holder" 7) (bind $"""{{"kind":"taskCancel","task":{value 0}}}""" (bind $"""{{"kind":"taskWait","tasks":{tasks [ 1 ]},"mode":"all"}}""" (ok (value 0)))))
          scoped "cancelled" waited "cancelBody"
          effectful "shieldBody" [ "svc", svcType; "start", unitType ] outcome
              (bind (work (lit 5)) (ok $"""{{"kind":"conditional","condition":{{"kind":"cancellationRequested"}},"whenTrue":{lit -1},"whenFalse":{value 0}}}"""))
          effectful "shielded" [ "svc", svcType ] outcome $"""{{"kind":"cancellationShield","body":"shieldBody","context":{ctx}}}"""
          effectful "addWork" [ "svc", svcType; "acc", int64Type; "n", int64Type ] int64Type (bind (work (p "n")) $"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{value 0}}}""")
          effectful "sumWork" [ "svc", svcType; "xs", ints ] int64Type $"""{{"kind":"listEffectFold","source":{p "xs"},"seed":{lit 0},"function":"addWork","context":{ctx}}}"""
          // Each call is caught, so only the runner's 1024-call limit ends the fold.
          effectful "workOnce" [ "svc", svcType; "start", unitType ] outcome (ok (work (lit 1)))
          fn "minusOne" [ "failure", exceptionType ] outcome (ok (lit -1))
          effectful "tryWork" [ "svc", svcType; "acc", int64Type; "n", int64Type ] outcome $"""{{"kind":"exceptionCatch","body":"workOnce","bodyContext":{ctx},"handler":"minusOne","handlerContext":[]}}"""
          effectful "manyTries" [ "svc", svcType ] outcome $"""{{"kind":"listEffectFold","source":{{"kind":"listRange","start":{lit 0},"end":{lit 2000}}},"seed":{lit 0},"function":"tryWork","context":{ctx}}}"""
          // One-shot hand-offs, failures surfacing from tasks, a Result-returning effectful fold, and a deadlock.
          inScope "handoffBody" handed (bind create (bind $"""{{"kind":"oneShotComplete","cell":{value 0},"value":{lit 5}}}""" (bind $"""{{"kind":"oneShotWait","cell":{value 1}}}""" (ok (value 0)))))
          scoped "handoff" handed "handoffBody"
          inScope "timeoutBody" handed (bind create (bind (waitCell (",\"limit\":" + some (lit 10))) (ok (value 0))))
          scoped "timeout" handed "timeoutBody"
          inScope "stuckBody" handed (bind create (bind (waitCell "") (ok (value 0))))
          scoped "stuck" handed "stuckBody"
          // Several tasks deadlocked at once: three park on a shared cell, one on a cell in its own nested scope,
          // and the body waits on them out of start order (one cancelled first in the second variant).
          let cellType = generic "oneShot" [ int64Type ]
          let startOn worker item depth =
              $"""{{"kind":"taskStart","scope":{p "scope"},"worker":"{worker}","item":{lit item},"context":[{{"parameter":"svc","value":{p "svc"}}},{{"parameter":"cell","value":{value depth}}}]}}"""
          effectful "parked" [ "svc", svcType; "cell", cellType; "n", int64Type ] outcome (bind $"""{{"kind":"oneShotWait","cell":{p "cell"}}}""" (ok (p "n")))
          inScope "nestedParkedBody" outcome (bind create (bind (waitCell "") (ok (lit 0))))
          effectful "nestedParked" [ "svc", svcType; "n", int64Type ] outcome $"""{{"kind":"taskScope","body":"nestedParkedBody","context":{ctx}}}"""
          let parkAll rest = bind create (bind (startOn "parked" 1 0) (bind (start "nestedParked" 2) (bind (startOn "parked" 3 2) (bind (startOn "parked" 4 3) rest))))
          inScope "manyStuckBody" waited (parkAll (bind $"""{{"kind":"taskWait","tasks":{tasks [ 1; 3; 0; 2 ]},"mode":"all"}}""" (ok (value 0))))
          scoped "manyStuck" waited "manyStuckBody"
          inScope "manyStuckCancelBody" waited
              (parkAll (bind $"""{{"kind":"taskCancel","task":{value 1}}}""" (bind $"""{{"kind":"taskWait","tasks":{tasks [ 1; 4; 3; 2 ]},"mode":"all"}}""" (ok (value 0)))))
          scoped "manyStuckCancel" waited "manyStuckCancelBody"
          inScope "failWaitBody" waited (bind (start "job" 1) (bind $"""{{"kind":"taskWait","tasks":{tasks [ 0 ]},"mode":"all"}}""" (ok (value 0))))
          scoped "failWait" waited "failWaitBody"
          inScope "failCloseBody" outcome (bind (start "job" 1) (bind (start "job" 2) (ok (lit 0))))
          scoped "failClose" outcome "failCloseBody"
          effectful "addChecked" [ "svc", svcType; "acc", int64Type; "n", int64Type ] outcome
              (bind (work (p "n")) $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{value 0},"right":{lit 0}}},"whenTrue":{{"kind":"resultErr","okType":{int64Type},"error":{{"kind":"stringLiteral","value":"negative"}}}},"whenFalse":{added}}}""")
          effectful "sumChecked" [ "svc", svcType; "xs", ints ] outcome $"""{{"kind":"listEffectFold","source":{p "xs"},"seed":{lit 0},"function":"addChecked","context":{ctx}}}""" ]

    /// Every declaration, callbacks before their callers; names are unique across the old tests' groups.
    let private declarations =
        let ab = [ "a", int64Type; "b", int64Type ]
        let field name kind = $"""{{"displayName":"{name}","type":{kind}}}"""
        let pointType = """{"kind":"named","name":"Point"}"""
        let shapeType = """{"kind":"named","name":"Shape"}"""
        let resultIntText = generic "result" [ int64Type; stringType ]
        let shapeField variant name = $"""{{"kind":"patternReference","scopeDepth":0,"slot":"field","type":"Shape","variant":"{variant}","field":"{name}"}}"""
        let mul a b = $"""{{"kind":"int64WrappingMultiply","left":{a},"right":{b}}}"""
        let counts = generic "map" [ stringType; int64Type ]
        let intSet = generic "set" [ int64Type ]
        let from name = $"""{{"kind":"bytesFromLatin1","value":{p name}}}"""
        let st = [ "s", stringType; "t", stringType ]
        let sti = st @ [ "i", int64Type ]
        let texts = listOf stringType
        let e = """{"kind":"typeParameter","name":"E"}"""
        let modulo name = $"""{{"kind":"int64FloorModulo","left":{p name},"right":{lit 10}}}"""
        let unaryFunction = $"""{{"kind":"function","parameters":[{int64Type}],"resultType":{int64Type}}}"""
        let storeType = """{"kind":"capability","name":"Store"}"""
        let chr value = $"""{{"kind":"optionGetOrDefault","option":{{"kind":"stringFromCodePoint","value":{value}}},"default":{{"kind":"stringLiteral","value":"-"}}}}"""
        // Every code point in [a, b), lowered and uppered.
        let all = $"""{{"kind":"stringJoin","list":{{"kind":"listMap","source":{{"kind":"listRange","start":{p "a"},"end":{p "b"}}},"function":"chr"}},"separator":{{"kind":"stringLiteral","value":""}}}}"""
        let checkedOf = $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 0}}},"whenTrue":{{"kind":"resultErr","okType":{int64Type},"error":{{"kind":"stringLiteral","value":"no"}}}},"whenFalse":{{"kind":"resultOk","errorType":{stringType},"value":{p "n"}}}}}"""
        let get key = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "store"},"capability":"Store","operation":"get","arguments":[{{"parameter":"key","value":{key}}},{{"parameter":"slot","value":{lit 1}}}]}}"""
        let count = $"""{{"kind":"capabilityInvoke","capabilityValue":{p "store"},"capability":"Store","operation":"count","arguments":[]}}"""
        // A tail self call: sum(n, acc) = n < 1 ? acc : sum(n - 1, acc + n), a loop on both backends.
        // grow(n, total, t) = n < 1 ? total + |t| : grow(n - 1, total + |t|, t + "é😀ab"): a long String measured
        // after each append (Zig counts only the new tail, C# carries the count through the concatenation).
        let growBody =
            let measured = $"""{{"kind":"int64WrappingAdd","left":{p "total"},"right":{unary "stringLength" "t"}}}"""
            let appended = $"""{{"kind":"stringConcat","left":{p "t"},"right":{{"kind":"stringLiteral","value":"é😀ab"}}}}"""
            let next = call "grow" [ "n", $"""{{"kind":"int64WrappingSubtract","left":{p "n"},"right":{lit 1}}}"""; "total", measured; "t", appended ]
            $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 1}}},"whenTrue":{measured},"whenFalse":{next}}}"""
        let sumBody =
            let next = call "sum" [ "n", $"""{{"kind":"int64WrappingSubtract","left":{p "n"},"right":{lit 1}}}"""; "acc", $"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{p "n"}}}""" ]
            $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 1}}},"whenTrue":{p "acc"},"whenFalse":{next}}}"""
        [ // Int64, Bool and tail self calls.
          for kind in int64Operations do fn kind ab int64Type (binary kind)
          for kind in [ "int64ShiftLeft"; "int64ShiftRight" ] do fn kind ab int64Type $"""{{"kind":"{kind}","value":{p "a"},"count":{p "b"}}}"""
          fn "less" ab boolType (binary "int64LessThan")
          fn "absNot" [ "a", int64Type ] int64Type $"""{{"kind":"int64BitNot","value":{{"kind":"int64WrappingAbs","value":{p "a"}}}}}"""
          $"""{{"id":"c","op":"simulateCreateFunctionsByName","functions":[{{"displayName":"sum","exported":true,"parameters":[{declare [ "n", int64Type; "acc", int64Type ]}],"resultType":{int64Type},"requiredEffects":[],"body":{sumBody}}}]}}"""
          fn "sumTo" [ "n", int64Type ] int64Type (call "sum" [ "n", p "n"; "acc", lit 0 ])
          // Strings, Options, Results, Products and Unions.
          createType "Point" $"""{{"kind":"product","fields":[{field "x" int64Type},{field "y" int64Type}]}}"""
          createType "Shape" $"""{{"kind":"union","variants":[{{"displayName":"Circle","fields":[{field "r" int64Type}]}},{{"displayName":"Rect","fields":[{field "w" int64Type},{field "h" int64Type}]}},{{"displayName":"Empty","fields":[]}}]}}"""
          fn "area" [ "s", shapeType ] int64Type
              $"""{{"kind":"matchUnion","type":"Shape","input":{p "s"},"arms":[{{"variant":"Circle","body":{mul (mul (shapeField "Circle" "r") (shapeField "Circle" "r")) (lit 3)}}},{{"variant":"Rect","body":{mul (shapeField "Rect" "w") (shapeField "Rect" "h")}}},{{"variant":"Empty","body":{lit 0}}}]}}"""
          fn "rect" [ "w", int64Type; "h", int64Type ] shapeType
              $"""{{"kind":"unionConstruct","type":"Shape","variant":"Rect","fields":[{{"field":"w","value":{p "w"}}},{{"field":"h","value":{p "h"}}}]}}"""
          fn "shift" [ "pt", pointType; "dx", int64Type ] pointType
              $"""{{"kind":"productUpdate","source":{p "pt"},"type":"Point","fields":[{{"field":"x","value":{{"kind":"int64WrappingAdd","left":{{"kind":"fieldAccess","source":{p "pt"},"type":"Point","field":"x"}},"right":{p "dx"}}}}}]}}"""
          fn "origin" [] pointType $"""{{"kind":"productConstruct","type":"Point","fields":[{{"field":"x","value":{lit 0}}},{{"field":"y","value":{lit 0}}}]}}"""
          fn "same" [ "a", pointType; "b", pointType ] boolType (binary "valueEqual")
          fn "greet" [ "name", stringType ] stringType $"""{{"kind":"stringConcat","left":{{"kind":"stringLiteral","value":"héllo, "}},"right":{p "name"}}}"""
          fn "size" [ "name", stringType ] int64Type (unary "stringLength" "name")
          $"""{{"id":"g","op":"simulateCreateFunctionsByName","functions":[{{"displayName":"grow","exported":true,"parameters":[{declare [ "n", int64Type; "total", int64Type; "t", stringType ]}],"resultType":{int64Type},"requiredEffects":[],"body":{growBody}}}]}}"""
          fn "describe" [ "n", int64Type ] stringType (unary "int64ToString" "n")
          fn "positive" [ "n", int64Type ] optionInt
              $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 0}}},"whenTrue":{{"kind":"optionNone","itemType":{int64Type}}},"whenFalse":{{"kind":"optionSome","value":{p "n"}}}}}"""
          fn "orZero" [ "o", optionInt ] int64Type $"""{{"kind":"matchOption","input":{p "o"},"someBody":{slot "value"},"noneBody":{lit 0}}}"""
          fn "checked" [ "n", int64Type ] resultIntText
              $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 0}}},"whenTrue":{{"kind":"resultErr","okType":{int64Type},"error":{{"kind":"stringLiteral","value":"negative ✗"}}}},"whenFalse":{{"kind":"resultOk","errorType":{stringType},"value":{p "n"}}}}}"""
          fn "settle" [ "r", resultIntText ] stringType
              $"""{{"kind":"matchResult","input":{p "r"},"okBody":{{"kind":"int64ToString","value":{slot "value"}}},"errBody":{slot "error"}}}"""
          // Lists, list callbacks and generic functions.
          fn "firstOr" [ "items", listOf e; "fallback", e ] e
              $"""{{"kind":"matchOption","input":{{"kind":"listItemAt","list":{p "items"},"index":{lit 0}}},"someBody":{slot "value"},"noneBody":{p "fallback"}}}"""
          fn "firstNum" [ "xs", ints ] int64Type (call "firstOr" [ "items", p "xs"; "fallback", lit -1 ])
          fn "firstName" [ "names", texts ] stringType (call "firstOr" [ "items", p "names"; "fallback", """{"kind":"stringLiteral","value":"none"}""" ])
          fn "add" [ "acc", int64Type; "item", int64Type ] int64Type $"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{p "item"}}}"""
          fn "listSum" [ "xs", ints ] int64Type $"""{{"kind":"listFold","source":{p "xs"},"seed":{lit 0},"function":"add"}}"""
          fn "scale" [ "factor", int64Type; "x", int64Type ] int64Type (mul (p "x") (p "factor"))
          fn "scaled" [ "xs", ints; "k", int64Type ] ints $"""{{"kind":"listMap","source":{p "xs"},"function":"scale","context":[{{"parameter":"factor","value":{p "k"}}}]}}"""
          fn "isEven" [ "x", int64Type ] boolType $"""{{"kind":"valueEqual","left":{{"kind":"int64FloorModulo","left":{p "x"},"right":{lit 2}}},"right":{lit 0}}}"""
          fn "evens" [ "xs", ints ] ints $"""{{"kind":"listFilter","source":{p "xs"},"function":"isEven"}}"""
          fn "edit" [ "xs", ints ] ints
              $"""{{"kind":"listReverse","value":{{"kind":"listAppendItem","list":{{"kind":"listInsertAt","list":{{"kind":"listRemoveAt","list":{{"kind":"listSetAt","list":{{"kind":"listSlice","list":{p "xs"},"start":{lit 1},"end":{lit 5}}},"index":{lit 1},"item":{lit 99}}},"index":{lit 0}}},"index":{lit 1},"item":{lit 7}}},"item":{lit 42}}}}}"""
          fn "upTo" [ "n", int64Type ] ints $"""{{"kind":"listRange","start":{lit 0},"end":{p "n"}}}"""
          // Float64, Maps and Sets.
          for kind in floatBinary do fn kind [ "a", float; "b", float ] float (binary kind)
          for kind in [ "float64Equal"; "float64LessThan" ] do fn kind [ "a", float; "b", float ] boolType (binary kind)
          for kind in floatUnary do fn kind [ "a", float ] float (unary kind "a")
          for kind in floatTests do fn kind [ "a", float ] boolType (unary kind "a")
          for kind in floatToInt do fn kind [ "a", float ] optionInt (unary kind "a")
          fn "toFloat" [ "n", int64Type ] float (unary "int64ToFloat64" "n")
          fn "bump" [ "acc", counts; "word", stringType ] counts
              $"""{{"kind":"mapInsert","map":{p "acc"},"key":{p "word"},"value":{{"kind":"int64WrappingAdd","left":{{"kind":"mapGetOrDefault","map":{p "acc"},"key":{p "word"},"default":{lit 0}}},"right":{lit 1}}}}}"""
          fn "count" [ "words", texts ] counts
              $"""{{"kind":"listFold","source":{p "words"},"seed":{{"kind":"mapEmpty","keyType":{stringType},"valueType":{int64Type}}},"function":"bump"}}"""
          fn "merge" [ "a", counts; "b", counts ] counts (binary "mapUnion")
          fn "dropKey" [ "m", counts; "k", stringType ] counts $"""{{"kind":"mapRemove","map":{p "m"},"key":{p "k"}}}"""
          fn "keysOf" [ "m", counts ] texts (unary "mapKeys" "m")
          fn "sameMap" [ "a", counts; "b", counts ] boolType (binary "valueEqual")
          fn "unique" [ "xs", ints ] intSet (unary "setFromList" "xs")
          for kind in setOperations do fn kind [ "a", intSet; "b", intSet ] intSet (binary kind)
          fn "subset" [ "a", intSet; "b", intSet ] boolType (binary "setIsSubset")
          fn "sameSet" [ "a", intSet; "b", intSet ] boolType (binary "valueEqual")
          // Bytes.
          fn "fill" [ "n", int64Type; "v", int64Type ] bytesType $"""{{"kind":"bytesFill","length":{p "n"},"value":{p "v"}}}"""
          fn "bytesEdit" [ "s", stringType; "i", int64Type; "v", int64Type ] stringType
              $"""{{"kind":"bytesToLatin1","value":{{"kind":"bytesAppend","bytes":{{"kind":"bytesSet","bytes":{from "s"},"index":{p "i"},"value":{p "v"}}},"value":{p "v"}}}}}"""
          fn "at" [ "s", stringType; "i", int64Type ] optionInt $"""{{"kind":"bytesGet","bytes":{from "s"},"index":{p "i"}}}"""
          fn "cut" [ "s", stringType; "i", int64Type; "j", int64Type ] bytesType $"""{{"kind":"bytesSlice","bytes":{from "s"},"start":{p "i"},"end":{p "j"}}}"""
          fn "bytesJoin" st bytesType $"""{{"kind":"bytesConcat","left":{from "s"},"right":{from "t"}}}"""
          fn "bytesSize" [ "s", stringType ] int64Type $"""{{"kind":"bytesLength","value":{from "s"}}}"""
          fn "sameBytes" st boolType $"""{{"kind":"valueEqual","left":{from "s"},"right":{from "t"}}}"""
          // Bits32.
          fn "toBits" [ "n", int64Type ] bits32Type (unary "bits32FromInt64" "n")
          fn "fromBits" [ "a", bits32Type ] int64Type (unary "bits32ToInt64" "a")
          fn "bits32Not" [ "a", bits32Type ] bits32Type (unary "bits32Not" "a")
          for kind in bits32Binary do fn kind [ "a", bits32Type; "b", bits32Type ] bits32Type (binary kind)
          for kind in bits32Shifts do fn kind [ "a", bits32Type; "b", int64Type ] bits32Type $"""{{"kind":"{kind}","value":{p "a"},"amount":{p "b"}}}"""
          fn "sameBits" [ "a", bits32Type; "b", bits32Type ] boolType (binary "valueEqual")
          fn "uniqueBits" [ "xs", listOf bits32Type ] (listOf bits32Type) $"""{{"kind":"setToList","value":{{"kind":"setFromList","value":{p "xs"}}}}}"""
          // Scripted fake capabilities.
          """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}"""
          $"""{{"id":"k","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Store","operations":[{{"displayName":"get","parameters":[{declare [ "key", stringType; "slot", int64Type ]}],"resultType":{generic "option" [ stringType ]},"effect":"io"}},{{"displayName":"count","parameters":[],"resultType":{int64Type},"effect":"io"}}]}}"""
          (fn "look" [ "store", storeType; "name", stringType ] stringType
              $"""{{"kind":"bind","value":{count},"body":{{"kind":"stringConcat","left":{{"kind":"int64ToString","value":{slot "value"}}},"right":{{"kind":"matchOption","input":{get (p "name")},"someBody":{slot "value"},"noneBody":{{"kind":"stringLiteral","value":"-"}}}}}}}}""")
              .Replace("\"requiredEffects\":[]", "\"requiredEffects\":[\"io\"]")
          // Sorting, min and max by, and function values; `before` orders by the last digit only.
          fn "before" [ "descending", boolType; "left", int64Type; "right", int64Type ] boolType
              $"""{{"kind":"conditional","condition":{p "descending"},"whenTrue":{{"kind":"int64LessThan","left":{modulo "right"},"right":{modulo "left"}}},"whenFalse":{{"kind":"int64LessThan","left":{modulo "left"},"right":{modulo "right"}}}}}"""
          fn "sorted" [ "xs", ints; "down", boolType ] ints $"""{{"kind":"listSortBy","source":{p "xs"},"function":"before","context":[{{"parameter":"descending","value":{p "down"}}}]}}"""
          fn "smallest" [ "xs", ints ] optionInt $"""{{"kind":"listMinBy","source":{p "xs"},"function":"before","context":[{{"parameter":"descending","value":{{"kind":"boolLiteral","value":false}}}}]}}"""
          fn "largest" [ "xs", ints ] optionInt $"""{{"kind":"listMaxBy","source":{p "xs"},"function":"before","context":[{{"parameter":"descending","value":{{"kind":"boolLiteral","value":false}}}}]}}"""
          fn "twice" [ "x", int64Type ] int64Type (mul (p "x") (lit 2))
          fn "apply" [ "f", unaryFunction; "x", int64Type ] int64Type
              $"""{{"kind":"functionInvoke","functionValue":{p "f"},"arguments":[{{"kind":"functionInvoke","functionValue":{p "f"},"arguments":[{p "x"}]}}]}}"""
          fn "quadruple" [ "x", int64Type ] int64Type (call "apply" [ "f", """{"kind":"functionReference","function":"twice"}"""; "x", p "x" ])
          // String search, split and join.
          fn "slice" [ "s", stringType; "i", int64Type; "j", int64Type ] stringType $"""{{"kind":"stringSlice","value":{p "s"},"start":{p "i"},"end":{p "j"}}}"""
          fn "indexOf" sti optionInt $"""{{"kind":"stringIndexOf","value":{p "s"},"substring":{p "t"},"from":{p "i"}}}"""
          fn "lastBefore" sti optionInt $"""{{"kind":"stringLastIndexOfBefore","value":{p "s"},"substring":{p "t"},"end":{p "i"}}}"""
          fn "span" sti int64Type $"""{{"kind":"stringSpan","value":{p "s"},"set":{p "t"},"from":{p "i"}}}"""
          fn "spanBack" sti int64Type $"""{{"kind":"stringSpanBack","value":{p "s"},"set":{p "t"},"end":{p "i"}}}"""
          fn "lastIndexOf" st optionInt $"""{{"kind":"stringLastIndexOf","value":{p "s"},"substring":{p "t"}}}"""
          fn "contains" st boolType $"""{{"kind":"stringContains","value":{p "s"},"substring":{p "t"}}}"""
          fn "startsWith" st boolType $"""{{"kind":"stringStartsWith","value":{p "s"},"prefix":{p "t"}}}"""
          fn "endsWith" st boolType $"""{{"kind":"stringEndsWith","value":{p "s"},"suffix":{p "t"}}}"""
          fn "stringLess" st boolType $"""{{"kind":"stringLessThan","left":{p "s"},"right":{p "t"}}}"""
          fn "split" st texts $"""{{"kind":"stringSplit","value":{p "s"},"separator":{p "t"}}}"""
          fn "lines" st texts $"""{{"kind":"stringSplitLines","value":{p "s"},"breaks":{p "t"}}}"""
          fn "replace" st stringType $"""{{"kind":"stringReplace","value":{p "s"},"pattern":{p "t"},"replacement":{{"kind":"stringLiteral","value":"<😀>"}}}}"""
          fn "stringJoin" [ "xs", texts; "t", stringType ] stringType $"""{{"kind":"stringJoin","list":{p "xs"},"separator":{p "t"}}}"""
          // Case mapping, trimming, code points and UTF-8.
          for kind in stringUnary do fn kind [ "s", stringType ] stringType (unary kind "s")
          fn "codePoints" [ "s", stringType ] texts (unary "stringCodePoints" "s")
          fn "byteCount" [ "s", stringType ] int64Type (unary "stringUtf8ByteCount" "s")
          fn "ord" [ "s", stringType ] optionInt (unary "stringToCodePoint" "s")
          fn "repeat" [ "s", stringType; "n", int64Type ] stringType $"""{{"kind":"stringRepeat","text":{p "s"},"count":{p "n"}}}"""
          fn "chr" [ "n", int64Type ] stringType (chr (p "n"))
          fn "cases" ab stringType $"""{{"kind":"stringConcat","left":{{"kind":"stringToLower","value":{all}}},"right":{{"kind":"stringToUpper","value":{all}}}}}"""
          // Number text, Float64 rounding and checked Int64.
          fn "hex" [ "n", int64Type ] stringType (unary "int64ToHexString" "n")
          fn "parse" [ "s", stringType ] optionInt (unary "stringParseInt64" "s")
          fn "parseHex" [ "s", stringType ] optionInt (unary "stringParseInt64Hex" "s")
          fn "parseFloat" [ "s", stringType ] (generic "option" [ float ]) (unary "stringParseFloat64" "s")
          fn "show" [ "x", float ] stringType (unary "float64ToString" "x")
          fn "round" [ "x", float; "n", int64Type ] float $"""{{"kind":"float64Round","value":{p "x"},"digits":{p "n"}}}"""
          for kind in checkedOperations do fn kind ab optionInt (binary kind)
          // The remaining list, map and set operations.
          fn "half" [ "x", int64Type ] int64Type $"""{{"kind":"int64FloorDivide","left":{p "x"},"right":{lit 2}}}"""
          fn "isPositive" [ "x", int64Type ] boolType $"""{{"kind":"int64GreaterThan","left":{p "x"},"right":{lit 0}}}"""
          fn "mul" ab int64Type (mul (p "a") (p "b"))
          fn "twiceList" [ "x", int64Type ] ints
              $"""{{"kind":"listConcat","left":{{"kind":"listEmpty","itemType":{int64Type}}},"right":{{"kind":"listAppendItem","list":{{"kind":"listAppendItem","list":{{"kind":"listEmpty","itemType":{int64Type}}},"item":{p "x"}}},"item":{p "x"}}}}}"""
          fn "keyAdd" [ "acc", int64Type; "k", int64Type; "v", int64Type ] int64Type $"""{{"kind":"int64WrappingAdd","left":{p "acc"},"right":{mul (p "k") (p "v")}}}"""
          fn "keyBig" [ "k", int64Type; "v", int64Type ] boolType $"""{{"kind":"int64GreaterThanOrEqual","left":{p "v"},"right":{p "k"}}}"""
          fn "keyLe" [ "k", int64Type; "v", int64Type ] int64Type $"""{{"kind":"int64WrappingSubtract","left":{p "v"},"right":{p "k"}}}"""
          fn "small" [ "x", int64Type ] boolType $"""{{"kind":"int64LessThanOrEqual","left":{p "x"},"right":{lit 3}}}"""
          fn "isTwo" [ "x", int64Type ] boolType $"""{{"kind":"int64Equal","left":{p "x"},"right":{lit 2}}}"""
          for name, result, body in listCases do fn name [ "xs", ints ] result body
          fn "okOr" [ "n", int64Type ] int64Type $"""{{"kind":"resultGetOrDefault","result":{checkedOf},"default":{lit -1}}}"""
          fn "isOk" [ "n", int64Type ] boolType $"""{{"kind":"resultIsOk","value":{checkedOf}}}"""
          fn "bumpBy" [ "by", int64Type; "n", int64Type ] int64Type $"""{{"kind":"int64WrappingAdd","left":{p "n"},"right":{p "by"}}}"""
          fn "shout" [ "text", stringType ] stringType $"""{{"kind":"stringConcat","left":{p "text"},"right":{{"kind":"stringLiteral","value":"!"}}}}"""
          fn "keepSmall" [ "n", int64Type ] optionInt $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 5}}},"whenTrue":{{"kind":"optionSome","value":{p "n"}}},"whenFalse":{{"kind":"optionNone","itemType":{int64Type}}}}}"""
          fn "okIfSmall" [ "n", int64Type ] (resultOf int64Type) $"""{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{p "n"},"right":{lit 5}}},"whenTrue":{{"kind":"resultOk","errorType":{stringType},"value":{p "n"}}},"whenFalse":{{"kind":"resultErr","okType":{int64Type},"error":{{"kind":"stringLiteral","value":"large"}}}}}}"""
          let bumpOne = $"""[{{"parameter":"by","value":{lit 1}}}]"""
          let okOption = $"""{{"kind":"resultOkValue","value":{checkedOf}}}"""
          fn "optionMapped" [ "n", int64Type ] optionInt $"""{{"kind":"optionMap","option":{okOption},"function":"bumpBy","context":{bumpOne}}}"""
          fn "optionBound" [ "n", int64Type ] optionInt $"""{{"kind":"optionAndThen","option":{okOption},"function":"keepSmall"}}"""
          fn "resultMapped" [ "n", int64Type ] (resultOf int64Type) $"""{{"kind":"resultMap","result":{checkedOf},"function":"bumpBy","context":{bumpOne}}}"""
          fn "errorMapped" [ "n", int64Type ] (resultOf int64Type) $"""{{"kind":"resultMapErr","result":{checkedOf},"function":"shout"}}"""
          fn "resultBound" [ "n", int64Type ] (resultOf int64Type) $"""{{"kind":"resultAndThen","result":{checkedOf},"function":"okIfSmall"}}"""
          fn "okValue" [ "n", int64Type ] optionInt $"""{{"kind":"resultOkValue","value":{checkedOf}}}"""
          fn "errValue" [ "n", int64Type ] (generic "option" [ stringType ]) $"""{{"kind":"resultErrValue","value":{checkedOf}}}"""
          fn "okOrError" [ "n", int64Type ] (resultOf int64Type) $"""{{"kind":"optionOkOr","option":{{"kind":"resultOkValue","value":{checkedOf}}},"error":{{"kind":"stringLiteral","value":"none"}}}}"""
          yield! effectfulDeclarations ]

    /// The exception boundary, resource, abort and task calls, with scripted Files and Svc fakes.
    let private effectfulCases =
        let typed kind (value: string) = if kind = "unit" then """{"kind":"unit"}""" else $"""{{"kind":"{kind}","value":{value}}}"""
        let primitive kind = $"""{{"kind":"primitive","primitive":"{kind}"}}"""
        let okFile kind (value: string) = $"""{{"kind":"return","value":{{"kind":"result","okType":{primitive kind},"errorType":{stringType},"isOk":true,"value":{typed kind value}}}}}"""
        let errFile kind = $"""{{"kind":"return","value":{{"kind":"result","okType":{primitive kind},"errorType":{stringType},"isOk":false,"value":{{"kind":"string","value":"no"}}}}}}"""
        let throw message = $"""{{"kind":"throw","message":"{message}"}}"""
        let files openResponse readResponses closeResponses =
            $"""{{"kind":"fakeCapability","capability":"Files","operations":[{{"operation":"open","responses":[{openResponse}]}},{{"operation":"read","responses":[{readResponses}]}},{{"operation":"close","responses":[{closeResponses}]}}]}}"""
        let opened = okFile "int64" "\"7\""
        let closed = okFile "unit" "\"unit\""
        let text = okFile "string" "\"text\""
        // Each script with whether withFile, guarded_recover, guarded_rethrowCancellation, guarded_rethrowAll and abortFile succeed.
        let scripts =
            [ files opened text closed, [ true; true; true; true; true ]
              files (errFile "int64") "" "", [ true; true; true; true; true ]
              files opened (throw "disk ✗") closed, [ false; true; true; false; false ]
              files opened (throw "disk ✗") (throw "disk ✗"), [ false; true; true; false; false ]
              files opened text (errFile "unit"), [ true; true; true; true; true ]
              files opened (errFile "string") (errFile "unit"), [ true; true; true; true; true ]
              files (throw "disk ✗") "" "", [ false; true; true; false; false ] ]
        let response (n: int) (delay: int option) =
            let delayed = delay |> Option.map (fun d -> $""","delayMillis":"{d}" """) |> Option.defaultValue ""
            $"""{{"kind":"return","value":{{"kind":"int64","value":"{n}"}}{delayed}}}"""
        let fake (work: string list) (hold: string list) =
            $"""{{"kind":"fakeCapability","capability":"Svc","operations":[{{"operation":"work","responses":[{String.Join(",", work)}]}},{{"operation":"hold","responses":[{String.Join(",", hold)}]}}]}}"""
        let awaiting = $"""{{"kind":"awaitCancellation","value":{int64 100L}}}"""
        let calcFake = $"""{{"kind":"fakeCapability","capability":"Calc","operations":[{{"operation":"scale","responses":[{{"kind":"return","value":{int64 9L}}}]}},{{"operation":"label","responses":[]}}]}}"""
        let throws = throw "task ✗"
        [ for script, succeeds in scripts do
            for name, succeeds in List.zip [ "withFile"; "guarded_recover"; "guarded_rethrowCancellation"; "guarded_rethrowAll"; "abortFile" ] succeeds do
                succeeds, name, [ script ]
          true, "programCalc", [ "true"; "21" ]
          true, "programCalc", [ "false"; "21" ]
          true, "programLabel", [ "\"ab✓\"" ]
          true, "useCalc", [ calcFake; "5" ]
          true, "fanOut", [ fake [ response 10 (Some 30); response 20 (Some 10); response 30 None ] []; "[1,2,3]" ]
          false, "fanOut", [ fake [ response 10 (Some 5) ] []; "[1,2]" ]
          true, "race", [ fake [ response 10 (Some 40); response 20 (Some 20) ] [] ]
          true, "race", [ fake [ response 10 (Some 40); response 20 (Some 30) ] [] ]
          true, "cancelled", [ fake [] [ awaiting ] ]
          false, "cancelled", [ fake [] [] ]
          true, "shielded", [ fake [ response 4 (Some 3) ] [] ]
          true, "sumWork", [ fake [ response 1 None; response 2 None; response 3 None ] []; "[1,2,3]" ]
          false, "manyTries", [ fake [] [] ]
          true, "handoff", [ fake [] [] ]
          true, "timeout", [ fake [] [] ]
          false, "stuck", [ fake [] [] ]
          false, "manyStuck", [ fake [] [] ]
          false, "manyStuckCancel", [ fake [] [] ]
          false, "failWait", [ fake [ throws ] [] ]
          false, "failClose", [ fake [ response 1 (Some 5); throws ] [] ]
          false, "failClose", [ fake [ throws; response 2 None ] [] ]
          true, "sumChecked", [ fake [ response 1 None; response -2 None; response 3 None ] []; "[1,2,3]" ]
          true, "sumChecked", [ fake [ response 1 None; response 2 None ] []; "[1,2]" ] ]

    /// The calls, each `true` when both backends must succeed; a `false` call fails on both.
    let private cases =
        let json (text: string) = JsonSerializer.Serialize text
        let edges = [ Int64.MinValue; -7L; -1L; 0L; 1L; 3L; 64L; Int64.MaxValue ]
        let floats = [ "0.5"; "-2.5"; "3"; "\"-0\""; "1e308"; "\"NaN\""; "\"Infinity\""; "-7.25" ]
        let bytesTexts = [ "\"\""; "\"abc\""; "\"\\u00ff\\u0000x\""; "\"\\u20ac\"" ]
        let strings = [ "\"\""; "\"aXbXXc\""; "\"héllo wörld\""; "\"a😀b😀\""; "\"x\\r\\ny\\n\\u2028z\\r\""; "\"€uro€\"" ]
        let patterns = [ "\"\""; "\"X\""; "\"😀\""; "\"ö\""; "\"\\r\\n\\u2028\""; "\"€\""; "\"lo wö\"" ]
        let caseStrings =
            [ "\"\""; "\"  \\t a b \\u00a0\\u3000\\n\""; "\"\\u200bx\\u200b\""; "\"Straße İstanbul ıi ΣΑΣ σς \\u212a k ǅ\""; "\"😀𐐷𐐏\""
              "\"\\u00c0\\u00ff\\u0080\\u00e2\\u0082\\u00ac\""; "\"\\u00ed\\u00a0\\u0080A\""; "\"\\u00f0\\u009f\\u0098\""; "\"\\u0100\\u00e2\\u0082\"" ]
        let checkedEdges = [ Int64.MinValue; -255L; -1L; 0L; 2L; 255L; 3037000500L; Int64.MaxValue ]
        let numbers =
            [ ""; "-"; "0"; "-0"; "007"; "+1"; " 1"; "1_0"; "12a"; "١"; "FFff"; "0x10"; "7fffffffffffffff"; "-8000000000000000"; "8000000000000000"
              "10000000000000000"; "9223372036854775807"; "9223372036854775808"; "-9223372036854775808"; "-9223372036854775809"
              "0.1"; "1e400"; "-1e-400"; "1E+2"; ".5"; "5."; "1e"; "00.5"; "1e99999999999999999999"; "NaN"; "inf"; "1.0e-5"; "2.675" ]
        let shownFloats =
            [ "0.1"; "1e16"; "1e17"; "1e-5"; "0.0001"; "\"-0\""; "\"NaN\""; "\"Infinity\""; "\"-Infinity\""; "5e-324"; "1.7976931348623157e308"
              "2.675"; "0.5"; "1.5"; "-2.5"; "123.456"; "100"; "1250"; "-0.004"; "1e22"; "1e23"; "123456789012345680"; "0.30000000000000004" ]
        let ordered responses = $"""{{"kind":"fakeCapability","capability":"Store","operations":[{{"operation":"count","responses":[{{"kind":"return","value":{int64 3L}}}]}},{{"operation":"get","responses":[{responses}]}}]}}"""
        let some text = $"""{{"kind":"return","value":{{"kind":"option","itemKind":"string","hasValue":true,"value":"{text}"}}}}"""
        let keyed = $"""{{"kind":"fakeCapability","capability":"Store","operations":[{{"operation":"count","responses":[{{"kind":"return","value":{int64 0L}}}]}},{{"operation":"get","cases":[{{"arguments":[{{"parameter":"key","value":{{"kind":"string","value":"ä 😀"}}}},{{"parameter":"slot","value":{int64 1L}}}],"responses":[{some "found"}]}}]}}]}}"""
        [ for kind in int64Operations @ [ "int64ShiftLeft"; "int64ShiftRight"; "less" ] do
            for a in edges do
                for b in edges do true, kind, [ int64 a; int64 b ]
          for a in edges do true, "absNot", [ int64 a ]
          true, "sumTo", [ int64 1000000L ]; true, "sumTo", [ int64 0L ]
          for shape in [ """{"Circle":{"r":2}}"""; """{"Rect":{"w":3,"h":4}}"""; """{"Empty":{}}""" ] do true, "area", [ shape ]
          true, "rect", [ "5"; "6" ]
          true, "shift", [ """{"x":1,"y":2}"""; "10" ]
          true, "origin", []
          true, "same", [ """{"x":1,"y":2}"""; """{"x":1,"y":2}""" ]
          true, "same", [ """{"x":1,"y":2}"""; """{"x":1,"y":3}""" ]
          for name in [ "\"\""; "\"wörld 😀\""; "\"a\\\"b\\nc\"" ] do
              true, "greet", [ name ]
              true, "size", [ name ]
          // Its token is over Linux's 128 KiB argv string limit, so it must reach the runner by file.
          true, "size", [ json (String('a', 60000)) ]
          true, "grow", [ int64 60L; int64 0L; json (String.replicate 100 "ä😀z") ]
          for n in [ "-9223372036854775808"; "-1"; "0"; "42" ] do
              for name in [ "describe"; "positive"; "checked" ] do true, name, [ n ]
          for o in [ "null"; "7" ] do true, "orZero", [ o ]
          for r in [ """{"ok":"5"}"""; """{"error":"bad ✗"}""" ] do true, "settle", [ r ]
          for xs in [ "[]"; "[5]"; "[1,2,3,4,5,6,-7]" ] do
              for name in [ "firstNum"; "listSum"; "evens"; "edit" ] do true, name, [ xs ]
              true, "scaled", [ xs; "3" ]
          for names in [ "[]"; "[\"ä\",\"b\"]" ] do true, "firstName", [ names ]
          for n in [ "0"; "5" ] do true, "upTo", [ n ]
          // An allocation the runtime cannot make is fatal on both backends.
          false, "upTo", [ "9223372036854775807" ]
          for a in floats do
              for kind in floatUnary @ floatTests @ floatToInt do true, kind, [ a ]
              // Pairs over a smaller set keep the run short; every value still meets every operation.
              for b in [ "-2.5"; "\"-0\""; "\"NaN\""; "\"Infinity\""; "3" ] do
                  for kind in floatBinary @ [ "float64Equal"; "float64LessThan" ] do true, kind, [ a; b ]
          for n in [ "-9007199254740993"; "0"; "9223372036854775807" ] do true, "toFloat", [ n ]
          let bits = [ "0"; "1"; "2147483647"; "2147483648"; "4294967295" ]
          for n in edges @ [ 2147483648L; 4294967295L; 4294967296L; 4294967301L ] do true, "toBits", [ int64 n ]
          for a in bits do
              true, "fromBits", [ a ]
              true, "bits32Not", [ a ]
              for b in bits do
                  for kind in bits32Binary @ [ "sameBits" ] do true, kind, [ a; b ]
              for n in [ Int64.MinValue; -33L; -1L; 0L; 1L; 31L; 32L; 33L; 64L; Int64.MaxValue ] do
                  for kind in bits32Shifts do true, kind, [ a; int64 n ]
          // More items than the Zig runtime scans without an index, so the Set hashes its Bits32 items.
          true, "uniqueBits", [ "[" + String.Join(",", [ for i in 0L .. 79L -> string ((i % 40L) * 2147483647L % 4294967296L) ]) + "]" ]
          true, "count", [ """["a","b","a","c","b","a"]""" ]
          let m1 = """[["a",1],["b",2]]"""
          let m2 = """[["b",20],["c",3]]"""
          for a, b in [ m1, m2; m2, m1; m1, "[]" ] do
              true, "merge", [ a; b ]
              true, "sameMap", [ a; b ]
          true, "sameMap", [ m1; """[["b",2],["a",1]]""" ]
          true, "dropKey", [ m1; "\"a\"" ]
          true, "keysOf", [ m2 ]
          true, "unique", [ "[3,1,3,2,1]" ]
          for a, b in [ "[1,2,3]", "[3,4]"; "[]", "[1]"; "[2,1]", "[1,2,3]" ] do
              for kind in setOperations @ [ "subset"; "sameSet" ] do true, kind, [ a; b ]
          for n, v in [ "-1", "5"; "0", "1"; "3", "-1"; "4", "256" ] do true, "fill", [ n; v ]
          for s in bytesTexts do
              true, "bytesSize", [ s ]
              for i in [ "-1"; "0"; "2"; "9" ] do
                  true, "bytesEdit", [ s; i; "300" ]
                  true, "at", [ s; i ]
                  true, "cut", [ s; i; "2" ]
              for t in bytesTexts do
                  true, "bytesJoin", [ s; t ]
                  true, "sameBytes", [ s; t ]
          true, "look", [ ordered (some "x"); "\"k\"" ]
          false, "look", [ ordered """{"kind":"throw","message":"down ✗"}"""; "\"k\"" ]
          false, "look", [ ordered ""; "\"k\"" ]
          true, "look", [ keyed; "\"ä 😀\"" ]
          false, "look", [ keyed; "\"other\"" ]
          for xs in [ "[]"; "[7]"; "[13,2,23,5,42,3,12,33]" ] do
              for down in [ "true"; "false" ] do true, "sorted", [ xs; down ]
              true, "smallest", [ xs ]
              true, "largest", [ xs ]
          for x in [ "3"; "-9223372036854775808" ] do true, "quadruple", [ x ]
          for s in strings do
              for i in [ "-1"; "0"; "2"; "100" ] do
                  for j in [ "-1"; "1"; "3"; "100" ] do true, "slice", [ s; i; j ]
              for t in patterns @ strings do
                  for name in [ "lastIndexOf"; "contains"; "startsWith"; "endsWith"; "stringLess"; "split"; "lines"; "replace" ] do true, name, [ s; t ]
              for t in patterns do
                  for i in [ "-1"; "0"; "2"; "100" ] do
                      for name in [ "indexOf"; "lastBefore"; "span"; "spanBack" ] do true, name, [ s; t; i ]
          for xs in [ "[]"; "[\"\"]"; "[\"a\",\"\",\"😀\"]" ] do
              for t in [ "\"\""; "\", \"" ] do true, "stringJoin", [ xs; t ]
          for s in caseStrings do
              for kind in stringUnary @ [ "codePoints"; "byteCount"; "ord" ] do true, kind, [ s ]
              for n in [ "-1"; "0"; "3" ] do true, "repeat", [ s; n ]
          for s in [ "\"a\""; "\"😀\""; "\"é\"" ] do true, "ord", [ s ]
          for n in [ "-1"; "0"; "65"; "55296"; "57343"; "128512"; "1114111"; "1114112" ] do true, "chr", [ n ]
          // A result holds at most 65536 code units, and no code point above U+1EFFF has a case mapping.
          for start in 0 .. 16000 .. 0x1F000 do true, "cases", [ string start; string (start + 16000) ]
          for a in checkedEdges do
              true, "hex", [ int64 a ]
              for b in checkedEdges do
                  for kind in checkedOperations do true, kind, [ int64 a; int64 b ]
          for text in numbers do
              for name in [ "parse"; "parseHex"; "parseFloat" ] do true, name, [ json text ]
          for x in shownFloats do
              true, "show", [ x ]
              for n in [ "-1001"; "-2"; "0"; "1"; "2"; "15"; "400" ] do true, "round", [ x; n ]
          for value in [ "[]"; "[3]"; "[1,2,3,4,3,-5,2]" ] do
              for name, _, _ in listCases do true, name, [ value ]
          for n in [ "7"; "-2" ] do
              true, "okOr", [ n ]
              true, "isOk", [ n ]
              true, "okValue", [ n ]
              true, "errValue", [ n ]
              true, "okOrError", [ n ]
              for name in [ "optionMapped"; "optionBound"; "resultMapped"; "errorMapped"; "resultBound" ] do true, name, [ n ]
          yield! effectfulCases ]

    /// The calls made: successful cases that share a function and all but the last argument run as
    /// one call of `<name>Batch`, which maps the function over the last arguments, since every call
    /// starts a runner child; failing and single cases run alone.
    let private calls =
        // `<name>Batch` is a pure listMap, which cannot call an effectful function.
        let effectful = effectfulCases |> List.map (fun (_, name, _) -> name) |> set
        [ for (succeeds, name, leading), group in cases |> List.groupBy (fun (succeeds, name, arguments) -> succeeds, name, List.truncate (arguments.Length - 1) arguments) do
            if succeeds && group.Length > 1 && not (effectful.Contains name) then
                let lasts = group |> List.map (fun (_, _, arguments) -> List.last arguments)
                true, name + "Batch", leading @ [ "[" + String.Join(",", lasts) + "]" ]
            else yield! group ]

    /// `<name>Batch` for each batched function: its leading parameters, then a list of its last one.
    let private batchDeclarations =
        let batched = calls |> List.map (fun (_, name, _) -> name) |> set
        [ for declaration in declarations do
            let node = JsonNode.Parse declaration
            let name = match node["displayName"] with null -> "" | value -> value.GetValue<string>()
            if batched.Contains(name + "Batch") then
                let parameters = node["parameters"].AsArray() |> Seq.map (fun parameter -> parameter["displayName"].GetValue<string>(), parameter["type"].ToJsonString()) |> Seq.toList
                let leading = List.truncate (parameters.Length - 1) parameters
                let context = leading |> List.map (fun (name, _) -> $"""{{"parameter":"{name}","value":{p name}}}""")
                let body = $"""{{"kind":"listMap","source":{p "batch"},"function":"{name}","context":[{String.Join(",", context)}]}}"""
                let resultType = node["resultType"].ToJsonString()
                fn (name + "Batch") (leading @ [ "batch", listOf (snd (List.last parameters)) ]) (listOf resultType) body ]

    let private request backend name (arguments: string list) =
        $"""{{"id":"r","op":"runPureByName","function":"{name}","backend":"{backend}","arguments":[{String.Join(",", arguments)}]}}"""

    /// A reply without its backend-specific parts: `frames`, and a fatal failure's own crash report.
    let private comparable (reply: string) =
        let rec strip (node: JsonNode) =
            match node with
            | :? JsonObject as o ->
                o.Remove "frames" |> ignore
                // Each runtime reports a crash its own way (an OutOfMemoryException, an abort).
                if (match o["failureKind"] with null -> "" | kind -> kind.ToString()) = "fatal" then
                    o.Remove "message" |> ignore
                    o.Remove "exceptionType" |> ignore
                for KeyValue(_, child) in Seq.toArray o do strip child
            | :? JsonArray as a -> for child in a do strip child
            | _ -> ()
        let node = JsonNode.Parse reply
        strip node
        node.ToJsonString()

    [<Fact; Trait("Suite", "Extended")>]
    let ``The C# and Zig backends agree on pure functions`` () =
        withDirectory (fun directory ->
            let path = Path.Combine(directory, "workspace.json")
            publishWithZigHost "create" path (declarations @ batchDeclarations)
            // A host handles its requests in turn and builds a runner per function (one core each), so each
            // backend deals the functions round-robin over read-only hosts, half a core's worth per backend
            // (more oversubscribes: builds took up to 50 s, past the 60 s silence limit): a function's calls
            // stay on one host (one build), and every host builds about as many runners.
            let hosts = max 4 (Environment.ProcessorCount / 2)
            let replies backend =
                calls
                |> List.mapi (fun index (_, name, arguments) -> index, name, request backend name arguments)
                |> List.groupBy (fun (_, name, _) -> name)
                |> List.mapi (fun position (_, group) -> position % hosts, group)
                |> List.groupBy fst
                |> List.map (fun (_, groups) ->
                    let indexed = List.collect snd groups
                    let requests = indexed |> List.map (fun (_, _, request) -> request)
                    Threading.Tasks.Task.Run(fun () ->
                        let code, errors, replies = run [ "open"; "--snapshot"; path; "--read-only" ] requests
                        Assert.Equal(0, code)
                        Assert.Equal("", errors)
                        Assert.Equal(requests.Length, replies.Length)
                        List.zip (indexed |> List.map (fun (index, _, _) -> index)) replies))
            // Back in call order.
            let collect (shards: Threading.Tasks.Task<_> list) = shards |> List.collect (fun shard -> shard.Result) |> List.sortBy fst |> List.map snd
            let csharp, zig = replies "csharp", replies "zig"
            let disagreements =
                [ for (succeeds, name, arguments), csharp, zig in List.zip3 calls (collect csharp) (collect zig) do
                    let ok (reply: string) = (JsonNode.Parse reply).["ok"].GetValue<bool>()
                    if ok csharp <> succeeds || ok zig <> succeeds || comparable csharp <> comparable zig then
                        let call = String.Join(", ", arguments)
                        $"{name}({call})\n  C#: {csharp}\n Zig: {zig}" ]
            Assert.True(disagreements.IsEmpty, $"{disagreements.Length} of {calls.Length} calls disagree:\n" + String.Join("\n", List.truncate 20 disagreements)))
