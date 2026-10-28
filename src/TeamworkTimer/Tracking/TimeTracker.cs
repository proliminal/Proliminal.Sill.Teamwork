using Proliminal.Sill.Plugins.TeamworkTimer.Settings;
using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using Proliminal.Sill.Sdk;
using Proliminal.Sill.Sdk.Abstractions;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Tracking;

internal sealed class TimeTracker : IDisposable
{
    // How often Teamwork is asked about timers started or stopped there, and how often a log it didn't take is tried again.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _retryInterval = TimeSpan.FromMinutes(1);

    private readonly IPluginContext _context;
    private readonly TeamworkClient _client;
    private readonly TeamworkConnection _connection;
    private readonly CancellationToken _cancellation;

    // The timer as Teamwork last said or as it was last changed here: running, or paused until it's logged. Null when
    // there's none.
    private readonly State<TrackedTimer?> _timer = Value.State<TrackedTimer?>(null);

    // How long it has run, as a clock that ticks while it runs.
    private readonly State<string?> _clock = Value.State<string?>(null);

    private readonly State<PendingLog?> _pendingLog = Value.State<PendingLog?>(null);

    // The timers paused here, until they're logged or discarded. A paused timer this plugin didn't pause is left alone,
    // as it may be paused on purpose in Teamwork.
    private readonly HashSet<long> _pausedHere = [];

    // The tags picked when a timer was started here, for its time log; a timer can't keep them in Teamwork.
    private readonly Dictionary<long, IReadOnlyList<TeamworkTag>> _tags = [];

    private Timer? _clockTicker;
    private Timer? _poller;

    // Whether the log that waits failed in a way that trying again may fix.
    private bool _isPendingLogRetryable;

    public TimeTracker(IPluginContext context, TeamworkClient client, TeamworkConnection connection, CancellationToken cancellation)
    {
        _context = context;
        _client = client;
        _connection = connection;
        _cancellation = cancellation;

        _timer.Changed += UpdateClock;
    }

    public event Action? TimersRead;

    public IReadOnlyValue<TrackedTimer?> Timer
        => _timer;

    public IReadOnlyValue<string?> Clock
        => _clock;

    public IReadOnlyValue<PendingLog?> PendingLog
        => _pendingLog;

