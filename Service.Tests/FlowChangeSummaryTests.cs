using System.Text.Json.Nodes;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class FlowChangeSummaryTests
{
    private const string SecretValue = "hunter2-private-value";

    private static string Header(string name = "Orders", bool enabled = false, string trigger = "OnDemand", string interval = "00:05:00",
        bool startup = false, bool retry = false, int attempts = 3, string? category = null)
    {
        var header = new JsonObject
        {
            ["flowId"] = "orders", ["flowName"] = name, ["enabled"] = enabled, ["triggerMode"] = trigger, ["intervalText"] = interval,
            ["runOnStartup"] = startup, ["hasDefaultRetry"] = retry, ["retryDelaySeconds"] = 10.0, ["retryBackoff"] = "Fixed"
        };
        if (retry) header["retryMaxAttempts"] = attempts;
        if (category is not null) header["category"] = category;
        return header.ToJsonString();
    }

    private static string Node(int stage, string module, string? name = null, string graphId = "", Dictionary<string, string>? settings = null,
        bool confirm = false, JsonObject? retry = null, JsonObject? compensation = null, double x = 0, double y = 0, int order = 0)
    {
        var node = new JsonObject
        {
            ["stage"] = stage, ["moduleId"] = module, ["settings"] = new JsonObject(),
            ["order"] = order, ["routeId"] = "", ["graphId"] = graphId, ["inputParseIds"] = new JsonArray(), ["fetchNodeIds"] = new JsonArray(),
            ["requireConfirmation"] = confirm, ["x"] = x, ["y"] = y
        };
        if (name is not null) node["name"] = name;
        foreach (var (key, value) in settings ?? []) node["settings"]![key] = value;
        if (retry is not null) node["retry"] = retry;
        if (compensation is not null) node["compensation"] = compensation;
        return node.ToJsonString();
    }

    private static FlowChangeSummary Build(string beforeHeader, string[] before, string afterHeader, string[] after)
        => FlowChangeSummary.Build(beforeHeader, before, afterHeader, after);

    [Fact]
    public void NoDifferenceOrOnlyMovedNodes_ProducesNothing()
    {
        var summary = Build(Header(), [Node(4, "sftp", "Send", x: 10, y: 10)], Header(), [Node(4, "sftp", "Send", x: 400, y: 90)]);

        Assert.True(summary.IsEmpty);
    }

    [Fact]
    public void EnablingAFlow_IsListedAndFlaggedToDoubleCheck()
    {
        var summary = Build(Header(), [], Header(enabled: true), []);

        Assert.Contains("The flow is now enabled.", summary.Changes);
        Assert.Single(summary.Cautions);
    }

    [Fact]
    public void DisablingAFlow_IsListedWithoutACaution()
    {
        var summary = Build(Header(enabled: true), [], Header(), []);

        Assert.Contains("The flow is now disabled.", summary.Changes);
        Assert.Empty(summary.Cautions);
    }

    [Fact]
    public void ChangingTheTriggerToAutomatic_IsFlagged_AndOnDemandIsNot()
    {
        var automatic = Build(Header(), [], Header(trigger: "Interval", interval: "00:01:00"), []);
        Assert.Contains("Trigger changed from OnDemand to Interval.", automatic.Changes);
        Assert.Contains(automatic.Cautions, line => line.Contains("starts by itself"));

        var manual = Build(Header(trigger: "Interval"), [], Header(trigger: "OnDemand"), []);
        Assert.Contains("Trigger changed from Interval to OnDemand.", manual.Changes);
        Assert.Empty(manual.Cautions);

        var interval = Build(Header(trigger: "Interval"), [], Header(trigger: "Interval", interval: "00:10:00"), []);
        Assert.Contains("Interval changed from 00:05:00 to 00:10:00.", interval.Changes);
    }

    [Fact]
    public void RemovingTheDefaultRetry_IsFlagged_AndChangingItIsNot()
    {
        var removed = Build(Header(retry: true), [], Header(), []);
        Assert.Contains("The flow's default retry was removed.", removed.Changes);
        Assert.Single(removed.Cautions);

        var changed = Build(Header(retry: true, attempts: 3), [], Header(retry: true, attempts: 5), []);
        Assert.Contains(changed.Changes, line => line.Contains("default retry changed") && line.Contains("3 attempts") && line.Contains("5 attempts"));
        Assert.Empty(changed.Cautions);
    }

    [Fact]
    public void AddingAndRemovingSteps_AreListed_AndANewDeliveryIsFlagged()
    {
        var summary = Build(Header(), [Node(2, "script", "Old step")], Header(), [Node(4, "sftp", "Send file")]);

        Assert.Contains("Removed Augment step 'Old step' (script).", summary.Changes);
        Assert.Contains("Added Delivery step 'Send file' (sftp).", summary.Changes);
        Assert.Contains(summary.Cautions, line => line.Contains("Send file") && line.Contains("additional destination"));
    }

    [Fact]
    public void ChangedSettings_NameTheSettingsButNeverShowValues()
    {
        var before = Node(4, "sftp", "Send", settings: new() { ["host"] = "old.example", ["password"] = SecretValue, ["path"] = "/in" });
        var after = Node(4, "sftp", "Send", settings: new() { ["host"] = "new.example", ["password"] = SecretValue + "2", ["port"] = "22" });

        var summary = Build(Header(), [before], Header(), [after]);

        var line = Assert.Single(summary.Changes);
        Assert.Contains("added port", line);
        Assert.Contains("removed path", line);
        Assert.Contains("changed host, password", line);
        Assert.Contains("values are not shown", line);
        Assert.DoesNotContain(SecretValue, string.Join("\n", summary.Changes.Concat(summary.Cautions)));
        Assert.DoesNotContain("example", line);
    }

    [Fact]
    public void RemovingConfirmationRetryOrCompensation_IsFlagged()
    {
        var retry = new JsonObject { ["maxAttempts"] = 3, ["delaySeconds"] = 5.0, ["backoff"] = "Fixed" };
        var compensation = new JsonObject { ["moduleId"] = "undo" };
        var before = Node(4, "sftp", "Send", confirm: true, retry: retry, compensation: compensation);
        var after = Node(4, "sftp", "Send");

        var summary = Build(Header(), [before], Header(), [after]);

        Assert.Contains(summary.Changes, line => line.Contains("no longer needs confirmation"));
        Assert.Contains(summary.Changes, line => line.Contains("its own retry was removed"));
        Assert.Contains(summary.Changes, line => line.Contains("undo (compensation) step was removed"));
        Assert.Equal(3, summary.Cautions.Count);
    }

    [Fact]
    public void RequiringConfirmationOrAddingRetry_IsListedWithoutCautions()
    {
        var retry = new JsonObject { ["maxAttempts"] = 3, ["delaySeconds"] = 5.0, ["backoff"] = "Fixed" };

        var summary = Build(Header(), [Node(4, "sftp", "Send")], Header(), [Node(4, "sftp", "Send", confirm: true, retry: retry)]);

        Assert.Contains(summary.Changes, line => line.Contains("now needs confirmation"));
        Assert.Contains(summary.Changes, line => line.Contains("retry is now 3 attempts"));
        Assert.Empty(summary.Cautions);
    }

    [Fact]
    public void ARenamedSourceStep_IsPairedByItsGraphId_NotReportedAsRemovedAndAdded()
    {
        var summary = Build(Header(), [Node(1, "json-parse", "Old", graphId: "parsed")], Header(), [Node(1, "json-parse", "New", graphId: "parsed")]);

        var line = Assert.Single(summary.Changes);
        Assert.Contains("renamed from 'Old'", line);
    }

    [Fact]
    public void AChangedModuleOnTheSameStep_IsFlaggedSoItsSettingsAreChecked()
    {
        var summary = Build(Header(), [Node(4, "ftp", "Send", graphId: "d")], Header(), [Node(4, "sftp", "Send", graphId: "d")]);

        Assert.Contains(summary.Changes, line => line.Contains("module changed from ftp to sftp"));
        Assert.Single(summary.Cautions);
    }

    [Fact]
    public void NamesAreCleanedAndShortened()
    {
        var hostile = "line1\nline2 " + new string('x', 200);

        var summary = Build(Header(), [], Header(), [Node(2, "script", hostile)]);

        var line = Assert.Single(summary.Changes);
        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length < 140, line);
        Assert.EndsWith("...' (script).", line);
    }

    [Fact]
    public void LongLists_AreCappedWithACount()
    {
        var after = Enumerable.Range(0, 40).Select(index => Node(2, "script", "Step " + index)).ToArray();

        var summary = Build(Header(), [], Header(), after);

        Assert.Equal(FlowChangeSummary.MaxLines + 1, summary.Changes.Count);
        Assert.Equal("...and 15 more.", summary.Changes[^1]);
    }

    [Fact]
    public void IdenticalStepsAreNotReported_WhenAnotherStepChanges()
    {
        var kept = Node(2, "script", "Kept", settings: new() { ["a"] = "1" });

        var summary = Build(Header(), [kept, Node(4, "sftp", "Send")], Header(), [kept, Node(4, "sftp", "Send", confirm: true)]);

        Assert.DoesNotContain(summary.Changes, line => line.Contains("Kept"));
        Assert.Single(summary.Changes);
    }
}
