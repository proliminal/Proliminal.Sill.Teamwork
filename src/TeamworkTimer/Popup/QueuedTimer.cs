using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Popup;

/// <summary>
///     What was typed for a task to start, kept while the timer that runs is stopped and logged first.
/// </summary>
/// <param name="Task">The task to start a timer for.</param>
/// <param name="Description">What the time is for.</param>
/// <param name="IsBillable">Whether the time is billable.</param>
internal sealed record QueuedTimer(TeamworkTask Task, string Description, bool IsBillable);
