namespace Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

internal sealed record TrackedTimer(long Id, TeamworkTask Task, string Description, bool IsBillable, bool IsRunning, TimeSpan Elapsed, DateTimeOffset ReadAt)
{
    public TimeSpan ElapsedAt(DateTimeOffset now)
        => IsRunning && now > ReadAt ? Elapsed + (now - ReadAt) : Elapsed;
}
