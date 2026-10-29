namespace Proliminal.Sill.Plugins.TeamworkTimer.Settings;

public sealed class TimerSettings
{
    public string Site { get; set; } = "";

    public MidpointRounding Rounding { get; set; } = MidpointRounding.AwayFromZero;

    public List<TimerProfile> Profiles { get; set; } = [];
}
