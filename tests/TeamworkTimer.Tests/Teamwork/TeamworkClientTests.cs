using System.Net;
using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;
using TeamworkTimer.Tests.Support;

namespace TeamworkTimer.Tests.Teamwork;

public sealed class TeamworkClientTests : IDisposable
{
    private static readonly Uri _site = new("https://example.teamwork.com/");

    private readonly StubHandler _handler = new();
    private readonly HttpClient _http;
    private readonly TeamworkClient _client;

    public TeamworkClientTests()
    {
        _http = new HttpClient(_handler);
        _client = new TeamworkClient(_http);
    }

    [Theory]
    [InlineData("example.teamwork.com", "https://example.teamwork.com/")]
    [InlineData("https://example.teamwork.com/app/tasks/123", "https://example.teamwork.com/")]
    [InlineData("http://example.teamwork.com", "https://example.teamwork.com/")]
    [InlineData("  example.eu.teamwork.com ", "https://example.eu.teamwork.com/")]
    public void ParseSite_KeepsOnlyTheHost_OverHttps(string text, string site)
        => Assert.Equal(new Uri(site), TeamworkClient.ParseSite(text));

    [Theory]
    [InlineData("")]
    [InlineData("example")]
    [InlineData("ftp://example.teamwork.com")]
    public void ParseSite_RefusesWhatIsntASite(string text)
        => Assert.Null(TeamworkClient.ParseSite(text));

