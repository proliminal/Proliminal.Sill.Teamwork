namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

internal sealed class TeamworkException(string message, bool isRetryable = false, Exception? inner = null) : Exception(message, inner)
{
    public bool IsRetryable { get; } = isRetryable;
}
