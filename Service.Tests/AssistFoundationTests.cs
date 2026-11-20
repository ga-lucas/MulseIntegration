using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Service.Assist;
using Service.Endpoints;
using Service.Models;

namespace Service.Tests;

public sealed class AssistFoundationTests
{
    private const string Secret = "hunter2-secret-value";

    private static ModuleDescriptor Sftp() => new("sftp", "SFTP deliver", ModuleKind.Deliver, "Sends a file over SFTP.",
        [new("host", "Host", "Server name", true, ModuleSettingInputKind.Text),
         new("password", "Password", "Login password", false, ModuleSettingInputKind.Password),
         new("path", "Folder", "Remote folder", false, ModuleSettingInputKind.Text)]);

    private static AssistExplainer Explainer() => new(new ReadOnlyCatalog(Sftp()));

    private static readonly string FlowJson = $$$"""
        {"version":1,"durable":true,"nodes":[
          {"id":"w","kind":"Wait","name":"Pause","durationSeconds":5},
          {"id":"d","kind":"Deliver","name":"Send file","module":"sftp","settings":{"password":"{{{Secret}}}","path":"/out"},"retry":{"attempts":3,"intervalSeconds":10,"retryAmbiguous":true}},
          {"id":"b","kind":"Blocked","blockReason":"Unknown functoid"},
          {"id":"m","kind":"Deliver","module":"missing-mod"},
          {"id":"t","kind":"Terminate","terminateMessage":"stop"}
        ]}
        """;

    [Fact]
    public void EveryNodeKindHasADescription()
    {
        foreach (var kind in Enum.GetValues<NativeFlowNodeKind>())
        {
            var info = NativeNodeCatalog.For(kind);
            Assert.Equal(kind.ToString(), info.Kind);
            Assert.False(string.IsNullOrWhiteSpace(info.Title));
            Assert.False(string.IsNullOrWhiteSpace(info.Summary));
            Assert.False(string.IsNullOrWhiteSpace(info.Effect));
        }
        Assert.Equal(Enum.GetValues<NativeFlowNodeKind>().Length, NativeNodeCatalog.All.Count);
    }

    [Fact]
    public void ConfigAndSecretsAreNeverSendable()
    {
        Assert.False(AssistSurfaces.CanBeSent(AssistCategory.ConfigAndSecrets));
        foreach (var part in AssistSurfaces.All.SelectMany(surface => surface.Context).Where(part => part.Category == AssistCategory.ConfigAndSecrets))
            Assert.Equal(0, part.BudgetChars);
        Assert.False(AssistSurfaces.Describe().Categories.Single(category => category.Category == nameof(AssistCategory.ConfigAndSecrets)).CanBeSent);
    }

    [Fact]
    public void SurfacesAreUniqueAndComplete()
    {
        Assert.Equal(AssistSurfaces.All.Count, AssistSurfaces.All.Select(surface => surface.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(AssistSurfaces.All, surface =>
        {
            Assert.NotEmpty(surface.Actions);
            Assert.Equal(surface.Actions.Count, surface.Actions.Select(action => action.Id).Distinct().Count());
        });
        Assert.NotNull(AssistSurfaces.Find("DESIGNER"));
        Assert.Null(AssistSurfaces.Find("nope"));
    }

    [Fact]
    public void GlossaryHasUniqueTermsAndFindsByAlias()
    {
        Assert.Equal(AssistGlossary.All.Count, AssistGlossary.All.Select(entry => entry.Term).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var entry = AssistGlossary.All.First(candidate => candidate.Aliases.Count > 0);
        Assert.Contains(AssistGlossary.Find(entry.Aliases[0].ToUpperInvariant()), found => found.Term == entry.Term);
        Assert.Equal(AssistGlossary.All.Count, AssistGlossary.Find(null).Count);
        Assert.Empty(AssistGlossary.Find("zzz-no-such-term"));
    }

    [Fact]
    public void FlowExplanationReportsFindingsWithoutSettingValues()
    {
        var explanation = Explainer().ExplainFlow(FlowJson);

        Assert.True(explanation.Durable);
        Assert.Equal(5, explanation.NodeCount);
        Assert.Equal(2, explanation.KindCounts["Deliver"]);
        Assert.Equal(5, explanation.Steps.Count);
        Assert.Contains(explanation.Steps, step => step.Id == "d" && step.ModuleName == "SFTP deliver" && step.Title == "Send file");

        string Text(string needle) => explanation.Findings.First(finding => finding.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)).NodeId ?? "";
        Assert.Equal("d", Text("send it twice"));
        Assert.Equal("d", Text("required setting 'Host'"));
        Assert.Equal("d", Text("typed-in value"));
        Assert.Equal("b", Text("not translated"));
        Assert.Equal("m", Text("not installed"));
        Assert.Equal("w", Text("parks the flow"));
        Assert.Equal("t", Text("stops the flow"));

        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(explanation));
        Assert.DoesNotContain("/out", JsonSerializer.Serialize(explanation));
    }

    [Fact]
    public void FlowWithoutDeliverIsFlagged()
    {
        var explanation = Explainer().ExplainFlow("""{"version":1,"nodes":[{"id":"s","kind":"Sequence"}]}""");
        Assert.Contains(explanation.Findings, finding => finding.Text.Contains("no Deliver"));
    }

    [Fact]
    public void NodeExplanationShowsSettingStatesNotValues()
    {
        var node = Explainer().ExplainNode(FlowJson, "d");

        Assert.Equal("Send file", node.Step.Title);
        Assert.Equal("Sends a file over SFTP.", node.ModuleDescription);
        Assert.Equal("not set", node.Settings.Single(setting => setting.Key == "host").State);
        Assert.Equal("set to a literal value", node.Settings.Single(setting => setting.Key == "password").State);
        Assert.Equal("set to a literal value", node.Settings.Single(setting => setting.Key == "path").State);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(node));
    }

