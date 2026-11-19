using System.Text.Json;
using Mulse.Modules.Abstractions;
using Service.Assist;
using Service.Models;

namespace Service.Tests;

public sealed class AssistRedactorTests
{
    private const string Secret = "hunter2-secret-value";
    private const string Folder = "/srv/payroll/outgoing";

    private static readonly ModuleDescriptor[] Modules =
    [
        new("sftp", "SFTP deliver", ModuleKind.Deliver, "Sends a file over SFTP.",
            [new("host", "Host", "Server", true, ModuleSettingInputKind.Text), new("password", "Password", "Login", false, ModuleSettingInputKind.Password)]),
        new("xml-render", "XML render", ModuleKind.Render, "Renders XML.")
    ];

    private static readonly string FlowJson = $$$"""
        {"version":1,"durable":true,"nodes":[
          {"id":"seq","kind":"Sequence","name":"Main","children":[
            {"id":"r","kind":"Render","name":"Make the file","module":"xml-render","settings":{"template":"{{config:TemplateName}}"}},
            {"id":"d","kind":"Deliver","name":"Send file","module":"sftp","messageName":"outMsg",
              "settings":{"host":"sftp.bank.example","password":"{{{Secret}}}","path":"{{{Folder}}}","user":"{{secret:BankUser}}","note":"{{config:TemplateName}} and {{config:Other}}"},
              "retry":{"attempts":3,"intervalSeconds":10,"retryAmbiguous":true}}]},
          {"id":"m","kind":"Deliver","module":"missing-mod"},
          {"id":"k","kind":"Render","module":"sftp"},
          {"id":"b","kind":"Blocked","name":"Jane Doe's account 12345678 <script>","blockReason":"Scripting functoid for jane@bank.example at https://x.example/a?token=abc password=hunter2"}
        ]}
        """;

    [Fact]
    public void FlowKeepsStructureButNeverValues()
    {
        var flow = AssistRedactor.Flow(FlowJson, Modules);
        var json = JsonSerializer.Serialize(flow);

        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain(Folder, json);
        Assert.DoesNotContain("sftp.bank.example", json);

        var delivery = flow.Nodes.Single(node => node.Id == "d");
        Assert.Equal(1, delivery.Depth);
        Assert.Equal("sftp", delivery.Module);
        Assert.Equal("installed", delivery.ModuleStatus);
        Assert.Equal("outMsg", delivery.Message);
        Assert.Equal("set to a literal value", delivery.Settings.Single(setting => setting.Key == "host").State);
        Assert.Equal("set to a literal value", delivery.Settings.Single(setting => setting.Key == "password").State);
        Assert.Equal("refers to a secret", delivery.Settings.Single(setting => setting.Key == "user").State);
        Assert.Equal(3, delivery.Retry!.Attempts);
        Assert.True(delivery.Retry.RetryAmbiguous);
    }

    [Fact]
    public void ModuleStatusSeparatesMissingFromWrongKind()
    {
        var flow = AssistRedactor.Flow(FlowJson, Modules);
        Assert.Equal("not installed", flow.Nodes.Single(node => node.Id == "m").ModuleStatus);
        Assert.Equal("installed as a different kind", flow.Nodes.Single(node => node.Id == "k").ModuleStatus);
        Assert.Null(flow.Nodes.Single(node => node.Id == "seq").ModuleStatus);
    }

    [Fact]
    public void NamesThatAreNotPlainLabelsAreWithheld()
    {
        var flow = AssistRedactor.Flow(FlowJson, Modules);
        var blocked = flow.Nodes.Single(node => node.Id == "b");
        Assert.Equal(AssistRedactor.Withheld, blocked.Label);
        Assert.Equal("Make the file", flow.Nodes.Single(node => node.Id == "r").Label);
        Assert.Contains(flow.Notes, note => note.Contains("replaced with"));
        Assert.DoesNotContain("Jane", JsonSerializer.Serialize(flow));
    }

    [Fact]
    public void BlockReasonsAreLeftOutUnlessRequestedAndThenScrubbed()
    {
        var without = AssistRedactor.Flow(FlowJson, Modules);
        Assert.Null(without.Nodes.Single(node => node.Id == "b").BlockReason);
        Assert.Contains(without.Nodes, node => node.Id == "b");
        Assert.DoesNotContain("functoid", JsonSerializer.Serialize(without));

        var with = AssistRedactor.Flow(FlowJson, Modules, includeBlockReasons: true);
        var reason = with.Nodes.Single(node => node.Id == "b").BlockReason!;
        Assert.Contains("Scripting functoid", reason);
        Assert.DoesNotContain("jane@bank.example", reason);
        Assert.DoesNotContain("x.example", reason);
        Assert.DoesNotContain("hunter2", reason);
        Assert.DoesNotContain("abc", reason);
    }

