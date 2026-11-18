using System.Text.Json;
using Service.Ai;

namespace Service.Tests;

public sealed class AiAuthoringDisclosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcelWatcherOrdinaryOptionsAreVisibleAndPathsRequireConsent(bool includeEnvironment)
    {
        var settings = new Dictionary<string, string>
        {
            ["searchPattern"] = "*.xlsx", ["settleSeconds"] = "2", ["retryDelayMs"] = "500",
            ["path"] = @"C:\private\inbox", ["archivePath"] = @"C:\private\archive",
            ["url"] = "https://internal.example/orders", ["unknownSetting"] = "private-free-text",
            ["token"] = "secret-token", ["apiKey"] = "123", ["password"] = "true",
            ["connectionString"] = "Server=private;Password=secret;", ["privateKeyPath"] = @"C:\keys\private.pem",
            ["reference"] = "{{secret:destination}}", ["config"] = "{{config:folder}}"
        };
        var result = Redact(settings, includeEnvironment);
        var disclosed = result.Context.Fetches![0].Fetch!.Settings!;
        Assert.Equal("*.xlsx", disclosed["searchPattern"]);
        Assert.Equal("2", disclosed["settleSeconds"]);
        Assert.Equal("500", disclosed["retryDelayMs"]);
        foreach (var key in new[] { "path", "archivePath", "url" })
            Assert.Equal(includeEnvironment ? settings[key] : AiAuthoringRedactor.Placeholder, disclosed[key]);
        foreach (var key in new[] { "unknownSetting", "token", "apiKey", "password", "connectionString", "privateKeyPath" })
            Assert.Equal(AiAuthoringRedactor.Placeholder, disclosed[key]);
        Assert.Equal("{{secret:destination}}", disclosed["reference"]);
        Assert.Equal("{{config:folder}}", disclosed["config"]);
        Assert.Equal("secret-token", result.Originals["s0"].Withheld["token"]);
        Assert.Equal(@"C:\private\inbox", settings["path"]);
    }

    [Theory]
    [InlineData("https://user:pass@internal.example/orders")]
    [InlineData("https://internal.example/orders?token=hidden")]
    [InlineData("https://internal.example/orders?filter=orders")]
    [InlineData("https://internal.example/orders#hidden")]
    [InlineData("Bearer hidden-credential")]
    [InlineData("https://internal.example/{{secret:key}}")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@")]
    [InlineData("//user:pass@internal.example/orders")]
    [InlineData("file://user:pass@internal.example/orders")]
    [InlineData("file:///private/orders?filter=hidden")]
    public void EnvironmentConsentNeverOverridesCredentialAndUrlGuards(string value)
    {
        var result = Redact(new() { ["endpoint"] = value }, true);
        Assert.Equal(AiAuthoringRedactor.Placeholder, result.Context.Fetches![0].Fetch!.Settings!["endpoint"]);
        Assert.Equal(value, result.Originals["s0"].Withheld["endpoint"]);
    }

    [Theory]
    [InlineData("searchPattern", "C:\\private\\*.xlsx")]
    [InlineData("searchPattern", "password=hidden")]
    [InlineData("settleSeconds", "hidden-value")]
    [InlineData("settleSeconds", "123456789012345")]
    [InlineData("searchPattern", "{{secret:key}}-suffix")]
    public void OrdinaryOptionNamesDoNotExposeArbitraryFreeText(string key, string value)
    {
        var disclosed = Redact(new() { [key] = value }, false).Context.Fetches![0].Fetch!.Settings!;
        Assert.Equal(AiAuthoringRedactor.Placeholder, disclosed[key]);
    }

    [Fact]
    public void PasswordSchemaOverridesSafeLookingNamesAndValues()
    {
        var catalog = new ModuleDescriptor("fetch", "Fetch", ModuleKind.Fetch, "test",
            [new("searchPattern", "Sensitive", "test", false, ModuleSettingInputKind.Password),
             new("settleSeconds", "Sensitive", "test", false, ModuleSettingInputKind.Password),
             new("apiKey", "Key", "test", false, ModuleSettingInputKind.Number),
             new("token", "Token", "test", false, ModuleSettingInputKind.Select, Options: [new("enabled", "Enabled")])]);
        var disclosed = Redact(new() { ["searchPattern"] = "*.xlsx", ["settleSeconds"] = "2", ["apiKey"] = "123", ["token"] = "enabled" }, true, catalog)
            .Context.Fetches![0].Fetch!.Settings!;
        Assert.All(disclosed.Values, value => Assert.Equal(AiAuthoringRedactor.Placeholder, value));
    }

    private static AiRedactedDraft Redact(Dictionary<string, string> settings, bool includeEnvironment, ModuleDescriptor? descriptor = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "excel", fetches = new[] { new { id = "f", fetch = new { module = "fetch", settings } } },
            parses = Array.Empty<object>(), augments = Array.Empty<object>(), deliveries = Array.Empty<object>()
        });
        var draft = JsonSerializer.Deserialize<AiFlowDoc>(json, AiAuthoringJson.Options)!;
        AiAuthoringRedactor.Normalize(draft);
        return AiAuthoringRedactor.Redact(draft, [descriptor ?? new("fetch", "Fetch", ModuleKind.Fetch, "test")], includeEnvironment);
    }
}
