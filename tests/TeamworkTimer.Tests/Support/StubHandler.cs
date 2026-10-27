using System.Net;
using System.Text;

namespace TeamworkTimer.Tests.Support;

public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void QueueAnswer(HttpStatusCode status, string json)
        => _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public void QueueFailure(Exception exception)
        => _answers.Enqueue(() => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));

        return _answers.Dequeue()();
    }
}
