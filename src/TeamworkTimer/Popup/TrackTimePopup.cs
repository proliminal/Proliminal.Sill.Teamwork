using System.Globalization;
using Proliminal.Sill.Plugins.TeamworkTimer.Settings;
using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using Proliminal.Sill.Plugins.TeamworkTimer.Tracking;
using Proliminal.Sill.Sdk;
using Proliminal.Sill.Sdk.Abstractions;
using Proliminal.Sill.Sdk.Pages;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Popup;

internal sealed class TrackTimePopup : IDisposable
{
    private const string TITLE = "Track time";

    // How long typing pauses before the task is looked up.
    private static readonly TimeSpan _lookupDelay = TimeSpan.FromMilliseconds(400);

    private readonly TimeTracker _tracker;
    private readonly TeamworkClient _client;
    private readonly TeamworkConnection _connection;
    private readonly CancellationToken _cancellation;

    // What the popup asks, and what's typed in it.
    private readonly State<TrackTimeStep> _step = Values.State(TrackTimeStep.NewTask);
    private readonly State<string> _taskNumber = Values.State("");
    private readonly State<string> _description = Values.State("");
    private readonly State<bool> _billable = Values.State(true);
    private readonly State<string> _duration = Values.State("");

    // A word or two beside the task number, and the task's name or what's wrong under its title, where there's room.
    private readonly State<string?> _taskStatus = Values.State<string?>(null);
    private readonly State<Tone> _taskTone = Values.State(Tone.Neutral);
    private readonly State<string> _taskFound = Values.State("");

    private readonly State<string?> _message = Values.State<string?>(null);
    private readonly State<Tone> _messageTone = Values.State(Tone.Neutral);

    // The paused timer whose time is being logged.
    private readonly State<TrackedTimer?> _stopping = Values.State<TrackedTimer?>(null);

    // The task to start once the running timer is logged, as asked when starting another.
    private QueuedTimer? _queued;

    // The task the number typed is, once it was looked up.
    private TeamworkTask? _lookedUpTask;
    private CancellationTokenSource? _lookup;

    // Discard asks once more before it throws the time away.
    private bool _isDiscardArmed;

    public TrackTimePopup(TimeTracker tracker, TeamworkClient client, TeamworkConnection connection, CancellationToken cancellation)
    {
        _tracker = tracker;
        _client = client;
        _connection = connection;
        _cancellation = cancellation;

        _taskNumber.Changed += () => _ = LookUpTaskAsync();
        _tracker.TimersRead += FollowTeamwork;
    }

    public void Show(IActionContext action)
    {
        _taskNumber.Value = "";
        _description.Value = "";
        _billable.Value = true;
        _message.Value = null;
        _queued = null;
        _isDiscardArmed = false;

        if (_connection.DescribeMissingSetup() is { } missing)
        {
            _step.Value = TrackTimeStep.MissingSetup;
            ShowMessage(missing, Tone.Caution);
        }
        else if (_tracker.Timer.Value is { IsRunning: false } paused)
            ShowLogStep(paused);
        else
            _step.Value = _tracker.Timer.Value is { IsRunning: true } ? TrackTimeStep.Running : TrackTimeStep.NewTask;

        // Described anew each time it opens; the values it shows are this class's, so they're set up first.
        action.ShowPopup(TITLE, DescribePopup);

        // What runs may have changed in Teamwork since it was last asked.
        if (_step.Value is TrackTimeStep.NewTask)
            _ = _tracker.RefreshTimersQuietlyAsync();
    }

    public async Task StopFromMenuAsync(IActionContext action)
    {
        if (_tracker.Timer.Value is { IsRunning: true } && _connection.TryGetConnection(out _, out _))
            await _tracker.PauseRunningTimerAsync();

        Show(action);
    }

    public void Dispose()
    {
        _lookup?.Cancel();
        _tracker.TimersRead -= FollowTeamwork;
    }