    public void StartPolling()
    {
        // What's posted after the plugin is turned off doesn't run.
        _clockTicker = new Timer(_ => _context.Post(UpdateClock), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _poller = new Timer(_ => _context.Post(PollTeamwork), null, TimeSpan.FromSeconds(2), _pollInterval);
    }

    public async Task StartTimerAsync(TeamworkTask task, string description, bool isBillable, IReadOnlyList<TeamworkTag> tags)
    {
        var (site, token) = _connection.RequireConnection();

        var started = await _client.StartTimerAsync(site, token, task, description, isBillable, _cancellation);

        if (tags.Count > 0)
            _tags[started.Id] = tags;

        _timer.Value = started;
    }

    // None for a timer started in Teamwork, or before Sill last started.
    public IReadOnlyList<TeamworkTag> TagsOf(TrackedTimer timer)
        => _tags.GetValueOrDefault(timer.Id) ?? [];

    public async Task<TrackedTimer> PauseRunningTimerAsync()
    {
        var (site, token) = _connection.RequireConnection();

        var running = _timer.Value is { IsRunning: true } timer
            ? timer
            : throw new TeamworkException("The timer isn't running any more; it may have been stopped in Teamwork.");

        var paused = await _client.PauseTimerAsync(site, token, running, _cancellation);

        _pausedHere.Add(paused.Id);
        _timer.Value = paused;

        return paused;
    }

    public async Task ResumeTimerAsync(TrackedTimer timer)
    {
        var (site, token) = _connection.RequireConnection();

        _timer.Value = await _client.ResumeTimerAsync(site, token, timer, _cancellation);
        _pausedHere.Remove(timer.Id);
    }

    public async Task LogTimeAsync(TrackedTimer timer, int minutes, string description, bool isBillable, IReadOnlyList<TeamworkTag> tags)
    {
        var (site, token) = _connection.RequireConnection();
        var log = new PendingLog(timer, minutes, description, isBillable, tags);

        long? timeLogId;

        try
        {
            timeLogId = await _client.LogTimerAsync(site, token, timer, minutes * 60, description, isBillable, _cancellation);
        }
        catch (TeamworkException ex)
        {
            KeepPendingLog(log, ex);
            throw;
        }

        ForgetTimer(timer);

        if (tags.Count > 0)
            await TagTimeLogAsync(log, timeLogId);
    }

    public async Task DiscardTimerAsync(TrackedTimer timer)
    {
        var (site, token) = _connection.RequireConnection();

        await _client.DeleteTimerAsync(site, token, timer.Id, _cancellation);

        ForgetTimer(timer);
    }

    public async Task RetryPendingLogAsync()
    {
        if (_pendingLog.Value is not { } log)
            return;

        await LogTimeAsync(log.Timer, log.Minutes, log.Description, log.IsBillable, log.Tags);

        _context.Notify(_context.Info.Name, $"Logged {Duration.FormatMinutes(log.Minutes)} on {log.Timer.Task.Label}.", NotificationKind.Info);
    }

    public async Task RefreshTimersAsync()
    {
        var (site, token) = _connection.RequireConnection();

        ApplyTimers(await _client.GetTimersAsync(site, token, _cancellation));
    }

    public async Task RefreshTimersQuietlyAsync()
    {
        if (!_connection.TryGetConnection(out _, out _))
            return;

        try
        {
            await RefreshTimersAsync();
        }
        catch (Exception ex)
        {
            _context.Log.Info($"Couldn't read the timers from Teamwork: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _clockTicker?.Dispose();
        _poller?.Dispose();
    }

    // The time is logged by now, so a tag Teamwork doesn't take is only told about: logging it again would log it twice.
    private async Task TagTimeLogAsync(PendingLog log, long? timeLogId)
    {
        var tags = string.Join(", ", log.Tags.Select(tag => tag.Name));
        var what = $"{Duration.FormatMinutes(log.Minutes)} on {log.Timer.Task.Label}";

        if (timeLogId is not { } id)
        {
            _context.Notify(_context.Info.Name, $"Logged {what}, but Teamwork didn't say which time log it is, so it doesn't have the tags {tags}. Add them in Teamwork.", NotificationKind.Warning);
            return;
        }

        var (site, token) = _connection.RequireConnection();

        try
        {
            await _client.TagTimeLogAsync(site, token, id, log.Tags, _cancellation);
        }
        catch (TeamworkException ex)
        {
            _context.Log.Info($"Couldn't tag time log {id}: {ex.Message}");
            _context.Notify(_context.Info.Name, $"Logged {what}, but not its tags {tags}: {ex.Message} Add them in Teamwork.", NotificationKind.Warning);
        }
    }

    private void UpdateClock()
        => _clock.Value = _timer.Value is { } timer ? Duration.FormatClock(timer.ElapsedAt(DateTimeOffset.UtcNow)) : null;

    // Every few minutes, and every minute while a log waits: tries that again, and reads what runs in Teamwork.
    private async void PollTeamwork()
    {
        if (!_connection.TryGetConnection(out _, out _))
            return;

        try
        {
            // A token Teamwork didn't take stays wrong until it's changed; a site that couldn't be reached may be back.
            if (_pendingLog.Value is not null && _isPendingLogRetryable)
                await RetryPendingLogAsync();

            await RefreshTimersAsync();
        }
        catch (Exception ex) when (ex is TeamworkException or OperationCanceledException)
        {
            _context.Log.Info($"Couldn't read the timers from Teamwork: {ex.Message}");
        }
        catch (Exception ex)
        {
            _context.Log.Warning("Reading the timers from Teamwork failed.", ex);
        }
    }

    // What Teamwork says runs: the running timer, or else one this plugin paused, as long as Teamwork still has it.
    private void ApplyTimers(IReadOnlyList<TrackedTimer> timers)
    {
        var known = new[] { _timer.Value, _pendingLog.Value?.Timer }.OfType<TrackedTimer>().Select(timer => timer.Id).Concat(_pausedHere).ToHashSet();

        _timer.Value = timers.FirstOrDefault(timer => timer.IsRunning)
            ?? timers.FirstOrDefault(timer => !timer.IsRunning && known.Contains(timer.Id));

        // Logged or deleted in Teamwork meanwhile.
        _pausedHere.IntersectWith(timers.Select(timer => timer.Id));

        foreach (var gone in _tags.Keys.Where(id => timers.All(timer => timer.Id != id)).ToArray())
            _tags.Remove(gone);

        if (_pendingLog.Value is { } pending && timers.All(timer => timer.Id != pending.Timer.Id))
            SetPendingLog(null);

        TimersRead?.Invoke();
    }

    // The timer is logged or thrown away: it's gone from Teamwork, and from here.
    private void ForgetTimer(TrackedTimer timer)
    {
        if (_timer.Value?.Id == timer.Id)
            _timer.Value = null;

        _pausedHere.Remove(timer.Id);
        _tags.Remove(timer.Id);

        if (_pendingLog.Value?.Timer.Id == timer.Id)
            SetPendingLog(null);
    }

    private void KeepPendingLog(PendingLog log, TeamworkException error)
    {
        _isPendingLogRetryable = error.IsRetryable;
        SetPendingLog(log);

        _context.ReportProblem(error.IsRetryable
            ? $"{Duration.FormatMinutes(log.Minutes)} on {log.Timer.Task.Label} isn't logged yet: {error.Message} Sill tries again every minute; the timer waits, paused, in Teamwork."
            : $"{Duration.FormatMinutes(log.Minutes)} on {log.Timer.Task.Label} isn't logged: {error.Message} The timer waits, paused, in Teamwork; log it again from Track time.");
    }

    // Tried again every minute while there's one, every few minutes otherwise.
    private void SetPendingLog(PendingLog? log)
    {
        _pendingLog.Value = log;

        if (log is null)
            _context.ReportProblem(null);

        var interval = log is null ? _pollInterval : _retryInterval;
        _poller?.Change(interval, interval);
    }
}
