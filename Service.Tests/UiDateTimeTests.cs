using Mulse.Ui.Models;

namespace Service.Tests;

public sealed class UiDateTimeTests
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("SyntheticPlus2", TimeSpan.FromHours(2), "Synthetic +2", "Synthetic +2");
    private static readonly TimeZoneInfo Dst = TimeZoneInfo.CreateCustomTimeZone("SyntheticDst", TimeSpan.FromHours(-5), "Synthetic DST", "Synthetic standard", "Synthetic daylight",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1))]);

    [Fact]
    public void ServerLocalEntryIsConvertedToStoredUtcAndShownBack()
    {
        Assert.True(UiDateTime.TryFromInput("2026-03-01T10:30:00", true, out var utc, Plus2));
        Assert.Equal("2026-03-01T08:30:00.0000000Z", utc);
        Assert.Equal("2026-03-01T10:30:00", UiDateTime.ToInput(utc, true, Plus2));
        Assert.Equal("2026-03-01T08:30:00", UiDateTime.ToInput(utc, false, Plus2));
    }

    [Fact]
    public void UtcEntryIsStoredUnchangedAndMinutePrecisionInputIsAccepted()
    {
        Assert.True(UiDateTime.TryFromInput("2026-03-01T10:30", false, out var utc, Plus2));
        Assert.Equal("2026-03-01T10:30:00.0000000Z", utc);
        Assert.True(UiDateTime.TryRead(utc, out var read));
        Assert.Equal(DateTimeKind.Utc, read.Kind);
    }

    [Fact]
    public void InvalidTextAndDaylightSavingGapsAreRejectedWhileUtcNeverHasGaps()
    {
        Assert.False(UiDateTime.TryFromInput("not a date", true, out _, Plus2));
        Assert.False(UiDateTime.TryFromInput("", false, out _, Plus2));
        Assert.False(UiDateTime.TryFromInput("2026-03-08T02:30:00", true, out _, Dst));
        Assert.True(UiDateTime.TryFromInput("2026-03-08T02:30:00", false, out _, Dst));
        Assert.True(UiDateTime.TryFromInput("2026-11-01T01:30:00", true, out var ambiguous, Dst));
        Assert.Equal("2026-11-01T06:30:00.0000000Z", ambiguous);
        Assert.Equal("", UiDateTime.ToInput("2026-03-01T10:30:00", true, Plus2));
        Assert.Equal("", UiDateTime.ToInput("2026-03-01T10:30:00.0000000+02:00", false, Plus2));
    }
}
