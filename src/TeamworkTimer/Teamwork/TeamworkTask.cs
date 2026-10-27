namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

internal sealed record TeamworkTask(long Id, string Name, string? Project, long? ProjectId = null)
{
    public string Label
        => Project is null ? $"#{Id} {Name}" : $"#{Id} {Name} · {Project}";

    public string NameWithProject
        => Project is null ? Name : $"{Name} · {Project}";
}
