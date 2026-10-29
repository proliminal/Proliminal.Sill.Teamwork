using System.Globalization;
using Proliminal.Sill.Plugins.TeamworkTimer.Settings;
using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using Proliminal.Sill.Sdk;
using Proliminal.Sill.Sdk.Abstractions;
using Proliminal.Sill.Sdk.Pages;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Popup;

// Adds or changes a profile, from the settings page.
internal sealed class ProfilePopup
{
    private readonly IPluginSettings<TimerSettings> _settings;
    private readonly TeamworkCatalog _catalog;
    private readonly TeamworkClient _client;
    private readonly TeamworkConnection _connection;
    private readonly CancellationToken _cancellation;

    private readonly State<string> _name = Value.State("");
    private readonly State<string> _taskNumber = Value.State("");
    private readonly State<TeamworkProject?> _project = Value.State<TeamworkProject?>(null);
    private readonly State<string> _description = Value.State("");
    private readonly State<bool> _billable = Value.State(true);
    private readonly State<IReadOnlyList<TeamworkTag>> _tags = Value.State<IReadOnlyList<TeamworkTag>>([]);

    private readonly IReadOnlyValue<bool> _isWithoutTask;

    // Every tag while a task number is typed, as its project isn't known until it's saved; the project's otherwise.
    private readonly IReadOnlyValue<IReadOnlyList<TeamworkTag>> _tagOptions;

    // The profile being changed, or null for a new one.
    private string? _editing;

    public ProfilePopup(IPluginSettings<TimerSettings> settings, TeamworkCatalog catalog, TeamworkClient client, TeamworkConnection connection, CancellationToken cancellation)
    {
        _settings = settings;
        _catalog = catalog;
        _client = client;
        _connection = connection;
        _cancellation = cancellation;

        _isWithoutTask = _taskNumber.Map(number => number.Trim().Length == 0);

        var isOption = _isWithoutTask.Combine(_project, (isWithoutTask, project) => isWithoutTask
            ? (Func<TeamworkTag, bool>)(tag => tag.IsFor(project?.Id))
            : _ => true);

        _tagOptions = catalog.Tags.Combine(isOption, (tags, isFor) => (IReadOnlyList<TeamworkTag>)
            [.. tags.Where(isFor).OrderBy(tag => tag.ProjectId == 0 ? 0 : 1)]);
    }

    public void ShowNew(IActionContext action)
        => Show(action, new TimerProfile());

    public void ShowEdit(TimerProfile profile, IActionContext action)
        => Show(action, profile);

    private void Show(IActionContext action, TimerProfile profile)
    {
        _editing = _settings.Current.Profiles.Any(saved => saved.Id == profile.Id) ? profile.Id : null;
        _name.Value = profile.Name;
        _taskNumber.Value = profile.TaskId?.ToString(CultureInfo.InvariantCulture) ?? "";
        _project.Value = profile is { TaskId: null, ProjectId: { } projectId } ? _catalog.FindProject(projectId, profile.Project) : null;
        _description.Value = profile.Description;
        _billable.Value = profile.IsBillable;
        _tags.Value = [.. profile.Tags.Select(tag => _catalog.FindTag(tag.ToTag()))];

        action.ShowPopup(_editing is null ? "New profile" : "Change profile", DescribePopup);

        _catalog.ReadQuietly();
    }

    private void DescribePopup(PopupBuilder popup)
        => popup
            .TextBox("Name", _name, text => text
                .Description("The button in Track time.")
                .Placeholder("Tickets")
                .MaxLength(30))
            .TextBox("Task number", _taskNumber, text => text
                .Placeholder("12345, or none for a project")
                .MaxLength(12)
                .ChangesWhileTyping())
            .Picker("Project", _project, picker => picker
                .Description("For time that isn't on a task. Leave both empty to type the task each time.")
                .Options(_catalog.Projects, project => project?.Name ?? "")
                .VisibleWhen(_isWithoutTask))
            .TextBox("Description", _description, text => text
                .Placeholder("Working on tickets")
                .MaxLength(500))
            .Tags("Tags", _tags, tags => tags
                .Icon(Glyph.Tag)
                .Options(_tagOptions, tag => tag.Name)
                .Color(tag => tag.Color)
                .Group(tag => tag.ProjectId == 0 ? "All projects" : _catalog.FindProject(tag.ProjectId, "Another project").Name)
                .Placeholder("Search tags"))
            .Toggle("Billable", _billable)
            .Command("Save", SaveAsync, command => command.IsDefault());

    private async Task SaveAsync(IActionContext action)
    {
        var name = _name.Value.Trim();

        if (name.Length == 0)
            throw new TeamworkException("Give the profile a name, for its button.");

        var profile = new TimerProfile
        {
            Id = _editing ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Description = _description.Value.Trim(),
            IsBillable = _billable.Value,
        };

        if (_taskNumber.Value.Trim().Length > 0)
        {
            if (!long.TryParse(_taskNumber.Value.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new TeamworkException("Type the task's number, such as 12345.");

            // Looked up to be sure it's there, and for its project's tags.
            var (site, token) = _connection.RequireConnection();
            var task = await _client.GetTaskAsync(site, token, id, _cancellation);

            profile.TaskId = id;
            profile.ProjectId = task.ProjectId;
            profile.Project = task.Project;
        }
        else if (_project.Value is { } project)
        {
            profile.ProjectId = project.Id;
            profile.Project = project.Name;
        }

        // Those of another project than the task's are left out, as Track time would leave them out too.
        profile.Tags = [.. _tags.Value.Where(tag => tag.IsFor(profile.ProjectId)).Select(TimerProfileTag.From)];

        _settings.Update(settings =>
        {
            var index = settings.Profiles.FindIndex(saved => saved.Id == profile.Id);

            if (index < 0)
                settings.Profiles.Add(profile);
            else
                settings.Profiles[index] = profile;
        });

        action.ClosePopup();
    }
}
