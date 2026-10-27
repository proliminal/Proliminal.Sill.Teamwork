using System.Globalization;
using System.Text.RegularExpressions;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Tracking;

internal static partial class Duration
{
    public static int RoundToMinutes(TimeSpan elapsed, MidpointRounding rounding)
        => (int)Math.Round((decimal)Math.Max(elapsed.TotalSeconds, 0) / 60m, rounding);

    public static string FormatClock(TimeSpan elapsed)
        => $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    public static string FormatMinutes(int minutes)
        => $"{minutes / 60}:{minutes % 60:00}";

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
