namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

/// <summary>
///     A Teamwork task, by its number.
/// </summary>
/// <param name="Id">The task's number, as in its link.</param>
/// <param name="Name">The task's name.</param>
/// <param name="Project">Its project's name, when Teamwork said it.</param>
/// <param name="ProjectId">
///     Its project, when Teamwork said it. Time is logged on a task within its project, so timers are started and logged
///     with it: without it, Teamwork takes the time as the site's rather than the project's, and refuses it.
/// </param>
internal sealed record TeamworkTask(long Id, string Name, string? Project, long? ProjectId = null)
{
    /// <summary>
    ///     How the task is shown: <c>#123 Fix the login · Website</c>.
    /// </summary>
    public string Label
        => Project is null ? $"#{Id} {Name}" : $"#{Id} {Name} · {Project}";

    /// <summary>
    ///     Its name with its project: <c>Fix the login · Website</c>.
    /// </summary>
    public string NameWithProject
        => Project is null ? Name : $"{Name} · {Project}";
}
