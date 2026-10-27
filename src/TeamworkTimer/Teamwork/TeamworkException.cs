namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

/// <summary>
///     What Teamwork answered when it didn't do what was asked, said so it can be shown as is.
/// </summary>
internal sealed class TeamworkException(string message, bool isRetryable = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    ///     Whether trying again later may work, as when Teamwork couldn't be reached, unlike a token it didn't accept.
    /// </summary>
    public bool IsRetryable { get; } = isRetryable;
}
