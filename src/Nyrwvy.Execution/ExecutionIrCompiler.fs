namespace Nyrwvy.Core

open System
open System.Collections.Generic
open System.Linq.Expressions

/// A compiled function: its delegate, filled in once every function of the program is compiled, and
/// its frame, made once.
[<AllowNullLiteral>]
type IrCompiledFunction(frame: IrFrame) =
    member val Body: Func<IrValue[], IrValue> = null with get, set
    member _.Frame = frame

/// Static helpers called by code that ExecutionIrCompiler generates.
type IrCompiledSupport =
    static member Int(value: IrValue) = match value with IrInt number -> number | _ -> invalidOp "Compiled IR: Int64 operand expected."
    static member Bool(value: IrValue) = match value with IrBool flag -> flag | _ -> invalidOp "Compiled IR: Bool operand expected."
    static member OfInt(number: int64) = IrInt number
    static member OfBool(flag: bool) = IrBool flag
    static member IsSome(value: IrValue) = match value with IrOption item -> item.IsSome | _ -> invalidOp "Compiled IR: Option operand expected."
    static member IsOk(value: IrValue) = match value with IrResult(isOk, _) -> isOk | _ -> invalidOp "Compiled IR: Result operand expected."
    static member Err(value: IrValue) = IrResult(false, value)

    static member Variant(value: IrValue, variants: VariantId[]) =
        match value with
        | IrUnion(variantId, _) ->
            match Array.IndexOf(variants, variantId) with
            | -1 -> raise (InvalidOperationException "Execution IR union value did not match a declared variant.")
            | index -> index
        | _ -> invalidOp "Compiled IR: Union operand expected."

    /// Instructions without generated code run in the interpreter, reading operands from the slots.
    static member Slow(interpreter: ExecutionIrInterpreter, fn: ExecutionFunction, instruction: ExecutionInstruction, slotOf: Dictionary<ExecutionValueId, int>, slots: IrValue[]) =
        interpreter.Evaluate(fn, (fun id -> slots[slotOf[id]]), instruction)

    static member Call(interpreter: ExecutionIrInterpreter, target: IrCompiledFunction, arguments: IrValue[]) =
        interpreter.EnterCompiled(target.Frame, target.Body, arguments)

    static member CheckDeadline(interpreter: ExecutionIrInterpreter) = interpreter.CheckDeadline()

    /// ListFold: `context` holds the bound parameters; the state and item go to their positions.
    static member Fold(interpreter: ExecutionIrInterpreter, target: IrCompiledFunction, context: IrValue[], statePosition: int, itemPosition: int, source: IrValue, seed: IrValue) =
        match source with
        | IrList items ->
            let mutable state = seed
            for item in items do
                let arguments = Array.copy context
                arguments[statePosition] <- state
                arguments[itemPosition] <- item
                state <- interpreter.EnterCompiled(target.Frame, target.Body, arguments)
            state
        | _ -> invalidOp "Compiled IR: List operand expected."

