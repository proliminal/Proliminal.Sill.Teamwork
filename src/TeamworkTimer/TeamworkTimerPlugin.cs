using Proliminal.Sill.Plugins.TeamworkTimer.Popup;
using Proliminal.Sill.Plugins.TeamworkTimer.Settings;
using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using Proliminal.Sill.Plugins.TeamworkTimer.Tracking;
using Proliminal.Sill.Sdk;
using Proliminal.Sill.Sdk.Abstractions;

[assembly: PluginUses(PluginCapability.Network,
    "Sends the task numbers, descriptions and times you track to your Teamwork site, and reads task names and your timers from it. Nothing else leaves your PC.")]

namespace Proliminal.Sill.Plugins.TeamworkTimer;

/// <summary>
///     Tracks time on Teamwork tasks with Teamwork's own timers: a page for the site, token and rounding, the Track time
///     popup to start and stop timers, and a section in the quick menu to stop the one that runs. What it does is in
///     <see cref="TimeTracker"/> and <see cref="TrackTimePopup"/>.
/// </summary>
public sealed class TeamworkTimerPlugin : IPlugin
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _disposed = new();

    private TimeTracker? _tracker;
    private TrackTimePopup? _popup;

    // The version the build gives it, which a release sets; as major.minor.patch, as Sill shows it.
    public PluginInfo Info { get; } = new("TeamworkTimer", "Teamwork timer", "Proliminal", ReleasedVersion());

    public void Configure(IPluginContext context)
    {
        var settings = context.GetSettings<TimerSettings>();
        var connection = new TeamworkConnection(settings, context.GetSecret("ApiToken"));
        var client = new TeamworkClient(_http);

        var tracker = _tracker = new TimeTracker(context, client, connection, _disposed.Token);
        var popup = _popup = new TrackTimePopup(tracker, client, connection, _disposed.Token);

        var site = settings.Value(options => options.Site, (options, value) => options.Site = value.Trim());
        var rounding = settings.Value(options => options.Rounding, (options, value) => options.Rounding = value);

        var timerLabel = tracker.Timer.Map(timer => timer is null ? "" : timer.IsRunning ? timer.Task.Label : $"Paused, not logged yet: {timer.Task.Label}");

        context.SetPage(Glyph.Clock, page => page
            .Intro("Track time on Teamwork tasks with Teamwork's own timers. Track time, in the quick menu or with a shortcut you give it, starts one for a task number; it runs in Teamwork until you stop it, from the same popup or the quick menu. Stopping asks how much to log, rounded as set below.")
            .Keywords("teamwork", "time tracking", "timesheet", "timer", "log time")
            .Group("Timer", group => group
                .Info("Now", info => info
                    .Icon(Glyph.Clock)
                    .Description(timerLabel.Map(label => label.Length == 0 ? "No timer runs." : label))
                    .Status(tracker.Clock)
                    .Action("Track time", popup.Show))
                .Info("Not logged yet", info => info
                    .Icon(Glyph.Warning)
                    .Description(tracker.PendingLog.Map(log => log is null ? "" : $"{Duration.FormatMinutes(log.Minutes)} on {log.Timer.Task.Label}. The timer waits, paused, in Teamwork."))
                    .VisibleWhen(tracker.PendingLog.Map(log => log is not null))
                    .Action("Try again", _ => tracker.RetryPendingLogAsync())))
            .Group("Teamwork", group => group
                .TextBox("Site", site, text => text
                    .Icon(Glyph.Globe)
                    .Description("Your Teamwork address.")
                    .Placeholder("example.teamwork.com")
                    .Status(site.Map(value => value.Length == 0 || TeamworkClient.ParseSite(value) is not null ? null : "Not a web address"), Values.State(Tone.Critical)))
                .TextBox("API token", connection.Token, text => text
                    .Icon(Glyph.Key)
                    .Description("In Teamwork, under your picture › Edit my details › API & Mobile. Kept encrypted on this PC, never in the settings file or an exported setup.")
                    .Placeholder("Paste your token")
                    .Secret()))
            .Group("Logging", group => group
                .Picker("Rounding", rounding, picker => picker
                    .Icon(Glyph.Clock)
                    .Description("How the time a timer ran is rounded to whole minutes when you stop it. You can still change it before it's logged.")
                    .Option(MidpointRounding.AwayFromZero, "Nearest minute, half a minute up")
                    .Option(MidpointRounding.ToEven, "Nearest minute, half a minute to even")
                    .Option(MidpointRounding.ToZero, "Down, towards zero")
                    .Option(MidpointRounding.ToPositiveInfinity, "Up, to the next minute"))));

        context.AddQuickAction("Track time", Glyph.Clock, popup.Show);

        // Stopping, not starting: from the menu it isn't clear which task a new timer would be for.
        context.AddQuickMenuSection("Teamwork timer", section => section
            .Status(tracker.Clock)
            .Description(timerLabel)
            .VisibleWhen(tracker.Timer.Map(timer => timer is not null))
            .Action("Stop", popup.StopFromMenuAsync, action => action.VisibleWhen(tracker.Timer.Map(timer => timer is { IsRunning: true })))
            .Action("Log time", popup.StopFromMenuAsync, action => action.VisibleWhen(tracker.Timer.Map(timer => timer is { IsRunning: false }))));

        tracker.StartPolling();
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _popup?.Dispose();
        _tracker?.Dispose();
        _http.Dispose();
    }

    private static Version ReleasedVersion()
    {
        var version = typeof(TeamworkTimerPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0);

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
