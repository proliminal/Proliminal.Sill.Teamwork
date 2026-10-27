using System.Globalization;
using System.Text.RegularExpressions;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Tracking;

/// <summary>
///     Times as they're shown and typed: a running clock as <c>1:23:07</c>, and time to log in whole minutes, typed as
///     <c>1:23</c>, <c>83</c>, <c>1h 23m</c>, <c>1h</c> or <c>45m</c>.
/// </summary>
internal static partial class Duration
{
    /// <summary>
    ///     <paramref name="elapsed"/> in whole minutes, rounded with <paramref name="rounding"/>.
    /// </summary>
    public static int RoundToMinutes(TimeSpan elapsed, MidpointRounding rounding)
        => (int)Math.Round((decimal)Math.Max(elapsed.TotalSeconds, 0) / 60m, rounding);

    /// <summary>
    ///     A running clock: <c>0:04:09</c>, <c>12:00:00</c>.
    /// </summary>
    public static string FormatClock(TimeSpan elapsed)
        => $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    /// <summary>
    ///     Minutes as hours and minutes: <c>1:23</c>, <c>0:05</c>.
    /// </summary>
    public static string FormatMinutes(int minutes)
        => $"{minutes / 60}:{minutes % 60:00}";

    /// <summary>
    ///     Reads a time typed in hours and minutes, in any of the ways this class says.
    /// </summary>
    /// <returns>Whether it could be read.</returns>
    public static bool TryParseMinutes(string? text, out int minutes)
    {
        minutes = 0;
        text = text?.Trim();

        if (string.IsNullOrEmpty(text))
            return false;

        if (ClockPattern().Match(text) is { Success: true } clock)
        {
            var clockMinutes = int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture);

            if (clockMinutes > 59)
                return false;

            minutes = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture) * 60 + clockMinutes;
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            return true;

        if (UnitsPattern().Match(text) is { Success: true } units && (units.Groups["h"].Success || units.Groups["m"].Success))
        {
            var hours = units.Groups["h"].Success ? int.Parse(units.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
            var rest = units.Groups["m"].Success ? int.Parse(units.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;

            minutes = hours * 60 + rest;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(?<h>\d{1,3}):(?<m>\d{2})$")]
    private static partial Regex ClockPattern();

    [GeneratedRegex(@"^(?:(?<h>\d{1,3})\s*h)?\s*(?:(?<m>\d{1,4})\s*m(?:in)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsPattern();
}
