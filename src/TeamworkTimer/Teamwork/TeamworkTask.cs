namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

// What a timer's time is for: a task, or a project itself when there's no task, as for time that isn't on any task.
internal sealed record TeamworkTask(long? Id, string Name, string? Project, long? ProjectId = null)
{
    private const string NO_TASK = "No task";

    public static TeamworkTask ForProject(long projectId, string project)
        => new(null, NO_TASK, project, projectId);

    public string Label
        => Id is null ? $"{Name} · {Project}" : Project is null ? $"#{Id} {Name}" : $"#{Id} {Name} · {Project}";

    public string NameWithProject
        => Project is null ? Name : $"{Name} · {Project}";

    // The task's number, or the project when there's no task.
    public string Reference
        => Id is { } id ? $"#{id}" : Project ?? NO_TASK;

    // Whether a timer for this is one for that: the same task, or the same project without a task.
    public bool IsSameAs(TeamworkTask other)
        => Id is null ? other.Id is null && ProjectId == other.ProjectId : Id == other.Id;
}