    private void DescribePopup(PopupBuilder popup)
        => popup
            .Info("Running", info => info
                .Icon(Glyph.Clock)
                .Description(_tracker.Timer.Map(timer => timer?.Task.Label ?? ""))
                .Status(_tracker.Clock)
                .VisibleWhen(IsStep(TrackTimeStep.Running, TrackTimeStep.ConfirmSwitch)))
            .TextBox("Task number", _taskNumber, text => text
                .Description(_taskFound)
                .Placeholder("12345")
                .MaxLength(12)
                .WhileTyping()
                .Status(_taskStatus, _taskTone)
                .VisibleWhen(IsStep(TrackTimeStep.NewTask)))
            .Info("Stopped", info => info
                .Icon(Glyph.Clock)
                .Description(_stopping.Map(timer => timer?.Task.Label ?? ""))
                .Status(_stopping.Map<TrackedTimer?, string?>(timer => timer is null ? null : $"Ran {Duration.FormatClock(timer.Elapsed)}"))
                .VisibleWhen(IsStep(TrackTimeStep.LogTime)))
            .TextBox("Description", _description, text => text
                .Placeholder("What you're working on")
                .MaxLength(500)
                .VisibleWhen(IsStep(TrackTimeStep.NewTask, TrackTimeStep.LogTime)))
            // Side by side, as neither needs the width; for a new task there's no time yet, and Billable takes the row.
            .Row(row => row
                .TextBox("Time to log", _duration, text => text
                    .Placeholder("1:30")
                    .MaxLength(8)
                    .WhileTyping()
                    .Status(_duration.Map<string, string?>(value => Duration.TryParseMinutes(value, out _) ? null : "Not a time"), Values.State(Tone.Critical))
                    .VisibleWhen(IsStep(TrackTimeStep.LogTime)))
                .Toggle("Billable", _billable, toggle => toggle
                    .VisibleWhen(IsStep(TrackTimeStep.NewTask, TrackTimeStep.LogTime))))
            .Message(_message, _messageTone)
            .Command("Back", _ => ReturnToRunningTimer(), command => command.Key('B').VisibleWhen(IsStep(TrackTimeStep.ConfirmSwitch)))
            .Command("Stop it first", PauseForLoggingAsync, command => command.Default().VisibleWhen(IsStep(TrackTimeStep.ConfirmSwitch)))
            .Command("New task", _ => ShowNewTask(), command => command.Key('N').VisibleWhen(IsStep(TrackTimeStep.Running)))
            .Command("Stop", PauseForLoggingAsync, command => command.Default().VisibleWhen(IsStep(TrackTimeStep.Running)))
            .Command("Start", StartTimerAsync, command => command.Default().VisibleWhen(IsStep(TrackTimeStep.NewTask)))
            .Command("Discard", DiscardTimeAsync, command => command.Key('D').VisibleWhen(IsStep(TrackTimeStep.LogTime)))
            .Command("Resume", ResumeTimerAsync, command => command.Key('R').VisibleWhen(IsStep(TrackTimeStep.LogTime)))
            .Command("Log time", LogTimeAsync, command => command.Default().VisibleWhen(IsStep(TrackTimeStep.LogTime)));

    private IReadOnlyValue<bool> IsStep(params TrackTimeStep[] steps)
        => _step.Map(steps.Contains);

    private async Task StartTimerAsync(IActionContext action)
    {
        var (site, token) = _connection.RequireConnection();
        var id = ParseTaskNumber() ?? throw new TeamworkException("Type the task's number, such as 12345.");

        var task = _lookedUpTask?.Id == id ? _lookedUpTask : await _client.GetTaskAsync(site, token, id, _cancellation);

        // One may have been started in Teamwork since.
        await _tracker.RefreshTimersAsync();

        var queued = new QueuedTimer(task, _description.Value.Trim(), _billable.Value);

        if (_tracker.Timer.Value is { IsRunning: true } running)
        {
            _queued = queued;
            _step.Value = TrackTimeStep.ConfirmSwitch;
            ShowMessage($"{running.Task.Label} is running. Stop it and log its time before starting #{task.Id}?");
            return;
        }

        await _tracker.StartTimerAsync(queued.Task, queued.Description, queued.IsBillable);

        ShowNextStep();
        action.ClosePopup();
    }

    private async Task PauseForLoggingAsync(IActionContext action)
    {
        if (_tracker.Timer.Value is not { IsRunning: true })
            _step.Value = TrackTimeStep.NewTask;

        var paused = await _tracker.PauseRunningTimerAsync();

        // What was typed for the queued task is kept in it, as the boxes show the stopped timer's now.
        ShowLogStep(paused);
    }

    private async Task LogTimeAsync(IActionContext action)
    {
        var stopping = _stopping.Value ?? throw new InvalidOperationException("There's no timer to log.");

        if (!Duration.TryParseMinutes(_duration.Value, out var minutes))
            throw new TeamworkException("Type the time as hours and minutes, such as 1:30.");

        if (minutes < 1)
            throw new TeamworkException("Log at least a minute, or discard the time with Ctrl+D.");

        await _tracker.LogTimeAsync(stopping, minutes, _description.Value.Trim(), _billable.Value);

        _stopping.Value = null;

        await StartQueuedTimerAsync();

        ShowNextStep();
        action.ClosePopup();
    }

    private async Task DiscardTimeAsync(IActionContext action)
    {
        var stopping = _stopping.Value ?? throw new InvalidOperationException("There's no timer to discard.");

        if (!_isDiscardArmed)
        {
            _isDiscardArmed = true;
            ShowMessage($"Press Discard again to throw away the {Duration.FormatClock(stopping.Elapsed)} on #{stopping.Task.Id} without logging it.", Tone.Caution);
            return;
        }

        await _tracker.DiscardTimerAsync(stopping);

        _stopping.Value = null;

        await StartQueuedTimerAsync();

        ShowNextStep();
        action.ClosePopup();
    }

