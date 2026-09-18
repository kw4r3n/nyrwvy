namespace Nyrwvy.Core

// Legacy F# source names for existing clients and evaluation tooling.
// These aliases do not promise purity. New code should use ExpressionRecipe and
// AgentExpressionStructuralWorkflow; there is only one construction engine.
type PureExpressionRecipe = ExpressionRecipe
type AgentReplaceFunctionBodyWithPureExpressionRecipe = AgentReplaceFunctionBodyWithExpressionRecipe
type AgentPureExpressionStructuralChangeReview = AgentExpressionStructuralChangeReview
type AgentPureExpressionStructuralChangeProposal = AgentExpressionStructuralChangeProposal

[<RequireQualifiedAccess>]
module AgentPureExpressionStructuralChangeProposal =
    let review proposal = AgentExpressionStructuralChangeProposal.review proposal
    let transactionId proposal = AgentExpressionStructuralChangeProposal.transactionId proposal
    let proposedSnapshotId proposal = AgentExpressionStructuralChangeProposal.proposedSnapshotId proposal

[<RequireQualifiedAccess>]
module AgentPureExpressionStructuralWorkflow =
    let simulate workspace request = AgentExpressionStructuralWorkflow.simulate workspace request
    let commit workspace proposal = AgentExpressionStructuralWorkflow.commit workspace proposal
    let commitWithPolicy workspace proposal policy = AgentExpressionStructuralWorkflow.commitWithPolicy workspace proposal policy
    let reject workspace proposal = AgentExpressionStructuralWorkflow.reject workspace proposal
