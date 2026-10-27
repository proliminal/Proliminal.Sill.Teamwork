namespace Proliminal.Sill.Plugins.TeamworkTimer.Settings;

/// <summary>
///     The plugin's options, kept in Sill's settings.json under its id. The API token isn't one of them: it's a secret,
///     kept apart from this file.
/// </summary>
public sealed class TimerSettings
{
    /// <summary>
    ///     The Teamwork site, such as <c>example.teamwork.com</c>.
    /// </summary>
    public string Site { get; set; } = "";

    /// <summary>
    ///     How the time a timer ran is rounded to whole minutes, before it can be changed and logged.
    /// </summary>
    public MidpointRounding Rounding { get; set; } = MidpointRounding.AwayFromZero;
}
