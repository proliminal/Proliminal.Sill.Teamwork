using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Tracking;

/// <summary>
///     A time log that Teamwork didn't take, tried again until it does. The timer stays paused in Teamwork meanwhile, so
///     the time isn't lost when Sill closes; only what was typed for it is.
/// </summary>
/// <param name="Timer">The paused timer to log.</param>
/// <param name="Minutes">The time to log.</param>
/// <param name="Description">What the time was for.</param>
/// <param name="IsBillable">Whether the time is billable.</param>
internal sealed record PendingLog(TrackedTimer Timer, int Minutes, string Description, bool IsBillable);
