using Proliminal.Sill.Plugins.TeamworkTimer.Settings;
using Proliminal.Sill.Sdk;
using Proliminal.Sill.Sdk.Abstractions;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

// The tags and projects in Teamwork, for the popups to pick from.
internal sealed class TeamworkCatalog(TeamworkClient client, TeamworkConnection connection, CancellationToken cancellation)
{
    // How long the tags and projects read from Teamwork are kept before they're read again.
    private static readonly TimeSpan _age = TimeSpan.FromMinutes(10);

    private readonly State<IReadOnlyList<TeamworkTag>> _tags = Value.State<IReadOnlyList<TeamworkTag>>([]);
    private readonly State<IReadOnlyList<TeamworkProject?>> _projects = Value.State<IReadOnlyList<TeamworkProject?>>([]);
    private DateTimeOffset? _tagsReadAt;
    private DateTimeOffset? _projectsReadAt;

    public IReadOnlyValue<IReadOnlyList<TeamworkTag>> Tags
        => _tags;

    public IReadOnlyValue<IReadOnlyList<TeamworkProject?>> Projects
        => _projects;

    public void ReadQuietly()
    {
        _ = ReadTagsQuietlyAsync();
        _ = ReadProjectsQuietlyAsync();
    }

    // The project, as Teamwork has it now if it was read, as its name may have changed since.
    public TeamworkProject FindProject(long id, string? name)
        => _projects.Value.FirstOrDefault(project => project?.Id == id) ?? new TeamworkProject(id, name ?? "");

    public TeamworkTag FindTag(TeamworkTag tag)
        => _tags.Value.FirstOrDefault(known => known.Id == tag.Id) ?? tag;

    // Once in a while, as tags are seldom added; without them, only the tags picked before can be picked.
    private async Task ReadTagsQuietlyAsync()
    {
        if (_tagsReadAt is { } readAt && DateTimeOffset.UtcNow - readAt < _age || !connection.TryGetConnection(out var site, out var token))
            return;

        _tagsReadAt = DateTimeOffset.UtcNow;

        try
        {
            _tags.Value = await client.GetTagsAsync(site, token, cancellation);
        }
        catch (Exception)
        {
            _tagsReadAt = null;
        }
    }

    // As seldom as the tags; without them, only a task number can be typed.
    private async Task ReadProjectsQuietlyAsync()
    {
        if (_projectsReadAt is { } readAt && DateTimeOffset.UtcNow - readAt < _age || !connection.TryGetConnection(out var site, out var token))
            return;

        _projectsReadAt = DateTimeOffset.UtcNow;

        try
        {
            _projects.Value = await client.GetProjectsAsync(site, token, cancellation);
        }
        catch (Exception)
        {
            _projectsReadAt = null;
        }
    }
}