    [Fact]
    public void SecretAndConfigReferencesAreRecognisedAndNotFlagged()
    {
        const string json = """{"version":1,"durable":true,"nodes":[{"id":"d","kind":"Deliver","module":"sftp","settings":{"host":"{{config:sftp.host}}","password":"{{secret:sftp.pw}}"}}]}""";
        var node = Explainer().ExplainNode(json, "d");
        Assert.Equal("refers to a config value", node.Settings.Single(setting => setting.Key == "host").State);
        Assert.Equal("refers to a secret", node.Settings.Single(setting => setting.Key == "password").State);
        Assert.DoesNotContain(node.Findings, finding => finding.Text.Contains("typed-in"));
    }

    [Fact]
    public void BadInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Explainer().ExplainFlow(" "));
        Assert.Throws<ArgumentException>(() => Explainer().ExplainFlow("{not json"));
        Assert.Throws<ArgumentException>(() => Explainer().ExplainNode(FlowJson, ""));
        Assert.Throws<KeyNotFoundException>(() => Explainer().ExplainNode(FlowJson, "missing"));
    }

    private const string NeedsAcme = """
        {"version":1,"durable":true,"nodes":[
          {"id":"s","kind":"Deliver","module":"acme-send","messageName":"","settings":{"url":"https://private.example/secret-path","token":"tok-12345","bad\nkey":"x"},"settingExpressions":{"host":{"kind":"Variable","variable":"host"}},"retry":{"attempts":2,"intervalSeconds":5}},
          {"id":"s2","kind":"Deliver","module":"ACME-send","settings":{"url":"u2"}}
        ]}
        """;

    [Fact]
    public void MissingModulesAreListedAndWrongKindIsNotCalledMissing()
    {
        var explanation = Explainer().ExplainFlow(FlowJson);
        var missing = Assert.Single(explanation.MissingModules!);
        Assert.Equal(("missing-mod", "Deliver"), (missing.Module, missing.Kind));
        Assert.Equal(["m"], missing.NodeIds);

        var renderOnly = new AssistExplainer(new ReadOnlyCatalog(new ModuleDescriptor("acme-send", "Acme", ModuleKind.Render, "d")));
        var wrongKind = renderOnly.ExplainFlow(NeedsAcme);
        Assert.Empty(wrongKind.MissingModules!);
        Assert.Contains(wrongKind.Findings, finding => finding.Text.Contains("is a Render module"));
    }

    [Fact]
    public void ModulePromptNamesTheModuleAndSettingKeysButNeverValues()
    {
        var authoring = new AssistModuleAuthoring(new ReadOnlyCatalog(Sftp()));
        var result = authoring.Build(NeedsAcme, "acme-send", "Posts the file to the Acme claims API with a bearer token.");

        Assert.Equal(("acme-send", "Deliver"), (result.Module, result.Kind));
        Assert.Equal(["s", "s2"], result.NodeIds);
        Assert.Equal(["bad?key", "host", "token", "url"], result.SettingKeys);
        Assert.Contains("IDeliverModule", result.Prompt);
        Assert.Contains("AddDeliver<T>()", result.Prompt);
        Assert.Contains("module id: acme-send", result.Prompt);
        Assert.Contains("- host (the flow calculates this when it runs)", result.Prompt);
        Assert.Contains("retries up to 2 time(s)", result.Prompt);
        Assert.Contains("Posts the file to the Acme claims API", result.Prompt);
        Assert.DoesNotContain("private.example", result.Prompt);
        Assert.DoesNotContain("tok-12345", result.Prompt);
        Assert.DoesNotContain("\r", result.Prompt);
        Assert.False(string.IsNullOrWhiteSpace(result.Disclaimer));
    }

    [Fact]
    public void ModulePromptWithoutADescriptionTellsTheToolToAskAndListsUnloadedPackages()
    {
        var result = new AssistModuleAuthoring(new ReadOnlyCatalog()).Build(NeedsAcme, "acme-send", "  ");
        Assert.Contains("Do not guess.", result.Prompt);
        Assert.DoesNotContain("<<<DESCRIPTION", result.Prompt);
        Assert.Empty(result.UnloadedPackages);

        var packages = new ReadOnlyCatalog { Packages = [new("loaded-pack", "C:\\secret\\path.dll", RuntimePackageSourceKind.BuiltIn, true, DateTimeOffset.UnixEpoch, 1), new("idle-pack", "C:\\secret\\idle.dll", RuntimePackageSourceKind.BuiltIn, false, DateTimeOffset.UnixEpoch, 0)] };
        var withPackages = new AssistModuleAuthoring(packages).Build(NeedsAcme, "acme-send", null);
        Assert.Equal(["idle-pack"], withPackages.UnloadedPackages);
        Assert.Contains("idle-pack", withPackages.Prompt);
        Assert.DoesNotContain("loaded-pack", withPackages.Prompt.Replace("idle-pack", ""));
        Assert.DoesNotContain("path.dll", withPackages.Prompt);
        Assert.DoesNotContain("idle.dll", withPackages.Prompt);
    }

    [Fact]
    public void ModulePromptRefusesWhatItCannotAnswerHonestly()
    {
        var authoring = new AssistModuleAuthoring(new ReadOnlyCatalog(Sftp()));
        Assert.Throws<ArgumentException>(() => authoring.Build(NeedsAcme, " ", null));
        Assert.Throws<ArgumentException>(() => authoring.Build(" ", "acme-send", null));
        Assert.Throws<KeyNotFoundException>(() => authoring.Build(NeedsAcme, "nobody", null));
        Assert.Throws<InvalidOperationException>(() => authoring.Build(FlowJson, "sftp", null));
        var wrongKind = new AssistModuleAuthoring(new ReadOnlyCatalog(new ModuleDescriptor("acme-send", "Acme", ModuleKind.Render, "d")));
        Assert.Contains("installed as a Render module", Assert.Throws<InvalidOperationException>(() => wrongKind.Build(NeedsAcme, "acme-send", null)).Message);
    }

    [Fact]
    public void ModulePromptForATransformStatesTheArgumentCount()
    {
        const string json = """{"version":1,"durable":true,"nodes":[{"id":"t","kind":"InvokeTransform","module":"acme-math","variable":"r","transformInputs":[{"kind":"Variable","variable":"a"},{"kind":"Variable","variable":"b"}]}]}""";
        var result = new AssistModuleAuthoring(new ReadOnlyCatalog()).Build(json, "acme-math", null);
        Assert.Equal("Transform", result.Kind);
        Assert.Contains("ITransformModule", result.Prompt);
        Assert.Contains("passes 2 argument(s)", result.Prompt);
        Assert.Contains("The flow passes 2 argument(s)", result.Prompt);
    }

    [Fact]
    public async Task EndpointsServeCatalogsAndExplanations()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog(Sftp()));
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            Assert.Contains("ConfigAndSecrets", await client.GetStringAsync("/api/assist/surfaces"));
            Assert.Contains("Deliver", await client.GetStringAsync("/api/assist/catalog/node-kinds"));
            var glossary = await client.GetFromJsonAsync<JsonElement>("/api/assist/glossary?search=" + Uri.EscapeDataString(AssistGlossary.All[0].Term));
            Assert.True(glossary.GetArrayLength() >= 1);

            using var flow = await client.PostAsJsonAsync("/api/assist/explain/flow", new { flowJson = FlowJson });
            Assert.True(flow.IsSuccessStatusCode, await flow.Content.ReadAsStringAsync());
            Assert.Equal("no-store", flow.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(Secret, await flow.Content.ReadAsStringAsync());

            using var node = await client.PostAsJsonAsync("/api/assist/explain/node", new { flowJson = FlowJson, nodeId = "d" });
            Assert.True(node.IsSuccessStatusCode);
            Assert.Equal("no-store", node.Headers.CacheControl?.ToString());

            using var missing = await client.PostAsJsonAsync("/api/assist/explain/node", new { flowJson = FlowJson, nodeId = "zzz" });
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

            using var prompt = await client.PostAsJsonAsync("/api/assist/module-prompt", new { flowJson = FlowJson, moduleId = "missing-mod", description = "Does a thing." });
            Assert.True(prompt.IsSuccessStatusCode, await prompt.Content.ReadAsStringAsync());
            Assert.Equal("no-store", prompt.Headers.CacheControl?.ToString());
            Assert.Contains("IDeliverModule", await prompt.Content.ReadAsStringAsync());
            using var installed = await client.PostAsJsonAsync("/api/assist/module-prompt", new { flowJson = FlowJson, moduleId = "sftp" });
            Assert.Equal(System.Net.HttpStatusCode.Conflict, installed.StatusCode);
            using var unused = await client.PostAsJsonAsync("/api/assist/module-prompt", new { flowJson = FlowJson, moduleId = "nobody" });
            Assert.Equal(System.Net.HttpStatusCode.NotFound, unused.StatusCode);
            using var bad = await client.PostAsJsonAsync("/api/assist/explain/flow", new { flowJson = "{oops" });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
