using System.Globalization;
using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Settings;

// What Track time fills in when it's picked: a task or a project, a description, tags and whether it's billable. Either
// may be left out, for a profile that only sets the description and tags.
public sealed class TimerProfile
{
    // Never changes, as the settings are read anew on every change and a profile is found again by it.
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public long? TaskId { get; set; }

    // The project for time that isn't on a task, or the task's, to pick its tags.
    public long? ProjectId { get; set; }

    public string? Project { get; set; }

    public string Description { get; set; } = "";

    public bool IsBillable { get; set; } = true;

    // As they were when picked, so they can be picked before Teamwork's tags are read.
    public List<TimerProfileTag> Tags { get; set; } = [];

    internal string Summary
        => string.Join(" · ", new[]
        {
            TaskId is { } id ? $"#{id.ToString(CultureInfo.InvariantCulture)}" : null,
            Project,
            Description.Length > 0 ? Description : null,
            Tags.Count > 0 ? string.Join(", ", Tags.Select(tag => tag.Name)) : null,
            IsBillable ? null : "Not billable",
        }.OfType<string>());
}

public sealed class TimerProfileTag
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Color { get; set; }

    public long ProjectId { get; set; }

    internal static TimerProfileTag From(TeamworkTag tag)
        => new() { Id = tag.Id, Name = tag.Name, Color = tag.Color, ProjectId = tag.ProjectId };

    internal TeamworkTag ToTag()
        => new(Id, Name, Color, ProjectId);
}