    [Fact]
    public async Task GetTaskAsync_ReadsItsName_AndItsProjectThroughItsTaskList()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """
            {
              "task": { "id": 123, "name": "Fix the login", "tasklistId": 7 },
              "included": {
                "tasklists": { "7": { "id": 7, "name": "Bugs", "projectId": 42 } },
                "projects": { "42": { "id": 42, "name": "Website" } }
              }
            }
            """);

        var task = await _client.GetTaskAsync(_site, "tkn", 123, TestContext.Current.CancellationToken);

        Assert.Equal(new TeamworkTask(123, "Fix the login", "Website", 42), task);
        Assert.Equal("#123 Fix the login · Website", task.Label);
        Assert.Equal("/projects/api/v3/tasks/123.json", _handler.Requests.Single().Path);
    }

    [Fact]
    public async Task GetTaskAsync_SignsIn_WithTheTokenAsTheUserName()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "task": { "id": 1, "name": "A" } }""");

        await _client.GetTaskAsync(_site, "tkn", 1, TestContext.Current.CancellationToken);

        Assert.Equal($"Basic {Convert.ToBase64String("tkn:x"u8.ToArray())}", _handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task GetTaskAsync_SaysThereIsNoSuchTask_WhenTeamworkDoesntKnowIt()
    {
        _handler.QueueAnswer(HttpStatusCode.NotFound, "");

        var error = await Assert.ThrowsAsync<TeamworkException>(() => _client.GetTaskAsync(_site, "tkn", 99, TestContext.Current.CancellationToken));

        Assert.Contains("no task 99", error.Message);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task GetTimersAsync_AddsTheTimeSinceTheLastStart_ToTheDurationOfARunningTimer()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """
            {
              "timers": [
                {
                  "id": 5, "taskId": 123, "projectId": 42, "description": "Login", "billable": true, "running": true,
                  "duration": 600, "lastStartedAt": "2026-10-27T09:00:00Z", "serverTime": "2026-10-27T09:30:00Z"
                },
                { "id": 6, "taskId": 0, "running": false, "duration": 60 }
              ],
              "included": {
                "tasks": { "123": { "id": 123, "name": "Fix the login" } },
                "projects": { "42": { "id": 42, "name": "Website" } }
              }
            }
            """);

        var timer = Assert.Single(await _client.GetTimersAsync(_site, "tkn", TestContext.Current.CancellationToken));

        Assert.Equal(5, timer.Id);
        Assert.Equal("#123 Fix the login · Website", timer.Task.Label);
        Assert.Equal("Login", timer.Description);
        Assert.True(timer.IsBillable);
        Assert.True(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(40), timer.Elapsed);
    }

    [Fact]
    public async Task GetTimersAsync_TakesTheDuration_OfAPausedTimer()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """
            { "timers": [ { "id": 5, "taskId": 123, "running": false, "duration": 600, "lastStartedAt": "2026-10-27T09:00:00Z" } ] }
            """);

        var timer = Assert.Single(await _client.GetTimersAsync(_site, "tkn", TestContext.Current.CancellationToken));

        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), timer.Elapsed);
        Assert.Equal("Task 123", timer.Task.Name);
    }

    [Fact]
    public async Task GetTimersAsync_LeavesTheProjectUnknown_ForATimerWithoutOne()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timers": [ { "id": 5, "taskId": 123, "projectId": 0, "running": false, "duration": 60 } ] }""");

        var timer = Assert.Single(await _client.GetTimersAsync(_site, "tkn", TestContext.Current.CancellationToken));

        Assert.Null(timer.Task.ProjectId);
    }

    [Fact]
    public async Task StartTimerAsync_StartsTheTimer_InItsTasksProject()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5, "taskId": 123, "projectId": 42, "running": true, "duration": 0 } }""");

        var timer = await _client.StartTimerAsync(_site, "tkn", new TeamworkTask(123, "Fix the login", "Website", 42), "Login", isBillable: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"timer":{"taskId":123,"projectId":42,"description":"Login","isBillable":true,"isRunning":true}}""", _handler.Requests.Single().Body);
        Assert.Equal(42, timer.Task.ProjectId);
    }

    [Fact]
    public async Task LogTimerAsync_SetsTheTimer_InItsTasksProject_ThenCompletesIt()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5 } }""");
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5 } }""");

        await _client.LogTimerAsync(_site, "tkn", PausedTimer(new TeamworkTask(123, "Fix the login", "Website", 42)), 5400, "Login", isBillable: false,
            TestContext.Current.CancellationToken);

        Assert.Collection(_handler.Requests,
            set =>
            {
                Assert.Equal("PUT /projects/api/v3/me/timers/5.json", $"{set.Method} {set.Path}");
                Assert.Equal("""{"timer":{"taskId":123,"projectId":42,"seconds":5400,"description":"Login","isBillable":false}}""", set.Body);
            },
            complete => Assert.Equal("PUT /projects/api/v3/me/timers/5/complete.json", $"{complete.Method} {complete.Path}"));
    }

    [Fact]
    public async Task LogTimerAsync_LooksUpTheProject_OfATimerWithoutOne()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """
            {
              "task": { "id": 123, "name": "Fix the login", "tasklistId": 7 },
              "included": { "tasklists": { "7": { "id": 7, "projectId": 42 } }, "projects": { "42": { "id": 42, "name": "Website" } } }
            }
            """);
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5 } }""");
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5 } }""");

        await _client.LogTimerAsync(_site, "tkn", PausedTimer(new TeamworkTask(123, "Task 123", null)), 60, "", isBillable: true, TestContext.Current.CancellationToken);

        Assert.Equal("/projects/api/v3/tasks/123.json", _handler.Requests[0].Path);
        Assert.Contains("\"projectId\":42", _handler.Requests[1].Body);
    }

    [Fact]
    public async Task LogTimerAsync_SaysWhichTimeLogTheTimerBecame()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5 } }""");
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timer": { "id": 5, "timeLogId": 777 } }""");

        var timeLogId = await _client.LogTimerAsync(_site, "tkn", PausedTimer(new TeamworkTask(123, "Fix the login", "Website", 42)), 60, "", isBillable: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(777, timeLogId);
    }

    [Fact]
    public async Task TagTimeLogAsync_SetsTheTagIds_OnTheTimeLog()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """{ "timelog": { "id": 777 } }""");

        await _client.TagTimeLogAsync(_site, "tkn", 777, [new TeamworkTag(1, "Review", "#d84640", 0), new TeamworkTag(9, "Bug", null, 42)],
            TestContext.Current.CancellationToken);

        var request = _handler.Requests.Single();
        Assert.Equal("PATCH /projects/api/v3/time/777.json", $"{request.Method} {request.Path}");
        Assert.Equal("""{"timelog":{"tagIds":[1,9]}}""", request.Body);
    }

    [Fact]
    public async Task GetTagsAsync_ReadsEveryPage_AndSkipsTagsWithoutAName()
    {
        _handler.QueueAnswer(HttpStatusCode.OK, """
            {
              "tags": [ { "id": 1, "name": "Review", "color": "#d84640", "projectId": 0 }, { "id": 2, "name": "" } ],
              "meta": { "page": { "hasMore": true } }
            }
            """);
        _handler.QueueAnswer(HttpStatusCode.OK, """
            { "tags": [ { "id": 9, "name": "Bug", "color": "", "projectId": 42 } ], "meta": { "page": { "hasMore": false } } }
            """);

        var tags = await _client.GetTagsAsync(_site, "tkn", TestContext.Current.CancellationToken);

        Assert.Equal([new TeamworkTag(1, "Review", "#d84640", 0), new TeamworkTag(9, "Bug", null, 42)], tags);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.True(tags[0].IsFor(42));
        Assert.False(tags[1].IsFor(7));
    }

    [Fact]
    public async Task SendRequestAsync_SaysTheTokenIsWrong_AndThatTryingAgainWontHelp()
    {
        _handler.QueueAnswer(HttpStatusCode.Unauthorized, "");

        var error = await Assert.ThrowsAsync<TeamworkException>(() => _client.GetTimersAsync(_site, "tkn", TestContext.Current.CancellationToken));

        Assert.Contains("API token", error.Message);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task SendRequestAsync_ShowsTeamworksError_ForWhatItRefused()
    {
        _handler.QueueAnswer(HttpStatusCode.BadRequest, """{ "errors": [ { "title": "Bad Request", "detail": "seconds must be positive" } ] }""");

        var error = await Assert.ThrowsAsync<TeamworkException>(() => _client.LogTimerAsync(_site, "tkn", PausedTimer(new TeamworkTask(123, "A", null, 42)), 0, "", false,
            TestContext.Current.CancellationToken));

        Assert.Equal("seconds must be positive", error.Message);
    }

    [Fact]
    public async Task SendRequestAsync_SaysTryingAgainMayHelp_WhenTeamworkCantBeReached()
    {
        _handler.QueueFailure(new HttpRequestException("No such host is known."));

        var error = await Assert.ThrowsAsync<TeamworkException>(() => _client.GetTimersAsync(_site, "tkn", TestContext.Current.CancellationToken));

        Assert.True(error.IsRetryable);
    }

    public void Dispose()
        => _http.Dispose();

    private static TrackedTimer PausedTimer(TeamworkTask task)
        => new(5, task, "", IsBillable: false, IsRunning: false, TimeSpan.FromMinutes(90), DateTimeOffset.UtcNow);
}
