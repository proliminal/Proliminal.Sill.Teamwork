namespace TeamworkTimer.Tests.Support;

public sealed record RecordedRequest(HttpMethod Method, string Path, string? Authorization, string? Body);
