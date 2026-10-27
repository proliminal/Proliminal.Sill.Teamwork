namespace Proliminal.Sill.Plugins.TeamworkTimer.Popup;

/// <summary>
///     What the Track time popup asks.
/// </summary>
internal enum TrackTimeStep
{
    /// <summary>The site or token is missing: it only says which, as they're set on the plugin's page.</summary>
    MissingSetup,

    /// <summary>Nothing runs: a task number to start a timer for.</summary>
    NewTask,

    /// <summary>A timer runs: stop it, or start another.</summary>
    Running,

    /// <summary>Another task was asked to start while a timer runs: stop that one first?</summary>
    ConfirmSwitch,

    /// <summary>The timer is paused: how much of it to log.</summary>
    LogTime,
}
