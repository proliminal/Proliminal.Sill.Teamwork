namespace TeamworkTimer.Tests.Support;

/// <summary>
///     A request <see cref="StubHandler"/> was sent: what a test checks of it.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path, without the site or query.</param>
/// <param name="Authorization">The Authorization header, if any.</param>
/// <param name="Body">The body, if any.</param>
public sealed record RecordedRequest(HttpMethod Method, string Path, string? Authorization, string? Body);