/// Compiles Execution IR functions to delegates with System.Linq.Expressions, so the JIT runs them.
/// Every value lives in one IrValue slot array per frame; control flow, Int64/Bool arithmetic, calls and
/// ListFold have generated code, and every other instruction calls back into the interpreter.
module ExecutionIrCompiler =
    let private support name = typeof<IrCompiledSupport>.GetMethod(name: string)
    let private irValue = typeof<IrValue>

    let private compileFunction (interpreter: ExecutionIrInterpreter) (functions: IDictionary<FunctionId, ExecutionFunction>) (targets: IDictionary<FunctionId, IrCompiledFunction>) (fn: ExecutionFunction) =
        let slotOf = Dictionary<ExecutionValueId, int>()
        let slot (id: ExecutionValueId) =
            match slotOf.TryGetValue id with
            | true, index -> index
            | _ ->
                let index = slotOf.Count
                slotOf[id] <- index
                index
        for parameter in fn.Parameters do slot parameter.Value.Id |> ignore
        for block in fn.Blocks do
            for parameter in block.Parameters do slot parameter.Id |> ignore
            for instruction in block.Instructions do slot instruction.Result.Id |> ignore
        let arguments = Expression.Parameter(typeof<IrValue[]>, "arguments")
        let slots = Expression.Variable(typeof<IrValue[]>, "slots")
        let at id = Expression.ArrayAccess(slots, Expression.Constant(slot id)) :> Expression
        let interpreterConstant = Expression.Constant(interpreter)
        let call name (operands: Expression list) = Expression.Call(support name, operands) :> Expression
        let int id = call "Int" [ at id ]
        let bool id = call "Bool" [ at id ]
        let labels = fn.Blocks |> List.map (fun block -> block.Id, Expression.Label(string (ExecutionBlockId.value block.Id))) |> dict
        let returnLabel = Expression.Label(irValue)
        let invoke functionId (values: Expression list) =
            call "Call" [ interpreterConstant; Expression.Constant(targets[functionId]); Expression.NewArrayInit(irValue, values) ]
        let parallelAssign (pairs: (ExecutionValueId * ExecutionValueId) list) (rest: Expression list) =
            let temporaries = pairs |> List.map (fun _ -> Expression.Variable(irValue))
            let reads = List.map2 (fun (temporary: ParameterExpression) (_, source) -> Expression.Assign(temporary, at source) :> Expression) temporaries pairs
            let writes = List.map2 (fun (temporary: ParameterExpression) (target, _) -> Expression.Assign(at target, temporary) :> Expression) temporaries pairs
            Expression.Block(temporaries, reads @ writes @ rest) :> Expression
        let instructionCode (instruction: ExecutionInstruction) : Expression =
            let value =
                match instruction.Kind with
                | ExecutionInstructionKind.ReadParameter id
                | ExecutionInstructionKind.ReadPatternBinding id -> at id
                | ExecutionInstructionKind.Int64Literal number -> Expression.Constant(IrInt number, irValue)
                | ExecutionInstructionKind.BoolLiteral flag -> Expression.Constant(IrBool flag, irValue)
                | ExecutionInstructionKind.StringLiteral text -> Expression.Constant(IrString text, irValue)
                | ExecutionInstructionKind.UnitLiteral -> Expression.Constant(IrUnit, irValue)
                | ExecutionInstructionKind.Int64WrappingAdd(left, right) -> call "OfInt" [ Expression.Add(int left, int right) ]
                | ExecutionInstructionKind.Int64WrappingSubtract(left, right) -> call "OfInt" [ Expression.Subtract(int left, int right) ]
                | ExecutionInstructionKind.Int64WrappingMultiply(left, right) -> call "OfInt" [ Expression.Multiply(int left, int right) ]
                | ExecutionInstructionKind.Int64Equal(left, right) -> call "OfBool" [ Expression.Equal(int left, int right) ]
                | ExecutionInstructionKind.Int64LessThan(left, right) -> call "OfBool" [ Expression.LessThan(int left, int right) ]
                | ExecutionInstructionKind.Int64LessThanOrEqual(left, right) -> call "OfBool" [ Expression.LessThanOrEqual(int left, int right) ]
                | ExecutionInstructionKind.Int64GreaterThan(left, right) -> call "OfBool" [ Expression.GreaterThan(int left, int right) ]
                | ExecutionInstructionKind.Int64GreaterThanOrEqual(left, right) -> call "OfBool" [ Expression.GreaterThanOrEqual(int left, int right) ]
                | ExecutionInstructionKind.BoolNot id -> call "OfBool" [ Expression.Not(bool id) ]
                | ExecutionInstructionKind.DirectCall(functionId, callArguments) ->
                    functions[functionId].Parameters
                    |> List.map (fun parameter ->
                        at (callArguments |> List.find (fun argument -> argument.TargetParameterId = parameter.SemanticParameterId)).ValueId)
                    |> invoke functionId
                | ExecutionInstructionKind.ListFold(source, seed, folder, context) ->
                    let parameters = functions[folder].Parameters
                    let bound = context |> List.map (fun argument -> argument.TargetParameterId, argument.ValueId) |> dict
                    let free = parameters |> List.indexed |> List.filter (fun (_, parameter) -> not (bound.ContainsKey parameter.SemanticParameterId)) |> List.map fst
                    let contextValues =
                        parameters |> List.map (fun parameter ->
                            match bound.TryGetValue parameter.SemanticParameterId with
                            | true, valueId -> at valueId
                            | _ -> Expression.Constant(null, irValue) :> Expression)
                    call "Fold" [ interpreterConstant; Expression.Constant(targets[folder]); Expression.NewArrayInit(irValue, contextValues)
                                  Expression.Constant(free[0]); Expression.Constant(free[1]); at source; at seed ]
                | _ ->
                    call "Slow" [ interpreterConstant; Expression.Constant(fn); Expression.Constant(instruction); Expression.Constant(slotOf); slots ]
            Expression.Assign(at instruction.Result.Id, value)
        let goto blockId = Expression.Goto(labels[blockId]) :> Expression
        let terminatorCode (terminator: ExecutionTerminator) : Expression =
            match terminator with
            | ExecutionTerminator.Return id -> Expression.Return(returnLabel, at id)
            | ExecutionTerminator.Abort(_, id) -> Expression.Return(returnLabel, call "Err" [ at id ])
            | ExecutionTerminator.Jump(target, jumpArguments) ->
                parallelAssign (jumpArguments |> List.map (fun argument -> argument.TargetValueId, argument.ValueId)) [ goto target ]
            | ExecutionTerminator.TailCall(_, callArguments) ->
                let pairs =
                    callArguments |> List.map (fun argument ->
                        (fn.Parameters |> List.find (fun parameter -> parameter.SemanticParameterId = argument.TargetParameterId)).Value.Id, argument.ValueId)
                parallelAssign pairs [ call "CheckDeadline" [ interpreterConstant ]; goto fn.EntryBlockId ]
            | ExecutionTerminator.Branch(condition, whenTrue, whenFalse) -> Expression.IfThenElse(bool condition, goto whenTrue, goto whenFalse)
            | ExecutionTerminator.MatchOption(input, someBlock, noneBlock) -> Expression.IfThenElse(call "IsSome" [ at input ], goto someBlock, goto noneBlock)
            | ExecutionTerminator.MatchResult(input, okBlock, errBlock) -> Expression.IfThenElse(call "IsOk" [ at input ], goto okBlock, goto errBlock)
            | ExecutionTerminator.MatchUnion(input, targets) ->
                let variants = targets |> List.map (fun target -> target.VariantId) |> Array.ofList
                let cases = targets |> List.mapi (fun index target -> Expression.SwitchCase(goto target.BlockId, Expression.Constant(index)))
                Expression.Switch(call "Variant" [ at input; Expression.Constant(variants) ], Array.ofList cases)
        let prologue =
            [ Expression.Assign(slots, Expression.NewArrayBounds(irValue, Expression.Constant(slotOf.Count))) :> Expression ]
            @ (fn.Parameters |> List.mapi (fun index parameter ->
                Expression.Assign(at parameter.Value.Id, Expression.ArrayIndex(arguments, Expression.Constant(index))) :> Expression))
            @ [ goto fn.EntryBlockId ]
        let blocks =
            fn.Blocks |> List.collect (fun block ->
                (Expression.Label(labels[block.Id]) :> Expression)
                :: (block.Instructions |> List.map instructionCode)
                @ [ terminatorCode block.Terminator ])
        let body = Expression.Block(irValue, [ slots ], prologue @ blocks @ [ Expression.Label(returnLabel, Expression.Constant(null, irValue)) :> Expression ])
        Expression.Lambda<Func<IrValue[], IrValue>>(body, [ arguments ]).Compile()

    /// Compiles every function of the program and registers it with the interpreter.
    let compile (interpreter: ExecutionIrInterpreter) (program: ExecutionProgram) =
        let functions = program.Functions |> List.map (fun fn -> fn.SemanticFunctionId, fn) |> dict
        let targets = program.Functions |> List.map (fun fn -> fn.SemanticFunctionId, IrCompiledFunction(IrFrame.Function fn.SemanticFunctionId)) |> dict
        for fn in program.Functions do
            let target = targets[fn.SemanticFunctionId]
            target.Body <- compileFunction interpreter functions targets fn
            interpreter.Register(fn.SemanticFunctionId, target.Body.Invoke)
