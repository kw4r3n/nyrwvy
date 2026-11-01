namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

module EffectRequirementsTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error

    [<Fact>]
    let ``body effects distinguish public contracts from unused declarations and preserve evidence`` () =
        let fixture = MilestoneZeroGraph.create ()
        let before = SnapshotCodec.serialize fixture.Snapshot |> unwrap
        let focus id = SemanticQuery.focusFunction fixture.Snapshot id |> unwrap
        let report = (focus fixture.ReportFunctionId).BodyEffectRequirements
        Assert.Equal<EffectId list>([ fixture.ConsoleEffectId ], report.EffectIds)
        Assert.All(report.Evidence, fun evidence ->
            Assert.Equal(EffectRequirementSource.CapabilityOperation fixture.ConsoleWriteLineOperationId, evidence.Source)
            Assert.Contains(evidence.NodeId, ValueGraph.collectNodes (focus fixture.ReportFunctionId).Function.Body |> List.map (fun n -> n.Id)))
        let main = (focus fixture.MainFunctionId).BodyEffectRequirements
        Assert.Contains(main.Evidence, fun evidence -> evidence.Source = EffectRequirementSource.FunctionContract fixture.ReportFunctionId)
        let validate = (focus fixture.ValidateFunctionId).Function
        let extra = { validate with RequiredEffects = [ fixture.ConsoleEffectId ] }
        Assert.Empty((EffectRequirements.derive fixture.Snapshot extra).EffectIds)
        // Public contracts remain requirements even if the callee implementation needs fewer effects.
        let widened = { fixture.Snapshot with Functions = extra :: (fixture.Snapshot.Functions |> List.filter (fun fn -> fn.Id <> extra.Id)) }
        let widenedMain = SemanticQuery.focusFunction widened fixture.MainFunctionId |> unwrap
        Assert.Contains(widenedMain.BodyEffectRequirements.Evidence, fun evidence -> evidence.Source = EffectRequirementSource.FunctionContract extra.Id)
        Assert.Equal(before, SnapshotCodec.serialize fixture.Snapshot |> unwrap)
        let reordered = { fixture.Snapshot with Functions = List.rev fixture.Snapshot.Functions; Capabilities = List.rev fixture.Snapshot.Capabilities }
        Assert.Equal(report, (SemanticQuery.focusFunction reordered fixture.ReportFunctionId |> unwrap).BodyEffectRequirements)
        let missing = { fixture.Snapshot with Capabilities = [] }
        Assert.Contains(fixture.ConsoleWriteLineOperationId, (EffectRequirements.derive missing (focus fixture.ReportFunctionId).Function).UnresolvedOperationIds)
        let missingTarget = { fixture.Snapshot with Functions = fixture.Snapshot.Functions |> List.filter (fun fn -> fn.Id <> fixture.ReportFunctionId) }
        Assert.Contains(fixture.ReportFunctionId, (EffectRequirements.derive missingTarget (focus fixture.MainFunctionId).Function).UnresolvedFunctionIds)
