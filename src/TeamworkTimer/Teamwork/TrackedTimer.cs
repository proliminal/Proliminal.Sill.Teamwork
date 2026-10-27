namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

/// <summary>
///     One of the user's timers in Teamwork, as it was when it was read.
/// </summary>
/// <param name="Id">Teamwork's id of the timer.</param>
/// <param name="Task">The task it runs for.</param>
/// <param name="Description">What the time is for, which goes on the time log.</param>
/// <param name="IsBillable">Whether the time is billable.</param>
/// <param name="IsRunning">Whether it runs, rather than being paused.</param>
/// <param name="Elapsed">How long it had run when it was read.</param>
/// <param name="ReadAt">When it was read, by this PC's clock.</param>
internal sealed record TrackedTimer(long Id, TeamworkTask Task, string Description, bool IsBillable, bool IsRunning, TimeSpan Elapsed, DateTimeOffset ReadAt)
{
    /// <summary>
    ///     How long it has run by <paramref name="now"/>.
    /// </summary>
    public TimeSpan ElapsedAt(DateTimeOffset now)
        => IsRunning && now > ReadAt ? Elapsed + (now - ReadAt) : Elapsed;
}
