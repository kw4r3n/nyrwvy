namespace Nayamnyam.Spike

type TypeEntity =
    {
        Id: TypeId
        Name: string
    }

type Parameter =
    {
        Id: ParameterId
        Name: string
        TypeId: TypeId
    }

type FunctionEntity =
    {
        Id: FunctionId
        Name: string
        Parameters: Parameter list
    }

type ArgumentBinding =
    {
        ParameterId: ParameterId
        ValueRef: string
    }

type CallEntity =
    {
        Id: CallId
        CalleeId: FunctionId
        Arguments: ArgumentBinding list
    }

type ObligationKind =
    | MissingArgument of callId: CallId * parameterId: ParameterId

type ObligationStatus =
    | Open

type Obligation =
    {
        Id: ObligationId
        Kind: ObligationKind
        Status: ObligationStatus
    }

type Snapshot =
    {
        SchemaVersion: int
        Id: SnapshotId
        Types: TypeEntity list
        Functions: FunctionEntity list
        Calls: CallEntity list
        Obligations: Obligation list
    }

type Diagnostic =
    {
        Code: string
        Message: string
    }

type SemanticChange =
    | FunctionRenamed of functionId: FunctionId * oldName: string * newName: string
    | ParameterAdded of functionId: FunctionId * parameterId: ParameterId
    | ObligationCreated of obligationId: ObligationId

type TransactionResult =
    | Rejected of baseSnapshotId: SnapshotId * diagnostics: Diagnostic list
    | Incomplete of snapshot: Snapshot * diff: SemanticChange list * blocking: ObligationId list
    | Executable of snapshot: Snapshot * diff: SemanticChange list
