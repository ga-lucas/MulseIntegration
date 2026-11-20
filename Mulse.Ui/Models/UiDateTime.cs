using System.Globalization;

namespace Mulse.Ui.Models;

/// <summary>Flows store every date/time as UTC. The editor can show and accept values in the server's local zone; this converts at the edge only.</summary>
public static class UiDateTime
{
    public const string InputFormat = "yyyy-MM-dd'T'HH:mm:ss";
    public const string DefaultUtcText = "2000-01-01T00:00:00.0000000Z";

    public static string ServerZoneLabel => $"server local ({TimeZoneInfo.Local.DisplayName})";

    public static bool TryRead(string? utcText, out DateTime utc)
        => DateTime.TryParseExact(utcText, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc) && utc.Kind == DateTimeKind.Utc;

    /// <summary>Text for a datetime-local input, or empty when the stored value is not valid UTC text.</summary>
    public static string ToInput(string? utcText, bool serverLocal, TimeZoneInfo? zone = null)
    {
        if (!TryRead(utcText, out var utc)) return "";
        var shown = serverLocal ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local) : utc;
        return shown.ToString(InputFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Converts input text to the stored UTC text. A server-local time inside a daylight-saving gap is rejected; an ambiguous one takes the standard-time reading.</summary>
    public static bool TryFromInput(string? input, bool serverLocal, out string utcText, TimeZoneInfo? zone = null)
    {
        utcText = "";
        if (!DateTime.TryParseExact(input, [InputFormat, "yyyy-MM-dd'T'HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var entered)) return false;
        DateTime utc;
        if (serverLocal)
        {
            var local = zone ?? TimeZoneInfo.Local;
            if (local.IsInvalidTime(entered)) return false;
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(entered, DateTimeKind.Unspecified), local);
        }
        else utc = DateTime.SpecifyKind(entered, DateTimeKind.Utc);
        utcText = utc.ToString("O", CultureInfo.InvariantCulture);
        return true;
    }
}
