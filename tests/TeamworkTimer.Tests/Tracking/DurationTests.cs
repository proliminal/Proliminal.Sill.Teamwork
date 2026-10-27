using Proliminal.Sill.Plugins.TeamworkTimer.Tracking;

namespace TeamworkTimer.Tests.Tracking;

public sealed class DurationTests
{
    [Theory]
    [InlineData(90, MidpointRounding.AwayFromZero, 2)]
    [InlineData(90, MidpointRounding.ToEven, 2)]
    [InlineData(150, MidpointRounding.ToEven, 2)]
    [InlineData(150, MidpointRounding.AwayFromZero, 3)]
    [InlineData(89, MidpointRounding.AwayFromZero, 1)]
    [InlineData(119, MidpointRounding.ToZero, 1)]
    [InlineData(61, MidpointRounding.ToPositiveInfinity, 2)]
    [InlineData(20, MidpointRounding.AwayFromZero, 0)]
    public void RoundToMinutes_RoundsAsChosen(int seconds, MidpointRounding rounding, int minutes)
        => Assert.Equal(minutes, Duration.RoundToMinutes(TimeSpan.FromSeconds(seconds), rounding));

    [Theory]
    [InlineData("1:30", 90)]
    [InlineData("0:05", 5)]
    [InlineData("12:00", 720)]
    [InlineData("45", 45)]
    [InlineData("1h 30m", 90)]
    [InlineData("2h", 120)]
    [InlineData("45m", 45)]
    [InlineData("45 min", 45)]
    [InlineData(" 1:30 ", 90)]
    public void TryParseMinutes_ReadsTheWaysTimeIsTyped(string text, int minutes)
    {
        Assert.True(Duration.TryParseMinutes(text, out var parsed));
        Assert.Equal(minutes, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:75")]
    [InlineData("1,5")]
    [InlineData("-5")]
    [InlineData("an hour")]
    [InlineData("h")]
    public void TryParseMinutes_RefusesWhatIsntATime(string text)
        => Assert.False(Duration.TryParseMinutes(text, out _));

    [Fact]
    public void FormatClock_ShowsHoursPastADay()
        => Assert.Equal("26:03:09", Duration.FormatClock(new TimeSpan(1, 2, 3, 9)));

    [Fact]
    public void FormatMinutes_ShowsHoursAndMinutes()
        => Assert.Equal("1:05", Duration.FormatMinutes(65));
}
