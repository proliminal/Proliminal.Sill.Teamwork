using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using Proliminal.Sill.Sdk.Abstractions;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Settings;

/// <summary>
///     The site and API token to reach Teamwork with, as set on the plugin's page: read each time, so a change applies
///     right away. The popup doesn't ask for them; it says which is missing.
/// </summary>
internal sealed class TeamworkConnection(IPluginSettings<TimerSettings> settings, IValue<string> token)
{
    /// <summary>
    ///     The API token, a secret of the plugin's, for the page to bind to.
    /// </summary>
    public IValue<string> Token
        => token;

    /// <summary>
    ///     How to round the time a timer ran to whole minutes.
    /// </summary>
    public MidpointRounding Rounding
        => settings.Current.Rounding;

    /// <summary>
    ///     The site and token, when both are set.
    /// </summary>
    /// <returns>Whether both are set.</returns>
    public bool TryGetConnection(out Uri site, out string apiToken)
    {
        site = TeamworkClient.ParseSite(settings.Current.Site)!;
        apiToken = token.Value;

        return site is not null && apiToken.Length > 0;
    }

    /// <summary>
    ///     The site and token, or what's missing as an error to show.
    /// </summary>
    public (Uri Site, string Token) RequireConnection()
        => TryGetConnection(out var site, out var apiToken)
            ? (site, apiToken)
            : throw new TeamworkException(DescribeMissingSetup()!);

    /// <summary>
    ///     What's missing to reach Teamwork, and where to set it; <see langword="null"/> when nothing is.
    /// </summary>
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
