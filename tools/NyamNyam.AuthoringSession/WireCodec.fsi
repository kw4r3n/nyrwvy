namespace NyamNyam.Tools.AuthoringSession

open System.Text.Json
open NyamNyam.Core

module internal WireJson =
    val objectValue: (string * obj) list -> obj
    val arrayValue: obj list -> obj
    val serialize: obj -> string

module internal WireInput =
    exception ProtocolInputException of string
    val fail: string -> 'T
    val parseDocument: string -> JsonDocument
    val ensureFields: string -> string list -> string list -> JsonElement -> unit
    val property: string -> JsonElement -> JsonElement
    val tryProperty: string -> JsonElement -> JsonElement option
    val requiredString: string -> JsonElement -> string
    val requiredBool: string -> JsonElement -> bool
    val optionalBool: string -> bool -> JsonElement -> bool
    val requiredInt: string -> JsonElement -> int
    val requiredInt64: string -> JsonElement -> int64
    val array: string -> JsonElement -> JsonElement list
    val stableId<'Kind>: string -> string -> StableId<'Kind>
    val id<'Kind>: string -> JsonElement -> StableId<'Kind>
    val typeRef: JsonElement -> TypeRef
    val recipe: JsonElement -> ExpressionRecipe
    val operation: JsonElement -> AgentOperationIntent

module internal WireEncode =
    val declarations: AuthoringDeclarations -> obj
    val focus: FunctionFocusView -> obj
    val proposal: AuthoringProposal -> obj
    val status: AuthoringSessionStatus -> obj
    val commitReceipt: AuthoringCommitReceipt -> obj
    val rejectionReceipt: AuthoringRejectionReceipt -> obj
    val diagnostics: TransactionDiagnostic list -> obj
    val error: AuthoringSessionError -> obj
