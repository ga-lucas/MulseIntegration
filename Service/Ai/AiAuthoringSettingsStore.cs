using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Service.Ai;

/// <summary>UI-managed overrides. <see cref="ApiKey"/> is only ever stored encrypted and never returned by the API.</summary>
internal sealed record AiAuthoringOverrides(bool Enabled, string Provider, string Model, string? Endpoint, int MaxOutputTokens, string? ApiKey, bool KeySuppressed = false);

internal interface IAiAuthoringSettingsStore
{
    AiAuthoringOverrides? Load();

    void Save(AiAuthoringOverrides overrides);
}

/// <summary>
/// Persists overrides in a single DataProtection-encrypted file under a dedicated directory with its own key ring and
/// application name, so it is neither shared with other protectors nor part of workspace backups.
/// </summary>
internal sealed class AiAuthoringSettingsStore : IAiAuthoringSettingsStore
{
    private const string Purpose = "Mulse.AiAuthoring.Settings.v1";
    private readonly string _directory;
    private readonly ILogger<AiAuthoringSettingsStore> _logger;
    private readonly Lock _gate = new();
    private IDataProtector? _protector;

    public AiAuthoringSettingsStore(string directory, ILogger<AiAuthoringSettingsStore> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_directory, "settings.bin");

    public AiAuthoringOverrides? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return null;
            try
            {
                var plain = GetProtector().Unprotect(File.ReadAllBytes(FilePath));
                return JsonSerializer.Deserialize<AiAuthoringOverrides>(plain, AiAuthoringJson.Options) ?? throw new JsonException("Empty settings.");
            }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError("AI authoring override file could not be read ({ExceptionType}); AI authoring is unavailable until it is repaired or removed.", exception.GetType().Name);
                throw new AiAuthoringException(StatusCodes.Status503ServiceUnavailable, "AI authoring settings are unavailable because the stored settings could not be read. Repair or remove the settings file.");
            }
        }
    }

    public void Save(AiAuthoringOverrides overrides)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var protectedBytes = GetProtector().Protect(JsonSerializer.SerializeToUtf8Bytes(overrides, AiAuthoringJson.Options));
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    private IDataProtector GetProtector()
    {
        if (_protector is not null) return _protector;
        Directory.CreateDirectory(_directory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys")), builder =>
        {
            builder.SetApplicationName("Mulse.AiAuthoring");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        return _protector = provider.CreateProtector(Purpose);
    }
}
