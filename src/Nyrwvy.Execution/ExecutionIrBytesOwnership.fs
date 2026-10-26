namespace Nyrwvy.Core

open Microsoft.FSharp.Reflection

/// Which Bytes updates of a lowered function may write in place (docs/plans/bytes-and-buffers.md).
/// Semantics stay value semantics: a backend that copies on every update is always correct. This plan
/// only marks where nothing can observe the old value, so a backend may reuse it.
type BytesOwnershipPlan =
    { /// Result value of every BytesSet/BytesAppend whose input has no use that can run after it.
      InPlace: Set<ExecutionValueId>
      /// Updates that must copy: the update's origin node and the origin of the use that forces the copy.
      CopiedUpdates: (GraphNodeId * GraphNodeId) list
      /// Per instruction (by result value), the Bytes operands it lets escape: the backend clears their
      /// owner before the instruction, so a buffer stored elsewhere is never changed in place later.
      Escapes: Map<ExecutionValueId, ExecutionValueId list>
      /// Per block ending in a tail self-call, Bytes arguments that alias another argument of the same call.
      TailCallReleases: Map<ExecutionBlockId, ExecutionValueId list> }

[<RequireQualifiedAccess>]
module ExecutionIrBytesOwnership =
    /// Every value id an instruction kind or terminator mentions, found by walking its union, record,
    /// tuple and list fields, so a new instruction kind can never be missed.
    let rec private valueIds (item: obj) : ExecutionValueId list =
        match item with
        | null -> []
        | :? ExecutionValueId as id -> [ id ]
        | :? string -> []
        | :? System.Collections.IEnumerable as items -> items |> Seq.cast<obj> |> Seq.collect valueIds |> List.ofSeq
        | _ ->
            let itemType = item.GetType()
            if FSharpType.IsUnion(itemType, true) then
                let _, fields = FSharpValue.GetUnionFields(item, itemType, true)
                fields |> Seq.collect valueIds |> List.ofSeq
            elif FSharpType.IsRecord(itemType, true) then
                FSharpValue.GetRecordFields(item, true) |> Seq.collect valueIds |> List.ofSeq
            elif FSharpType.IsTuple itemType then
                FSharpValue.GetTupleFields item |> Seq.collect valueIds |> List.ofSeq
            else []

    /// Instructions that only read a Bytes operand; their results never share a buffer that stays owned.
    let private reads = function
        | ExecutionInstructionKind.BytesLength _
        | ExecutionInstructionKind.BytesGet _
        | ExecutionInstructionKind.BytesConcat _
        | ExecutionInstructionKind.BytesSlice _
        | ExecutionInstructionKind.BytesToLatin1 _
        | ExecutionInstructionKind.ValueEqual _ -> true
        | _ -> false

    /// Instructions that only give an existing value a new name; their result is the same buffer.
    let private moved = function
        | ExecutionInstructionKind.ReadParameter source
        | ExecutionInstructionKind.ReadPatternBinding source -> Some source
        | _ -> None

    let private updatedInput = function
        | ExecutionInstructionKind.BytesSet(bytesId, _, _)
        | ExecutionInstructionKind.BytesAppend(bytesId, _) -> Some bytesId
        | _ -> None

    let private successors = function
        | ExecutionTerminator.Jump(target, _) -> [ target ]
        | ExecutionTerminator.Branch(_, whenTrue, whenFalse) -> [ whenTrue; whenFalse ]
        | ExecutionTerminator.MatchUnion(_, targets) -> targets |> List.map (fun target -> target.BlockId)
        | ExecutionTerminator.MatchOption(_, someBlock, noneBlock) -> [ someBlock; noneBlock ]
        | ExecutionTerminator.MatchResult(_, okBlock, errBlock) -> [ okBlock; errBlock ]
        // A tail self-call starts the next turn: its values are the parameters there, not uses after.
        | ExecutionTerminator.TailCall _
        | ExecutionTerminator.Abort _
        | ExecutionTerminator.Return _ -> []

    let plan (functionEntity: ExecutionFunction) : BytesOwnershipPlan option =
        let isBytes (typeRef: TypeRef) = typeRef = TypeRef.Primitive PrimitiveType.Bytes
        let types =
            (functionEntity.Parameters |> List.map (fun parameter -> parameter.Value))
            @ (functionEntity.Blocks |> List.collect (fun block -> block.Parameters @ (block.Instructions |> List.map (fun instruction -> instruction.Result))))
            |> List.map (fun value -> value.Id, value.Type)
            |> Map.ofList
        let bytesValue id = Map.tryFind id types |> Option.exists isBytes
        let updates =
            functionEntity.Blocks
            |> List.collect (fun block ->
                block.Instructions
                |> List.indexed
                |> List.choose (fun (index, instruction) ->
                    updatedInput instruction.Kind |> Option.map (fun input -> block.Id, index, instruction, input)))
        if not (types |> Map.exists (fun _ typeRef -> isBytes typeRef)) then None
        else
            // Jump arguments alias block parameters, and parameter and pattern reads alias what they read:
            // one buffer under several names is one alias class.
            let parent = System.Collections.Generic.Dictionary<ExecutionValueId, ExecutionValueId>()
            let rec find id =
                match parent.TryGetValue id with
                | true, next when next <> id ->
                    let root = find next
                    parent[id] <- root
                    root
                | _ -> id
            for block in functionEntity.Blocks do
                match block.Terminator with
                | ExecutionTerminator.Jump(_, arguments) ->
                    for argument in arguments do
                        let left, right = find argument.ValueId, find argument.TargetValueId
                        if left <> right then parent[left] <- right
                | _ -> ()
                for instruction in block.Instructions do
                    match moved instruction.Kind with
                    | Some source ->
                        let left, right = find instruction.Result.Id, find source
                        if left <> right then parent[left] <- right
                    | None -> ()
            // Use sites: (block, position, origin node, used value). The terminator sits after the instructions.
            let uses =
                functionEntity.Blocks
                |> List.collect (fun block ->
                    let instructionUses =
                        block.Instructions
                        |> List.indexed
                        |> List.filter (fun (_, instruction) -> Option.isNone (moved instruction.Kind))
                        |> List.collect (fun (index, instruction) ->
                            valueIds (box instruction.Kind) |> List.map (fun id -> block.Id, index, instruction.OriginNodeId, Some instruction, id))
                    let terminatorUses =
                        match block.Terminator with
                        | ExecutionTerminator.Jump _ -> []
                        | ExecutionTerminator.TailCall(origin, _)
                        | ExecutionTerminator.Abort(origin, _) ->
                            valueIds (box block.Terminator) |> List.map (fun id -> block.Id, block.Instructions.Length, origin, None, id)
                        | terminator ->
                            // Return and branch inputs carry no origin of their own; the block's last instruction stands in.
                            let origin =
                                block.Instructions |> List.tryLast |> Option.map (fun instruction -> instruction.OriginNodeId)
                            valueIds (box terminator)
                            |> List.choose (fun id -> origin |> Option.map (fun origin -> block.Id, block.Instructions.Length, origin, None, id))
                    instructionUses @ terminatorUses)
            let blocks = functionEntity.Blocks |> List.map (fun block -> block.Id, block) |> Map.ofList
            let reachableFrom =
                let memo = System.Collections.Generic.Dictionary<ExecutionBlockId, Set<ExecutionBlockId>>()
                fun (start: ExecutionBlockId) ->
                    match memo.TryGetValue start with
                    | true, found -> found
                    | _ ->
                        let rec walk seen pending =
                            match pending with
                            | [] -> seen
                            | next :: rest when Set.contains next seen -> walk seen rest
                            | next :: rest -> walk (Set.add next seen) (successors (Map.find next blocks).Terminator @ rest)
                        let found = walk Set.empty (successors (Map.find start blocks).Terminator)
                        memo[start] <- found
                        found
            let decisions =
                updates
                |> List.map (fun (blockId, index, update, input) ->
                    let alias = find input
                    let later =
                        uses
                        |> List.tryFind (fun (useBlock, useIndex, _, useInstruction, id) ->
                            find id = alias
                            && not (useInstruction |> Option.exists (fun used -> obj.ReferenceEquals(used, update)))
                            && ((useBlock = blockId && useIndex > index) || (useBlock <> blockId && Set.contains useBlock (reachableFrom blockId))))
                    update, later)
            let escapes =
                functionEntity.Blocks
                |> List.collect (fun block -> block.Instructions)
                |> List.choose (fun instruction ->
                    if reads instruction.Kind || Option.isSome (updatedInput instruction.Kind) || Option.isSome (moved instruction.Kind) then None
                    else
                        match valueIds (box instruction.Kind) |> List.filter bytesValue |> List.distinct with
                        | [] -> None
                        | escaping -> Some(instruction.Result.Id, escaping))
                |> Map.ofList
            let tailCallReleases =
                functionEntity.Blocks
                |> List.choose (fun block ->
                    match block.Terminator with
                    | ExecutionTerminator.TailCall(_, arguments) ->
                        let passed = valueIds (box arguments) |> List.filter bytesValue
                        let shared =
                            passed
                            |> List.countBy find
                            |> List.filter (fun (_, count) -> count > 1)
                            |> List.map fst
                            |> Set.ofList
                        match passed |> List.filter (fun id -> Set.contains (find id) shared) |> List.distinct with
                        | [] -> None
                        | released -> Some(block.Id, released)
                    | _ -> None)
                |> Map.ofList
            Some
                { InPlace = decisions |> List.choose (fun (update, later) -> if Option.isNone later then Some update.Result.Id else None) |> Set.ofList
                  CopiedUpdates =
                    decisions
                    |> List.choose (fun (update, later) -> later |> Option.map (fun (_, _, origin, _, _) -> update.OriginNodeId, origin))
                  Escapes = escapes
                  TailCallReleases = tailCallReleases }
