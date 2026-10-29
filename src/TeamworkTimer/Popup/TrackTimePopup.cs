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

    // How long the tags and projects read from Teamwork are kept before the popup reads them again.
    private static readonly TimeSpan _tagsAge = TimeSpan.FromMinutes(10);

    private readonly TimeTracker _tracker;
    private readonly TeamworkClient _client;
    private readonly TeamworkConnection _connection;
    private readonly CancellationToken _cancellation;

    // What the popup asks, and what's typed in it.
    private readonly State<TrackTimeStep> _step = Value.State(TrackTimeStep.NewTask);
    private readonly State<string> _taskNumber = Value.State("");
    private readonly State<string> _description = Value.State("");
    private readonly State<bool> _billable = Value.State(true);
    private readonly State<string> _duration = Value.State("");
    private readonly State<IReadOnlyList<TeamworkTag>> _tags = Value.State<IReadOnlyList<TeamworkTag>>([]);

    // The project for time that isn't on a task, picked when no task number is typed.
    private readonly State<TeamworkProject?> _project = Value.State<TeamworkProject?>(null);
    private readonly State<IReadOnlyList<TeamworkProject?>> _projects = Value.State<IReadOnlyList<TeamworkProject?>>([]);
    private readonly IReadOnlyValue<bool> _isWithoutTask;
    private DateTimeOffset? _projectsReadAt;

    // The tags to pick from: those for every project, then those of the task's project once it's known.
    private readonly State<IReadOnlyList<TeamworkTag>> _allTags = Value.State<IReadOnlyList<TeamworkTag>>([]);
    private readonly State<TeamworkTask?> _tagsTask = Value.State<TeamworkTask?>(null);
    private readonly IReadOnlyValue<IReadOnlyList<TeamworkTag>> _tagOptions;
    private DateTimeOffset? _tagsReadAt;

    // A word or two beside the task number, and the task's name or what's wrong under its title, where there's room.
    private readonly State<string?> _taskStatus = Value.State<string?>(null);
    private readonly State<Tone> _taskTone = Value.State(Tone.Neutral);
    private readonly State<string> _taskFound = Value.State("");

    private readonly State<string?> _message = Value.State<string?>(null);
    private readonly State<Tone> _messageTone = Value.State(Tone.Neutral);

    // The paused timer whose time is being logged.
    private readonly State<TrackedTimer?> _stopping = Value.State<TrackedTimer?>(null);

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

        _tagOptions = _allTags.Combine(_tagsTask, (tags, task) => (IReadOnlyList<TeamworkTag>)
            [.. tags.Where(tag => tag.IsFor(task?.ProjectId)).OrderBy(tag => tag.ProjectId == 0 ? 0 : 1)]);

        _isWithoutTask = _step.Combine(_taskNumber, (step, number) => step == TrackTimeStep.NewTask && number.Trim().Length == 0);

        _taskNumber.Changed += () => _ = LookUpTaskAsync();
        _project.Changed += FollowProject;
        _tracker.TimersRead += FollowTeamwork;
    }

    public void Show(IActionContext action)
    {
        _taskNumber.Value = "";
        _project.Value = null;
        _description.Value = "";
        _billable.Value = true;
        _tags.Value = [];
        _tagsTask.Value = null;
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

        _ = ReadTagsQuietlyAsync();
        _ = ReadProjectsQuietlyAsync();
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
        _project.Changed -= FollowProject;
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
                .Placeholder("12345, or none for a project")
                .MaxLength(12)
                .ChangesWhileTyping()
                .Status(_taskStatus, _taskTone)
                .VisibleWhen(IsStep(TrackTimeStep.NewTask)))
            .Picker("Project", _project, picker => picker
                .Description("For time that isn't on a task.")
                .Options(_projects, project => project?.Name ?? "")
                .VisibleWhen(_isWithoutTask))
            .Info("Stopped", info => info
                .Icon(Glyph.Clock)
                .Description(_stopping.Map(timer => timer?.Task.Label ?? ""))
                .Status(_stopping.Map<TrackedTimer?, string?>(timer => timer is null ? null : $"Ran {Duration.FormatClock(timer.Elapsed)}"))
                .VisibleWhen(IsStep(TrackTimeStep.LogTime)))
            .TextBox("Description", _description, text => text
                .Placeholder("What you're working on")
                .MaxLength(500)
                .VisibleWhen(IsStep(TrackTimeStep.NewTask, TrackTimeStep.LogTime)))
            .Tags("Tags", _tags, tags => tags
                .Icon(Glyph.Tag)
                .Options(_tagOptions, tag => tag.Name)
                .Color(tag => tag.Color)
                .Group(tag => tag.ProjectId == 0 ? "All projects" : _tagsTask.Value?.Project ?? "This project")
                .Placeholder("Search tags")
                .VisibleWhen(IsStep(TrackTimeStep.NewTask, TrackTimeStep.LogTime)))
            // Side by side, as neither needs the width; for a new task there's no time yet, and Billable takes the row.
            .Row(row => row
                .TextBox("Time to log", _duration, text => text
                    .Placeholder("1:30")
                    .MaxLength(8)
                    .ChangesWhileTyping()
                    .Status(_duration.Map<string, string?>(value => Duration.TryParseMinutes(value, out _) ? null : "Not a time"), Value.State(Tone.Critical))
                    .VisibleWhen(IsStep(TrackTimeStep.LogTime)))
                .Toggle("Billable", _billable, toggle => toggle
                    .VisibleWhen(IsStep(TrackTimeStep.NewTask, TrackTimeStep.LogTime))))
            .Message(_message, _messageTone)
            .Command("Back", _ => ReturnToRunningTimer(), command => command.Key('B').VisibleWhen(IsStep(TrackTimeStep.ConfirmSwitch)))
            .Command("Stop it first", PauseForLoggingAsync, command => command.IsDefault().VisibleWhen(IsStep(TrackTimeStep.ConfirmSwitch)))
            .Command("New task", _ => ShowNewTask(), command => command.Key('N').VisibleWhen(IsStep(TrackTimeStep.Running)))
            .Command("Stop", PauseForLoggingAsync, command => command.IsDefault().VisibleWhen(IsStep(TrackTimeStep.Running)))
            .Command("Start", StartTimerAsync, command => command.IsDefault().VisibleWhen(IsStep(TrackTimeStep.NewTask)))
            .Command("Discard", DiscardTimeAsync, command => command.Key('D').VisibleWhen(IsStep(TrackTimeStep.LogTime)))
            .Command("Resume", ResumeTimerAsync, command => command.Key('R').VisibleWhen(IsStep(TrackTimeStep.LogTime)))
            .Command("Log time", LogTimeAsync, command => command.IsDefault().VisibleWhen(IsStep(TrackTimeStep.LogTime)));

    private IReadOnlyValue<bool> IsStep(params TrackTimeStep[] steps)
        => _step.Map(steps.Contains);

    private async Task StartTimerAsync(IActionContext action)
    {
        var (site, token) = _connection.RequireConnection();

        var task = ParseTaskNumber() is { } id
            ? _lookedUpTask?.Id == id ? _lookedUpTask : await _client.GetTaskAsync(site, token, id, _cancellation)
            : _taskNumber.Value.Trim().Length > 0
                ? throw new TeamworkException("Type the task's number, such as 12345.")
                : _project.Value is { } project
                    ? TeamworkTask.ForProject(project.Id, project.Name)
                    : throw new TeamworkException("Type a task number, or pick the project for time that isn't on a task.");

        // One may have been started in Teamwork since.
        await _tracker.RefreshTimersAsync();

        var queued = new QueuedTimer(task, _description.Value.Trim(), _billable.Value, PickedTags());

        if (_tracker.Timer.Value is { IsRunning: true } running)
        {
            if (running.Task.IsSameAs(task))
            {
                ShowNextStep();
                ShowMessage($"{task.Reference} is running already.");
                return;
            }

            _queued = queued;
            _step.Value = TrackTimeStep.ConfirmSwitch;
            ShowMessage($"{running.Task.Label} is running. Stop it and log its time before starting {task.Reference}?");
            return;
        }

        if (!await StartOrPickUpAsync(queued))
            return;

        ShowNextStep();
        action.ClosePopup();
    }

    // Teamwork starts no second timer for a task that has a paused one, so that one is shown instead, to resume, log or
    // discard. False when it is, and the popup stays open for it.
    private async Task<bool> StartOrPickUpAsync(QueuedTimer queued)
    {
        if (_tracker.FindPausedTimer(queued.Task) is not { } paused)
        {
            await _tracker.StartTimerAsync(queued.Task, queued.Description, queued.IsBillable, queued.Tags);
            return true;
        }

        _tracker.PickUp(paused);
        ShowLogStep(paused);

        // What was typed for it, where Teamwork has nothing.
        if (_description.Value.Length == 0)
            _description.Value = queued.Description;

        if (_tags.Value.Count == 0)
            _tags.Value = queued.Tags;

        ShowMessage($"{queued.Task.Reference} has a paused timer in Teamwork already, at {Duration.FormatClock(paused.Elapsed)}. Resume it, log its time, or discard it.",
            Tone.Caution);

        return false;
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

        await _tracker.LogTimeAsync(stopping, minutes, _description.Value.Trim(), _billable.Value, PickedTags());

        _stopping.Value = null;

        if (!await StartQueuedTimerAsync())
            return;

        ShowNextStep();
        action.ClosePopup();
    }

    private async Task DiscardTimeAsync(IActionContext action)
    {
        var stopping = _stopping.Value ?? throw new InvalidOperationException("There's no timer to discard.");

        if (!_isDiscardArmed)
        {
            _isDiscardArmed = true;
            ShowMessage($"Press Discard again to throw away the {Duration.FormatClock(stopping.Elapsed)} on {stopping.Task.Reference} without logging it.", Tone.Caution);
            return;
        }

        await _tracker.DiscardTimerAsync(stopping);

        _stopping.Value = null;

        if (!await StartQueuedTimerAsync())
            return;

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

    // Starts what was asked for while another ran. If that fails, the popup goes back to it, as typed. False when it
    // shows a paused timer of it instead.
    private async Task<bool> StartQueuedTimerAsync()
    {
        if (_queued is not { } queued)
            return true;

        _queued = null;

        try
        {
            return await StartOrPickUpAsync(queued);
        }
        catch (Exception)
        {
            _project.Value = queued.Task.Id is null ? _projects.Value.FirstOrDefault(project => project?.Id == queued.Task.ProjectId) : null;
            _taskNumber.Value = queued.Task.Id?.ToString(CultureInfo.InvariantCulture) ?? "";
            _description.Value = queued.Description;
            _billable.Value = queued.IsBillable;
            _tags.Value = queued.Tags;
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
        _tagsTask.Value = paused.Task;
        _tags.Value = _tracker.TagsOf(paused);
        _isDiscardArmed = false;
        _message.Value = _queued is { } queued ? $"Then {queued.Task.Label} starts." : null;
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
        _project.Value = null;
        _description.Value = "";
        _billable.Value = true;
        _tags.Value = [];
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
        else if (_step.Value == TrackTimeStep.NewTask && _taskNumber.Value.Length == 0 && _project.Value is null && isRunning)
            _step.Value = TrackTimeStep.Running;
    }

    private async Task LookUpTaskAsync()
    {
        _lookup?.Cancel();
        _lookedUpTask = null;
        _tagsTask.Value = null;

        if (_taskNumber.Value.Trim().Length == 0)
        {
            SetTaskStatus(null, Tone.Neutral, "");
            FollowProject();
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

            _lookedUpTask = _tagsTask.Value = task;
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

    // Without a task number, the tags to pick from are those of the project picked.
    private void FollowProject()
    {
        if (_step.Value == TrackTimeStep.NewTask && _taskNumber.Value.Trim().Length == 0)
            _tagsTask.Value = _project.Value is { } project ? TeamworkTask.ForProject(project.Id, project.Name) : null;
    }

    // The tags picked that are still options, as the task may have changed to one of another project since.
    private IReadOnlyList<TeamworkTag> PickedTags()
        => [.. _tags.Value.Where(_tagOptions.Value.Contains)];

    // Once in a while, as tags are seldom added; without them, only the tags picked before can be picked.
    private async Task ReadTagsQuietlyAsync()
    {
        if (_tagsReadAt is { } readAt && DateTimeOffset.UtcNow - readAt < _tagsAge || !_connection.TryGetConnection(out var site, out var token))
            return;

        _tagsReadAt = DateTimeOffset.UtcNow;

        try
        {
            _allTags.Value = await _client.GetTagsAsync(site, token, _cancellation);
        }
        catch (Exception)
        {
            _tagsReadAt = null;
        }
    }

    // As seldom as the tags; without them, only a task number can be typed.
    private async Task ReadProjectsQuietlyAsync()
    {
        if (_projectsReadAt is { } readAt && DateTimeOffset.UtcNow - readAt < _tagsAge || !_connection.TryGetConnection(out var site, out var token))
            return;

        _projectsReadAt = DateTimeOffset.UtcNow;

        try
        {
            _projects.Value = await _client.GetProjectsAsync(site, token, _cancellation);
        }
        catch (Exception)
        {
            _projectsReadAt = null;
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
