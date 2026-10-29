namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

// A tag on time logs and tasks: for every project when its project is 0, or for only that project.
internal sealed record TeamworkTag(long Id, string Name, string? Color, long ProjectId)
{
    public bool IsFor(long? projectId)
        => ProjectId == 0 || ProjectId == projectId;
}
