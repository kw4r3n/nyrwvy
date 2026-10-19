namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
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

    [<Fact>]
    let ``inspect and review show derived effects without changing saved graph or declared effects`` () =
        let fixture = MilestoneZeroGraph.create ()
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-effects-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let before = SnapshotCodec.serialize fixture.Snapshot |> unwrap
            File.WriteAllText(path, before)
            use session = AuthoringSession.openExisting path |> unwrap
            for op in [ "readFunction" ] do
                use response = AuthoringProtocol.executeLine session ($"""{{"id":"read","op":"{op}","functionId":"{StableId.value fixture.ReportFunctionId}"}}""") |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                let body = response.RootElement.GetProperty("result").GetProperty("bodyEffectRequirements")
                Assert.Equal(StableId.value fixture.ConsoleEffectId, body.GetProperty("effectIds").[0].GetString())
                Assert.NotEmpty(body.GetProperty("evidence").EnumerateArray())
            use review = AuthoringProtocol.executeLine session ($"""{{"id":"review","op":"simulateOperations","operations":[{{"kind":"renameFunction","functionId":"{StableId.value fixture.ReportFunctionId}","displayName":"renamed"}}]}}""") |> JsonDocument.Parse
            Assert.True(review.RootElement.GetProperty("ok").GetBoolean(), review.RootElement.ToString())
            Assert.Contains("bodyEffectRequirements", review.RootElement.GetRawText())
            use created = AuthoringProtocol.executeLine session ($"""{{"id":"create","op":"simulateCreateFunction","exported":true,"displayName":"extraContract","parameters":[],"resultType":{{"kind":"primitive","primitive":"string"}},"requiredEffects":["{StableId.value fixture.ConsoleEffectId}"],"body":{{"kind":"stringLiteral","value":"pure body"}}}}""") |> JsonDocument.Parse
            Assert.True(created.RootElement.GetProperty("ok").GetBoolean(), created.RootElement.ToString())
            let creationReview = created.RootElement.GetProperty("result").GetProperty("review")
            Assert.Equal(1, creationReview.GetProperty("createdFunction").GetProperty("requiredEffects").GetArrayLength())
            Assert.Equal(0, creationReview.GetProperty("bodyEffectRequirements").GetProperty("effectIds").GetArrayLength())
            let projected = session.FocusFunction fixture.ReportFunctionId |> unwrap |> ViewerProjection.focus
            let html = ViewerHtml.renderFocusDocument projected
            Assert.Contains("Declared required effects", html)
            Assert.Contains("Body effect requirements (derived)", html)
            Assert.Contains(StableId.value fixture.ConsoleWriteLineOperationId, html)
            Assert.Equal(before, File.ReadAllText path)
        finally
            Directory.Delete(root, true)
