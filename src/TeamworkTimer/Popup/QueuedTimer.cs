using Proliminal.Sill.Plugins.TeamworkTimer.Teamwork;

namespace Proliminal.Sill.Plugins.TeamworkTimer.Popup;

internal sealed record QueuedTimer(TeamworkTask Task, string Description, bool IsBillable);
