using System.Net;
using System.Text;

namespace TeamworkTimer.Tests.Support;

/// <summary>
///     A stand-in for the network: answers each request with the next answer given, in order, and keeps what was asked.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    /// <summary>
    ///     The requests sent so far, in order.
    /// </summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    ///     Answers the next request with <paramref name="status"/> and <paramref name="json"/>.
    /// </summary>
    public void QueueAnswer(HttpStatusCode status, string json)
        => _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    /// <summary>
    ///     Fails the next request with <paramref name="exception"/>, as when the network is down.
    /// </summary>
    public void QueueFailure(Exception exception)
        => _answers.Enqueue(() => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));

        return _answers.Dequeue()();
    }
}