    [Fact]
    public void ReferencesListNamesAndStepsOnly()
    {
        var references = AssistRedactor.References(FlowJson);

        Assert.Equal(["config:Other", "config:TemplateName", "secret:BankUser"], references.Select(reference => $"{reference.Kind}:{reference.Name}"));
        Assert.Equal(["d", "r"], references.Single(reference => reference.Name == "TemplateName").NodeIds);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(references));
    }

    [Fact]
    public void ReferencesWithUnusualNamesAreWithheld()
    {
        var references = AssistRedactor.References("""{"version":1,"durable":true,"nodes":[{"id":"a","kind":"Deliver","module":"x","settings":{"s":"{{secret:Jane <Doe>}}"}}]}""");
        Assert.Equal(AssistRedactor.Withheld, Assert.Single(references).Name);
    }

    [Fact]
    public void RunKeepsStagesAndLeavesErrorTextOutByDefault()
    {
        var run = new AssistRunInput("Failed",
            [new("fetch", "Completed", null), new("deliver", "Failed", "Could not connect to sftp.bank.example (10.1.2.3) as jane@bank.example")],
            [new("deliver", "Unknown")]);

        var redacted = AssistRedactor.Run(run, includeErrorText: false);

        Assert.Equal("Failed", redacted.State);
        Assert.Equal(2, redacted.Events.Count);
        Assert.All(redacted.Events, item => Assert.Null(item.Detail));
        Assert.Contains(redacted.Notes, note => note.Contains("left out"));
        Assert.Equal("Unknown", Assert.Single(redacted.Deliveries).State);
    }

    [Fact]
    public void RunErrorTextIsScrubbedWhenIncluded()
    {
        var detail = "SftpException: Could not connect to sftp.bank.example (10.1.2.3:22) as jane@bank.example. "
            + @"Key file C:\keys\bank.pem and /home/svc/.ssh/id_rsa rejected. Order 1234567890 id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 "
            + "password=hunter2 Authorization: Bearer abc.def-ghi https://bank.example/path?sig=zzz QWERTYUIOPASDFGHJKLZXCVBNM1234567890";
        var redacted = AssistRedactor.Run(new("Failed", [new("deliver", "Failed", detail)], null), includeErrorText: true);

        var text = redacted.Events.Single().Detail!;
        Assert.Contains("SftpException", text);
        Assert.Contains("Could not connect", text);
        foreach (var leaked in new[] { "bank.example", "10.1.2.3", "jane", @"C:\keys", "/home/svc", "1234567890", "3f2504e0", "hunter2", "abc.def", "sig=zzz", "QWERTY" })
            Assert.DoesNotContain(leaked, text);
        Assert.Contains(redacted.Notes, note => note.Contains("personal text"));
    }

    [Fact]
    public void RunStagesThatAreNotIdentifiersAreWithheldAndLongRunsAreBounded()
    {
        var events = Enumerable.Range(0, AssistRedactor.MaxRunEvents + 5).Select(index => new AssistRunEventInput(index == 0 ? "weird stage <x>" : "step", "Done", null)).ToList();
        var redacted = AssistRedactor.Run(new("Done now", events, null), includeErrorText: false);

        Assert.Equal(AssistRedactor.Withheld, redacted.State);
        Assert.Equal(AssistRedactor.MaxRunEvents, redacted.Events.Count);
        Assert.Equal(AssistRedactor.Withheld, redacted.Events[0].Stage);
        Assert.Contains(redacted.Notes, note => note.Contains("5 later event"));
    }

    [Fact]
    public void ScrubClipsAndRemovesControlCharacters()
    {
        var result = AssistRedactor.Scrub("line one\r\nline two\u0007" + new string('x', 50), 20);
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\u0007', result);
        Assert.EndsWith("...", result);
        Assert.Equal("", AssistRedactor.Scrub(null, 10));
    }

    [Fact]
    public void InvalidFlowsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => AssistRedactor.Flow("", Modules));
        Assert.Throws<ArgumentException>(() => AssistRedactor.Flow("not json", Modules));
        Assert.Throws<ArgumentException>(() => AssistRedactor.References(" "));
    }
}