    private async Task ResumeTimerAsync(IActionContext action)
    {
        var stopping = _stopping.Value ?? throw new InvalidOperationException("There's no timer to resume.");

        await _tracker.ResumeTimerAsync(stopping);

        _stopping.Value = null;
        _queued = null;
        _message.Value = null;
        _step.Value = TrackTimeStep.Running;
    }

    // Starts what was asked for while another ran. If that fails, the popup goes back to it, as typed.
    private async Task StartQueuedTimerAsync()
    {
        if (_queued is not { } queued)
            return;

        _queued = null;

        try
        {
            await _tracker.StartTimerAsync(queued.Task, queued.Description, queued.IsBillable);
        }
        catch (Exception)
        {
            _taskNumber.Value = queued.Task.Id.ToString(CultureInfo.InvariantCulture);
            _description.Value = queued.Description;
            _billable.Value = queued.IsBillable;
            _step.Value = TrackTimeStep.NewTask;
            throw;
        }
    }

    private void ShowLogStep(TrackedTimer paused)
    {
        _stopping.Value = paused;
        _duration.Value = Duration.FormatMinutes(Duration.RoundToMinutes(paused.Elapsed, _connection.Rounding));
        _description.Value = paused.Description;
        _billable.Value = paused.IsBillable;
        _isDiscardArmed = false;
        _message.Value = _queued is { } queued ? $"Then #{queued.Task.Id} {queued.Task.Name} starts." : null;
        _messageTone.Value = Tone.Neutral;
        _step.Value = TrackTimeStep.LogTime;
    }

    private void ReturnToRunningTimer()
    {
        _queued = null;
        _message.Value = null;
        _step.Value = _tracker.Timer.Value is { IsRunning: true } ? TrackTimeStep.Running : TrackTimeStep.NewTask;
    }

    private void ShowNewTask()
    {
        _taskNumber.Value = "";
        _description.Value = "";
        _billable.Value = true;
        _message.Value = null;
        _step.Value = TrackTimeStep.NewTask;
    }

    // What the popup shows once a command is done, which a pinned popup goes on showing: what runs, or a new task.
    private void ShowNextStep()
    {
        _queued = null;
        _isDiscardArmed = false;

        if (_tracker.Timer.Value is { IsRunning: true })
        {
            _message.Value = null;
            _step.Value = TrackTimeStep.Running;
        }
        else
            ShowNewTask();
    }

    // A pinned popup follows what changed in Teamwork: a timer that stopped there leaves a new task to start, and one
    // started there while nothing's typed yet shows instead.
    private void FollowTeamwork()
    {
        var isRunning = _tracker.Timer.Value is { IsRunning: true };

        if (_step.Value is TrackTimeStep.Running or TrackTimeStep.ConfirmSwitch && !isRunning)
            ShowNextStep();
        else if (_step.Value == TrackTimeStep.NewTask && _taskNumber.Value.Length == 0 && isRunning)
            _step.Value = TrackTimeStep.Running;
    }

    private async Task LookUpTaskAsync()
    {
        _lookup?.Cancel();
        _lookedUpTask = null;

        if (_taskNumber.Value.Trim().Length == 0)
        {
            SetTaskStatus(null, Tone.Neutral, "");
            return;
        }

        if (ParseTaskNumber() is not { } id)
        {
            SetTaskStatus("Not a number", Tone.Critical, "Only the task's number, such as 12345.");
            return;
        }

        if (!_connection.TryGetConnection(out var site, out var token))
            return;

        var lookup = _lookup = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);

        SetTaskStatus("Looking…", Tone.Neutral, "");

        try
        {
            await Task.Delay(_lookupDelay, lookup.Token);

            var task = await _client.GetTaskAsync(site, token, id, lookup.Token);

            if (lookup.IsCancellationRequested)
                return;

            _lookedUpTask = task;
            SetTaskStatus("Found", Tone.Good, task.NameWithProject);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!lookup.IsCancellationRequested)
                SetTaskStatus("Not found", Tone.Critical, ex is TeamworkException ? ex.Message : "It couldn't be looked up.");
        }
    }

    private void SetTaskStatus(string? status, Tone tone, string found)
    {
        _taskTone.Value = tone;
        _taskStatus.Value = status;
        _taskFound.Value = found;
    }

    private void ShowMessage(string message, Tone tone = Tone.Neutral)
    {
        _messageTone.Value = tone;
        _message.Value = message;
    }

    // A task's number, with or without a # in front of it.
    private long? ParseTaskNumber()
        => long.TryParse(_taskNumber.Value.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
}
