using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using Proliminal.Sill.Sdk.Abstractions;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Settings;

internal sealed class TeamworkConnection(IPluginSettings<TimerSettings> settings, IValue<string> token)
{
    public IValue<string> Token
        => token;

    public MidpointRounding Rounding
        => settings.Current.Rounding;

    public bool TryGetConnection(out Uri site, out string apiToken)
    {
        site = TeamworkClient.ParseSite(settings.Current.Site)!;
        apiToken = token.Value;

        return site is not null && apiToken.Length > 0;
    }

    public (Uri Site, string Token) RequireConnection()
        => TryGetConnection(out var site, out var apiToken)
            ? (site, apiToken)
            : throw new TeamworkException(DescribeMissingSetup()!);

    public string? DescribeMissingSetup()
    {
        var hasSite = TeamworkClient.ParseSite(settings.Current.Site) is not null;
        var hasToken = token.Value.Length > 0;

        var missing = (hasSite, hasToken) switch
        {
            (false, false) => "your Teamwork site and API token are set",
            (false, true) => "your Teamwork site is set",
            (true, false) => "your API token is set",
            _ => null,
        };

        return missing is null ? null : $"Track time doesn't work until {missing}. Set it in Sill's settings, on the Teamwork timer page.";
    }
}
