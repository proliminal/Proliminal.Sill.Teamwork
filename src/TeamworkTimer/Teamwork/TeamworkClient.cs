using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

internal sealed class TeamworkClient(HttpClient http)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static Uri? ParseSite(string? text)
    {
        text = text?.Trim();

        if (string.IsNullOrEmpty(text))
            return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.')
            ? new UriBuilder("https", uri.Host).Uri
            : null;
    }

    public async Task<TeamworkTask> GetTaskAsync(Uri site, string token, long id, CancellationToken cancellation)
    {
        using var document = await SendRequestAsync(site, token, HttpMethod.Get, $"projects/api/v3/tasks/{id}.json?include=tasklists,projects", null, cancellation,
            notFound: $"There's no task {id}, or you can't see it.");

        var root = document.RootElement;
        var task = root.GetProperty("task");

        string? project = null;
        long? projectId = null;

        // A task is in a task list, which is in a project.
        if (task.TryGetProperty("tasklistId", out var tasklistId)
            && FindIncluded(root, "tasklists", tasklistId.GetInt64()) is { } tasklist
            && tasklist.TryGetProperty("projectId", out var projectElement)
            && projectElement.ValueKind == JsonValueKind.Number
            && projectElement.GetInt64() is > 0 and var found)
        {
            projectId = found;
            project = FindIncluded(root, "projects", found)?.GetProperty("name").GetString();
        }

        return new TeamworkTask(id, task.GetProperty("name").GetString() ?? $"Task {id}", project, projectId);
    }

    public async Task<IReadOnlyList<TrackedTimer>> GetTimersAsync(Uri site, string token, CancellationToken cancellation)
    {
        using var document = await SendRequestAsync(site, token, HttpMethod.Get, "projects/api/v3/me/timers.json?include=tasks,projects&pageSize=50", null, cancellation);

        var root = document.RootElement;

        return root.GetProperty("timers").EnumerateArray()
            .Select(timer => ReadTimer(timer, root))
            .OfType<TrackedTimer>()
            .ToArray();
    }

    public async Task<TrackedTimer> StartTimerAsync(Uri site, string token, TeamworkTask task, string description, bool isBillable, CancellationToken cancellation)
    {
        task = await EnsureProjectAsync(site, token, task, cancellation);

        var body = new { timer = new { taskId = task.Id, projectId = task.ProjectId, description, isBillable, isRunning = true } };

        using var document = await SendRequestAsync(site, token, HttpMethod.Post, "projects/api/v3/me/timers.json", body, cancellation);

        return ReadTimer(document.RootElement.GetProperty("timer"), document.RootElement, task)
            ?? throw new TeamworkException("Teamwork started a timer that isn't on a task.");
    }

    public Task<TrackedTimer> PauseTimerAsync(Uri site, string token, TrackedTimer timer, CancellationToken cancellation)
        => ChangeTimerAsync(site, token, timer, "pause", cancellation);

    public Task<TrackedTimer> ResumeTimerAsync(Uri site, string token, TrackedTimer timer, CancellationToken cancellation)
        => ChangeTimerAsync(site, token, timer, "resume", cancellation);

    // The time log the completed timer became, if Teamwork says, for its tags.
    public async Task<long?> LogTimerAsync(Uri site, string token, TrackedTimer timer, int seconds, string description, bool isBillable, CancellationToken cancellation)
    {
        var task = await EnsureProjectAsync(site, token, timer.Task, cancellation);

        var body = new { timer = new { taskId = task.Id, projectId = task.ProjectId, seconds, description, isBillable } };

        (await SendRequestAsync(site, token, HttpMethod.Put, $"projects/api/v3/me/timers/{timer.Id}.json", body, cancellation)).Dispose();

        using var completed = await SendRequestAsync(site, token, HttpMethod.Put, $"projects/api/v3/me/timers/{timer.Id}/complete.json", new { }, cancellation);

        return completed.RootElement.TryGetProperty("timer", out var logged)
            && logged.TryGetProperty("timeLogId", out var timeLogId)
            && timeLogId.ValueKind == JsonValueKind.Number
            && timeLogId.GetInt64() is > 0 and var id
                ? id
                : null;
    }

    // A timer can't have tags, so they're put on the time log it became.
    public async Task TagTimeLogAsync(Uri site, string token, long timeLogId, IReadOnlyList<TeamworkTag> tags, CancellationToken cancellation)
    {
        var body = new { timelog = new { tagIds = tags.Select(tag => tag.Id).ToArray() } };

        (await SendRequestAsync(site, token, HttpMethod.Patch, $"projects/api/v3/time/{timeLogId}.json", body, cancellation,
            notFound: "Teamwork doesn't know the time log any more; it may have been deleted there.")).Dispose();
    }

    // Every tag that can be seen, of every project, by name; a few hundred at a time.
    public async Task<IReadOnlyList<TeamworkTag>> GetTagsAsync(Uri site, string token, CancellationToken cancellation)
    {
        const int pageSize = 500;

        var tags = new List<TeamworkTag>();

        for (var page = 1; ; page++)
        {
            using var document = await SendRequestAsync(site, token, HttpMethod.Get,
                $"projects/api/v3/tags.json?orderBy=name&pageSize={pageSize}&page={page}", null, cancellation);

            var root = document.RootElement;
            var found = root.TryGetProperty("tags", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];

            tags.AddRange(found.Select(ReadTag).OfType<TeamworkTag>());

            var hasMore = root.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("page", out var paging)
                && paging.TryGetProperty("hasMore", out var more)
                && more.ValueKind == JsonValueKind.True;

            if (!hasMore || found.Length == 0)
                return tags;
        }
    }

    private static TeamworkTag? ReadTag(JsonElement tag)
    {
        if (!tag.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
            || !tag.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } text)
            return null;

        var color = tag.TryGetProperty("color", out var colorElement) && colorElement.ValueKind == JsonValueKind.String ? colorElement.GetString() : null;
        var projectId = tag.TryGetProperty("projectId", out var project) && project.ValueKind == JsonValueKind.Number ? project.GetInt64() : 0;

        return new TeamworkTag(id.GetInt64(), text, string.IsNullOrWhiteSpace(color) ? null : color, projectId);
    }

    public async Task DeleteTimerAsync(Uri site, string token, long timerId, CancellationToken cancellation)
        => (await SendRequestAsync(site, token, HttpMethod.Delete, $"projects/api/v3/me/timers/{timerId}.json", null, cancellation)).Dispose();

    // The task with its project, looked up when it isn't known, as for a timer started in Teamwork without one.
    private async Task<TeamworkTask> EnsureProjectAsync(Uri site, string token, TeamworkTask task, CancellationToken cancellation)
    {
        if (task.ProjectId is not null)
            return task;

        var found = await GetTaskAsync(site, token, task.Id, cancellation);

        return found.ProjectId is not null
            ? found
            : throw new TeamworkException($"Teamwork didn't say which project task #{task.Id} is in, so its time can't be logged.");
    }

    private async Task<TrackedTimer> ChangeTimerAsync(Uri site, string token, TrackedTimer timer, string change, CancellationToken cancellation)
    {
        using var document = await SendRequestAsync(site, token, HttpMethod.Put, $"projects/api/v3/me/timers/{timer.Id}/{change}.json", new { }, cancellation);

        return ReadTimer(document.RootElement.GetProperty("timer"), document.RootElement, timer.Task) ?? timer;
    }

    private async Task<JsonDocument> SendRequestAsync(Uri site, string token, HttpMethod method, string path, object? body, CancellationToken cancellation,
        string? notFound = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(site, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{token}:x")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
            request.Content = JsonContent.Create(body, options: _json);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellation);
        }
        catch (HttpRequestException ex)
        {
            throw new TeamworkException($"Teamwork can't be reached: {ex.Message}", isRetryable: true, ex);
        }
        catch (TaskCanceledException ex) when (!cancellation.IsCancellationRequested)
        {
            throw new TeamworkException("Teamwork didn't answer in time.", isRetryable: true, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellation);

            if (response.IsSuccessStatusCode)
                return text.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);

            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new TeamworkException("Teamwork didn't accept the API token. Check it on the Teamwork timer page."),
                HttpStatusCode.Forbidden => new TeamworkException($"Teamwork doesn't allow this: {ReadErrorMessage(text) ?? "no reason given"}."),
                HttpStatusCode.NotFound => new TeamworkException(notFound ?? "Teamwork doesn't know that timer any more; it may have been logged or deleted there."),
                HttpStatusCode.TooManyRequests => new TeamworkException("Teamwork asks to slow down; it's tried again in a while.", isRetryable: true),
                >= HttpStatusCode.InternalServerError => new TeamworkException($"Teamwork had a problem ({(int)response.StatusCode}).", isRetryable: true),
                _ => new TeamworkException(ReadErrorMessage(text) ?? $"Teamwork said no ({(int)response.StatusCode})."),
            };
        }
    }

    // Teamwork's errors: { "errors": [ { "title": "...", "detail": "..." } ] }.
    private static string? ReadErrorMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var error = errors[0];

                foreach (var name in (string[])["detail", "title", "message"])
                {
                    if (error.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } message)
                        return message;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    // What the response includes besides what was asked for, by type and id, such as the task of a timer.
    private static JsonElement? FindIncluded(JsonElement root, string type, long id)
        => root.TryGetProperty("included", out var included)
            && included.TryGetProperty(type, out var items)
            && items.ValueKind == JsonValueKind.Object
            && items.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out var item)
                ? item
                : null;

    // A timer as Teamwork describes it. The time it ran so far is its duration, which counts the intervals before the
    // last start, and the time since it was last started while it runs; worked out against Teamwork's clock and kept
    // against this PC's, so a clock that's off doesn't change it. A timer without a task isn't one this plugin keeps.
    private static TrackedTimer? ReadTimer(JsonElement timer, JsonElement root, TeamworkTask? task = null)
    {
        if (!timer.TryGetProperty("taskId", out var taskIdElement) || taskIdElement.ValueKind != JsonValueKind.Number || taskIdElement.GetInt64() == 0)
            return null;

        var taskId = taskIdElement.GetInt64();
        var isRunning = timer.TryGetProperty("running", out var running) && running.GetBoolean();
        var seconds = timer.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetInt64() : 0;

        var elapsed = TimeSpan.FromSeconds(seconds);

        if (isRunning && ReadTime(timer, "lastStartedAt") is { } startedAt)
        {
            var now = ReadTime(timer, "serverTime") ?? DateTimeOffset.UtcNow;

            if (now > startedAt)
                elapsed += now - startedAt;
        }

        if (task is null || task.Id != taskId)
        {
            var included = FindIncluded(root, "tasks", taskId);
            var name = included?.TryGetProperty("name", out var taskName) == true ? taskName.GetString() : null;

            var projectId = timer.TryGetProperty("projectId", out var projectElement) && projectElement.ValueKind == JsonValueKind.Number ? projectElement.GetInt64() : 0;
            var project = FindIncluded(root, "projects", projectId)?.GetProperty("name").GetString();

            // A timer without a project says 0; its task's is looked up when it's logged.
            task = new TeamworkTask(taskId, name ?? $"Task {taskId}", project, projectId > 0 ? projectId : null);
        }

        return new TrackedTimer(
            timer.GetProperty("id").GetInt64(),
            task,
            timer.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            timer.TryGetProperty("billable", out var billable) && billable.ValueKind is JsonValueKind.True,
            isRunning,
            elapsed,
            DateTimeOffset.UtcNow);
    }

    private static DateTimeOffset? ReadTime(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var time)
            ? time
            : null;
}
