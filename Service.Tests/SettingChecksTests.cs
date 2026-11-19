using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class SettingChecksTests
{
    private const string Secret = "s3cr3t-Value!";

    [Theory]
    [InlineData("password", "Text")]
    [InlineData("apiKey", "Text")]
    [InlineData("anything", "Password")]
    [InlineData("connectionString", "TextArea")]
    public void ALiteralCredentialIsFlaggedAndCanBeMoved(string key, string kind)
    {
        var finding = Assert.Single(SettingChecks.Evaluate(key, Secret, kind), item => item.Code == SettingChecks.PlainSecret);

        Assert.True(finding.CanStore);
        Assert.DoesNotContain(Secret, finding.Message);
    }

    [Theory]
    [InlineData("tokenUrl", "https://login.example.com/token")]
    [InlineData("passwordFile", "/etc/secret")]
    [InlineData("path", "/data/in")]
    public void KeysThatNameWhereASecretLivesAreNotFlagged(string key, string value)
        => Assert.Empty(SettingChecks.Evaluate(key, value, "Text"));

    [Theory]
    [InlineData("{{secret:sftp-password}}")]
    [InlineData("{{config:db-conn}}")]
    [InlineData("Server=x;Password={{secret:db}}")]
    public void AReferenceIsNotACredentialTypedInPlainText(string value)
        => Assert.Empty(SettingChecks.Evaluate("password", value, "Password"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyValueIsNeverFlagged(string? value)
        => Assert.Empty(SettingChecks.Evaluate("password", value, "Password"));

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Select")]
    public void ChoicesAreNeverChecked(string kind)
        => Assert.Empty(SettingChecks.Evaluate("password", "true", kind));

    [Theory]
    [InlineData("TODO")]
    [InlineData("<host>")]
    [InlineData("CHANGEME")]
    public void AnUnfilledPlaceholderIsNoted(string value)
    {
        var finding = Assert.Single(SettingChecks.Evaluate("host", value, "Text"));

        Assert.Equal(SettingChecks.Placeholder, finding.Code);
        Assert.False(finding.CanStore);
    }

    [Fact]
    public void AnIncompleteReferenceIsNotedOnSingleLineInputsOnly()
    {
        Assert.Equal(SettingChecks.BrokenReference, Assert.Single(SettingChecks.Evaluate("host", "{{secret:abc", "Text")).Code);
        Assert.Equal(SettingChecks.BrokenReference, Assert.Single(SettingChecks.Evaluate("host", "{{ secret:abc }}", "Text")).Code);
        Assert.Empty(SettingChecks.Evaluate("flowJson", "{\"a\":{\"b\":{{1}}}}", "TextArea"));
        Assert.Empty(SettingChecks.Evaluate("host", "{{secret:abc}} and {{config:x}}", "Text"));
    }

    [Theory]
    [InlineData("sftpPassword", "sftp-password")]
    [InlineData("apiKey", "api-key")]
    [InlineData("db.connection_string", "db-connection-string")]
    [InlineData("", "credential")]
    [InlineData("###", "credential")]
    [InlineData(null, "credential")]
    public void ASuggestedNameIsShortAndValid(string? key, string expected)
    {
        var name = SettingChecks.SuggestName(key, []);

        Assert.Equal(expected, name);
        Assert.Null(SettingChecks.ValidateName(name, []));
    }

    [Fact]
    public void ASuggestedNameNeverCollidesWithAStoredOne()
        => Assert.Equal("api-key-3", SettingChecks.SuggestName("apiKey", ["API-KEY", "api-key-2"]));

    [Fact]
    public void ALongKeyStillGivesAValidName()
        => Assert.Null(SettingChecks.ValidateName(SettingChecks.SuggestName(new string('a', 500) + "Password", []), []));

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("a b", "Use letters")]
    [InlineData("{{x}}", "Use letters")]
    [InlineData("../x", "Use letters")]
    [InlineData("Taken", "already exists")]
    public void BadOrTakenNamesAreRefused(string name, string reason)
        => Assert.Contains(reason, SettingChecks.ValidateName(name, ["taken"]));

    [Fact]
    public void TheReferenceUsesTheSecretKind()
        => Assert.Equal("{{secret:db-password}}", SettingChecks.SecretReference("db-password"));
}
