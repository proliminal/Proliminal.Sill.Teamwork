using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Tracking;

internal sealed record PendingLog(TrackedTimer Timer, int Minutes, string Description, bool IsBillable, IReadOnlyList<TeamworkTag> Tags);
